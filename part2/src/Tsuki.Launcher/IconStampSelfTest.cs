// The stamp self-test (package K43): `TsukihimeLauncher.exe --stamp-selftest FILE --data DIR [--game DIR] [--stamp-off]`, no window. It runs the window's own path on
// the launcher's own folder (the switch of Help & about set as asked, then the refresh of the game folder that builds the Arcueid icon into the profile DIR and
// starts the stamp of the program icons), then checks the result and writes ok / FAIL lines to FILE: what each program carried before and after, that both are
// still the apphosts they were (headers, sections, version block, manifest, the .dll files untouched, still a release and not a personal build), that a second
// run writes nothing, which copies are left, and that the stamped programs start (Tsukihime.exe --help, TsukihimeLauncher.exe --selftest, both without a window).
// It changes the programs of the folder it runs from: run it on a COPY of a release folder.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tsuki.Data.Icons;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>The verification mode of the program-icon stamp.</summary>
internal static class IconStampSelfTest
{
    /// <summary>What one program was before the run.</summary>
    private sealed record Snapshot(string Sha256, DateTime WriteTime, long Length, IReadOnlyList<IcoImage>? Icon, byte[]? Version, byte[]? Manifest);

    /// <summary>Runs the stamp the window runs, asking for Arcueid (<paramref name="arcueid"/>) or the crescent, and returns the report (a FAIL line for each check that did not hold).</summary>
    /// <param name="model">The view model over the temporary profile.</param>
    /// <param name="paths">The temporary profile (<c>--data</c>).</param>
    /// <param name="arcueid">True for Arcueid (the default), false for the crescent (<c>--stamp-off</c>).</param>
    /// <param name="reportPath">The report file (the launcher's own self-test writes beside it).</param>
    public static async Task<string> RunAsync(LauncherModel model, LauncherPaths paths, bool arcueid, string reportPath)
    {
        var report = new StringBuilder();
        void Check(string what, bool ok) => report.AppendLine((ok ? "ok    " : "FAIL  ") + what);
        void Info(string what) => report.AppendLine("info  " + what);
        string app = AppContext.BaseDirectory;
        bool personal = model.Build.IsPersonal;
        Check($"launcher {model.Build.InformationalVersion} runs from {app} (personal build: {personal}); asked for {(arcueid ? LauncherModel.ArcueidName : LauncherModel.CrescentName)}", true);

        byte[]? crescentIco = LauncherModel.CrescentIco();
        IReadOnlyList<IcoImage>? crescent = crescentIco is null ? null : IcoFile.Read(crescentIco);
        Check($"the launcher's own crescent icon reads ({crescent?.Count.ToString(CultureInfo.InvariantCulture) ?? "no"} images)", crescent is not null);
        var known = new List<IconChoice>();
        if (crescent is not null)
        {
            known.Add(new IconChoice(LauncherModel.CrescentName, crescent));
        }

        var before = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (string program in ExeIconStamp.Programs)
        {
            string exe = Path.Combine(app, program);
            Check($"{program} is in the folder", File.Exists(exe));
            if (File.Exists(exe))
            {
                before[program] = Take(exe);
            }
        }

        string[] dlls = [.. ExeIconStamp.Programs.Select(p => Path.Combine(app, Path.ChangeExtension(p, ".dll"))).Where(File.Exists)];
        Dictionary<string, string> dllsBefore = dlls.ToDictionary(d => d, ReleaseSums.Sha256Hex, StringComparer.OrdinalIgnoreCase);

        // the window's path: the switch as asked (written to the temporary profile), then the refresh that reads the game folder and starts the stamp
        model.ShowArcueidOnPrograms = arcueid;
        model.StampProgramIcons = true;
        var clock = Stopwatch.StartNew();
        await model.RefreshAsync();
        await model.ProgramIconsIdle;
        Info($"the refresh and the stamp took {clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms (the window does not wait for the stamp)");
        Check($"settings.ini of the profile says arcueid_icon={(arcueid ? "on (or nothing: on is the default)" : "off")}", personal || (Tsuki.App.Services.AppSettings.Load(paths.SettingsFile).ArcueidIcon ?? true) == arcueid);

        string? facePath = model.GameIconPath;
        IReadOnlyList<IcoImage>? face = facePath is null ? null : IcoFile.ReadFile(facePath);
        if (face is not null)
        {
            known.Add(new IconChoice(LauncherModel.ArcueidName, face));
        }

        // what the programs carried before the run, named now that the Arcueid icon of the game folder is known
        foreach ((string program, Snapshot was) in before)
        {
            Info($"before: {program}: {ExeIconStamp.Describe(was.Icon, known)}, {was.Length.ToString(CultureInfo.InvariantCulture)} bytes, SHA-256 {was.Sha256[..16]}...");
        }

        if (arcueid)
        {
            Check($"the Arcueid icon was built from the game folder into the profile ({facePath ?? "none: pass --game DIR or set TSUKI_GAME_DIR"}; {face?.Count.ToString(CultureInfo.InvariantCulture) ?? "no"} images, {(face is null ? "-" : IcoFile.Sizes(face))})", face is not null);
        }

        IReadOnlyList<IcoImage>? wanted = arcueid ? face : crescent;
        string wantedName = arcueid ? LauncherModel.ArcueidName : LauncherModel.CrescentName;
        foreach (string program in before.Keys)
        {
            string exe = Path.Combine(app, program);
            Snapshot was = before[program];
            Snapshot now = Take(exe);
            Info($"after:  {program}: {ExeIconStamp.Describe(now.Icon, known)}, {now.Length.ToString(CultureInfo.InvariantCulture)} bytes, SHA-256 {now.Sha256[..16]}...");
            if (personal)
            {
                Check($"{program} was not written (a personal build carries her from the build)", now.Sha256 == was.Sha256);
                continue;
            }

            Check($"{program} carries {wantedName}", IcoFile.SameImages(now.Icon, wanted));
            Check($"{program} is still a plain apphost with its .dll beside it ({ExeIconStamp.ApphostProblem(exe) ?? "no problem"})", ExeIconStamp.ApphostProblem(exe) is null);
            Check($"{program}'s version block and manifest are the ones it had", Same(now.Version, was.Version) && Same(now.Manifest, was.Manifest));
        }

        Check("the .dll files are untouched (the build's version and marks)", dlls.All(d => ReleaseSums.Sha256Hex(d) == dllsBefore[d]));
        string launcherDll = Path.Combine(app, Path.ChangeExtension(GameLauncher.LauncherFileName, ".dll"));
        AssemblyMarks? marks = File.Exists(launcherDll) ? PeIcons.Marks(launcherDll) : null;
        bool releaseMarks = marks is not null && !marks.IsPersonalBuild && !(marks.InformationalVersion?.Contains("-personal", StringComparison.OrdinalIgnoreCase) ?? false)
            && !File.Exists(Path.Combine(app, UpdateInstaller.PersonalNote));
        if (!personal)
        {
            Check($"the folder still counts as a release for the updater (TsukihimeLauncher.dll {marks?.InformationalVersion ?? "unreadable"}, no personal mark, no {UpdateInstaller.PersonalNote})", releaseMarks);
        }

        // a second run (the next start) writes nothing
        Dictionary<string, Snapshot> first = before.Keys.ToDictionary(p => p, p => Take(Path.Combine(app, p)), StringComparer.OrdinalIgnoreCase);
        await model.StampIconsAsync("self-test, second run");
        foreach (string program in first.Keys)
        {
            Snapshot again = Take(Path.Combine(app, program));
            Check($"a second run left {program} as it was (the same SHA-256 and time)", again.Sha256 == first[program].Sha256 && again.WriteTime == first[program].WriteTime);
        }

        // what is left beside the programs
        string[] copies = Directory.GetFiles(app, "*" + ExeIconStamp.NewSuffix);
        Check($"no {ExeIconStamp.NewSuffix} copy is left ({copies.Length.ToString(CultureInfo.InvariantCulture)})", copies.Length == 0);
        string[] olds = [.. Directory.GetFiles(app, "*" + ExeIconStamp.OldSuffix).Select(f => Path.GetFileName(f))];
        Check(
            olds.Length == 0
                ? $"no {ExeIconStamp.OldSuffix} copy is left"
                : $"left: {string.Join(", ", olds)}: the image of the launcher that runs this test (renamed, the stamped one moved in), deleted at the next start",
            olds.All(o => o.StartsWith(GameLauncher.LauncherFileName, StringComparison.OrdinalIgnoreCase)));

        // the stamped programs start (no window: the game prints its usage and ends; the launcher runs its own self-test on a profile of its own)
        string game = Path.Combine(app, GameLauncher.AppFileName);
        if (File.Exists(game))
        {
            (int code, string output) = Run(game, ["--help"], TimeSpan.FromSeconds(60));
            Check($"the stamped {GameLauncher.AppFileName} starts and ends (--help: exit {code.ToString(CultureInfo.InvariantCulture)}, usage printed: {output.Contains("usage: Tsukihime", StringComparison.Ordinal)})", code == 0 && output.Contains("usage: Tsukihime", StringComparison.Ordinal));
        }

        string launcher = Path.Combine(app, GameLauncher.LauncherFileName);
        if (File.Exists(launcher))
        {
            string childReport = reportPath + ".launcher.txt";
            string childProfile = Path.Combine(paths.DataDirectory, "launcher-selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            (int code, _) = Run(launcher, ["--selftest", childReport, "--data", childProfile], TimeSpan.FromSeconds(120));
            string child = File.Exists(childReport) ? File.ReadAllText(childReport) : "(no report)";
            Check($"the stamped {GameLauncher.LauncherFileName} starts and passes its own --selftest (exit {code.ToString(CultureInfo.InvariantCulture)}, {child.Split('\n').Count(l => l.StartsWith("ok", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)} ok lines; {childReport})",
                code == 0 && !child.Contains("FAIL", StringComparison.Ordinal));
        }

        foreach (string line in model.Log.Lines().Where(l => l.Contains("program icons", StringComparison.Ordinal)))
        {
            Info("launcher.log: " + line);
        }

        return report.ToString();
    }

    private static Snapshot Take(string exe)
    {
        var info = new FileInfo(exe);
        return new Snapshot(
            ReleaseSums.Sha256Hex(exe),
            info.LastWriteTimeUtc,
            info.Length,
            ExeIconStamp.ReadIcon(exe),
            PeIcons.ResourceData(exe, PeIcons.VersionType, 1, 0),
            PeIcons.ResourceData(exe, PeIcons.ManifestType, 1, 0));
    }

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>Starts <paramref name="exe"/> without a window and without the player's game folder, waits for it and returns its exit code and what it printed.</summary>
    private static (int Code, string Output) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        info.Environment.Remove("TSUKI_GAME_DIR");
        try
        {
            using Process p = Process.Start(info) ?? throw new InvalidOperationException("no process");
            Task<string> output = p.StandardOutput.ReadToEndAsync();
            Task<string> error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                p.Kill(entireProcessTree: true);
                return (-1, "timed out");
            }

            return (p.ExitCode, output.Result + error.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, e.Message);
        }
    }
}
