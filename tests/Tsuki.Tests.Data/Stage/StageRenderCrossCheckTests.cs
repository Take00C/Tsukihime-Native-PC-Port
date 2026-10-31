using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Render.Compositor;

namespace Tsuki.Tests.Data.Stage;

/// <summary>
/// The stage builds its own transition commands (the engine project cannot reference the renderer); <see cref="FadsCommands"/>, <see cref="Fads"/> and
/// <see cref="DrawEffect"/> of the renderer are the other implementation of the same rules (compositing.md 5, 6, 7). The two must agree: every
/// transition type, the per-frame progress, the blend state of every draw word.
/// </summary>
public sealed class StageRenderCrossCheckTests
{
    private static StageRuntime NewStage() => new(new ArrayPictureCatalog([], []), new ArrayMotionTables([], [], [], [], [], []));

    [Fact]
    public void TheBlendStateOfEveryDrawWordEqualsTheRenderer()
    {
        for (int word = 0; word < 0x400; word++)
        {
            Assert.True(DrawEffect.BlendStateOf(word) == EffectTables.BlendStateOf(word), $"word 0x{word:X}: renderer {DrawEffect.BlendStateOf(word)} / stage {EffectTables.BlendStateOf(word)}");
        }

        foreach (int word in new[] { 0x1FF, 0x2000, 0x4033, 0xFFFF, -1, 0x7FFFFFFF })
        {
            Assert.Equal(DrawEffect.BlendStateOf(word), EffectTables.BlendStateOf(word));
        }
    }

    [Fact]
    public void TheProgressStepOfATransitionEqualsTheRenderer()
    {
        for (int kind = 0; kind <= 72; kind++)
        {
            foreach (int ms in new[] { -5, 0, 1, 16, 17, 100, 150, 333, 500, 1000, 1200, 1500, 2766, 5000 })
            {
                Assert.True(
                    BitConverter.SingleToUInt32Bits(Fads.Step(ms, kind)) == BitConverter.SingleToUInt32Bits(StageRuntime.FadsStep(ms, kind)),
                    $"type {kind} {ms} ms: renderer {Fads.Step(ms, kind):R} / stage {StageRuntime.FadsStep(ms, kind):R}");
            }
        }
    }

    [Fact]
    public void TheCommandsOfEveryTransitionTypeEqualTheRenderer()
    {
        int compared = 0;
        for (int kind = 0; kind <= 70; kind++)
        {
            foreach ((int ms, int rule) in new[] { (200, 0), (1000, 0), (1500, 3), (60, 0), (2766, 1) })
            {
                StageRuntime rt = NewStage();
                Assert.True(rt.Execute(StageData.Parse($"_FADS({kind},{ms},{rule});")));
                rt.Update(1);
                if (!rt.TransitionActive)
                {
                    continue;
                }

                // what the stage uses as rule picture: allpac3 entry 85 + n (compositing.md 7.4)
                TextureRef? ruleTexture = kind is 2 or 69 && rule > 0 ? TextureRef.ForPictureTile(4, 85 + rule, 0) : null;
                float step = Fads.Step(ms, kind);
                float progress = 1f;
                bool first = true;
                for (int frame = 0; frame < 400 && rt.TransitionActive; frame++)
                {
                    var got = new DisplayList();
                    ulong randState = rt.Rng.State;                    // type 20 draws one rand() per scan line: both sides start from the same state
                    StageDrawInfo info = rt.Draw(got);
                    var want = new DisplayList();
                    if (first)
                    {
                        FadsCommands.Begin(want, kind);
                        first = false;
                    }

                    TransitionParams? parameters = null;
                    try
                    {
                        parameters = FadsCommands.Frame(want, kind, progress, ruleTexture, rand: new Ps4Rand(randState).Next);
                    }
                    catch (NotSupportedException)
                    {
                        // no type throws any more: the engine screens are drawn by TransitionScreens
                    }

                    string where = $"type {kind} {ms} ms rule {rule} frame {frame}";
                    Assert.True(BitConverter.SingleToUInt32Bits(progress) == BitConverter.SingleToUInt32Bits(rt.TransitionProgress), $"{where}: progress {progress:R} / stage {rt.TransitionProgress:R}");
                    Assert.True(want.Count == got.Count, $"{where}: renderer {want.Count} commands / stage {got.Count}");
                    for (int i = 0; i < want.Count; i++)
                    {
                        Assert.True(want.Commands[i].Equals(got.Commands[i]), $"{where}: command {i}: renderer {want.Commands[i]} / stage {got.Commands[i]}");
                    }

                    if (parameters is { } p)
                    {
                        Assert.Equal(ruleTexture, info.RuleTexture);
                        Assert.Equal(progress, info.RuleProgress);
                        Assert.Equal(TransitionParams.ForRule(progress), p);
                    }
                    else
                    {
                        Assert.Null(info.RuleTexture);
                    }

                    compared++;
                    rt.Update(1);
                    progress += step;
                    if (progress <= 0f)
                    {
                        break;
                    }
                }

                Assert.False(rt.TransitionActive, $"type {kind} {ms} ms: the transition ends when the progress reaches 0");
            }
        }

        Assert.True(compared > 2000, $"only {compared} transition frames were compared");
    }
}
