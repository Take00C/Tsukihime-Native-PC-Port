using System.Diagnostics;
using Tsuki.Cli.Commands;

namespace Tsuki.Tests.Data.Cli;

public sealed class MacPackagingAgreementTests
{
    [Fact]
    public void ThePythonPackagerAndCSharpProduceExactlyTheSameIcnsBytes()
    {
        string root = RepoRoot();
        // python3 first (macOS and Linux), then python (Windows); Windows' Store alias "python" exits 9009 when no Python is installed
        foreach (string program in new[] { "python3", "python" })
        {
            if (RunIcnsWriter(program, root) is not { } run) { continue; }
            Assert.True(run.Exited, "the Python ICNS writer timed out");
            Assert.True(run.ExitCode == 0, run.Error);
            Assert.Equal(run.Output, MacRelease.Icns());
            return;
        }

        Assert.Skip("python is not installed; CI runs the packaging agreement test");
    }

    private sealed record IcnsRun(bool Exited, int ExitCode, byte[] Output, string Error);

    private static IcnsRun? RunIcnsWriter(string program, string root)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import pathlib,runpy,sys; m=runpy.run_path(sys.argv[1]); sys.stdout.buffer.write(m['make_icns'](pathlib.Path(sys.argv[2])))");
        start.ArgumentList.Add(Path.Combine(root, "tools", "setup", "package_macos.py"));
        start.ArgumentList.Add(Path.Combine(root, "assets", "icon"));
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("python could not start");
            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            string error = process.StandardError.ReadToEnd();
            bool exited = process.WaitForExit(30_000);
            if (exited && process.ExitCode == 9009 && OperatingSystem.IsWindows()) { return null; }
            return new IcnsRun(exited, exited ? process.ExitCode : -1, output.ToArray(), error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Tsuki.slnx"))) { return directory.FullName; }
        }

        throw new InvalidOperationException("repository root not found");
    }
}
