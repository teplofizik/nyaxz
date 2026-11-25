#nullable disable

namespace NyaXZ
{
    /// <summary>CRC-64/XZ (poly 0xC96C5795D7870F42, init/xorout all ones).</summary>
    internal static class Crc64
    {
        private const ulong Polynomial = 0xC96C5795D7870F42UL;
        private static readonly ulong[] Table = BuildTable();

        private static ulong[] BuildTable()
        {
            var Res = new ulong[256];
            for (ulong i = 0; i < 256; i++)
            {
                ulong C = i;
                for (int k = 0; k < 8; k++)
                    C = ((C & 1) != 0) ? (Polynomial ^ (C >> 1)) : (C >> 1);
                Res[i] = C;
            }
            return Res;
        }

        public static ulong Compute(byte[] Data)
        {
            ulong Crc = 0xFFFFFFFFFFFFFFFFUL;
            for (int i = 0; i < Data.Length; i++)
                Crc = Table[(Crc ^ Data[i]) & 0xFF] ^ (Crc >> 8);
            return Crc ^ 0xFFFFFFFFFFFFFFFFUL;
        }
    }
}
