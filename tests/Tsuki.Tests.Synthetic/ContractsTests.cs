using System.Collections.Immutable;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic;

/// <summary>Small invariants of the contracts in Tsuki.Engine/Contracts (ARCHITECTURE 3.2).</summary>
public sealed class ContractsTests
{
    [Fact]
    public void CommandNameKeyMatchesTheEngineWorkedExample()
    {
        // engine.md 5.2: _WKST -> c0 = 'W', key = 'K' << 16 | 'S' << 8 | 'T' = 0x4B5354.
        var name = CommandName.FromAscii("WKST");
        Assert.Equal((byte)'W', name.First);
        Assert.Equal(0x4B5354u, name.Key);
        Assert.Equal("WKST", name.ToString());
        Assert.Equal(name, CommandName.FromBytes("WKST(F823,eq,0);"u8));
    }

    [Fact]
    public void StepResultDefaultsResumeAfterTheStatement()
    {
        Assert.Equal(StepKind.Continue, StepResult.Continue.Kind);
        Assert.Equal(StepKind.Yield, StepResult.Yield.Kind);
        Assert.Equal(StepKind.Halt, StepResult.Halt.Kind);
        Assert.False(StepResult.Yield.HasTarget);
        var jump = StepResult.YieldAt(new ScriptPosition(3, 0x120));
        Assert.True(jump.HasTarget);
        Assert.Equal(new ScriptPosition(3, 0x120), jump.Target);
    }

    [Fact]
    public void StatementPositions()
    {
        var s = new Statement(2, 10, 27, CommandName.FromAscii("WTTM"), "(500);"u8.ToArray());
        Assert.Equal(new ScriptPosition(2, 10), s.Position);
        Assert.Equal(new ScriptPosition(2, 27), s.Next);
        Assert.Equal(0x300, Statement.MaxSegmentBytes);
        Assert.Equal(1000, Statement.SegmentsPerStep);
    }

    [Fact]
    public void ScriptImageSegmentEnds()
    {
        var image = new ScriptImage(0, "_A;_B;_C;"u8.ToArray(), [0, 3, 6]);
        Assert.Equal(3, image.SegmentEnd(0));
        Assert.Equal(6, image.SegmentEnd(1));
        Assert.Equal(9, image.SegmentEnd(2));
        Assert.Throws<ArgumentException>(() => new ScriptImage(0, ReadOnlyMemory<byte>.Empty, default(ImmutableArray<int>)));
    }

    [Fact]
    public void DisplayListIsImmutableOnceSealed()
    {
        var list = new DisplayList(capacity: 1);
        list.Reset(frame: 7);
        list.Add(new DrawCmd { Kind = DrawKind.FillQuad });
        list.Add(new DrawCmd { Kind = DrawKind.TexturedQuad, Texture = TextureRef.ForCapture(15) });
        list.Seal();
        Assert.Equal(7, list.Frame);
        Assert.Equal(2, list.Count);
        Assert.Equal(TextureKind.Capture, list.Commands[1].Texture.Kind);
        Assert.Throws<InvalidOperationException>(() => list.Add(default));
    }

    [Fact]
    public void DisplayListBufferPublishesTheNewestList()
    {
        var buffer = new DisplayListBuffer();
        Assert.True(buffer.Front.IsSealed);
        Assert.Equal(0, buffer.Front.Count);

        var back = buffer.BeginFrame(1);
        back.Add(new DrawCmd { Kind = DrawKind.Capture, Texture = TextureRef.ForCapture(0) });
        buffer.Publish();
        Assert.Same(back, buffer.Front);
        Assert.True(buffer.Front.IsSealed);
        Assert.Equal(1, buffer.Front.Frame);

        var next = buffer.BeginFrame(2);
        Assert.NotSame(back, next);
        Assert.Equal(1, buffer.Front.Count);
    }

    [Fact]
    public void BuildInfoRecognisesTheAnalysedEboot()
    {
        Assert.True(new BuildInfo("x", BuildInfo.AnalysedEbootSize, BuildInfo.AnalysedEbootSha256).IsAnalysedBuild);
        Assert.False(new BuildInfo("x", BuildInfo.AnalysedEbootSize - 1, BuildInfo.AnalysedEbootSha256).IsAnalysedBuild);
        Assert.Equal(64, BuildInfo.AnalysedEbootSha256.Length);
    }

    [Fact]
    public void AudioStreamsAndLoops()
    {
        Assert.Equal(6, AudioStreams.Se(0));
        Assert.Equal(25, AudioStreams.Se(AudioStreams.SeChannelCount - 1));
        Assert.Equal(AudioStreams.Count, AudioStreams.Se(AudioStreams.SeChannelCount));
        // ARCHITECTURE 7.2: bgm #33 loops over [962000, 8257999], end inclusive.
        Assert.Equal(8_257_999 - 962_000 + 1, new AudioLoop(962_000, 8_257_999).Length);
        var play = AudioCommand.Play(AudioStreams.Bgm0, AudioBank.Bgm, 33, loop: true, volume: 0.13f);
        Assert.Equal((AudioOp.Play, 1, 0.13f), (play.Op, play.Arg0, play.Value0));
    }

    [Fact]
    public void InputEdgesAreNewlyPressedBits()
    {
        var previous = PadButtons.Decide | PadButtons.L1;
        var held = PadButtons.Decide | PadButtons.R2;
        Assert.Equal(PadButtons.R2, InputFrame.Edges(previous, held));
    }
}
