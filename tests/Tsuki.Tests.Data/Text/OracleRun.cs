using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Tsuki.Tests.Data.Text;

/// <summary>
/// Runs a thin Python caller of the oracle (<c>tools/tsuki</c>) that lives next to its tests, with the project venv, and keeps the JSON lines it
/// prints. The caller writes nothing to disk. Each (script, mode) pair runs once per test process.
/// </summary>
internal static class OracleRun
{
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>> Cache = new(StringComparer.Ordinal);

    /// <summary>The project venv's interpreter.</summary>
    public static string Python => Path.Combine(GoldenData.RepoRoot, ".venv", "Scripts", "python.exe");

    /// <summary>The stdout lines of <c>python -I &lt;script&gt; &lt;mode&gt;</c> (script relative to the repository root).</summary>
    public static IReadOnlyList<string> Lines(string script, string mode, TimeSpan timeout) =>
        Cache.GetOrAdd(script + " " + mode, _ => new Lazy<IReadOnlyList<string>>(() => Execute(script, mode, timeout))).Value;

    /// <summary>The parsed records of one kind (<c>"k":"..."</c>), in output order. Dispose each document when done.</summary>
    public static IEnumerable<JsonDocument> Records(string script, string mode, string kind, TimeSpan timeout)
    {
        string prefix = $"{{\"k\":\"{kind}\"";
        foreach (string line in Lines(script, mode, timeout))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return JsonDocument.Parse(line);
            }
        }
    }

    /// <summary>The single record of a kind.</summary>
    public static JsonDocument One(string script, string mode, string kind, TimeSpan timeout) =>
        Records(script, mode, kind, timeout).Single();

    private static IReadOnlyList<string> Execute(string script, string mode, TimeSpan timeout)
    {
        if (!File.Exists(Python))
        {
            throw new FileNotFoundException(
                $"the project venv is missing ({Python}); create it with tools/setup (ROADMAP 4) - the oracle caller needs it");
        }

        string scriptPath = Path.Combine(GoldenData.RepoRoot, script.Replace('/', Path.DirectorySeparatorChar));
        var psi = new ProcessStartInfo(Python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = GoldenData.RepoRoot,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(mode);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {Python}");
        var stderr = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        p.BeginErrorReadLine();
        var lines = new List<string>();
        var watch = Stopwatch.StartNew();
        try
        {
            while (p.StandardOutput.ReadLine() is { } s)
            {
                lines.Add(s);
                if (watch.Elapsed > timeout)
                {
                    throw new TimeoutException($"{script} {mode} ran longer than {timeout}");
                }
            }

            if (!p.WaitForExit((int)Math.Max(1000, (timeout - watch.Elapsed).TotalMilliseconds)))
            {
                throw new TimeoutException($"{script} {mode} did not exit within {timeout}");
            }

            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                lock (stderr)
                {
                    throw new InvalidOperationException($"{script} {mode} exited with {p.ExitCode}: {stderr}");
                }
            }
        }
        finally
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit();
            }
        }

        return lines;
    }
}
