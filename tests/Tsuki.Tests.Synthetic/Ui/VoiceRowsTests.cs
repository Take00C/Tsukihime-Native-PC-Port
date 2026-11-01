using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// Package K36: the Sound tab's character rows (PS4 VA 0x3DF20): a row shows once its row word has bit 4, Shiki's face and name follow the script and the
/// progress, the face and name bits of the other rows, the rows ignore Play All Voices, and a row's slider changes that character's volume.
/// </summary>
public sealed class VoiceRowsTests
{
    private static int Word(int row) => UiTestKit.Data().Eboot.Ui.VoiceRowWords[row];

    private static (UiRoot Root, PreviewServices Services, ConfigScreen Screen) OpenSound(Action<PreviewServices> tweak, bool fromTitle = false)
    {
        var services = new PreviewServices(Language.En);
        tweak(services);
        UiRoot root = UiTestKit.Root(services);
        ConfigScreen screen;
        if (fromTitle)
        {
            root.Push(new TitleScreen());
            UiTestKit.Run(root, 30);
            screen = root.Push(new ConfigScreen());
        }
        else
        {
            root.OpenSystemMenu();
            UiTestKit.Run(root, 20);
            root.Context.MenuTab = 2;
            UiTestKit.Press(root, PadButtons.Decide);
            UiTestKit.Run(root, 60);
            screen = Assert.IsType<ConfigScreen>(root.Top);
        }

        UiTestKit.Run(root, 60);
        UiTestKit.Press(root, PadButtons.R1);
        UiTestKit.Run(root, 40);
        Assert.Equal(1, screen.CurrentTab);
        return (root, services, screen);
    }

    [Theory]
    [InlineData(1, 19, 0)]          // 00_00: the unknown face, his own name
    [InlineData(2, 18, 19)]         // 00_01, 00_02: unknown face and name
    [InlineData(3, 18, 19)]
    [InlineData(4, 0, 0)]           // the story proper
    [InlineData(300, 0, 0)]
    public void ShikisRowInAStoryFollowsTheScript(int script, int face, int name)
    {
        (_, _, ConfigScreen screen) = OpenSound(s =>
        {
            s.SetGlobal(Word(0), 4);
            s.Phase = 1;
            s.Script = script;
        });
        Assert.Equal([(0, face, name)], screen.VoiceRows);
    }

    [Fact]
    public void ShikisRowHidesWhileThePhaseIsZeroInAStory()
    {
        (_, _, ConfigScreen screen) = OpenSound(s =>
        {
            s.SetGlobal(Word(0), 4);
            s.SetGlobal(Word(3), 4);
            s.Phase = 0;
            s.Script = 4;
        });
        Assert.Equal([3], screen.VoiceRows.Select(r => r.Row));
    }

    [Theory]
    [InlineData(false, false, 18, 19)]      // the prologue movie (node 485) not reached: unknown face and name
    [InlineData(true, false, 19, 0)]        // reached, node 1 not: his name, the unknown face
    [InlineData(true, true, 0, 0)]
    public void ShikisRowFromTheTitleFollowsTheFlowchartNodes(bool node485, bool node1, int face, int name)
    {
        (_, _, ConfigScreen screen) = OpenSound(s =>
        {
            s.SetGlobal(Word(0), 4);
            s.Phase = 0;                                   // the phase does not matter from the title
            s.Script = -1;
            s.SetSceneBit(485, node485);
            s.SetSceneBit(1, node1);
        }, fromTitle: true);
        Assert.Equal([(0, face, name)], screen.VoiceRows);
    }

    [Fact]
    public void TheFaceAndNameBitsOfTheOtherRows()
    {
        (_, _, ConfigScreen screen) = OpenSound(s =>
        {
            s.SetGlobal(Word(3), 4);        // Akiha: in mask 0x430C9, her own name at once
            s.SetGlobal(Word(15), 4);       // rows 15 / 16: the unknown face until bit 1, the unknown name until bit 2
            s.SetGlobal(Word(16), 5);
            s.SetGlobal(Word(17), 4 | 2);   // outside the mask: its own name with bit 2 (its face never hides)
            s.SetGlobal(Word(1), 2);        // bit 2 without bit 4: no row
        });
        Assert.Equal([(3, 3, 3), (15, 18, 19), (16, 16, 19), (17, 17, 17)], screen.VoiceRows);
    }

    [Fact]
    public void TheRowsDoNotDependOnPlayAllVoices()
    {
        (_, PreviewServices s, ConfigScreen screen) = OpenSound(x =>
        {
            x.SetGlobal(Word(1), 4);
            x.Config.PlayAllVoices = 0;
        });
        Assert.Equal([1], screen.VoiceRows.Select(r => r.Row));
        Assert.Equal(0, s.Config.PlayAllVoices);
    }

    [Fact]
    public void ARowsPlusStepsThatCharactersVolumeAndTheGameHearsOfIt()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = OpenSound(x => x.SetGlobal(Word(1), 4));
        Assert.Equal(50, s.Config.GetCharacterVoiceVolume(1));
        (Ctl _, float x, float y) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Plus, 3 + 1));
        UiTestKit.Click(root, x + 12f, y + 12f);
        UiTestKit.Run(root, 5);
        Assert.Equal(51, s.Config.GetCharacterVoiceVolume(1));
        Assert.Equal(50, s.Config.GetCharacterVoiceVolume(0));
        Assert.Contains("config Volume", s.Calls);
        (Ctl _, float tx, float ty) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Test, 1 + 1));
        UiTestKit.Click(root, tx + 12f, ty + 12f);
        UiTestKit.Run(root, 5);
        Assert.Contains("test voice 1", s.Calls);
    }
}
