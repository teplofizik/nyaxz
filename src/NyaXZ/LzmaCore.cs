#nullable disable

// LZMA/LZMA2 decoder core adapted from SharpCompress (MIT) and the public-domain
// LZMA SDK. Only the decode path needed by NyaXZ is kept.

using System;
using System.IO;

namespace NyaXZ
{
    internal sealed class LzmaDataException : Exception
    {
        public LzmaDataException() : base("LZMA data error") { }
        public LzmaDataException(string Message) : base(Message) { }
    }

    internal sealed class LzmaInvalidParamException : Exception
    {
        public LzmaInvalidParamException(string Message) : base(Message) { }
    }

    internal static class LzmaBase
    {
        public const uint K_NUM_STATES = 12;
        public const int K_NUM_POS_SLOT_BITS = 6;
        public const int K_NUM_LEN_TO_POS_STATES_BITS = 2;
        public const uint K_NUM_LEN_TO_POS_STATES = 1 << K_NUM_LEN_TO_POS_STATES_BITS;
        public const uint K_MATCH_MIN_LEN = 2;
        public const int K_NUM_ALIGN_BITS = 4;
        public const uint K_START_POS_MODEL_INDEX = 4;
        public const uint K_END_POS_MODEL_INDEX = 14;
        public const uint K_NUM_FULL_DISTANCES = 1 << ((int)K_END_POS_MODEL_INDEX / 2);
        public const int K_NUM_POS_STATES_BITS_MAX = 4;
        public const uint K_NUM_POS_STATES_MAX = 1 << K_NUM_POS_STATES_BITS_MAX;
        public const int K_NUM_LOW_LEN_BITS = 3;
        public const int K_NUM_MID_LEN_BITS = 3;
        public const int K_NUM_HIGH_LEN_BITS = 8;
        public const uint K_NUM_LOW_LEN_SYMBOLS = 1 << K_NUM_LOW_LEN_BITS;
        public const uint K_NUM_MID_LEN_SYMBOLS = 1 << K_NUM_MID_LEN_BITS;
        public const uint K_NUM_LEN_SYMBOLS =
            K_NUM_LOW_LEN_SYMBOLS + K_NUM_MID_LEN_SYMBOLS + (1 << K_NUM_HIGH_LEN_BITS);

        public static uint GetLenToPosState(uint len)
        {
            len -= K_MATCH_MIN_LEN;
            if (len < K_NUM_LEN_TO_POS_STATES)
                return len;
            return K_NUM_LEN_TO_POS_STATES - 1;
        }

        public struct State
        {
            public uint _index;

            public void Init() => _index = 0;

            public void UpdateChar()
            {
                if (_index < 4) _index = 0;
                else if (_index < 10) _index -= 3;
                else _index -= 6;
            }

            public void UpdateMatch() => _index = (uint)(_index < 7 ? 7 : 10);
            public void UpdateRep() => _index = (uint)(_index < 7 ? 8 : 11);
            public void UpdateShortRep() => _index = (uint)(_index < 7 ? 9 : 11);
            public bool IsCharState() => _index < 7;
        }
    }

    internal struct LzmaBitDecoder
    {
        private const int K_NUM_BIT_MODEL_TOTAL_BITS = 11;
        private const uint K_BIT_MODEL_TOTAL = (1 << K_NUM_BIT_MODEL_TOTAL_BITS);
        private const int K_NUM_MOVE_BITS = 5;

        private uint _prob;

        public void Init() => _prob = K_BIT_MODEL_TOTAL >> 1;

        public uint Decode(LzmaRangeDecoder RangeDecoder)
        {
            var NewBound = (RangeDecoder._range >> K_NUM_BIT_MODEL_TOTAL_BITS) * _prob;
            if (RangeDecoder._code < NewBound)
            {
                RangeDecoder._range = NewBound;
                _prob += (K_BIT_MODEL_TOTAL - _prob) >> K_NUM_MOVE_BITS;
                RangeDecoder.Normalize2();
                return 0;
            }
            RangeDecoder._range -= NewBound;
            RangeDecoder._code -= NewBound;
            _prob -= _prob >> K_NUM_MOVE_BITS;
            RangeDecoder.Normalize2();
            return 1;
        }
    }

