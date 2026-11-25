#nullable disable

using System;
using System.IO;

namespace NyaXZ
{
    /// <summary>
    /// Stateful LZMA2 chunk decoder (LZMA2 -> raw bytes) using the managed LZMA core.
    /// </summary>
    internal sealed class Lzma2Decoder
    {
        private readonly LzmaDecoder Decoder = new LzmaDecoder();
        private readonly LzmaOutWindow OutWindow = new LzmaOutWindow();
        private readonly LzmaRangeDecoder RangeDecoder = new LzmaRangeDecoder();
        private readonly byte[] Properties = new byte[1];
        private readonly int DictionarySize;

        public Lzma2Decoder(int DictionarySize)
        {
            if (DictionarySize <= 0)
                throw new LzmaInvalidParamException("Invalid LZMA2 dictionary size");

            this.DictionarySize = Math.Max(DictionarySize, 4096);
            OutWindow.Create(this.DictionarySize);
        }

        public void Decode(Stream Input, Stream Output)
        {
            bool NeedDictionaryReset = true;
            bool NeedProperties = true;
            bool PropertiesSet = false;

            OutWindow.Init(Output);

            while (true)
            {
                int Control = ReadByte(Input);
                if (Control == 0x00)
                    break;

                if ((Control >= 0xE0) || (Control == 0x01))
                {
                    NeedProperties = true;
                    NeedDictionaryReset = false;
                    OutWindow.Reset();
                }
                else if (NeedDictionaryReset)
                {
                    throw new LzmaDataException("LZMA2 stream starts without a dictionary reset");
                }

                if (Control >= 0x80)
                {
                    int Unpacked = ((Control & 0x1F) << 16) + (ReadByte(Input) << 8) + ReadByte(Input) + 1;
                    int Packed = (ReadByte(Input) << 8) + ReadByte(Input) + 1;

                    if (Control >= 0xC0)
                    {
                        NeedProperties = false;
                        Properties[0] = (byte)ReadByte(Input);
                        Decoder.SetDecoderProperties(Properties);
                        PropertiesSet = true;
                    }
                    else if (NeedProperties)
                    {
                        throw new LzmaDataException("LZMA2 chunk without properties");
                    }
                    else if (Control >= 0xA0)
                    {
                        Decoder.SetDecoderProperties(Properties);
                    }

                    if (!PropertiesSet)
                        throw new LzmaDataException("LZMA2 chunk before properties were set");

                    RangeDecoder.Init(Input);
                    OutWindow.SetLimit(Unpacked);

                    Decoder.Code(DictionarySize, OutWindow, RangeDecoder);
                    OutWindow.CopyPending();

                    if (OutWindow.HasPending)
                        throw new LzmaDataException("LZMA2 chunk left pending output");

                    if (RangeDecoder._total != Packed)
                        throw new LzmaDataException("LZMA2 packed size mismatch");
                }
                else if (Control > 0x02)
                {
                    throw new LzmaDataException($"Invalid LZMA2 control byte 0x{Control:X2}");
                }
                else
                {
                    int Unpacked = (ReadByte(Input) << 8) + ReadByte(Input) + 1;
                    OutWindow.SetLimit(Unpacked);

                    if (OutWindow.CopyStream(Input, Unpacked) != Unpacked)
                        throw new LzmaDataException("Truncated LZMA2 uncompressed chunk");
                }
            }

            OutWindow.ReleaseStream();
        }

        private static int ReadByte(Stream Stream)
        {
            int Value = Stream.ReadByte();
            if (Value < 0)
                throw new LzmaDataException("Truncated LZMA2 stream");
            return Value;
        }
    }
}
