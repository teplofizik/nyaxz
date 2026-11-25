#nullable disable

// BCJ (branch/call/jump) and delta filters adapted from SharpCompress (MIT)
// and the xz-embedded/xz reference algorithms. Applied to a decoded block.

using System;

namespace NyaXZ
{
    internal static class XzFilters
    {
        public static void Delta(byte[] Data, int Distance)
        {
            if (Distance <= 0)
                throw new LzmaInvalidParamException("Invalid delta distance");

            var History = new byte[Distance];
            int Pos = 0;

            for (int i = 0; i < Data.Length; i++)
            {
                byte Value = (byte)(Data[i] + History[Pos]);
                Data[i] = Value;
                History[Pos] = Value;
                if (++Pos == Distance)
                    Pos = 0;
            }
        }

        public static void Apply(ulong Id, byte[] Properties, byte[] Data)
        {
            uint Ip = 0;
            if (Properties.Length == 4)
                Ip = ReadUInt32LE(Properties, 0);
            else if (Properties.Length != 0)
                throw new LzmaDataException("Invalid BCJ filter properties");

            switch (Id)
            {
                case 0x04: X86(Data, Ip); break;
                case 0x05: PowerPC(Data); break;
                case 0x06: IA64(Data, Ip); break;
                case 0x07: Arm(Data); break;
                case 0x08: ArmThumb(Data, Ip); break;
                case 0x09: Sparc(Data); break;
                default: throw new LzmaDataException($"Unsupported XZ filter 0x{Id:X2}");
            }
        }

        private static bool X86TestByte(byte B) => (B == 0x00) || (B == 0xFF);

        private static void X86(byte[] Data, uint Ip)
        {
            var MaskToAllowedStatus = new[] { true, true, true, false, true, false, false, false };
            var MaskToBitNum = new byte[] { 0, 1, 2, 2, 3, 3, 3, 3 };

            int PrevPos = -1;
            uint PrevMask = 0;
            uint Pos = Ip;
            int Size = Data.Length;

            if (Size <= 4)
                return;

            Size -= 4;

            for (int i = 0; i < Size; i++)
            {
                if ((Data[i] & 0xFE) != 0xE8)
                    continue;

                PrevPos = i - PrevPos;
                if (PrevPos > 3)
                {
                    PrevMask = 0;
                }
                else
                {
                    PrevMask = (PrevMask << (PrevPos - 1)) & 7;
                    if (PrevMask != 0)
                    {
                        byte B = Data[i + 4 - MaskToBitNum[PrevMask]];
                        if (!MaskToAllowedStatus[PrevMask] || X86TestByte(B))
                        {
                            PrevPos = i;
                            PrevMask = (PrevMask << 1) | 1;
                            continue;
                        }
                    }
                }

                PrevPos = i;

                if (X86TestByte(Data[i + 4]))
                {
                    uint Src = ((uint)Data[i + 4] << 24) | ((uint)Data[i + 3] << 16) | ((uint)Data[i + 2] << 8) | Data[i + 1];
                    uint Dest;

                    while (true)
                    {
                        Dest = Src - (Pos + (uint)i + 5);
                        if (PrevMask == 0)
                            break;

                        uint J = MaskToBitNum[PrevMask] * 8u;
                        byte B = (byte)(Dest >> (24 - (int)J));
                        if (!X86TestByte(B))
                            break;

                        Src = Dest ^ ((1u << (32 - (int)J)) - 1u);
                    }

                    Dest &= 0x01FFFFFF;
                    Dest |= 0u - (Dest & 0x01000000);
                    Data[i + 1] = (byte)Dest;
                    Data[i + 2] = (byte)(Dest >> 8);
                    Data[i + 3] = (byte)(Dest >> 16);
                    Data[i + 4] = (byte)(Dest >> 24);
                    i += 4;
                }
                else
                {
                    PrevMask = (PrevMask << 1) | 1;
                }
            }
        }

        private static void PowerPC(byte[] Data)
        {
            long Size = Data.Length & ~3L;

            for (long i = 4; i <= Size; i += 4)
            {
                if (((Data[i - 4] & 0xFC) == 0x48) && ((Data[i - 1] & 3) == 1))
                {
                    uint Inst = ReadUInt32BE(Data, (int)i - 4);
                    Inst -= (uint)i;
                    Inst &= 0x03FFFFFF;
                    Inst |= 0x48000000;
                    WriteUInt32BE(Data, (int)i - 4, Inst);
                }
            }
        }

