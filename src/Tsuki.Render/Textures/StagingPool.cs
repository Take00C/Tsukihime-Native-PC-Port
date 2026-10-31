// Bounded staging pool (S4 upload rule 5; ARCHITECTURE 4.3): about 64 MiB of pre-touched tile buffers.
// Decode workers rent a buffer before they decode a tile and wait while the pool is exhausted; the
// upload scheduler returns the buffer once the tile is on the GPU. This bounds the decoded bytes in
// flight (S4: peak working set 241-279 MB instead of 359-603 MB, load time unchanged).
namespace Tsuki.Render.Textures;

/// <summary>A rented tile buffer. <see cref="Length"/> bytes of <see cref="Array"/> are the tile.</summary>
public sealed class StagingBuffer
{
    internal StagingBuffer(byte[] array) => Array = array;

    /// <summary>The backing array (capacity may exceed <see cref="Length"/>).</summary>
    public byte[] Array { get; }

    /// <summary>Bytes in use.</summary>
    public int Length { get; internal set; }

    /// <summary>The bytes in use.</summary>
    public Span<byte> Span => Array.AsSpan(0, Length);
}

/// <summary>A byte-bounded pool of pinned, pre-touched buffers. Thread-safe.</summary>
public sealed class StagingPool
{
    private const int Page = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<int, Stack<byte[]>> _free = [];
    private long _freeBytes;
    private long _outstandingBytes;

    /// <summary>Creates an empty pool.</summary>
    /// <param name="budgetBytes">Bound on the bytes of all buffers, rented and free (default 64 MiB).</param>
    public StagingPool(long budgetBytes = 64L << 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetBytes, 1);
        BudgetBytes = budgetBytes;
    }

    /// <summary>The bound on the pool's bytes.</summary>
    public long BudgetBytes { get; }

    /// <summary>Bytes currently rented.</summary>
    public long OutstandingBytes
    {
        get
        {
            lock (_gate)
            {
                return _outstandingBytes;
            }
        }
    }

    /// <summary>The largest <see cref="OutstandingBytes"/> seen.</summary>
    public long PeakOutstandingBytes { get; private set; }

    /// <summary>The largest total of rented and free bytes seen (never above the budget, except for a single oversize buffer).</summary>
    public long PeakTotalBytes { get; private set; }

    /// <summary>How often a renter had to wait for a return.</summary>
    public long Waits { get; private set; }

    /// <summary>Buffers allocated (and pre-touched) so far.</summary>
    public long Allocations { get; private set; }

    /// <summary>
    /// Rents a buffer of <paramref name="size"/> bytes, waiting while the pool is at its budget. A request larger than
    /// the budget is served alone, when nothing else is rented.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was cancelled while waiting.</exception>
    public StagingBuffer Rent(int size, CancellationToken cancel = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        byte[]? array = null;

        // The wake-up registration is disposed outside the lock. Disposing a registration waits for its callback when that is running on another thread, and
        // this callback takes the lock: a renter that left the registration's scope while it held the lock deadlocked against the thread that called Cancel
        // (package K10: DecodeWorkers.Dispose, so the process never exited after the last frame).
        CancellationTokenRegistration wake = default;
        bool registered = false;
        try
        {
            lock (_gate)
            {
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (_free.TryGetValue(size, out Stack<byte[]>? s) && s.Count > 0)
                    {
                        array = s.Pop();
                        _freeBytes -= array.Length;
                        break;
                    }

                    if (_outstandingBytes + _freeBytes + size <= BudgetBytes || _outstandingBytes == 0)
                    {
                        // Make room by dropping free buffers of other sizes.
                        foreach (Stack<byte[]> other in _free.Values)
                        {
                            while (other.Count > 0 && _outstandingBytes + _freeBytes + size > BudgetBytes)
                            {
                                _freeBytes -= other.Pop().Length;
                            }
                        }

                        if (_outstandingBytes + _freeBytes + size <= BudgetBytes || _outstandingBytes == 0)
                        {
                            break;                            // allocate outside the lock
                        }
                    }

                    if (cancel.CanBeCanceled && !registered)
                    {
                        wake = cancel.Register(
                            static state =>
                            {
                                var pool = (StagingPool)state!;
                                lock (pool._gate)
                                {
                                    Monitor.PulseAll(pool._gate);
                                }
                            },
                            this);
                        registered = true;
                        continue;                             // a Cancel that raced with the registration ran the callback at once and its pulse is gone: look again first
                    }

                    Waits++;
                    Monitor.Wait(_gate);
                }

                _outstandingBytes += size;
                PeakOutstandingBytes = Math.Max(PeakOutstandingBytes, _outstandingBytes);
                PeakTotalBytes = Math.Max(PeakTotalBytes, _outstandingBytes + _freeBytes);
                if (array is null)
                {
                    Allocations++;
                }
            }
        }
        finally
        {
            wake.Dispose();
        }

        array ??= Allocate(size);
        return new StagingBuffer(array) { Length = size };
    }

    /// <summary>Returns a rented buffer.</summary>
    public void Return(StagingBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_gate)
        {
            _outstandingBytes -= buffer.Array.Length;
            if (_outstandingBytes + _freeBytes + buffer.Array.Length <= BudgetBytes)
            {
                if (!_free.TryGetValue(buffer.Array.Length, out Stack<byte[]>? s))
                {
                    s = new Stack<byte[]>();
                    _free[buffer.Array.Length] = s;
                }

                s.Push(buffer.Array);
                _freeBytes += buffer.Array.Length;
            }

            Monitor.PulseAll(_gate);
        }
    }

    private static byte[] Allocate(int size)
    {
        byte[] a = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        for (int i = 0; i < a.Length; i += Page)
        {
            a[i] = 0;                                    // pre-touch: commit every page now, not in the upload
        }

        a[^1] = 0;
        return a;
    }
}
