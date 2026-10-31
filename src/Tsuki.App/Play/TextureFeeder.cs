// Resolves every TextureRef a display list names into a GL texture (ARCHITECTURE 4.3, 6.3; package K6): picture tiles, UI sheet tiles (and the whole-sheet
// quads of the message window, D = -1), glyph pages, parts.mrg pictures, save thumbnails and the current frame of each movie slot. Tiles are decoded on the
// decode workers and uploaded in byte-capped slices by the upload scheduler; a frame that needs a texture which is not resident yet waits for it (up to a
// budget), so a scene change never shows a quad missing for a few frames. Captures are the compositor's own.
using System.Diagnostics;
using Tsuki.Data;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using Tsuki.Media.Movie;
using Tsuki.Render.Textures;
using Tsuki.Ui;

namespace Tsuki.App.Play;

/// <summary>Counters of a <see cref="TextureFeeder"/>.</summary>
/// <param name="Requested">Tiles queued for decoding.</param>
/// <param name="Waits">Frames that waited for a texture.</param>
/// <param name="WaitMilliseconds">Total time spent waiting.</param>
/// <param name="TimedOut">Frames whose wait ran out of budget.</param>
/// <param name="Failures">Decode jobs that threw.</param>
/// <param name="MovieUploads">Movie frames uploaded.</param>
/// <param name="Prefetched">Tiles queued by the look-ahead before a frame named them.</param>
public readonly record struct FeederStats(long Requested, long Waits, double WaitMilliseconds, long TimedOut, long Failures, long MovieUploads, long Prefetched = 0);

/// <summary>Loads and uploads the textures of display lists.</summary>
public sealed class TextureFeeder : IDisposable
{
    [ThreadStatic]
    private static TileScratch? t_scratch;

    private readonly GameFolderSource _data;
    private readonly TextureCache _cache;
    private readonly StagingPool _pool = new();
    private readonly UploadScheduler _scheduler;
    private readonly DecodeWorkers _workers;
    private readonly HashSet<TextureRef> _queued = [];
    private readonly List<TextureRef> _wanted = [];
    private long _prefetched;

    /// <summary>The keys the last <see cref="EnsureResident"/> had to queue itself (not seen by the look-ahead): the hitch log names their pictures.</summary>
    public List<TextureRef> LastFresh { get; } = [];
    private readonly Dictionary<int, (long Index, double Pts, int Movie)> _movieShown = [];
    private byte[] _rgba = [];
    private long _requested;
    private long _waits;
    private double _waitMs;
    private long _timedOut;
    private long _movieUploads;
    private Picture? _partsPicture;
    private int _partsIndex = -1;

    /// <summary>Creates the feeder over a game source and a cache.</summary>
    public TextureFeeder(GameFolderSource data, TextureCache cache, int workers = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(cache);
        _data = data;
        _cache = cache;
        _scheduler = new UploadScheduler(cache, _pool);
        _workers = new DecodeWorkers(_scheduler, workers);
    }

    /// <summary>The save thumbnails (slot to pixels), set by the services.</summary>
    public Func<int, UiThumbnail?>? Thumbnails { get; set; }

    /// <summary>The frame to show for a movie slot, set by the movie host.</summary>
    public Func<int, VideoFrame?>? MovieFrames { get; set; }

    /// <summary>The pictures the menus compose at run time from the player's sheets (package K33), by texture key; set by the host from the UI's assets.</summary>
    public Func<TextureRef, UiComposedPicture?>? Composed { get; set; }

    /// <summary>The staging pool (for the memory log).</summary>
    public StagingPool Pool => _pool;

    /// <summary>The sizes of the feeder's own sets, for the memory log (package K11).</summary>
    public string DescribeQueues() => $"queued set {_queued.Count}, wanted {_wanted.Count}, movie slots {_movieShown.Count}";

    /// <summary>The first decode failure, or null.</summary>
    public Exception? Fault => _workers.Fault;

    /// <summary>The counters.</summary>
    public FeederStats Stats => new(_requested, _waits, _waitMs, _timedOut, _workers.Fault is null ? 0 : 1, _movieUploads, _prefetched);

    /// <summary>
    /// Names textures that the next frames are expected to need (the host's look-ahead): their tiles are decoded by the workers behind every tile a frame is
    /// waiting for, so a scene change that names them finds them resident. Keys that are resident or already queued are skipped.
    /// </summary>
    public void Prefetch(IReadOnlyList<TextureRef> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        for (int i = 0; i < keys.Count; i++)
        {
            TextureRef key = keys[i];
            if (key.Kind == TextureKind.PictureTile && key.A != UiPictureArchive.Parts && !_cache.Contains(key) && _queued.Add(key))
            {
                _requested++;
                _prefetched++;
                _workers.EnqueueBackground(PictureJob(key));
            }
        }
    }