        private static void Arm(byte[] Data)
        {
            long Size = Data.Length & ~3L;

            for (long i = 4; i <= Size; i += 4)
            {
                if (Data[i - 1] == 0xEB)
                {
                    uint Inst = ReadUInt32LE(Data, (int)i - 4);
                    Inst <<= 2;
                    Inst -= (uint)(i + 4);
                    Inst >>= 2;
                    Inst &= 0x00FFFFFF;
                    Inst |= 0xEB000000;
                    WriteUInt32LE(Data, (int)i - 4, Inst);
                }
            }
        }

        private static void ArmThumb(byte[] Data, uint Ip)
        {
            long Size = Data.Length & ~1L;
            long Lim = Size - 4;

            for (long i = 0; i <= Lim; )
            {
                uint B1;
                uint B3;
                do
                {
                    if (i > Lim)
                        return;

                    B1 = Data[i + 1];
                    B3 = Data[i + 3];
                    i += 2;
                    B1 ^= 8;
                } while ((B3 & B1) < 0xF8);

                uint Inst = (B1 << 19) + (((uint)Data[i + 1] & 0x7) << 8) + (((uint)Data[i - 2]) << 11) + Data[i];
                i += 2;

                uint Cur = ((uint)(Ip + i)) >> 1;
                Inst -= Cur;

                Data[i - 4] = (byte)(Inst >> 11);
                Data[i - 3] = (byte)(0xF0 | ((Inst >> 19) & 0x7));
                Data[i - 2] = (byte)Inst;
                Data[i - 1] = (byte)(0xF8 | (Inst >> 8));
            }
        }

        private static void IA64(byte[] Data, uint Ip)
        {
            long Size = Data.Length;
            if (Size < 16)
                return;

            Size -= 16;

            long i = 0;
            do
            {
                uint M = ((uint)0x334B0000 >> (Data[i] & 0x1E)) & 3;
                if (M != 0)
                {
                    M++;
                    do
                    {
                        long Iterator = i + (M * 5) - 8;
                        if ((((Data[Iterator + 3] >> (int)M) & 15) == 5) &&
                            ((((Data[Iterator - 1] | ((uint)Data[Iterator] << 8)) >> (int)M) & 0x70) == 0))
                        {
                            uint Raw = ReadUInt32LE(Data, (int)Iterator);
                            uint Inst = Raw >> (int)M;
                            Inst = (Inst & 0xFFFFF) | ((Inst & (1 << 23)) >> 3);
                            Inst <<= 4;
                            Inst -= Ip + (uint)i;
                            Inst >>= 4;
                            Inst &= 0x1FFFFF;
                            Inst += 0x700000;
                            Inst &= 0x8FFFFF;
                            Raw &= ~((uint)0x8FFFFF << (int)M);
                            Raw |= Inst << (int)M;
                            WriteUInt32LE(Data, (int)Iterator, Raw);
                        }
                    } while (++M <= 4);
                }
                i += 16;
            } while (i <= Size);
        }

        private static void Sparc(byte[] Data)
        {
            long Size = Data.Length & ~3L;

            for (long i = 4; i <= Size; i += 4)
            {
                if (((Data[i - 4] == 0x40) && ((Data[i - 3] & 0xC0) == 0)) ||
                    ((Data[i - 4] == 0x7F) && (Data[i - 3] >= 0xC0)))
                {
                    uint Inst = ReadUInt32BE(Data, (int)i - 4);
                    Inst <<= 2;
                    Inst -= (uint)i;
                    Inst &= 0x01FFFFFF;
                    Inst -= (uint)1 << 24;
                    Inst ^= 0xFF000000;
                    Inst >>= 2;
                    Inst |= 0x40000000;
                    WriteUInt32BE(Data, (int)i - 4, Inst);
                }
            }
        }

        private static uint ReadUInt32LE(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8) | (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));

        private static uint ReadUInt32BE(byte[] Data, int Offset) =>
            (uint)((Data[Offset] << 24) | (Data[Offset + 1] << 16) | (Data[Offset + 2] << 8) | Data[Offset + 3]);

        private static void WriteUInt32LE(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }

        private static void WriteUInt32BE(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)(Value >> 24);
            Data[Offset + 1] = (byte)(Value >> 16);
            Data[Offset + 2] = (byte)(Value >> 8);
            Data[Offset + 3] = (byte)Value;
        }
    }
}
