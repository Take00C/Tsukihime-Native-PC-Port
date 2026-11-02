using Tsuki.App.Services;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>Package K44 over the app services: a profile of v0.1.0..v0.1.4 gets the flowchart marks it lacks at start, once, straight into the system block.</summary>
public sealed class FlowchartRepairAppTests
{
    [Fact]
    public void AnOldProfileThatPlayedThePrologueGetsItsMarksAtStartOnce()
    {
        SaveStore store = AppRig.NewStore();
        store.SystemData.Block.SetG(SystemData.SceneSeenWord, 1 << 1);                    // node 1 started, node 485 not done: written before K44
        var rig = new AppRig(store: store);
        AppServices s = rig.Services;
        Assert.NotNull(s.FlowMarksRepaired);
        Assert.True(s.FlowMarksRepaired!.Value.Prologue);
        Assert.True(s.FlowMarksRepaired.Value.Bits > 0);
        Assert.NotEqual(0, store.SystemData.Block.GetG(SystemData.SceneDoneWord + (485 >> 4)) & (1 << (485 & 15)));   // captured at once

        var next = new AppRig(store: store);
        Assert.Null(next.Services.FlowMarksRepaired);
    }

    [Fact]
    public void AFreshProfileNeedsNoRepair()
    {
        Assert.Null(new AppRig().Services.FlowMarksRepaired);
    }
}
