using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Tests.Data.Stage;

/// <summary>
/// The layer stage against the engine itself. <c>engine_stage.py</c> runs a random scenario of layer statements and per-frame updates through the
/// PS4 code under emulation (the oracle's <c>Rig</c> with the PS4 <c>rand()</c> and <c>sinf</c>) and prints, per step, the changed scene-state bytes and
/// the changed drawn / committed records; this test runs the same scenario through <see cref="StageRuntime"/> and compares scene state, drawn
/// records A, committed records B, the draw-order slots and the <c>rand()</c> state after every step.
/// </summary>
public sealed class StageEngineTests
{
    private const int TrackedLayers = 12;

    [GameDataTheory]
    [InlineData(1, 90)]
    [InlineData(2, 90)]
    [InlineData(3, 90)]
    public void TheStageEqualsTheEngineOnARandomScenario(int seed, int statements) => RunScenario(seed, statements);

    /// <summary>Longer runs for soak testing: set <c>TSUKI_STAGE_SOAK</c> to <c>seed:statements</c> (for example <c>11:400</c>); passes at once without it.</summary>
    [GameDataFact]
    public void TheStageEqualsTheEngineOnASoakScenario()
    {
        string? spec = Environment.GetEnvironmentVariable("TSUKI_STAGE_SOAK");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return;
        }

        string[] p = spec.Split(':');
        RunScenario(int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void RunScenario(int seed, int statements)
    {
        StageRuntime rt = StageEngineRun.BaseRuntime(StageData.Catalog, StageData.Motions);
        rt.HazeTable = StageData.Haze;
        rt.Layouts = StageData.Layouts;
        StageEngineResult r = StageEngineRun.Compare(rt, $"{seed} {statements}", TrackedLayers, null, TimeSpan.FromSeconds(300));
        Assert.True(r.Failures.Count == 0, string.Join(Environment.NewLine, r.Failures));
        Assert.True(r.Steps > 20, $"only {r.Steps} steps were compared");
        Assert.True(r.Draws > 5 || r.Halted is not null, $"only {r.Draws} scene draws were compared");
        if (r.Halted is not null)
        {
            TestContext.Current.SendDiagnosticMessage($"seed {seed}: {r.Steps} steps compared, then the oracle stopped: {r.Halted}");
        }
    }
}