    internal readonly struct LzmaBitTreeDecoder
    {
        private readonly LzmaBitDecoder[] _models;
        private readonly int _numBitLevels;

        public LzmaBitTreeDecoder(int NumBitLevels)
        {
            _numBitLevels = NumBitLevels;
            _models = new LzmaBitDecoder[1 << NumBitLevels];
        }

        public void Init()
        {
            for (uint i = 1; i < (1 << _numBitLevels); i++)
                _models[i].Init();
        }

        public uint Decode(LzmaRangeDecoder RangeDecoder)
        {
            uint m = 1;
            for (int BitIndex = _numBitLevels; BitIndex > 0; BitIndex--)
                m = (m << 1) + _models[m].Decode(RangeDecoder);
            return m - ((uint)1 << _numBitLevels);
        }

        public uint ReverseDecode(LzmaRangeDecoder RangeDecoder)
        {
            uint m = 1;
            uint Symbol = 0;
            for (int BitIndex = 0; BitIndex < _numBitLevels; BitIndex++)
            {
                uint Bit = _models[m].Decode(RangeDecoder);
                m = (m << 1) + Bit;
                Symbol |= Bit << BitIndex;
            }
            return Symbol;
        }

        public static uint ReverseDecode(LzmaBitDecoder[] Models, uint StartIndex, LzmaRangeDecoder RangeDecoder, int NumBitLevels)
        {
            uint m = 1;
            uint Symbol = 0;
            for (int BitIndex = 0; BitIndex < NumBitLevels; BitIndex++)
            {
                uint Bit = Models[StartIndex + m].Decode(RangeDecoder);
                m = (m << 1) + Bit;
                Symbol |= Bit << BitIndex;
            }
            return Symbol;
        }
    }

    internal sealed class LzmaRangeDecoder
    {
        private const uint K_TOP_VALUE = 1 << 24;
        public uint _range;
        public uint _code;
        public long _total;
        private Stream _stream;

        public void Init(Stream Stream)
        {
            _stream = Stream;
            _code = 0;
            _range = 0xFFFFFFFF;
            for (int i = 0; i < 5; i++)
                _code = (_code << 8) | (byte)ReadByte();
            _total = 5;
        }

        public void ReleaseStream() => _stream = null;

        public void Normalize()
        {
            while (_range < K_TOP_VALUE)
            {
                _code = (_code << 8) | (byte)ReadByte();
                _range <<= 8;
                _total++;
            }
        }

        public void Normalize2()
        {
            if (_range < K_TOP_VALUE)
            {
                _code = (_code << 8) | (byte)ReadByte();
                _range <<= 8;
                _total++;
            }
        }

        public uint DecodeDirectBits(int NumTotalBits)
        {
            uint Range = _range;
            uint Code = _code;
            uint Result = 0;

            for (int i = NumTotalBits; i > 0; i--)
            {
                Range >>= 1;
                uint t = (Code - Range) >> 31;
                Code -= Range & (t - 1);
                Result = (Result << 1) | (1 - t);

                if (Range < K_TOP_VALUE)
                {
                    Code = (Code << 8) | (byte)ReadByte();
                    Range <<= 8;
                    _total++;
                }
            }

            _range = Range;
            _code = Code;
            return Result;
        }

        public uint DecodeBit(uint Size0, int NumTotalBits)
        {
            uint NewBound = (_range >> NumTotalBits) * Size0;
            uint Symbol;
            if (_code < NewBound)
            {
                Symbol = 0;
                _range = NewBound;
            }
            else
            {
                Symbol = 1;
                _code -= NewBound;
                _range -= NewBound;
            }
            Normalize2();
            return Symbol;
        }

        public bool IsFinished => _code == 0;

        private int ReadByte()
        {
            int b = _stream.ReadByte();
            if (b < 0)
                throw new LzmaDataException("Truncated LZMA range stream");
            return b;
        }
    }

    internal sealed class LzmaOutWindow
    {
        private byte[] _buffer;
        private int _windowSize;
        private int _pos;
        private int _streamPos;
        private int _pendingLen;
        private int _pendingDist;
        private Stream _stream;
        private long _total;
        private long _limit;

        public long Total => _total;

