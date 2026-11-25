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
        /// <summary>Maximum packed size of one LZMA2 compressed chunk (16-bit field + 1).</summary>
        public const int MaxPackedSize = 1 << 16;

        /// <summary>Maximum unpacked size of one LZMA2 compressed chunk (21-bit field + 1).</summary>
        public const int MaxUnpackedSize = 1 << 21;

        /// <summary>Uncompressed chunk size used for incompressible data.</summary>
        public const int MaxChunkSize = MaxPackedSize;

        private const int MinChunkSize = 4096;
        private const int StartChunkSize = 1 << 17;

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

                // Packed/unpacked ratio of the last compressed chunk. It lets us
                // pick the largest chunk that still fits into MaxPackedSize, so
                // fewer LZMA model resets are needed per SquashFS block.
                double Ratio = 0;

                while (Pos < Data.Length)
                {
                    int Remaining = Data.Length - Pos;

                    int Len = (Ratio > 0)
                        ? (int)(MaxPackedSize * 0.98 / Ratio)
                        : StartChunkSize;
                    Len = Math.Max(MinChunkSize, Math.Min(Remaining, Math.Min(MaxUnpackedSize, Len)));
                    Len = Math.Min(Len, Remaining);

                    byte[] Compressed;
                    while (true)
                    {
                        Compressed = CompressChunk(Data, Pos, Len, DictionarySize);

                        if ((Compressed.Length < Len) && (Compressed.Length <= MaxPackedSize))
                            break;

                        // Incompressible at this size: do not shrink, store it raw.
                        if ((Compressed.Length >= Len) || (Len <= MinChunkSize))
                        {
                            Compressed = null;
                            break;
                        }

                        Len = Math.Max(MinChunkSize, (int)(Len * 0.75));
                    }

                    if (Compressed != null)
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

                        double ChunkRatio = (double)Compressed.Length / Len;
                        Ratio = (Ratio > 0) ? (Ratio * 0.5 + ChunkRatio * 0.5) : ChunkRatio;

                        Pos += Len;
                    }
                    else
                    {
                        int Size = Math.Min(Remaining, MaxChunkSize);
                        int SizeM1 = Size - 1;

                        Out.WriteByte((byte)(First ? 0x01 : 0x02));
                        Out.WriteByte((byte)((SizeM1 >> 8) & 0xFF));
                        Out.WriteByte((byte)(SizeM1 & 0xFF));
                        Out.Write(Data, Pos, Size);

                        Ratio = 1.0;
                        Pos += Size;
                    }

                    First = false;
                }

                Out.WriteByte(0x00); // end of LZMA2 stream

                return Out.ToArray();
            }
        }

        private static byte[] CompressChunk(byte[] Data, int Pos, int Len, int DictionarySize)
        {
            using (var In = new MemoryStream(Data, Pos, Len, false))
            using (var Chunk = new MemoryStream())
            {
                CreateEncoder(DictionarySize).Code(In, Chunk, Len, Len, null);
                return Chunk.ToArray();
            }
        }
    }
}
