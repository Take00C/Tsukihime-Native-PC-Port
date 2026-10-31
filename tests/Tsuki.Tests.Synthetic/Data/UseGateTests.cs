using Tsuki.Data.Containers;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>Package K10: the gate that keeps a mapping mapped while a thread reads it through a raw pointer.</summary>
public sealed class UseGateTests
{
    [Fact]
    public void AClosedGateRefusesNewReadersAndAnEmptyOneClosesAtOnce()
    {
        var gate = new UseGate();
        using (UseGate.Use use = gate.Enter(this))
        {
            Assert.Equal(1, gate.Users);
        }

        Assert.Equal(0, gate.Users);
        Assert.True(gate.Close(TimeSpan.Zero));
        Assert.True(gate.IsClosed);
        Assert.False(gate.TryEnter(out _));
        Assert.Equal(0, gate.Users);
        Assert.Throws<ObjectDisposedException>(() => gate.Enter(this));
        Assert.True(gate.Close(TimeSpan.Zero), "closing twice is fine");
    }

    [Fact]
    public void CloseWaitsForTheReaderInsideAndGivesUpAtItsTimeout()
    {
        var gate = new UseGate();
        using var inside = new ManualResetEventSlim(false);
        using var leave = new ManualResetEventSlim(false);
        var reader = BackgroundWork.Run(() =>
        {
            using UseGate.Use use = gate.Enter(this);
            inside.Set();
            leave.Wait(30_000);
        });
        Assert.True(BackgroundWork.Wait(inside, 10_000));

        Assert.False(gate.Close(TimeSpan.FromMilliseconds(60)), "a reader is still inside: the wait runs out");
        Assert.False(gate.TryEnter(out _), "and a new reader is refused from the first Close on");

        var closer = BackgroundWork.Run(() => gate.Close(TimeSpan.FromSeconds(20)));
        Thread.Sleep(50);
        Assert.False(closer.IsCompleted, "Close does not return while the reader is inside");
        leave.Set();
        Assert.True(BackgroundWork.Wait(closer, 10_000));
        Assert.True(BackgroundWork.Result(closer));
        Assert.True(BackgroundWork.Wait(reader, 10_000));
        Assert.Equal(0, gate.Users);
    }

    [Fact]
    public void ManyReadersAgainstAClosingGateNeverSeeTheResourceReleased()
    {
        // the resource is a flag the owner raises once Close returned true; a reader that is inside must never observe it raised
        for (int round = 0; round < 40; round++)
        {
            var gate = new UseGate();
            int released = 0;
            int violations = 0;
            int refused = 0;
            int served = 0;
            using var go = new ManualResetEventSlim(false);
            Task[] readers = [.. Enumerable.Range(0, 6).Select(_ => BackgroundWork.Run(() =>
            {
                go.Wait();
                for (int i = 0; i < 20_000; i++)
                {
                    if (!gate.TryEnter(out UseGate.Use use))
                    {
                        Interlocked.Increment(ref refused);
                        return;
                    }

                    using (use)
                    {
                        if (Volatile.Read(ref released) != 0)
                        {
                            Interlocked.Increment(ref violations);
                        }

                        Thread.SpinWait(20);
                        if (Volatile.Read(ref released) != 0)
                        {
                            Interlocked.Increment(ref violations);
                        }

                        Interlocked.Increment(ref served);
                    }
                }
            }))];
            go.Set();
            Thread.Sleep(round % 5);
            Assert.True(gate.Close(TimeSpan.FromSeconds(30)));
            Volatile.Write(ref released, 1);                     // "unmap"
            BackgroundWork.WaitAll(readers);
            Assert.Equal(0, violations);
            Assert.Equal(0, gate.Users);
            Assert.True(served > 0 || refused > 0);
        }
    }
}
