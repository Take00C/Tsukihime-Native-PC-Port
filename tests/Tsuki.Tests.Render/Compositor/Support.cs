using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Tsuki.Platform;
using Tsuki.Render.Gl;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>Repository paths and the Python oracle (tools/tsuki, .venv) for the compositor tests.</summary>
internal static class Oracle
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root (first ancestor of the test binary with src/Tsuki.slnx).</summary>
    public static string RepoRoot => RootPath.Value;

    /// <summary>The Python interpreter: <c>TSUKI_PYTHON</c>, else the repository's .venv.</summary>
    public static string Python
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("TSUKI_PYTHON");
            return string.IsNullOrWhiteSpace(env)
                ? OperatingSystem.IsWindows()
                    ? Path.Combine(RepoRoot, ".venv", "Scripts", "python.exe")
                    : Path.Combine(RepoRoot, ".venv", "bin", "python")
                : env;
        }
    }

    /// <summary>The folder of this test file's Python helpers.</summary>
    public static string HelperDir => Path.Combine(RepoRoot, "tests", "Tsuki.Tests.Render", "Compositor");

    /// <summary>
    /// Runs Python with PYTHONPATH = tools and the given arguments; fails the test with the output when it exits
    /// non-zero. Returns stdout.
    /// </summary>
    public static string Run(TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(Python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["PYTHONPATH"] = Path.Combine(RepoRoot, "tools");
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("python did not start: " + Python);
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"python {string.Join(' ', args)} did not finish in {timeout}");
        }

        p.WaitForExit();
        string o = stdout.Result, e = stderr.Result;
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"python {string.Join(' ', args)} exited {p.ExitCode}:\n{o}\n{e}");
        }

        return o;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root (src/Tsuki.slnx) not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A fresh folder under %TEMP% that is deleted with everything in it on dispose (game-derived data never stays).</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string tag)
    {
        // Folders of an earlier test host that was killed mid-test are removed first (they may hold game-derived data).
        foreach (string stale in Directory.EnumerateDirectories(Path.GetTempPath(), "tsuki-d1-*"))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(stale) > TimeSpan.FromMinutes(30))
            {
                Delete(stale, attempts: 1);
            }
        }

        FullName = Path.Combine(Path.GetTempPath(), $"tsuki-d1-{tag}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(FullName);
    }

    public string FullName { get; }

    public string File(string name) => Path.Combine(FullName, name);

    public void Dispose()
    {
        if (!Delete(FullName, attempts: 10))
        {
            throw new IOException($"could not delete the temporary folder {FullName}");
        }
    }

    private static bool Delete(string path, int attempts)
    {
        for (int attempt = 0; attempt < attempts && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }

        return !Directory.Exists(path);
    }
}

/// <summary>A hidden window with a GL 3.3 core context, current on the test thread.</summary>
internal sealed class GlContext : IDisposable
{
    private GlContext(GameWindow window, GlDevice device)
    {
        Window = window;
        Device = device;
    }

    public GameWindow Window { get; }

    public GlDevice Device { get; }

    public static GlContext CreateHidden(string title = "Tsuki.Tests.Render compositor") =>
        Create(new WindowOptions(title, 320, 180, Hidden: true, Resizable: false, SwapInterval: 0));

    public static GlContext Create(WindowOptions options)
    {
        GameWindow w = GameWindow.Create(options);
        try
        {
            return new GlContext(w, GlDevice.Create(w.GetProcAddress));
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    public void Dispose() => Window.Dispose();
}

/// <summary>
/// The GPU and timing lock of decision 4: <c>artifacts/gpu.lock</c> is created exclusively, deleted when done;
/// a holder waits while it exists, and a lock older than 30 minutes is stale.
/// </summary>
internal sealed class GpuLock : IDisposable
{
    private readonly FileStream _stream;

    private GpuLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static GpuLock Acquire(string holder, TimeSpan wait)
    {
        string dir = System.IO.Path.Combine(Oracle.RepoRoot, "artifacts");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "gpu.lock");
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.DeleteOnClose);
                byte[] text = Encoding.UTF8.GetBytes($"{holder} (pid {Environment.ProcessId}) {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                fs.Write(text);
                fs.Flush();
                return new GpuLock(path, fs);
            }
            catch (IOException) when (System.IO.File.Exists(path))
            {
                DateTime written = System.IO.File.GetLastWriteTimeUtc(path);
                if (DateTime.UtcNow - written > TimeSpan.FromMinutes(30))
                {
                    try
                    {
                        System.IO.File.Delete(path);              // stale (decision 4)
                    }
                    catch (IOException)
                    {
                    }

                    continue;
                }

                if (sw.Elapsed > wait)
                {
                    throw new TimeoutException($"{path} is held by: {SafeRead(path)} (waited {sw.Elapsed.TotalSeconds:F0} s)");
                }

                Thread.Sleep(2000);
            }
        }
    }

    public void Dispose() => _stream.Dispose();

    private static string SafeRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd().Trim();
        }
        catch (IOException)
        {
            return "?";
        }
    }
}

/// <summary>The user's CUSA47999 folder, by the rule of tests/Tsuki.Tests.Data/GameData.cs (the environment variable TSUKI_GAME_DIR; no built-in default path).</summary>
internal static class GameFolder
{
    public const string EnvironmentVariable = "TSUKI_GAME_DIR";

    private static readonly Lazy<string?> Resolved = new(Resolve);

    public static string? Path => Resolved.Value;

    public static string? MissingReason { get; } = Resolved.Value is null
        ? Candidate() is { } tried
            ? $"game data missing: TSUKI_GAME_DIR must name the CUSA47999 folder (holding eboot.bin and image\\); tried {tried}"
            : "game data missing: set the environment variable TSUKI_GAME_DIR to the CUSA47999 folder (holding eboot.bin and image\\)"
        : null;

    public static string Require() => Path ?? throw new InvalidOperationException(MissingReason);

    private static string? Candidate()
    {
        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    private static string? Resolve()
    {
        string? dir = Candidate();
        return dir is not null && Directory.Exists(System.IO.Path.Combine(dir, "image")) && System.IO.File.Exists(System.IO.Path.Combine(dir, "eboot.bin"))
            ? System.IO.Path.GetFullPath(dir)
            : null;
    }
}

/// <summary>
/// The one allowed skip (ARCHITECTURE 10.2, 10.8), as <c>tests/Tsuki.Tests.Data/GameData.cs</c> defines it: a fact
/// that needs the game folder and skips, with the reason, only when the folder is missing. (Tsuki.Tests.Render
/// does not reference Tsuki.Tests.Data; the lead may link that file instead of this copy.)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GameDataFactAttribute : FactAttribute
{
    public GameDataFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (GameFolder.MissingReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Raises the calling thread's priority for a scope (the frame thread of the timing tests).</summary>
internal sealed class ThreadPriorityScope : IDisposable
{
    private readonly ThreadPriority _saved = Thread.CurrentThread.Priority;

    public ThreadPriorityScope(ThreadPriority priority) => Thread.CurrentThread.Priority = priority;

    public void Dispose() => Thread.CurrentThread.Priority = _saved;
}
