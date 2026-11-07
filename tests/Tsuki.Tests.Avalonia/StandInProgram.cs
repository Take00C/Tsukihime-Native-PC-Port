using System.Diagnostics;
using System.Text;

namespace Tsuki.Tests.Avalonia;

/// <summary>
/// A tiny C program that sleeps for the seconds of its first argument, built once per test run with the system's cc into a temporary folder. macOS 13 and later
/// kill a copy of a system program (/bin/bash) run from outside the system volume (a launch constraint), so the tests that need a program named Tsukihime
/// running from their own folder copy this one there on a Mac. clang signs it ad hoc as it links, which an Apple-silicon Mac requires.
/// </summary>
internal static class StandInProgram
{
    /// <summary>Why the stand-in cannot be built here.</summary>
    public const string NoCompiler = "no C compiler (cc) to build the stand-in game: on macOS a copied /bin/bash is killed, so install the Xcode command line tools (xcode-select --install)";

    private const string Source = "#include <stdlib.h>\n#include <unistd.h>\nint main(int argc, char **argv) { sleep(argc > 1 ? (unsigned)atoi(argv[1]) : 30); return 0; }\n";

    private static readonly Lazy<string?> Built = new(Build);

    /// <summary>The built program, or null when this machine has no C compiler (on macOS: no Xcode command line tools).</summary>
    public static string? Executable => Built.Value;

    /// <summary>The built program; the test is skipped, with the reason, when there is no C compiler.</summary>
    public static string Require()
    {
        if (Executable is { } program) { return program; }
        Assert.Skip(NoCompiler);
        throw new UnreachableException();
    }

    private static string? Build()
    {
        if (OperatingSystem.IsWindows() || FindOnPath("cc") is not { } cc) { return null; }
        // macOS's /usr/bin/cc is there without the command line tools too: it then offers to install them and fails
        if (OperatingSystem.IsMacOS() && Run("/usr/bin/xcode-select", ["-p"]).Code != 0) { return null; }
        string folder = TestFolders.Temp("tsuki-standin-");
        Directory.CreateDirectory(folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        };
        string source = Path.Combine(folder, "standin.c");
        string program = Path.Combine(folder, "standin");
        File.WriteAllText(source, Source);
        (int code, string output) = Run(cc, ["-o", program, source]);
        if (code != 0 || !File.Exists(program)) { throw new InvalidOperationException($"{cc} could not build the stand-in game (exit {code}): {output}"); }
        return program;
    }

    private static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder, name))
        .FirstOrDefault(File.Exists);

    private static (int Code, string Output) Run(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        var output = new StringBuilder();
        using Process process = new() { StartInfo = start };
        DataReceivedEventHandler collect = (_, e) => { if (e.Data is { } line) { lock (output) { output.AppendLine(line); } } };
        process.OutputDataReceived += collect;
        process.ErrorDataReceived += collect;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            lock (output) { output.AppendLine("(stopped after 2 minutes)"); }
        }

        process.WaitForExit();
        lock (output) { return (process.ExitCode, output.ToString().Trim()); }
    }
}
