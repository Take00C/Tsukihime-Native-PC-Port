// GPU texture cache (ARCHITECTURE 4.3): one GL texture per picture tile, glyph page, UI tile or movie
// plane, keyed by TextureRef, LRU by bytes (default cap 3 GB). Evicted textures are kept for reuse by
// size, so a later upload of the same size recycles one with glTexSubImage2D instead of allocating
// (S4 upload rules 3 and 4). The cache also holds each texture's transition parameters, the state the
// PS4 keeps on the texture object (+0xF0..+0xFC, set by 0xD73B0; compositing 2.2, 3).
using Silk.NET.OpenGL;
using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;
using Tsuki.Render.Grade;

namespace Tsuki.Render.Textures;

/// <summary>Counters of a <see cref="TextureCache"/>.</summary>
/// <param name="Resident">Textures held.</param>
/// <param name="ResidentBytes">Their bytes (RGBA8).</param>
/// <param name="Allocations">glTexImage2D allocations so far.</param>
/// <param name="Recycles">Uploads into a recycled texture (glTexSubImage2D) so far.</param>
/// <param name="Evictions">Textures evicted so far.</param>
/// <param name="SpareBytes">Bytes of evicted textures kept for reuse.</param>
public readonly record struct TextureCacheStats(int Resident, long ResidentBytes, long Allocations, long Recycles, long Evictions, long SpareBytes);

