// Tile decode workers (S4.md 5.3 rule 1; ARCHITECTURE 4.3): dedicated threads at below-normal
// priority, at most the physical core count (14 workers on the 8-core test machine produced 34.5 and
// 66.8 ms frames; 8 produced none). Each job rents a staging buffer (rule 5: the workers wait on the
// bounded pool), decodes one tile into it and hands it to the upload scheduler.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Tsuki.Engine.Contracts;

namespace Tsuki.Render.Textures;

/// <summary>Writes one tile's RGBA8 pixels (rows top-down) into <paramref name="rgba"/>.</summary>
public delegate void TileDecode(Span<byte> rgba);

/// <summary>One tile to decode.</summary>
/// <param name="Key">The texture it becomes.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Decode">Fills the pixels.</param>
/// <param name="Background">True for a look-ahead job: its tile waits in the scheduler's background queue until a frame asks for it.</param>
public readonly record struct DecodeJob(TextureRef Key, int Width, int Height, TileDecode Decode, bool Background = false);

/// <summary>A fixed set of decode threads feeding an <see cref="UploadScheduler"/>.</summary>
public sealed class DecodeWorkers : IDisposable
{
    private readonly ConcurrentQueue<DecodeJob> _jobs = [];
    private readonly ConcurrentDictionary<TextureRef, DecodeJob> _background = [];
    private readonly ConcurrentQueue<TextureRef> _backgroundOrder = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread[] _threads;
    private readonly UploadScheduler _scheduler;
    private readonly CancellationTokenSource _stop = new();
    private int _inFlight;
    private long _decoded;
    private Exception? _fault;

    /// <summary>Starts the threads.</summary>
    /// <param name="scheduler">Where decoded tiles go.</param>
    /// <param name="workers">Thread count; 0 = <see cref="CpuTopology.DefaultDecodeWorkers"/>. Capped at the physical core count.</param>
    public DecodeWorkers(UploadScheduler scheduler, int workers = 0)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _scheduler = scheduler;
        int cores = CpuTopology.PhysicalCores;
        WorkerCount = Math.Clamp(workers <= 0 ? CpuTopology.DefaultDecodeWorkers : workers, 1, cores);
        _threads = new Thread[WorkerCount];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Run)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"tsuki-tile-decode-{i}",
            };
            _threads[i].Start();
        }
    }

    /// <summary>Number of threads.</summary>
    public int WorkerCount { get; }

    /// <summary>Jobs queued or being decoded.</summary>
    public int Pending => _jobs.Count + _background.Count + Volatile.Read(ref _inFlight);

    /// <summary>Tiles decoded so far.</summary>
    public long Decoded => Interlocked.Read(ref _decoded);

    /// <summary>The first exception a decode threw, if any (the job is dropped).</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Queues a job that a frame is waiting for.</summary>
    public void Enqueue(in DecodeJob job)
    {
        ArgumentNullException.ThrowIfNull(job.Decode);
        _jobs.Enqueue(job);
        _signal.Release();
    }

    /// <summary>Queues a job nobody waits for yet (the look-ahead of the next scene): the workers take it only when no urgent job is queued.</summary>
    public void EnqueueBackground(in DecodeJob job)
    {
        ArgumentNullException.ThrowIfNull(job.Decode);
        if (_background.TryAdd(job.Key, job with { Background = true }))
        {
            _backgroundOrder.Enqueue(job.Key);
            _signal.Release();
        }
    }

    /// <summary>A frame now waits for <paramref name="key"/>: when its job is still a background job it moves to the front (the urgent queue).</summary>
    public void Promote(in TextureRef key)
    {
        if (_background.TryRemove(key, out DecodeJob job))
        {
            _jobs.Enqueue(job with { Background = false });
            _signal.Release();
        }

        _scheduler.Promote(key);                    // decoded already and waiting for its upload
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stop.Cancel();
        foreach (Thread t in _threads)
        {
            t.Join();
        }

        _signal.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                _signal.Wait(_stop.Token);
                if (!_jobs.TryDequeue(out DecodeJob job))
                {
                    // a background job: its key may have been promoted already (the token of the promotion serves the urgent copy)
                    if (!_backgroundOrder.TryDequeue(out TextureRef next) || !_background.TryRemove(next, out job))
                    {
                        continue;
                    }
                }

                Interlocked.Increment(ref _inFlight);
                StagingBuffer? buf = null;
                try
                {
                    buf = _scheduler.Pool.Rent(job.Width * job.Height * 4, _stop.Token);
                    job.Decode(buf.Span);
                    bool graded = false;
                    if (_scheduler.Cache.Grader is { } grader && Grade.TileGrader.IsGraded(job.Key))
                    {
                        grader.Apply(job.Key, buf.Span);          // the recolour runs on the worker, not on the GL thread
                        graded = true;
                    }

                    var decoded = new DecodedTile(job.Key, job.Width, job.Height, buf, graded);
                    if (job.Background)
                    {
                        _scheduler.EnqueueBackground(decoded);
                    }
                    else
                    {
                        _scheduler.Enqueue(decoded);
                    }

                    buf = null;
                    Interlocked.Increment(ref _decoded);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _fault, e, null);
                }
                finally
                {
                    if (buf is not null)
                    {
                        _scheduler.Pool.Return(buf);
                    }

                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>CPU topology for the decode-worker bound.</summary>
public static partial class CpuTopology
{
    private static readonly Lazy<int> Cores = new(CountPhysicalCores);

    /// <summary>Physical cores (Windows: GetLogicalProcessorInformation; elsewhere half the logical processors).</summary>
    public static int PhysicalCores => Cores.Value;

    /// <summary>The default decode-worker count: the physical cores, at most 8 (S4 measured 8 workers on 8 cores).</summary>
    public static int DefaultDecodeWorkers => Math.Clamp(PhysicalCores, 1, 8);

    private static int CountPhysicalCores()
    {
        int fallback = Math.Max(1, Environment.ProcessorCount / 2);
        if (!OperatingSystem.IsWindows())
        {
            return fallback;
        }

        try
        {
            uint length = 0;
            GetLogicalProcessorInformation(0, ref length);
            if (length == 0)
            {
                return fallback;
            }

            nint buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformation(buffer, ref length))
                {
                    return fallback;
                }

                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION: ULONG_PTR mask, int relationship, union of 16 bytes.
                int size = IntPtr.Size == 8 ? 32 : 24;
                int cores = 0;
                for (int off = 0; off + size <= length; off += size)
                {
                    if (Marshal.ReadInt32(buffer, off + IntPtr.Size) == 0)
                    {
                        cores++;                         // RelationProcessorCore
                    }
                }

                return cores > 0 ? cores : fallback;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (EntryPointNotFoundException)
        {
            return fallback;
        }
        catch (DllNotFoundException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Whole-machine CPU times (GetSystemTimes, 100 ns units summed over all processors): idle and total (kernel time
    /// includes idle). For hitch diagnostics: the machine's load while frame times are measured. Null off Windows.
    /// </summary>
    public static (ulong Idle, ulong Total)? SystemTimes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return GetSystemTimes(out ulong idle, out ulong kernel, out ulong user) ? (idle, kernel + user) : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformation(nint buffer, ref uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
