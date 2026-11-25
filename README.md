# NyaXZ

Pure-managed .NET implementation of the XZ (`.xz`) format with **no native and no
third-party compression dependencies**. It uses the managed LZMA1 encoder from
the [LZMA-SDK](https://www.nuget.org/packages/LZMA-SDK) package for writing and a
self-contained managed LZMA2/range-coder decoder for reading.

- `NyaXZ.XzCompressor.Compress(byte[] data)` — `byte[]` (`.xz`)
- `NyaXZ.XzCompressor.Compress(byte[] data, int dictionarySize)`
- `NyaXZ.XzDecompressor.Decompress(byte[] data)` — `byte[]`

## Install

```
dotnet add package NyaXZ
```

## Usage

```csharp
using System.IO;
using NyaXZ;

byte[] data = File.ReadAllBytes("input.bin");

byte[] compressed = XzCompressor.Compress(data);
File.WriteAllBytes("output.xz", compressed);

byte[] restored = XzDecompressor.Decompress(compressed);
```

## Limited support — please read

NyaXZ is a **small** XZ implementation meant for embedded-image tooling, not a
drop-in replacement for liblzma.

Supported:

- Single-stream `.xz` files with one or more blocks.
- Filters: **LZMA2** (`0x21`), **Delta** (`0x03`) and **BCJ** (x86, PowerPC,
  IA64, ARM, ARM-Thumb, SPARC).
- Check types: `None`, `CRC32`, `CRC64`, `SHA-256`; block checks are verified.
- Multi-chunk LZMA2 streams (compressed and uncompressed chunks, with
  state/property/dictionary resets or continuation).

Limitations:

- **The write path always uses the LZMA2 filter, a single block and a CRC32 check.**
  Every chunk is a *full-reset* chunk. Chunks are sized adaptively (up to the
  LZMA2 packed-size limit of 64 KiB, unpacked up to 2 MiB), so there are fewer
  model resets than a fixed 64 KiB split. The result is valid and interoperates
  with liblzma and 7-Zip, but the compression ratio is still lower than native
  `xz` (no cross-chunk LZMA model, no BCJ executable filters).
- Concatenated `.xz` streams: only the first stream is decoded.
- LZMA2 dictionary is limited to 256 MiB (sufficient for firmware images).
- Not intended for large archives or maximum-ratio compression.

Output is validated against liblzma (Python `lzma`) and 7-Zip in both directions.

## License

MIT
