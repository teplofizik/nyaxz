using System;

namespace NyaXZ
{
    /// <summary>
    /// CRC32 (IEEE 802.3, poly 0xEDB88320) as used by the XZ container.
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var Res = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                uint C = i;
                for (int k = 0; k < 8; k++)
                    C = ((C & 1) != 0) ? (0xEDB88320u ^ (C >> 1)) : (C >> 1);
                Res[i] = C;
            }

            return Res;
        }

        public static uint Compute(byte[] Data) => Compute(Data, 0, Data.Length);

        public static uint Compute(byte[] Data, int Offset, int Count)
        {
            uint Crc = 0xFFFFFFFF;

            for (int i = 0; i < Count; i++)
                Crc = Table[(Crc ^ Data[Offset + i]) & 0xFF] ^ (Crc >> 8);

            return Crc ^ 0xFFFFFFFF;
        }
    }
}
