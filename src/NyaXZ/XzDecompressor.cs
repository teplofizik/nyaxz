using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace NyaXZ
{
    /// <summary>
    /// Managed XZ (.xz) decoder: parses the container and block filter chains and
    /// decodes stateful LZMA2 with delta/BCJ filters. Block checks are verified.
    /// </summary>
    public static class XzDecompressor
    {
        private const ulong Lzma2FilterId = 0x21;
        private const ulong DeltaFilterId = 0x03;
        private const int MaxDictionarySize = 256 * 1024 * 1024;

        public static byte[] Decompress(byte[] Data)
        {
            if (Data == null) throw new ArgumentNullException(nameof(Data));

            return DecompressXz(Data);
        }

        private static byte[] DecompressXz(byte[] Data)
        {
            if ((Data.Length < 24) ||
                (Data[0] != 0xFD) || (Data[1] != 0x37) || (Data[2] != 0x7A) ||
                (Data[3] != 0x58) || (Data[4] != 0x5A) || (Data[5] != 0x00))
                throw new InvalidDataException("Not an XZ stream");

            int CheckSize = GetCheckSize(Data[7]);
            var Result = new MemoryStream();
            int Pos = 12;

            while (Pos < Data.Length)
            {
                int BlockStart = Pos;
                byte SizeByte = Data[Pos++];

                if (SizeByte == 0x00)
                    break; // index indicator: no more blocks

                int HeaderSize = (SizeByte + 1) * 4;
                int HeaderEnd = BlockStart + HeaderSize;

                if ((HeaderSize < 8) || (HeaderEnd > Data.Length))
                    throw new InvalidDataException("Truncated XZ block header");

                int BodyEnd = HeaderEnd - 4;
                byte Flags = Data[Pos++];
                int FilterCount = (Flags & 0x03) + 1;

                if ((Flags & 0x3C) != 0)
                    throw new InvalidDataException("Unsupported XZ block flags");

                bool HasCompressedSize = (Flags & 0x40) != 0;
                bool HasUncompressedSize = (Flags & 0x80) != 0;
                ulong CompressedSize = 0;
                ulong UncompressedSize = 0;

                if (HasCompressedSize)
                    CompressedSize = Varint.Read(Data, ref Pos);

                if (HasUncompressedSize)
                    UncompressedSize = Varint.Read(Data, ref Pos);

                var Filters = new List<(ulong Id, byte[] Properties)>(FilterCount);
                for (int i = 0; i < FilterCount; i++)
                {
                    ulong Id = Varint.Read(Data, ref Pos);
                    ulong PropertySize = Varint.Read(Data, ref Pos);

                    if (PropertySize > Int32.MaxValue)
                        throw new InvalidDataException("XZ filter properties too large");

                    var Properties = new byte[(int)PropertySize];
                    Array.Copy(Data, Pos, Properties, 0, Properties.Length);
                    Pos += Properties.Length;
                    Filters.Add((Id, Properties));
                }

                if (Pos > BodyEnd)
                    throw new InvalidDataException("XZ block header overflow");

                for (int i = Pos; i < BodyEnd; i++)
                {
                    if (Data[i] != 0)
                        throw new InvalidDataException("XZ block header padding is not zero");
                }

                uint HeaderCrc = ReadUInt32LE(Data, BodyEnd);
                if (HeaderCrc != Crc32.Compute(Data, BlockStart, HeaderSize - 4))
                    throw new InvalidDataException("XZ block header CRC mismatch");

                if ((Filters.Count == 0) || (Filters[Filters.Count - 1].Id != Lzma2FilterId))
                    throw new InvalidDataException("XZ block has no trailing LZMA2 filter");

                Pos = HeaderEnd;
                int DataStart = Pos;

                int DictionarySize = GetLzma2DictionarySize(Filters[Filters.Count - 1].Properties);
                byte[] Block;
                int Consumed;

                using (var Input = new MemoryStream(Data, DataStart, Data.Length - DataStart, false))
                using (var BlockOut = new MemoryStream())
                {
                    new Lzma2Decoder(DictionarySize).Decode(Input, BlockOut);
                    Consumed = (int)Input.Position;
                    Block = BlockOut.ToArray();
                }

                for (int i = Filters.Count - 2; i >= 0; i--)
                    ApplyFilter(Filters[i].Id, Filters[i].Properties, Block);

                if (HasCompressedSize && ((ulong)Consumed != CompressedSize))
                    throw new InvalidDataException("XZ block compressed size mismatch");

                if (HasUncompressedSize && ((ulong)Block.Length != UncompressedSize))
                    throw new InvalidDataException("XZ block uncompressed size mismatch");

                int DataEnd = DataStart + Consumed;
                int Padding = (DataEnd - BlockStart) % 4;

                if (Padding != 0)
                {
                    int Skip = 4 - Padding;
                    for (int i = 0; i < Skip; i++, DataEnd++)
                    {
                        if (Data[DataEnd] != 0)
                            throw new InvalidDataException("XZ block padding is not zero");
                    }
                }

                if (DataEnd + CheckSize > Data.Length)
                    throw new InvalidDataException("Truncated XZ block check");

                VerifyCheck(Data, DataEnd, CheckSize, Block);
                Pos = DataEnd + CheckSize;

                Result.Write(Block, 0, Block.Length);
            }

            return Result.ToArray();
        }

        private static int GetLzma2DictionarySize(byte[] Properties)
        {
            if (Properties.Length != 1)
                throw new InvalidDataException("Invalid LZMA2 filter properties");

            byte Code = Properties[0];
            if ((Code & 0xC0) != 0)
                throw new InvalidDataException("Reserved bits set in LZMA2 properties");

            Code &= 0x3F;
            if (Code > 40)
                throw new InvalidDataException("Invalid LZMA2 dictionary code");

            long Size = (long)(2 | (Code & 1)) << ((Code >> 1) + 11);
            if ((Size <= 0) || (Size > MaxDictionarySize))
                throw new InvalidDataException($"LZMA2 dictionary too large: {Size}");

            return (int)Size;
        }

        private static void ApplyFilter(ulong Id, byte[] Properties, byte[] Block)
        {
            if (Id == DeltaFilterId)
            {
                if (Properties.Length != 1)
                    throw new InvalidDataException("Invalid delta filter properties");
                XzFilters.Delta(Block, Properties[0] + 1);
                return;
            }

            if ((Id >= 0x04) && (Id <= 0x09))
            {
                XzFilters.Apply(Id, Properties, Block);
                return;
            }

            throw new InvalidDataException($"Unsupported XZ filter 0x{Id:X2}");
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

        private static void VerifyCheck(byte[] Data, int Offset, int CheckSize, byte[] Block)
        {
            switch (CheckSize)
            {
                case 0:
                    return;

                case 4:
                    if (ReadUInt32LE(Data, Offset) != Crc32.Compute(Block))
                        throw new InvalidDataException("XZ block CRC32 mismatch");
                    return;

                case 8:
                    if (ReadUInt64LE(Data, Offset) != Crc64.Compute(Block))
                        throw new InvalidDataException("XZ block CRC64 mismatch");
                    return;

                case 32:
                    using (var Sha = SHA256.Create())
                    {
                        var Expected = Sha.ComputeHash(Block);
                        for (int i = 0; i < Expected.Length; i++)
                        {
                            if (Data[Offset + i] != Expected[i])
                                throw new InvalidDataException("XZ block SHA-256 mismatch");
                        }
                    }
                    return;

                default:
                    throw new InvalidDataException("Unsupported XZ check size");
            }
        }

        private static uint ReadUInt32LE(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8) | (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));

        private static ulong ReadUInt64LE(byte[] Data, int Offset) =>
            (ulong)ReadUInt32LE(Data, Offset) | ((ulong)ReadUInt32LE(Data, Offset + 4) << 32);
    }
}
