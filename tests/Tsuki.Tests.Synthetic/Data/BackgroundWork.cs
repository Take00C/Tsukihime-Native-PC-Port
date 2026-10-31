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
}
