using System;
using System.IO;
using SevenZip.Compression.LZMA;

namespace NyaXZ
{
    /// <summary>
    /// Managed XZ (.xz) decompressor. Handles a single-stream XZ with LZMA2-filtered
    /// blocks and CRC32/CRC64/SHA-256/None checks (checks are skipped, not verified).
    /// </summary>
    public static class XzDecompressor
    {
        private const int Lzma2FilterId = 0x21;

        public static byte[] Decompress(byte[] Data)
        {
            if (Data == null) throw new ArgumentNullException(nameof(Data));

            if ((Data.Length < 24) ||
                (Data[0] != 0xFD) || (Data[1] != 0x37) || (Data[2] != 0x7A) ||
                (Data[3] != 0x58) || (Data[4] != 0x5A) || (Data[5] != 0x00))
                throw new InvalidDataException("Not an XZ stream");

            int CheckSize = GetCheckSize(Data[7]);

            using (var Out = new MemoryStream())
            {
                int Pos = 12;

                while (Pos < Data.Length)
                {
                    if (Data[Pos] == 0x00)
                        break; // index indicator: no more blocks

                    int HeaderSize = (Data[Pos] + 1) * 4;
                    int BlockStart = Pos;
                    int DataStart = Pos + HeaderSize;

                    byte FilterId;
                    byte[] FilterProps;
                    ParseBlockHeader(Data, Pos, out FilterId, out FilterProps);

                    if (FilterId != Lzma2FilterId)
                        throw new InvalidDataException($"Unsupported XZ filter 0x{FilterId:X2}");

                    Pos = DataStart;
                    DecodeLzma2(Data, ref Pos, FilterProps[0], Out);

                    int Lzma2Len = Pos - DataStart;
                    int Pad = (4 - ((HeaderSize + Lzma2Len) % 4)) % 4;
                    Pos += Pad + CheckSize;

                    if (Pos <= BlockStart)
                        throw new InvalidDataException("XZ block parse did not advance");
                }

                return Out.ToArray();
            }
        }

        private static int GetCheckSize(byte StreamFlags)
        {
            switch (StreamFlags & 0x0F)
            {
                case 0x00: return 0;  // None
                case 0x01: return 4;  // CRC32
                case 0x04: return 8;  // CRC64
                case 0x0A: return 32; // SHA-256
                default: throw new InvalidDataException($"Unsupported XZ check id 0x{StreamFlags & 0x0F:X2}");
            }
        }

        private static void ParseBlockHeader(byte[] Data, int Pos, out byte FilterId, out byte[] FilterProps)
        {
            int HeaderSize = (Data[Pos] + 1) * 4;
            int P = Pos + 2; // skip size byte and block flags

            FilterId = (byte)Varint.Read(Data, ref P);
            int PropsSize = (int)Varint.Read(Data, ref P);
            FilterProps = new byte[PropsSize];
            Array.Copy(Data, P, FilterProps, 0, PropsSize);

            if ((FilterId != Lzma2FilterId) || (PropsSize != 1))
                throw new InvalidDataException("Only the LZMA2 filter is supported");

            _ = HeaderSize;
        }

        private static long DictionarySize(byte Code) => (long)(2 | (Code & 1)) << ((Code >> 1) + 11);

        private static byte[] CoderProperties(byte Lclppb, long DictionarySize)
        {
            var Res = new byte[5];
            Res[0] = Lclppb;
            Res[1] = (byte)(DictionarySize & 0xFF);
            Res[2] = (byte)((DictionarySize >> 8) & 0xFF);
            Res[3] = (byte)((DictionarySize >> 16) & 0xFF);
            Res[4] = (byte)((DictionarySize >> 24) & 0xFF);
            return Res;
        }

        private static void DecodeLzma2(byte[] Data, ref int Pos, byte DictionaryCode, MemoryStream Out)
        {
            long DictSize = DictionarySize(DictionaryCode);
            var Decoder = new Decoder();
            byte Lclppb = 0;
            bool HasProps = false;

            while (true)
            {
                byte Control = Data[Pos++];

                if (Control == 0x00)
                    return;

                if (Control < 0x80)
                {
                    if (Control > 0x02)
                        throw new InvalidDataException($"Invalid LZMA2 control 0x{Control:X2}");

                    int Size = ((Data[Pos] << 8) | Data[Pos + 1]) + 1;
                    Pos += 2;
                    Out.Write(Data, Pos, Size);
                    Pos += Size;
                    continue;
                }

                int Uncompressed = ((Control & 0x1F) << 16) + ((Data[Pos] << 8) | Data[Pos + 1]) + 1;
                Pos += 2;
                int Compressed = ((Data[Pos] << 8) | Data[Pos + 1]) + 1;
                Pos += 2;

                if ((Control & 0x40) != 0)
                {
                    Lclppb = Data[Pos++];
                    HasProps = true;
                }

                if (!HasProps)
                    throw new InvalidDataException("LZMA2 chunk without properties");

                // 0xA0/0xC0/0xE0 reset the LZMA state; 0x80 continues it.
                if ((Control & 0x60) != 0)
                {
                    Decoder = new Decoder();
                    Decoder.SetDecoderProperties(CoderProperties(Lclppb, DictSize));
                }

                using (var In = new MemoryStream(Data, Pos, Compressed, false))
                {
                    Decoder.Code(In, Out, Compressed, Uncompressed, null);
                }

                Pos += Compressed;
            }
        }
    }
}