        public void Create(int WindowSize)
        {
            if (WindowSize <= 0)
                throw new LzmaInvalidParamException($"Invalid dictionary size {WindowSize}");

            if ((_buffer == null) || (_windowSize != WindowSize))
                _buffer = new byte[WindowSize];

            _windowSize = WindowSize;
            _pos = 0;
            _streamPos = 0;
            _pendingLen = 0;
            _total = 0;
            _limit = 0;
        }

        public void Init(Stream Stream) => _stream = Stream;

        public void Reset()
        {
            Flush();
            _pos = 0;
            _streamPos = 0;
            _pendingLen = 0;
            _total = 0;
            _limit = 0;
        }

        public void Flush()
        {
            if (_stream == null)
                return;

            int Size = _pos - _streamPos;
            if (Size == 0)
                return;

            _stream.Write(_buffer, _streamPos, Size);
            if (_pos >= _windowSize)
                _pos = 0;
            _streamPos = _pos;
        }

        public void ReleaseStream()
        {
            Flush();
            _stream = null;
        }

        public void CopyPending()
        {
            if (_pendingLen < 1)
                return;

            int Rem = _pendingLen;
            int Pos = ((_pendingDist < _pos ? _pos : _pos + _windowSize) - _pendingDist - 1);
            while ((Rem > 0) && HasSpace)
            {
                if (Pos >= _windowSize)
                    Pos = 0;
                PutByte(_buffer[Pos++]);
                Rem--;
            }
            _pendingLen = Rem;
        }

        public void CopyBlock(int Distance, int Len)
        {
            int Rem = Len;
            int Pos = (Distance < _pos ? _pos : _pos + _windowSize) - Distance - 1;

            while ((Rem > 0) && HasSpace)
            {
                if (Pos >= _windowSize)
                    Pos = 0;
                PutByte(_buffer[Pos++]);
                Rem--;
            }

            _pendingLen = Rem;
            _pendingDist = Distance;
        }

        public void PutByte(byte B)
        {
            _buffer[_pos++] = B;
            _total++;
            if (_pos >= _windowSize)
                Flush();
        }

        public byte GetByte(int Distance)
        {
            int Pos = _pos - Distance - 1;
            if (Pos < 0)
                Pos += _windowSize;
            return _buffer[Pos];
        }

        public int CopyStream(Stream Stream, int Len)
        {
            int Size = Len;
            while ((Size > 0) && (_pos < _windowSize) && (_total < _limit))
            {
                int CurSize = _windowSize - _pos;
                if (CurSize > _limit - _total)
                    CurSize = (int)(_limit - _total);
                if (CurSize > Size)
                    CurSize = Size;

                int Read = Stream.Read(_buffer, _pos, CurSize);
                if (Read == 0)
                    throw new LzmaDataException("Truncated LZMA2 uncompressed chunk");

                Size -= Read;
                _pos += Read;
                _total += Read;
                if (_pos >= _windowSize)
                    Flush();
            }
            return Len - Size;
        }

        public void SetLimit(long Size) => _limit = _total + Size;

        public bool HasSpace => (_pos < _windowSize) && (_total < _limit);
        public bool HasPending => _pendingLen > 0;
    }

    internal sealed class LzmaDecoder
    {
        public bool HasEndMarker => _rep0 == uint.MaxValue;

        private sealed class LenDecoder
        {
            private LzmaBitDecoder _choice;
            private LzmaBitDecoder _choice2;
            private readonly LzmaBitTreeDecoder[] _lowCoder = new LzmaBitTreeDecoder[LzmaBase.K_NUM_POS_STATES_MAX];
            private readonly LzmaBitTreeDecoder[] _midCoder = new LzmaBitTreeDecoder[LzmaBase.K_NUM_POS_STATES_MAX];
            private LzmaBitTreeDecoder _highCoder = new LzmaBitTreeDecoder(LzmaBase.K_NUM_HIGH_LEN_BITS);
            private uint _numPosStates;

            public void Create(uint NumPosStates)
            {
                for (uint PosState = _numPosStates; PosState < NumPosStates; PosState++)
                {
                    _lowCoder[PosState] = new LzmaBitTreeDecoder(LzmaBase.K_NUM_LOW_LEN_BITS);
                    _midCoder[PosState] = new LzmaBitTreeDecoder(LzmaBase.K_NUM_MID_LEN_BITS);
                }
                _numPosStates = NumPosStates;
            }

