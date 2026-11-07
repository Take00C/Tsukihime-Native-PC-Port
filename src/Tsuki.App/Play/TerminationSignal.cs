// SIGTERM on Linux and macOS: Steam's Exit Game, gamescope's reaper, a logout, systemd at shutdown. The .NET runtime would end the process at once, before the
// loop's last save of the read marks and unlocks of the scene being read; the first SIGTERM is turned into a quit instead, so the loop ends, saves and writes its
// summary. A second SIGTERM is not held back and ends the process as before; SIGKILL cannot be caught, and the saves made at each scene start stay the floor.
using System.Runtime.InteropServices;

namespace Tsuki.App.Play;

/// <summary>The loop's SIGTERM (see the file header). On Windows nothing is registered, as in v0.1.6.</summary>
internal sealed class TerminationSignal : IDisposable
{
    private readonly PosixSignalRegistration? _registration;
    private int _requested;

    private TerminationSignal(bool listen)
    {
        if (listen && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            _registration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        }
    }

    /// <summary>True once a SIGTERM came: the loop quits the way the title's EXIT does.</summary>
    public bool Requested => Volatile.Read(ref _requested) != 0;

    /// <summary>True when SIGTERM is being held back for the loop.</summary>
    public bool Listening => _registration is not null;

    /// <summary>Starts listening on Linux and macOS (the loop calls this once it runs; a SIGTERM during the set-up still ends the process at once).</summary>
    public static TerminationSignal Listen() => new(!OperatingSystem.IsWindows());

    /// <inheritdoc/>
    public void Dispose() => _registration?.Dispose();

    private void OnSignal(PosixSignalContext context)
    {
        if (Interlocked.Exchange(ref _requested, 1) == 0)
        {
            context.Cancel = true;                  // the first one: the loop ends and saves; the next one is not cancelled
        }
    }
}