    /// <summary>Tiles queued or being decoded.</summary>
    public int Pending => _workers.Pending + _scheduler.Pending;

    /// <summary>
    /// Makes every texture the list names resident: movie frames and thumbnails are uploaded at once, tiles are decoded and uploaded, waiting at most
    /// <paramref name="budget"/> for them. Returns the textures still missing.
    /// </summary>
    public int EnsureResident(DisplayList list, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(list);
        _scheduler.UploadSlice();
        _wanted.Clear();
        LastFresh.Clear();
        int fresh = 0;
        foreach (ref readonly DrawCmd c in list.Commands)
        {
            if (c.Kind != DrawKind.TexturedQuad)
            {
                continue;
            }

            TextureRef key = c.Texture;
            switch (key.Kind)
            {
                case TextureKind.Capture:
                case TextureKind.None:
                    break;
                case TextureKind.MovieFrame:
                    UploadMovieFrame(key);
                    break;
                case TextureKind.Thumbnail:
                    UploadThumbnail(key);
                    break;
                case TextureKind.UiComposed:
                    UploadComposed(key);
                    break;
                default:
                    if (_cache.Touch(key))
                    {
                        break;                                  // resident: now the newest, so the uploads of this frame cannot evict it (package K15)
                    }

                    if (!_cache.Contains(key))
                    {
                        // a tile the look-ahead queued is already on its way; if it still waits among the background jobs it moves to the urgent queue
                        _wanted.Add(key);
                        if (_queued.Add(key))
                        {
                            Enqueue(key);
                            LastFresh.Add(key);
                            fresh++;
                        }
                        else
                        {
                            _workers.Promote(key);              // queued by the look-ahead: now a frame waits for it
                        }
                    }

                    break;
            }
        }

        if (_wanted.Count == 0)
        {
            UploadSliceAndPrune();
            return 0;
        }

        var sw = Stopwatch.StartNew();
        int missing = _wanted.Count;
        while (missing > 0 && sw.Elapsed < budget)
        {
            _scheduler.UploadSlice();
            missing = CountMissing();
            if (missing > 0)
            {
                if (_workers.Fault is not null && _workers.Pending == 0 && _scheduler.Pending == 0)
                {
                    break;                                 // the rest failed to decode: do not wait for them
                }

                Thread.Sleep(0);
            }
        }

        UploadSliceAndPrune();
        missing = CountMissing();
        if (fresh > 0 || missing > 0 || sw.Elapsed.TotalMilliseconds >= 1)
        {
            _waits++;
            _waitMs += sw.Elapsed.TotalMilliseconds;
        }

        if (missing > 0 && sw.Elapsed >= budget)
        {
            _timedOut++;
        }

        return missing;
    }

    private int CountMissing()
    {
        int missing = 0;
        foreach (TextureRef k in _wanted)
        {
            if (!_cache.Contains(k))
            {
                missing++;
                _workers.Promote(k);                        // a tile of the look-ahead that a frame now needs jumps the queues
            }
        }

        return missing;
    }

    private void UploadSliceAndPrune()
    {
        _scheduler.UploadSlice();
        _queued.RemoveWhere(k => _cache.Contains(k));
    }

    private void Enqueue(in TextureRef key)
    {
        _requested++;
        switch (key.Kind)
        {
            case TextureKind.PictureTile when key.A == UiPictureArchive.Parts:
                _workers.Enqueue(PartsJob(key));
                break;
            case TextureKind.PictureTile:
                _workers.Enqueue(PictureJob(key));
                break;
            case TextureKind.UiTile when key.D < 0:
                _workers.Enqueue(WholeSheetJob(key));
                break;
            case TextureKind.UiTile:
                _workers.Enqueue(SheetTileJob(key));
                break;
            case TextureKind.GlyphPage:
                _workers.Enqueue(GlyphJob(key));
                break;
            default:
                throw new InvalidOperationException($"no texture source for {key}");
        }
    }

    private DecodeJob PictureJob(TextureRef key)
    {
        ImageMzp image = _data.OpenImage(key.A, key.B);
        ImageHeader h = image.Header;

        // a key with D != 0 is a STEC / SNX grade key (GradeKeys): the colour kernel runs on the decode worker through TextureCache.Grader (package K7)
        return new DecodeJob(key, h.TileWidth, h.TileHeight, rgba => image.DecodeTile(key.C, Scratch(), rgba));
    }