            public void Init()
            {
                _choice.Init();
                for (uint PosState = 0; PosState < _numPosStates; PosState++)
                {
                    _lowCoder[PosState].Init();
                    _midCoder[PosState].Init();
                }
                _choice2.Init();
                _highCoder.Init();
            }

            public uint Decode(LzmaRangeDecoder RangeDecoder, uint PosState)
            {
                if (_choice.Decode(RangeDecoder) == 0)
                    return _lowCoder[PosState].Decode(RangeDecoder);

                uint Symbol = LzmaBase.K_NUM_LOW_LEN_SYMBOLS;
                if (_choice2.Decode(RangeDecoder) == 0)
                {
                    Symbol += _midCoder[PosState].Decode(RangeDecoder);
                }
                else
                {
                    Symbol += LzmaBase.K_NUM_MID_LEN_SYMBOLS;
                    Symbol += _highCoder.Decode(RangeDecoder);
                }
                return Symbol;
            }
        }

        private sealed class LiteralDecoder
        {
            private struct Decoder2
            {
                private LzmaBitDecoder[] _decoders;
                private int _baseIndex;

                public void Create(LzmaBitDecoder[] Decoders, int BaseIndex)
                {
                    _decoders = Decoders;
                    _baseIndex = BaseIndex;
                }

                public void Init()
                {
                    for (int i = 0; i < 0x300; i++)
                        _decoders[_baseIndex + i].Init();
                }

                public byte DecodeNormal(LzmaRangeDecoder RangeDecoder)
                {
                    uint Symbol = 1;
                    do
                    {
                        Symbol = (Symbol << 1) | _decoders[_baseIndex + Symbol].Decode(RangeDecoder);
                    } while (Symbol < 0x100);
                    return (byte)Symbol;
                }

                public byte DecodeWithMatchByte(LzmaRangeDecoder RangeDecoder, byte MatchByte)
                {
                    uint Symbol = 1;
                    do
                    {
                        uint MatchBit = (uint)(MatchByte >> 7) & 1;
                        MatchByte <<= 1;
                        uint Bit = _decoders[_baseIndex + ((1 + MatchBit) << 8) + Symbol].Decode(RangeDecoder);
                        Symbol = (Symbol << 1) | Bit;
                        if (MatchBit != Bit)
                        {
                            while (Symbol < 0x100)
                                Symbol = (Symbol << 1) | _decoders[_baseIndex + Symbol].Decode(RangeDecoder);
                            break;
                        }
                    } while (Symbol < 0x100);
                    return (byte)Symbol;
                }
            }

            private Decoder2[] _coders;
            private LzmaBitDecoder[] _models;
            private int _numPrevBits;
            private int _numPosBits;
            private uint _posMask;

            public void Create(int NumPosBits, int NumPrevBits)
            {
                if ((_coders != null) && (_numPrevBits == NumPrevBits) && (_numPosBits == NumPosBits))
                    return;

                _numPosBits = NumPosBits;
                _posMask = ((uint)1 << NumPosBits) - 1;
                _numPrevBits = NumPrevBits;

                uint NumStates = (uint)1 << (_numPrevBits + _numPosBits);
                _models = new LzmaBitDecoder[checked((int)(NumStates * 0x300))];
                _coders = new Decoder2[NumStates];

                for (uint i = 0; i < NumStates; i++)
                    _coders[i].Create(_models, checked((int)(i * 0x300)));
            }

            public void Init()
            {
                uint NumStates = (uint)1 << (_numPrevBits + _numPosBits);
                for (uint i = 0; i < NumStates; i++)
                    _coders[i].Init();
            }

            private uint GetState(uint Pos, byte PrevByte) =>
                ((Pos & _posMask) << _numPrevBits) + (uint)(PrevByte >> (8 - _numPrevBits));

            public byte DecodeNormal(LzmaRangeDecoder RangeDecoder, uint Pos, byte PrevByte) =>
                _coders[GetState(Pos, PrevByte)].DecodeNormal(RangeDecoder);

