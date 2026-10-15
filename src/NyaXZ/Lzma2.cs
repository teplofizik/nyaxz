using System;
using System.IO;
using SevenZip;
using SevenZip.Compression.LZMA;

namespace NyaXZ
{
    /// <summary>
    /// LZMA2 chunk encoder built on top of the managed LZMA1 encoder (LZMA-SDK).
    /// Every chunk is a full-reset chunk (control 0xE0) so chunks are independently
    /// decodable; incompressible chunks fall back to uncompressed chunks (0x01/0x02).
    /// </summary>
    internal static class Lzma2
    {
        /// <summary>Uncompressed chunk size (keeps both size fields within 16 bits).</summary>
        public const int MaxChunkSize = 1 << 16;

        private static readonly CoderPropID[] PropIds =
        {
            CoderPropID.DictionarySize,
            CoderPropID.LitContextBits,
            CoderPropID.LitPosBits,
            CoderPropID.PosStateBits,
            CoderPropID.NumFastBytes
        };

        private static Encoder CreateEncoder(int DictionarySize)
        {
            var E = new Encoder();
            E.SetCoderProperties(PropIds, new object[] { DictionarySize, 3, 0, 2, 128 });
            return E;
        }

        /// <summary>
        /// LZMA coder properties (5 bytes: lclppb + dictionary size little-endian).
        /// </summary>
        public static byte[] CoderProperties(int DictionarySize)
        {
            using (var Ms = new MemoryStream())
            {
                CreateEncoder(DictionarySize).WriteCoderProperties(Ms);
                return Ms.ToArray();
            }
        }

        public static byte[] Encode(byte[] Data, int DictionarySize)
        {
            byte Lclppb = CoderProperties(DictionarySize)[0];

            using (var Out = new MemoryStream())
            {
                bool First = true;
                int Pos = 0;

                while (Pos < Data.Length)
                {
                    int Len = Math.Min(MaxChunkSize, Data.Length - Pos);

                    byte[] Compressed;
                    using (var In = new MemoryStream(Data, Pos, Len, false))
                    using (var Chunk = new MemoryStream())
                    {
                        CreateEncoder(DictionarySize).Code(In, Chunk, Len, Len, null);
                        Compressed = Chunk.ToArray();
                    }

                    if ((Compressed.Length < Len) && (Compressed.Length <= MaxChunkSize))
                    {
                        int UncompM1 = Len - 1;
                        int CompM1 = Compressed.Length - 1;

                        Out.WriteByte((byte)(0xE0 | ((UncompM1 >> 16) & 0x1F)));
                        Out.WriteByte((byte)((UncompM1 >> 8) & 0xFF));
                        Out.WriteByte((byte)(UncompM1 & 0xFF));
                        Out.WriteByte((byte)((CompM1 >> 8) & 0xFF));
                        Out.WriteByte((byte)(CompM1 & 0xFF));
                        Out.WriteByte(Lclppb);
                        Out.Write(Compressed, 0, Compressed.Length);
                    }
                    else
                    {
                        int SizeM1 = Len - 1;
                        Out.WriteByte((byte)(First ? 0x01 : 0x02));
                        Out.WriteByte((byte)((SizeM1 >> 8) & 0xFF));
                        Out.WriteByte((byte)(SizeM1 & 0xFF));
                        Out.Write(Data, Pos, Len);
                    }

                    First = false;
                    Pos += Len;
                }

                Out.WriteByte(0x00); // end of LZMA2 stream

                return Out.ToArray();
            }
        }
    }
}
