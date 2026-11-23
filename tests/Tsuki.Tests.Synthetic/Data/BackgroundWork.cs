namespace Tsuki.Tests.Synthetic;

/// <summary>
/// Thread-pool work and waits for the lifetime tests of package K10. They block on purpose (a test thread waits for another thread to reach a state), which
/// the xUnit analyzers flag in a test method body, so the calls live here.
/// </summary>
internal static class BackgroundWork
{
    // a thread of its own (not the pool): the waits below are about the order of events, and a busy pool adds threads slowly
    public static Task Run(Action work) => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task<T> Run<T>(Func<T> work) => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static bool Wait(Task task, int milliseconds) => task.Wait(milliseconds);

    public static bool Wait(ManualResetEventSlim signal, int milliseconds) => signal.Wait(milliseconds);

    public static T Result<T>(Task<T> task) => task.GetAwaiter().GetResult();

    public static void WaitAll(Task[] tasks) => Task.WaitAll(tasks);

    /// <summary>
    /// Runs <paramref name="body"/> on a thread of its own, as the game runs the engine code that queues decodes on the pool (its main thread), and rethrows what
    /// it threw. An xUnit test runs on a pool thread, and work it queues with <c>Task.Run</c> goes to that thread's own queue, which the other pool threads take
    /// from only when the pool's shared queue is empty (the order of .NET's <c>ThreadPoolWorkQueue.Dequeue</c>): while the test then waits for that work, a full
    /// parallel run can leave it there for seconds (in one loaded run of each suite: 4 s against 1 s for the shared queue at the same moment, 17 s against 2 s in
    /// Data; in other full runs at most about 60 ms later). From a thread of its own it goes to the shared queue, as in the game; the waits allow tens of
    /// seconds, since the shared queue alone took up to 7 s in that Data run.
    /// </summary>
    public static void OnOwnThread(Action body)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            }
        })
        {
            IsBackground = true,
            Name = "test engine thread",
        };
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(5)))
        {
            throw new TimeoutException("the test body did not end within 5 minutes");
        }

        failure?.Throw();
    }
}

/// <summary>
/// Tells when the pool work a call queued has ended, with no hook in the code under test: a value set around the call flows into the work items it queues (with
/// the execution context <c>Task.Run</c> captures), and its change handler runs on the pool thread when the thread leaves that context after the item, its own
/// catch blocks included, has finished. Waiting for a decode to have been called is not enough: what the item does after it (a failure forgotten, a clip
/// counted) takes longer than any fixed sleep on a slow machine. It sees only work that flows the execution context (<c>Task.Run</c> does; work queued with
/// <c>UnsafeQueueUserWorkItem</c>, under <c>ExecutionContext.SuppressFlow</c>, or done at once on the calling thread is never counted): a wait that runs out
/// says so (<see cref="NeverEnded"/>).
/// </summary>
internal sealed class QueuedWork
{
    private readonly object _mark = new();
    private readonly AsyncLocal<object?> _flow;
    private int _ended;

    private QueuedWork() => _flow = new AsyncLocal<object?>(OnChanged);

    /// <summary>Work items that ran <see cref="Of"/>'s call's context and have ended.</summary>
    public int Ended => Volatile.Read(ref _ended);

    /// <summary>Runs <paramref name="call"/> and watches the pool work it queues.</summary>
    public static QueuedWork Of(Action call)
    {
        var work = new QueuedWork();
        work._flow.Value = work._mark;
        try
        {
            call();
        }
        finally
        {
            work._flow.Value = null;
        }

        return work;
    }

    /// <summary>True when <paramref name="count"/> work items have ended within <paramref name="milliseconds"/>.</summary>
    public bool WaitEnded(int count, int milliseconds) => SpinWait.SpinUntil(() => Ended >= count, milliseconds);

    /// <summary>The words of a wait for <paramref name="what"/> that ran out.</summary>
    public static string NeverEnded(string what) =>
        $"{what} never ended on the pool, or its work did not flow the execution context (queued without it, or done at once on the calling thread)";

    private void OnChanged(AsyncLocalValueChangedArgs<object?> change)
    {
        // a thread leaving the call's context: the end of a work item (the test thread's own changes above are not context changes)
        if (change.ThreadContextChanged && ReferenceEquals(change.PreviousValue, _mark) && change.CurrentValue is null)
        {
            Interlocked.Increment(ref _ended);
        }
    }
}