            public byte DecodeWithMatchByte(LzmaRangeDecoder RangeDecoder, uint Pos, byte PrevByte, byte MatchByte) =>
                _coders[GetState(Pos, PrevByte)].DecodeWithMatchByte(RangeDecoder, MatchByte);
        }

        private readonly LzmaBitDecoder[] _isMatchDecoders = new LzmaBitDecoder[LzmaBase.K_NUM_STATES << LzmaBase.K_NUM_POS_STATES_BITS_MAX];
        private readonly LzmaBitDecoder[] _isRepDecoders = new LzmaBitDecoder[LzmaBase.K_NUM_STATES];
        private readonly LzmaBitDecoder[] _isRepG0Decoders = new LzmaBitDecoder[LzmaBase.K_NUM_STATES];
        private readonly LzmaBitDecoder[] _isRepG1Decoders = new LzmaBitDecoder[LzmaBase.K_NUM_STATES];
        private readonly LzmaBitDecoder[] _isRepG2Decoders = new LzmaBitDecoder[LzmaBase.K_NUM_STATES];
        private readonly LzmaBitDecoder[] _isRep0LongDecoders = new LzmaBitDecoder[LzmaBase.K_NUM_STATES << LzmaBase.K_NUM_POS_STATES_BITS_MAX];
        private readonly LzmaBitTreeDecoder[] _posSlotDecoder = new LzmaBitTreeDecoder[LzmaBase.K_NUM_LEN_TO_POS_STATES];
        private readonly LzmaBitDecoder[] _posDecoders = new LzmaBitDecoder[LzmaBase.K_NUM_FULL_DISTANCES - LzmaBase.K_END_POS_MODEL_INDEX];
        private LzmaBitTreeDecoder _posAlignDecoder = new LzmaBitTreeDecoder(LzmaBase.K_NUM_ALIGN_BITS);
        private readonly LenDecoder _lenDecoder = new LenDecoder();
        private readonly LenDecoder _repLenDecoder = new LenDecoder();
        private readonly LiteralDecoder _literalDecoder = new LiteralDecoder();
        private uint _posStateMask;
        private LzmaBase.State _state;
        private uint _rep0, _rep1, _rep2, _rep3;

        public LzmaDecoder()
        {
            for (int i = 0; i < LzmaBase.K_NUM_LEN_TO_POS_STATES; i++)
                _posSlotDecoder[i] = new LzmaBitTreeDecoder(LzmaBase.K_NUM_POS_SLOT_BITS);
        }

        private void Init()
        {
            for (uint i = 0; i < LzmaBase.K_NUM_STATES; i++)
            {
                for (uint j = 0; j <= _posStateMask; j++)
                {
                    uint Index = (i << LzmaBase.K_NUM_POS_STATES_BITS_MAX) + j;
                    _isMatchDecoders[Index].Init();
                    _isRep0LongDecoders[Index].Init();
                }
                _isRepDecoders[i].Init();
                _isRepG0Decoders[i].Init();
                _isRepG1Decoders[i].Init();
                _isRepG2Decoders[i].Init();
            }

            _literalDecoder.Init();
            for (uint i = 0; i < LzmaBase.K_NUM_LEN_TO_POS_STATES; i++)
                _posSlotDecoder[i].Init();
            for (uint i = 0; i < LzmaBase.K_NUM_FULL_DISTANCES - LzmaBase.K_END_POS_MODEL_INDEX; i++)
                _posDecoders[i].Init();

            _lenDecoder.Init();
            _repLenDecoder.Init();
            _posAlignDecoder.Init();

            _state.Init();
            _rep0 = _rep1 = _rep2 = _rep3 = 0;
        }

        public void SetDecoderProperties(byte[] Properties)
        {
            if ((Properties == null) || (Properties.Length < 1))
                throw new LzmaInvalidParamException("Empty LZMA properties");

            int Lc = Properties[0] % 9;
            int Remainder = Properties[0] / 9;
            int Lp = Remainder % 5;
            int Pb = Remainder / 5;

            if (Lp > 8 || Lc > 8 || Pb > LzmaBase.K_NUM_POS_STATES_BITS_MAX)
                throw new LzmaInvalidParamException("Invalid LZMA properties");

            _literalDecoder.Create(Lp, Lc);

            uint NumPosStates = (uint)1 << Pb;
            _lenDecoder.Create(NumPosStates);
            _repLenDecoder.Create(NumPosStates);
            _posStateMask = NumPosStates - 1;

            Init();
        }

