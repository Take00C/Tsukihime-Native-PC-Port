using System.Security.Cryptography;
using Tsuki.Data.Containers;

namespace Tsuki.Data.Images;

/// <summary>
/// SHA-1 of the displayed picture (<c>DisplayWidth x DisplayHeight</c> straight-alpha RGBA8, row-major, flag-0 tiles
/// transparent), the value the Python oracle records for every picture (<c>hashlib.sha1(decode_image(...).rgba)</c>).
/// The bitmap is never built: tiles are decoded one tile row at a time, copied into a band of rows and hashed, so a
/// 343 MB picture needs about 25 MB. One hasher per thread; it keeps its buffers between pictures.
/// </summary>
public sealed class PictureHasher : IDisposable
{
    private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private readonly TileScratch _scratch = new();
    private byte[] _tile = [];
    private byte[] _band = [];

    /// <summary>Number of tiles decoded since creation.</summary>
    public long TilesDecoded { get; private set; }

    /// <summary>Bytes of displayed RGBA hashed since creation.</summary>
    public long BytesHashed { get; private set; }

    /// <summary>The SHA-1 of the displayed picture of <paramref name="image"/> as 20 bytes.</summary>
    /// <exception cref="ImageFormatException">A tile is malformed.</exception>
    public byte[] Hash(ImageMzp image, SimdLevel simd = SimdLevel.Auto)
    {
        ArgumentNullException.ThrowIfNull(image);
        var h = image.Header;
        int outW = h.DisplayWidth;
        int outH = h.DisplayHeight;
        int iw = h.InnerWidth;
        int ih = h.InnerHeight;
        int b = h.Border;
        long rowBytes = 4L * outW;
        long bandBytes = rowBytes * Math.Min(ih, outH);
        if (bandBytes > Array.MaxLength)
        {
            throw new ImageFormatException($"a band of {ih} rows of {outW} pixels does not fit one array");
        }

        if (_band.Length < bandBytes)
        {
            _band = GC.AllocateUninitializedArray<byte>((int)bandBytes);
        }

        if (_tile.Length < h.TileRgbaBytes)
        {
            _tile = GC.AllocateUninitializedArray<byte>(h.TileRgbaBytes);
        }

        // Every displayed pixel is covered by a tile unless the header is odd (a border with a width below 8): then clear the band.
        int bands = (outH + ih - 1) / ih;
        bool coverageGaps = (long)h.TilesX * iw < outW || bands > h.TilesY;
        for (int by = 0; by < bands; by++)
        {
            int y0 = by * ih;
            int rows = Math.Min(ih, outH - y0);
            var band = _band.AsSpan(0, checked((int)(rowBytes * rows)));
            if (coverageGaps)
            {
                band.Clear();
            }

            if (by < h.TilesY)
            {
                for (int tx = 0; tx < h.TilesX; tx++)
                {
                    int x0 = tx * iw;
                    if (x0 >= outW)
                    {
                        break;
                    }

                    int cw = Math.Min(iw, outW - x0);
                    int t = (by * h.TilesX) + tx;
                    if (!h.IsLive(t))
                    {
                        if (!coverageGaps)
                        {
                            for (int y = 0; y < rows; y++)
                            {
                                band.Slice((int)((y * rowBytes) + (4L * x0)), 4 * cw).Clear();
                            }
                        }

                        continue;
                    }

                    image.DecodeTile(t, _scratch, _tile, simd);
                    TilesDecoded++;
                    for (int y = 0; y < rows; y++)
                    {
                        _tile.AsSpan(4 * (((y + b) * h.TileWidth) + b), 4 * cw)
                            .CopyTo(band.Slice((int)((y * rowBytes) + (4L * x0)), 4 * cw));
                    }
                }
            }

            _sha1.AppendData(band);
            BytesHashed += band.Length;
        }

        return _sha1.GetHashAndReset();
    }

    /// <summary>The SHA-1 as 40 lower-case hex digits.</summary>
    public string HashHex(ImageMzp image, SimdLevel simd = SimdLevel.Auto) => Convert.ToHexStringLower(Hash(image, simd));

    /// <summary>The SHA-1 of the displayed picture of the image container <paramref name="container"/>.</summary>
    public string HashHex(MzpContainer container, SimdLevel simd = SimdLevel.Auto) => HashHex(ImageMzp.Open(container), simd);

    /// <inheritdoc />
    public void Dispose() => _sha1.Dispose();
}
