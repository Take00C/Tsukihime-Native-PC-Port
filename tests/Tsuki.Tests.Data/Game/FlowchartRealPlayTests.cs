using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Package K44 (issue #9) on the user's data: the prologue played from the title with "I'll stay in the classroom." (choice point 2) and "Someone else I played
/// with as a kid." (choice point 4). Both scenes end in the shared tail of the other option's scene; the PS4 keeps them the current node across that jump
/// (0xB9460), so their own <c>_RDED</c> completes them. v0.1.0..v0.1.4 left both dark in the gallery for good.
/// </summary>
public sealed class FlowchartRealPlayTests
{
    private const int StayInTheClassroom = 68;
    private const int Cafeteria = 67;
    private const int SomeoneElse = 57;
    private const int Makihisa = 55;

    private static IUiGalleryTables Tables => SharedSource.Source.Eboot.Gallery ?? throw new InvalidOperationException("no gallery tables");

    [GameDataFact]
    public void StayInTheClassroomAndSomeoneElseAreDoneOnceTheirSharedTailsEnd()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        FirstChoiceRig.RunToChoice(host);
        bool checkedInside = false;
        bool inside = false;
        host.MessageStarted += (script, _, _) => inside |= script == 9;
        for (int n = 0; n < 400_000 && !(host.ChoiceSelectable && host.ChoicePoint == 5); n++)
        {
            if (host.ChoiceSelectable)
            {
                Assert.True(host.Choose(host.ChoicePoint switch { 2 => 1, 4 => 2, _ => 0 }));
            }

            if (!checkedInside && inside)
            {
                // inside 01_00_ARC01_2_2: the current node, shown (its link 631 is set) as a black frame; the in-game chart can select it, the gallery cannot
                checkedInside = true;
                Assert.Equal(StayInTheClassroom, host.F(110));
                Assert.False(host.IsSceneDone(StayInTheClassroom));
                Assert.True(host.IsEdgeSeen(630) && host.IsEdgeSeen(631) && host.IsEdgeSeen(632));   // choice point 2 marked all three options when it opened
                var p = new HostProgress(host);
                FlowchartChart game = FlowchartChart.Build(Tables, Language.En, p, gallery: false);
                ChartNode node = Assert.Single(game.Nodes, x => x.Node == StayInTheClassroom);
                Assert.DoesNotContain(game.Sprites, s => s.Sheet == ChartSheet.Mosaic && s.Dst.X == node.X + 1f && s.Dst.Y == node.Y + 1f);
                Assert.DoesNotContain(FlowchartChart.Build(Tables, Language.En, p, gallery: true).Nodes, x => x.Node == StayInTheClassroom);
            }

            host.SetSkipLatch(true);
            host.Step(new InputFrame { Pressed = (n & 1) == 0 ? PadButtons.Decide : PadButtons.None });
        }

        Assert.True(checkedInside, "01_00_ARC01_2_2 was not played");
        Assert.True(host.ChoiceSelectable && host.ChoicePoint == 5, "the choice of 01_00_ARC01_5 was not reached");
        Assert.True(host.IsSceneDone(StayInTheClassroom));
        Assert.True(host.IsSceneDone(SomeoneElse));
        Assert.False(host.IsSceneDone(Cafeteria));
        Assert.False(host.IsSceneDone(Makihisa));
        Assert.True(host.IsEdgeSeen(634));              // 67 -> 66 after node 68: the edge 68 -> 66 (0xB9B7C)

        // the gallery's chart: both have their picture and replay
        FlowchartChart gallery = FlowchartChart.Build(Tables, Language.En, new HostProgress(host), gallery: true);
        foreach (int id in new[] { StayInTheClassroom, SomeoneElse })
        {
            ChartNode node = Assert.Single(gallery.Nodes, x => x.Node == id);
            Assert.Contains(gallery.Sprites, s => s.Sheet == ChartSheet.Mosaic && s.Dst.X == node.X + 1f && s.Dst.Y == node.Y + 1f);
        }
    }

    [GameDataFact]
    public void ThePrologueLightsItsOwnNodesAndTheHiddenOnesBetween()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        FirstChoiceRig.RunToChoice(host);
        // START_SCRIPT -> 00_00 (no node -> 486) goes through 485 and 528, 00_00 -> 01_00_ARC01_1 (486 -> 1) through 529 (0xB94F3, 0xB9A89)
        foreach (int n in new[] { 485, 486, 528, 529 })
        {
            Assert.True(host.IsSceneDone(n), $"node {n}");
        }

        foreach (int id in new[] { 1170, 1179, 1180, 1181 })
        {
            Assert.True(host.IsEdgeSeen(id), $"edge {id}");
        }

        Assert.Equal(1, host.F(110));
    }

    private sealed class HostProgress(GameHost host) : IUiProgress
    {
        public bool SceneSeen(int n) => host.IsSceneReached(n);

        public bool SceneDone(int n) => host.IsSceneDone(n);

        public bool EdgeSeen(int id) => host.IsEdgeSeen(id);

        public bool MovieSeen(int bit) => false;

        public bool PictureSeen(int seenIndex) => false;

        public bool BgmHeard(int track) => false;

        public bool RouteScene(int n) => n is >= 0 and <= 0x23F && (host.Vars.GetF(201 + (n >> 3)) >> (n & 7) & 1) != 0;

        public bool RouteEdge(int id) => id is >= 564 and <= 1267 && (host.Vars.GetF(600 + ((id - 564) >> 3)) >> ((id - 564) & 7) & 1) != 0;
    }
}
