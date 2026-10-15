using System;
using System.Collections.Generic;

namespace NyaXZ
{
    /// <summary>
    /// XZ container (single stream, single block, CRC32 check).
    /// </summary>
    internal static class XzContainer
    {
        public const byte CheckCrc32 = 0x01;

        public const int CheckSize = 4;
        public const int BlockHeaderSize = 12;

        // liblzma stores the Check ID in the low nibble of the second flag byte.
        private static readonly byte[] StreamFlags = { 0x00, CheckCrc32 };

        public static byte[] StreamHeader()
        {
            var Res = new byte[12];

            Res[0] = 0xFD;
            Res[1] = 0x37;
            Res[2] = 0x7A;
            Res[3] = 0x58;
            Res[4] = 0x5A;
            Res[5] = 0x00;

            Res[6] = StreamFlags[0];
            Res[7] = StreamFlags[1];

            WriteU32LE(Res, 8, Crc32.Compute(StreamFlags));

            return Res;
        }

        public static byte[] BlockHeader(byte DictionaryCode)
        {
            // [sizeByte][flags][filterId][propsSize][props][padding..][crc32]
            var Res = new byte[BlockHeaderSize];

            Res[0] = (byte)(BlockHeaderSize / 4 - 1);
            Res[1] = 0x00; // block flags: one filter, no sizes
            Res[2] = 0x21; // LZMA2 filter id
            Res[3] = 0x01; // filter properties size
            Res[4] = DictionaryCode;

            WriteU32LE(Res, 8, Crc32.Compute(Res, 0, 8));

            return Res;
        }

        public static byte[] Index(long UnpaddedSize, long UncompressedSize)
        {
            var Body = new List<byte>();

            Body.Add(0x00); // index indicator
            Varint.Write(Body, 1); // number of records
            Varint.Write(Body, (ulong)UnpaddedSize);
            Varint.Write(Body, (ulong)UncompressedSize);

            while ((Body.Count % 4) != 0)
                Body.Add(0x00);

            var Res = new byte[Body.Count + 4];
            Array.Copy(Body.ToArray(), Res, Body.Count);
            WriteU32LE(Res, Body.Count, Crc32.Compute(Res, 0, Body.Count));

            return Res;
        }

        public static byte[] StreamFooter(long IndexSize)
        {
            var Tail = new byte[6];
            WriteU32LE(Tail, 0, (uint)(IndexSize / 4 - 1));
            Tail[4] = StreamFlags[0];
            Tail[5] = StreamFlags[1];

            var Res = new byte[12];
            WriteU32LE(Res, 0, Crc32.Compute(Tail));
            Array.Copy(Tail, 0, Res, 4, 6);
            Res[10] = 0x59; // 'Y'
            Res[11] = 0x5A; // 'Z'

            return Res;
        }

        public static byte DictionaryCode(int DictionarySize)
        {
            for (int Code = 0; Code <= 40; Code++)
            {
                long Size = (long)(2 | (Code & 1)) << ((Code >> 1) + 11);
                if (Size == DictionarySize) return (byte)Code;
            }

            for (int Code = 0; Code <= 40; Code++)
            {
                long Size = (long)(2 | (Code & 1)) << ((Code >> 1) + 11);
                if (Size >= DictionarySize) return (byte)Code;
            }

            return 40;
        }

        public static void WriteU32LE(byte[] Dst, int Offset, uint Value)
        {
            Dst[Offset + 0] = (byte)(Value & 0xFF);
            Dst[Offset + 1] = (byte)((Value >> 8) & 0xFF);
            Dst[Offset + 2] = (byte)((Value >> 16) & 0xFF);
            Dst[Offset + 3] = (byte)((Value >> 24) & 0xFF);
        }
    }
}