    private DecodeJob SheetTileJob(TextureRef key)
    {
        ImageMzp image = _data.OpenUiSheetImage((UiSet)key.A, (Language)key.B, key.C);
        ImageHeader h = image.Header;
        return new DecodeJob(key, h.TileWidth, h.TileHeight, rgba => image.DecodeTile(key.D, Scratch(), rgba));
    }

    private DecodeJob WholeSheetJob(TextureRef key)
    {
        ImageMzp image = _data.OpenUiSheetImage((UiSet)key.A, (Language)key.B, key.C);
        ImageHeader h = image.Header;
        return new DecodeJob(key, h.DisplayWidth, h.DisplayHeight, rgba => image.DecodeDisplayed().CopyTo(rgba));
    }

    private DecodeJob GlyphJob(TextureRef key)
    {
        FontPage page = _data.FontPage((Language)key.A, key.B);
        return new DecodeJob(key, page.Width, page.Height, rgba => page.Rgba.Span.CopyTo(rgba));
    }

    private DecodeJob PartsJob(TextureRef key)
    {
        Picture picture;
        lock (this)
        {
            if (_partsIndex != key.B || _partsPicture is null)
            {
                _partsPicture = _data.Parts(key.B) ?? throw new InvalidOperationException($"parts.mrg has no entry {key.B}");
                _partsIndex = key.B;
            }

            picture = _partsPicture;
        }

        PictureTile tile = picture.Tiles.First(t => t.Index == key.C);
        return new DecodeJob(key, tile.StoredWidth, tile.StoredHeight, rgba => tile.Rgba.Span.CopyTo(rgba));
    }

    private static TileScratch Scratch() => t_scratch ??= new TileScratch();

    private void UploadThumbnail(in TextureRef key)
    {
        if (_cache.Contains(key) || Thumbnails?.Invoke(key.A) is not { } thumb)
        {
            return;
        }

        _cache.Upload(key, thumb.Width, thumb.Height, thumb.Rgba.Span);
    }

    private void UploadComposed(in TextureRef key)
    {
        if (_cache.Contains(key) || Composed?.Invoke(key) is not { } pic)
        {
            return;
        }

        _cache.Upload(key, pic.Width, pic.Height, pic.Rgba.Span);
    }

    private void UploadMovieFrame(in TextureRef key)
    {
        VideoFrame? frame = MovieFrames?.Invoke(key.A);
        if (frame is null)
        {
            return;
        }

        (long Index, double Pts, int Movie) shown = (frame.Index, frame.Pts, 0);
        if (_movieShown.TryGetValue(key.A, out var last) && last == shown && _cache.Contains(key))
        {
            return;
        }

        int w = frame.Width, h = frame.Height;
        if (_rgba.Length < w * h * 4)
        {
            _rgba = new byte[w * h * 4];
        }

        YuvConverter.ToRgba(frame, _rgba);
        _cache.Upload(key, w, h, _rgba);
        _movieShown[key.A] = shown;
        _movieUploads++;
    }

    /// <inheritdoc/>
    public void Dispose() => _workers.Dispose();
}

/// <summary>yuv420p to RGBA8 (BT.709 limited range, the way the movies are tagged; ARCHITECTURE 7.5).</summary>
public static class YuvConverter
{
    /// <summary>Converts <paramref name="frame"/> into <paramref name="rgba"/> (<c>Width * Height * 4</c> bytes, alpha 255).</summary>
    public static void ToRgba(VideoFrame frame, byte[] rgba)
    {
        int w = frame.Width;
        int h = frame.Height;
        byte[] yp = frame.Y, up = frame.U, vp = frame.V;
        Parallel.For(0, h / 2, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, row =>
        {
            for (int dy = 0; dy < 2; dy++)
            {
                int y = (row * 2) + dy;
                int yo = y * w;
                int co = row * (w / 2);
                int o = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int c = yp[yo + x] - 16;
                    int d = up[co + (x >> 1)] - 128;
                    int e = vp[co + (x >> 1)] - 128;
                    int c298 = 298 * c;
                    rgba[o] = Clamp((c298 + (459 * e) + 128) >> 8);
                    rgba[o + 1] = Clamp((c298 - (55 * d) - (136 * e) + 128) >> 8);
                    rgba[o + 2] = Clamp((c298 + (541 * d) + 128) >> 8);
                    rgba[o + 3] = 255;
                    o += 4;
                }
            }
        });
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
