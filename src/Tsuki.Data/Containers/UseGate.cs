using System.Diagnostics;

namespace Tsuki.Data.Containers;

/// <summary>
/// Lets the owner of a resource that threads read through raw pointers (a memory mapping, the packet bytes a native decoder copies) close it
/// only after those threads have left. A reader brackets its use with <see cref="Enter"/>; <see cref="Close"/> refuses new readers and waits
/// for the ones inside. Without it, disposing the owner unmaps the pages under a decode that is still running, and the access violation that
/// follows cannot be caught: .NET ends the process (package K10, docs/status/knife/K10.md).
/// </summary>
/// <remarks>Safe from any thread. A reader that holds the gate must not call <see cref="Close"/> itself (it would wait for its own exit).</remarks>
public sealed class UseGate
{
    private int _users;
    private int _closed;

    /// <summary>Readers inside the gate now.</summary>
    public int Users => Volatile.Read(ref _users);

    /// <summary>True once <see cref="Close"/> has been called.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Enters the gate; dispose the result to leave it.</summary>
    /// <param name="owner">The object that owns the resource (names it in the exception).</param>
    /// <exception cref="ObjectDisposedException">The gate is closed.</exception>
    public Use Enter(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!TryEnter(out Use use))
        {
            throw new ObjectDisposedException(owner.GetType().Name);
        }

        return use;
    }

    /// <summary>Enters the gate unless it is closed.</summary>
    public bool TryEnter(out Use use)
    {
        // Increment first, then look at the flag: Close sets the flag first and then looks at the count, and both sides are full fences,
        // so a reader is either refused here or seen (and waited for) by Close.
        Interlocked.Increment(ref _users);
        if (Volatile.Read(ref _closed) != 0)
        {
            Interlocked.Decrement(ref _users);
            use = default;
            return false;
        }

        use = new Use(this);
        return true;
    }

    /// <summary>Refuses new readers and waits until the ones inside have left.</summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>True when no reader is left; false when the wait ran out (the caller must then leave the resource mapped).</returns>
    public bool Close(TimeSpan timeout)
    {
        Interlocked.Exchange(ref _closed, 1);
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (Volatile.Read(ref _users) > 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            if (spin.Count < 20)
            {
                spin.SpinOnce();
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        return true;
    }

    /// <summary>One reader's stay in the gate.</summary>
    public readonly struct Use : IDisposable
    {
        private readonly UseGate? _gate;

        internal Use(UseGate gate) => _gate = gate;

        /// <summary>Leaves the gate.</summary>
        public void Dispose()
        {
            if (_gate is not null)
            {
                Interlocked.Decrement(ref _gate._users);
            }
        }
    }
}