        public bool Code(int DictionarySize, LzmaOutWindow OutWindow, LzmaRangeDecoder RangeDecoder)
        {
            long DictionarySizeCheck = Math.Max(DictionarySize, 1);

            OutWindow.CopyPending();

            while (OutWindow.HasSpace)
            {
                uint PosState = (uint)OutWindow.Total & _posStateMask;
                if (_isMatchDecoders[(_state._index << LzmaBase.K_NUM_POS_STATES_BITS_MAX) + PosState].Decode(RangeDecoder) == 0)
                {
                    byte PrevByte = OutWindow.GetByte(0);
                    byte B = !_state.IsCharState()
                        ? _literalDecoder.DecodeWithMatchByte(RangeDecoder, (uint)OutWindow.Total, PrevByte, OutWindow.GetByte((int)_rep0))
                        : _literalDecoder.DecodeNormal(RangeDecoder, (uint)OutWindow.Total, PrevByte);

                    OutWindow.PutByte(B);
                    _state.UpdateChar();
                }
                else
                {
                    uint Len;
                    if (_isRepDecoders[_state._index].Decode(RangeDecoder) == 1)
                    {
                        if (_isRepG0Decoders[_state._index].Decode(RangeDecoder) == 0)
                        {
                            if (_isRep0LongDecoders[(_state._index << LzmaBase.K_NUM_POS_STATES_BITS_MAX) + PosState].Decode(RangeDecoder) == 0)
                            {
                                _state.UpdateShortRep();
                                OutWindow.PutByte(OutWindow.GetByte((int)_rep0));
                                continue;
                            }
                        }
                        else
                        {
                            uint Distance;
                            if (_isRepG1Decoders[_state._index].Decode(RangeDecoder) == 0)
                            {
                                Distance = _rep1;
                            }
                            else
                            {
                                if (_isRepG2Decoders[_state._index].Decode(RangeDecoder) == 0)
                                {
                                    Distance = _rep2;
                                }
                                else
                                {
                                    Distance = _rep3;
                                    _rep3 = _rep2;
                                }
                                _rep2 = _rep1;
                            }
                            _rep1 = _rep0;
                            _rep0 = Distance;
                        }

                        Len = _repLenDecoder.Decode(RangeDecoder, PosState) + LzmaBase.K_MATCH_MIN_LEN;
                        _state.UpdateRep();
                    }
                    else
                    {
                        _rep3 = _rep2;
                        _rep2 = _rep1;
                        _rep1 = _rep0;
                        Len = LzmaBase.K_MATCH_MIN_LEN + _lenDecoder.Decode(RangeDecoder, PosState);
                        _state.UpdateMatch();

                        uint PosSlot = _posSlotDecoder[LzmaBase.GetLenToPosState(Len)].Decode(RangeDecoder);
                        if (PosSlot >= LzmaBase.K_START_POS_MODEL_INDEX)
                        {
                            int NumDirectBits = (int)((PosSlot >> 1) - 1);
                            _rep0 = (2 | (PosSlot & 1)) << NumDirectBits;
                            if (PosSlot < LzmaBase.K_END_POS_MODEL_INDEX)
                            {
                                _rep0 += LzmaBitTreeDecoder.ReverseDecode(_posDecoders, _rep0 - PosSlot - 1, RangeDecoder, NumDirectBits);
                            }
                            else
                            {
                                _rep0 += (RangeDecoder.DecodeDirectBits(NumDirectBits - LzmaBase.K_NUM_ALIGN_BITS) << LzmaBase.K_NUM_ALIGN_BITS);
                                _rep0 += _posAlignDecoder.ReverseDecode(RangeDecoder);
                            }
                        }
                        else
                        {
                            _rep0 = PosSlot;
                        }
                    }

                    if ((_rep0 >= OutWindow.Total) || (_rep0 >= DictionarySizeCheck))
                    {
                        if (_rep0 == 0xFFFFFFFF)
                            return true;
                        throw new LzmaDataException();
                    }

                    OutWindow.CopyBlock((int)_rep0, (int)Len);
                }
            }

            return false;
        }
    }
}