/// <summary>
/// LRU texture cache. Not thread-safe: the main (GL) thread owns it; decode workers hand their tiles to the
/// <see cref="UploadScheduler"/>, which uploads here.
/// </summary>
public sealed unsafe class TextureCache : ITextureSource, IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<TextureRef, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _lru = new();                    // first = most recently used
    private readonly Dictionary<(int W, int H), Stack<uint>> _spare = [];
    private readonly Dictionary<TextureRef, TransitionParams> _transition = [];
    private long _allocations;
    private long _recycles;
    private long _evictions;
    private long _spareBytes;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="gl">Bindings of the current context.</param>
    /// <param name="capacityBytes">LRU cap in bytes (default 3 GB, ARCHITECTURE 4.3).</param>
    /// <param name="spareCapacityBytes">Bytes of evicted textures kept for recycling (default 256 MiB).</param>
    public TextureCache(GL gl, long capacityBytes = 3L << 30, long spareCapacityBytes = 256L << 20)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1);
        _gl = gl;
        CapacityBytes = capacityBytes;
        SpareCapacityBytes = spareCapacityBytes;
    }

    /// <summary>The LRU cap in bytes.</summary>
    public long CapacityBytes { get; }

    /// <summary>
    /// The colour-grade seam (docs/formats/compositing.md 9): when set, a picture tile whose key carries a grade
    /// (<c>TextureRef.D</c>, resolved through <c>GradeKeys.Resolve</c>) is recoloured while its texture is built, and the graded
    /// texture is cached under the full key, so the plain and the graded versions of a tile are different cache entries.
    /// </summary>
    public TileGrader? Grader { get; set; }

    /// <summary>Bytes of evicted textures kept for reuse.</summary>
    public long SpareCapacityBytes { get; }

    /// <summary>Bytes held by resident textures.</summary>
    public long ResidentBytes { get; private set; }

    /// <summary>Number of resident textures.</summary>
    public int Count => _map.Count;

    /// <summary>The counters.</summary>
    public TextureCacheStats Stats => new(_map.Count, ResidentBytes, _allocations, _recycles, _evictions, _spareBytes);

    /// <summary>True when <paramref name="key"/> is resident (does not touch the LRU order).</summary>
    public bool Contains(in TextureRef key) => _map.ContainsKey(key);

    /// <summary>
    /// Marks <paramref name="key"/> as used now (moves it to the front of the LRU order) when it is resident; false when it is not (package K15). The feeder calls it for every resident
    /// tile a frame draws before it uploads the frame's missing ones: a tile that a look-ahead uploaded earlier and nothing has drawn since sits at the tail of the order, and the uploads
    /// of the same frame would evict it (a 36-tile hole in a CG at the first frame of a scene in skip mode, with the cache at its cap).
    /// </summary>
    public bool Touch(in TextureRef key)
    {
        if (!_map.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            return false;
        }

        if (!ReferenceEquals(_lru.First, node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        return true;
    }

    /// <inheritdoc/>
    public bool TryGetTexture(in TextureRef key, out uint texture, out TransitionParams? transition)
    {
        transition = _transition.TryGetValue(key, out TransitionParams tp) ? tp : null;
        if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            texture = node.Value.Texture;
            return true;
        }

        texture = 0;
        return false;
    }

    /// <summary>
    /// Sets or clears the transition parameters of <paramref name="key"/> (0xD73B0 / 0xFB090 on the PS4). A texture
    /// with parameters is drawn through PS #0 in mode 0.
    /// </summary>
    public void SetTransitionParams(in TextureRef key, TransitionParams? value)
    {
        if (value is { } v)
        {
            _transition[key] = v;
        }
        else
        {
            _transition.Remove(key);
        }
    }

    /// <summary>
    /// Uploads RGBA8 pixels (rows top-down) as the texture of <paramref name="key"/>, straight from client memory:
    /// a recycled texture of the same size gets glTexSubImage2D, otherwise one new texture is allocated with
    /// glTexImage2D (S4 rules 3 and 4). Replaces an existing texture of the key. Evicts least recently used
    /// textures beyond the cap. A graded key (see <see cref="Grader"/>) is recoloured first unless <paramref name="pregraded"/>.
    /// </summary>
    /// <returns>True when a texture was allocated, false when one was recycled.</returns>
    public bool Upload(in TextureRef key, int width, int height, ReadOnlySpan<byte> rgba, bool pregraded = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        long bytes = (long)width * height * 4;
        if (rgba.Length < bytes)
        {
            throw new ArgumentException($"{rgba.Length} bytes for a {width} x {height} texture", nameof(rgba));
        }

        byte[]? graded = null;
        if (!pregraded && Grader is { } grader && TileGrader.IsGraded(key))
        {
            graded = System.Buffers.ArrayPool<byte>.Shared.Rent((int)bytes);
            rgba[..(int)bytes].CopyTo(graded);
            grader.Apply(key, graded.AsSpan(0, (int)bytes));
            rgba = graded;
        }

        try
        {
            return UploadPixels(key, width, height, bytes, rgba);
        }
        finally
        {
            if (graded is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(graded);
            }
        }
    }

    private bool UploadPixels(in TextureRef key, int width, int height, long bytes, ReadOnlySpan<byte> rgba)
    {
        Remove(key);
        bool allocated;
        uint tex;
        _gl.ActiveTexture(TextureUnit.Texture0);
        if (_spare.TryGetValue((width, height), out Stack<uint>? stack) && stack.Count > 0)
        {
            tex = stack.Pop();
            _spareBytes -= bytes;
            _gl.BindTexture(TextureTarget.Texture2D, tex);
            fixed (byte* p = rgba)
            {
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }

            _recycles++;
            allocated = false;
        }
        else
        {
            tex = GpuCompositor.NewTexture(_gl);
            fixed (byte* p = rgba)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }

            _allocations++;
            allocated = true;
        }

        _map[key] = _lru.AddFirst(new Entry(key, tex, width, height, bytes));
        ResidentBytes += bytes;
        Trim();
        return allocated;
    }

    /// <summary>
    /// Creates up to <paramref name="maxCount"/> spare textures of <paramref name="width"/> x <paramref name="height"/>
    /// (storage allocated and first-touched with zeros), within <see cref="SpareCapacityBytes"/>, so later uploads of
    /// that size recycle them with glTexSubImage2D (S4 rule 4: "pre-creating them over the warm-up frames works too").
    /// Call it with a small count per frame (S4: creating 330 textures in one frame stalled the next swap 24-37 ms).
    /// </summary>
    /// <returns>Textures created.</returns>
    public int Prewarm(int width, int height, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        long bytes = (long)width * height * 4;
        int made = 0;
        byte[]? zeros = null;
        while (made < maxCount && _spareBytes + bytes <= SpareCapacityBytes)
        {
            zeros ??= new byte[bytes];
            uint tex = GpuCompositor.NewTexture(_gl);
            fixed (byte* p = zeros)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);

                // Also warm the recycle path. A process's first glTexSubImage2D uploads ran 6-18 ms slower than later
                // ones (D1 re-check: 12 of 12 first loads), so the warm-up frames pay that cost, not the first scene.
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }

            if (!_spare.TryGetValue((width, height), out Stack<uint>? s))
            {
                s = new Stack<uint>();
                _spare[(width, height)] = s;
            }

            s.Push(tex);
            _spareBytes += bytes;
            _allocations++;
            made++;
        }

        return made;
    }

    /// <summary>Evicts <paramref name="key"/> (its texture goes to the spare pool).</summary>
    public bool Remove(in TextureRef key)
    {
        if (!_map.Remove(key, out LinkedListNode<Entry>? node))
        {
            return false;
        }

        _lru.Remove(node);
        ResidentBytes -= node.Value.Bytes;
        Retire(node.Value);
        return true;
    }

    /// <summary>Reads a resident texture back (RGBA8, rows top-down); for tests.</summary>
    public byte[] ReadBack(in TextureRef key)
    {
        Entry e = _map[key].Value;
        var buf = new byte[e.Bytes];
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, e.Texture);
        fixed (byte* p = buf)
        {
            _gl.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        return buf;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (Entry e in _lru)
        {
            _gl.DeleteTexture(e.Texture);
        }

        foreach (Stack<uint> s in _spare.Values)
        {
            foreach (uint t in s)
            {
                _gl.DeleteTexture(t);
            }
        }

        _lru.Clear();
        _map.Clear();
        _spare.Clear();
        ResidentBytes = 0;
        _spareBytes = 0;
    }

    private void Trim()
    {
        while (ResidentBytes > CapacityBytes && _lru.Last is { } last && _lru.Count > 1)
        {
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            ResidentBytes -= last.Value.Bytes;
            _evictions++;
            Retire(last.Value);
        }
    }

    private void Retire(Entry e)
    {
        if (_spareBytes + e.Bytes > SpareCapacityBytes)
        {
            _gl.DeleteTexture(e.Texture);
            return;
        }

        if (!_spare.TryGetValue((e.Width, e.Height), out Stack<uint>? s))
        {
            s = new Stack<uint>();
            _spare[(e.Width, e.Height)] = s;
        }

        s.Push(e.Texture);
        _spareBytes += e.Bytes;
    }

    private readonly record struct Entry(TextureRef Key, uint Texture, int Width, int Height, long Bytes);
}
