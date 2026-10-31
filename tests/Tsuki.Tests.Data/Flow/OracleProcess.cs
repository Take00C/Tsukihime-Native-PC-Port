using System.Diagnostics;

namespace Tsuki.Tests.Data.Flow;

/// <summary>
/// Runs a thin Python caller of the oracle (<c>tools/tsuki</c>) with the project venv and streams its stdout lines. The
/// caller writes nothing to disk; it prints JSON lines that the C# test compares one by one.
/// </summary>
internal static class OracleProcess
{
    /// <summary>The project venv's interpreter.</summary>
    public static string Python => Path.Combine(GoldenData.RepoRoot, ".venv", "Scripts", "python.exe");

    /// <summary>
    /// Runs <c>python -I script args</c> and calls <paramref name="line"/> for every stdout line; fails when the venv is
    /// missing, the script exits non-zero or runs longer than <paramref name="timeout"/>.
    /// </summary>
    public static void Run(string script, string args, TimeSpan timeout, Action<string> line)
    {
        if (!File.Exists(Python))
        {
            throw new FileNotFoundException(
                $"the project venv is missing ({Python}); create it with tools/setup (ROADMAP 4) - the oracle caller needs it");
        }

        var psi = new ProcessStartInfo(Python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = GoldenData.RepoRoot,
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(script);
        foreach (string a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(a);
        }

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
        var watch = Stopwatch.StartNew();
        try
        {
            while (p.StandardOutput.ReadLine() is { } s)
            {
                line(s);
                if (watch.Elapsed > timeout)
                {
                    throw new TimeoutException($"{script} ran longer than {timeout}");
                }
            }

            if (!p.WaitForExit((int)Math.Max(1000, (timeout - watch.Elapsed).TotalMilliseconds)))
            {
                throw new TimeoutException($"{script} did not exit within {timeout}");
            }

            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                lock (stderr)
                {
                    throw new InvalidOperationException($"{script} exited with {p.ExitCode}: {stderr}");
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
    }
}
