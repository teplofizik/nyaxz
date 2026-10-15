using System;
using System.Collections.Generic;

namespace NyaXZ
{
    /// <summary>
    /// XZ variable-length integer: little-endian base-128, high bit = continuation.
    /// </summary>
    internal static class Varint
    {
        public static void Write(List<byte> Dst, ulong Value)
        {
            do
            {
                byte B = (byte)(Value & 0x7F);
                Value >>= 7;
                if (Value != 0) B |= 0x80;
                Dst.Add(B);
            }
            while (Value != 0);
        }

        public static ulong Read(byte[] Src, ref int Pos)
        {
            ulong Res = 0;
            int Shift = 0;

            while (true)
            {
                if (Pos >= Src.Length)
                    throw new InvalidOperationException("Truncated XZ varint");

                byte B = Src[Pos++];
                Res |= (ulong)(B & 0x7F) << Shift;

                if ((B & 0x80) == 0) break;

                Shift += 7;
                if (Shift > 63)
                    throw new InvalidOperationException("XZ varint too long");
            }

            return Res;
        }
    }
}
