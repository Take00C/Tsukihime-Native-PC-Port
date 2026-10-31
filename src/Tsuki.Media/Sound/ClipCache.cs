using Tsuki.Data.Containers;
using Tsuki.Engine.Contracts;

namespace Tsuki.Media.Sound;

/// <summary>
/// Decoded clips by (bank, entry), shared between the engine thread and prefetch workers, evicting the least recently used clips
/// above a byte budget. A clip a mixer voice plays stays alive through the voice's own reference, whatever the cache does.
/// </summary>
public sealed class ClipCache : IDisposable
{
    private readonly Lock _gate = new();
    private readonly UseGate _decoding = new();
    private readonly Func<AudioBank, int, AudioClip> _decode;
    private readonly long _budgetBytes;
    private readonly long _collectEveryBytes;
    private readonly Action _collect;
    private long _decodedSinceCollect;
    private readonly Dictionary<(AudioBank, int), Entry> _entries = [];
    private readonly LinkedList<(AudioBank, int)> _lru = new();
    private long _bytes;

    private sealed class Entry(Lazy<AudioClip> clip)
    {
        public Lazy<AudioClip> Clip { get; } = clip;

        public LinkedListNode<(AudioBank, int)>? Node { get; set; }

        public long Bytes { get; set; }
    }

    /// <summary>Creates a cache over <paramref name="decode"/> (for example <see cref="IGameData.Audio"/>).</summary>
    /// <param name="decode">Decodes one entry; may be called from any thread.</param>
    /// <param name="budgetBytes">Soft limit of the decoded PCM kept (float32 samples); 256 MB by default.</param>
    /// <param name="collectEveryBytes">
    /// Decoded PCM, in bytes, after which <paramref name="collect"/> runs (default 128 MiB; 0 = never). Every clip is a large-object-heap array, and the collector frees
    /// those only with a generation-2 collection, which it schedules from its own budget: a play that decodes fast (voices, music, effects of a skipped scene) let a
    /// gigabyte of evicted clips pile up before the first one was freed (package K11, docs/status/knife/K11.md).
    /// </param>
    /// <param name="collect">The collection to run; default a non-blocking (background) generation-2 collection.</param>
    public ClipCache(Func<AudioBank, int, AudioClip> decode, long budgetBytes = 256L << 20, long collectEveryBytes = 128L << 20, Action? collect = null)
    {
        ArgumentNullException.ThrowIfNull(decode);
        _decode = decode;
        _budgetBytes = budgetBytes;
        _collectEveryBytes = collectEveryBytes;
        _collect = collect ?? (() => GC.Collect(2, GCCollectionMode.Forced, blocking: false));
    }

    /// <summary>Times the collection was requested after <c>collectEveryBytes</c> of decoded PCM.</summary>
    public long Collections { get; private set; }

    /// <summary>Decodes performed (cache misses).</summary>
    public long Decodes { get; private set; }

    /// <summary>Requests answered from the cache.</summary>
    public long Hits { get; private set; }

    /// <summary>Bytes of decoded PCM held.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>
    /// Stops the cache: no decode starts any more (<see cref="Get"/> throws <see cref="ObjectDisposedException"/>, <see cref="Prefetch"/> does nothing) and the decodes
    /// that are running are waited for. Call it before the data the decode function reads is disposed: a decode that outlives its data reads unmapped memory,
    /// which ends the process (package K10).
    /// </summary>
    /// <param name="timeout">The longest wait for running decodes (one takes 0.1 s, a long BGM a few times that).</param>
    /// <returns>True when no decode is running any more.</returns>
    public bool Shutdown(TimeSpan timeout) => _decoding.Close(timeout);

    /// <inheritdoc />
    public void Dispose() => Shutdown(TimeSpan.FromSeconds(15));

    /// <summary>The clip, decoding it on the calling thread if no one has yet (concurrent callers share one decode).</summary>
    public AudioClip Get(AudioBank bank, int entry)
    {
        Entry e = Acquire(bank, entry);
        AudioClip clip;
        try
        {
            clip = e.Clip.Value;                             // outside the lock: a decode takes up to ~0.1 s
        }
        catch (Exception)
        {
            Forget(bank, entry, e);                          // a failed decode must not stay cached
            throw;
        }

        bool collect = false;
        lock (_gate)
        {
            if (e.Bytes == 0 && _entries.TryGetValue((bank, entry), out Entry? current) && ReferenceEquals(current, e))
            {
                e.Bytes = (long)clip.Samples.Length * sizeof(float);
                _bytes += e.Bytes;
                Evict(keep: (bank, entry));
                _decodedSinceCollect += e.Bytes;
                if (_collectEveryBytes > 0 && _decodedSinceCollect >= _collectEveryBytes)
                {
                    _decodedSinceCollect = 0;
                    Collections++;
                    collect = true;
                }
            }
        }

        if (collect)
        {
            _collect();                                      // outside the lock
        }

        return clip;
    }

    /// <summary>Starts decoding an entry on a worker; the next <see cref="Get"/> finds it ready (or joins the decode in progress).</summary>
    public void Prefetch(AudioBank bank, int entry)
    {
        Entry e;
        try
        {
            e = Acquire(bank, entry);
        }
        catch (ObjectDisposedException)
        {
            return;                                          // the cache is shut down: a speculative decode is not wanted
        }

        if (!e.Clip.IsValueCreated)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Get(bank, entry);
                }
                catch (Exception)
                {
                    // Get already forgot the entry; the failure surfaces to the caller that needs the clip, not to a speculative prefetch
                }
            });
        }
    }

    private void Forget(AudioBank bank, int entry, Entry only)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((bank, entry), out Entry? e) && ReferenceEquals(e, only))
            {
                _entries.Remove((bank, entry));
                _bytes -= e.Bytes;
                if (e.Node is not null)
                {
                    _lru.Remove(e.Node);
                }
            }
        }
    }

    private AudioClip Decode(AudioBank bank, int entry)
    {
        using UseGate.Use use = _decoding.Enter(this);       // Shutdown waits for this decode; one that starts after it is refused
        return _decode(bank, entry);
    }

    private Entry Acquire(AudioBank bank, int entry)
    {
        ObjectDisposedException.ThrowIf(_decoding.IsClosed, this);
        lock (_gate)
        {
            if (_entries.TryGetValue((bank, entry), out Entry? e))
            {
                Hits++;
                if (e.Node is not null)
                {
                    _lru.Remove(e.Node);
                    _lru.AddFirst(e.Node);
                }

                return e;
            }

            Decodes++;
            e = new Entry(new Lazy<AudioClip>(() => Decode(bank, entry), LazyThreadSafetyMode.ExecutionAndPublication));
            e.Node = _lru.AddFirst((bank, entry));
            _entries[(bank, entry)] = e;
            return e;
        }
    }

    private void Evict((AudioBank, int) keep)
    {
        // oldest first, skipping the clip that was just decoded (it is the oldest by request time but the newest by completion: stopping at it left
        // every clip requested after it in memory, however far over the budget that went)
        LinkedListNode<(AudioBank, int)>? node = _lru.Last;
        while (_bytes > _budgetBytes && node is not null)
        {
            LinkedListNode<(AudioBank, int)>? previous = node.Previous;
            if (!node.Value.Equals(keep))
            {
                _lru.Remove(node);
                if (_entries.Remove(node.Value, out Entry? e))
                {
                    _bytes -= e.Bytes;
                }
            }

            node = previous;
        }
    }
}
