# NyaXZ

Pure-managed .NET implementation of the XZ (`.xz`) format. It reuses the managed
LZMA1 encoder/decoder from the [LZMA-SDK](https://www.nuget.org/packages/LZMA-SDK)
package and adds the LZMA2 chunk layer and the XZ container on top — **no native
dependencies**.

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

- Single-stream, single-block `.xz` files.
- The **LZMA2 filter only** (filter id `0x21`). No BCJ / delta / other filters.
- Reading check types: `None`, `CRC32`, `CRC64`, `SHA-256` (checks are parsed to
  locate the index; they are not verified).
- Reading multi-chunk LZMA2 streams (compressed and uncompressed chunks, with
  state/property/dictionary resets or continuation).

Limitations:

- **The write path always uses the LZMA2 filter, a single block and a CRC32 check.**
  Every chunk is a *full-reset* chunk. The result is valid and interoperates with
  liblzma and 7-Zip, but the compression ratio is lower than native `xz`
  (no cross-chunk LZMA model, no BCJ executable filters).
- No BCJ executable filters, no multi-block / multi-stream, no index/padding tuning.
- Not intended for large archives or maximum-ratio compression.

Output is validated against liblzma (Python `lzma`) and 7-Zip in both directions.

## License

MIT
