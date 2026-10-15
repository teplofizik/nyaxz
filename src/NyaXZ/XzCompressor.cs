using System;

namespace NyaXZ
{
    /// <summary>
    /// Managed XZ (.xz) compressor. Produces a single-block XZ stream with a CRC32 check.
    /// </summary>
    public static class XzCompressor
    {
        public const int DefaultDictionarySize = 1 << 20; // 1 MiB

        public static byte[] Compress(byte[] Data) => Compress(Data, DefaultDictionarySize);

        public static byte[] Compress(byte[] Data, int DictionarySize)
        {
            if (Data == null) throw new ArgumentNullException(nameof(Data));

            var Lzma2Data = Lzma2.Encode(Data, DictionarySize);
            var Header = XzContainer.StreamHeader();
            var BlockHeader = XzContainer.BlockHeader(XzContainer.DictionaryCode(DictionarySize));

            int Pad = (4 - ((BlockHeader.Length + Lzma2Data.Length) % 4)) % 4;

            var Check = new byte[XzContainer.CheckSize];
            XzContainer.WriteU32LE(Check, 0, Crc32.Compute(Data));

            long Unpadded = BlockHeader.Length + Lzma2Data.Length + Check.Length;

            var Index = XzContainer.Index(Unpadded, Data.Length);
            var Footer = XzContainer.StreamFooter(Index.Length);

            int Total = Header.Length + BlockHeader.Length + Lzma2Data.Length + Pad + Check.Length + Index.Length + Footer.Length;
            var Res = new byte[Total];

            int P = 0;
            Array.Copy(Header, 0, Res, P, Header.Length); P += Header.Length;
            Array.Copy(BlockHeader, 0, Res, P, BlockHeader.Length); P += BlockHeader.Length;
            Array.Copy(Lzma2Data, 0, Res, P, Lzma2Data.Length); P += Lzma2Data.Length;
            P += Pad; // zero padding
            Array.Copy(Check, 0, Res, P, Check.Length); P += Check.Length;
            Array.Copy(Index, 0, Res, P, Index.Length); P += Index.Length;
            Array.Copy(Footer, 0, Res, P, Footer.Length);

            return Res;
        }
    }
}
