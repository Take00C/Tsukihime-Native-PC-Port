using System.Text;
using Tsuki.App.Play;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K23: the one plain-text producer behind the clipboard, the GDI hook point and the WebSocket, over the hand-made game: markup and ruby gone, the speaker's
/// plate above a message when the game shows one, a choice list one line per choice, each output with its own switch, the hook point fed through an injectable sink.
/// </summary>
public sealed class TextFeedTests
{
    private sealed class ListOutput(string name, Func<bool> enabled) : ITextOutput
    {
        public List<(TextKind Kind, string Text)> Received { get; } = [];

        public int Ticks { get; private set; }

        public bool Refuse { get; set; }

        public string Name => name;

        public bool Enabled => enabled();

        public void Tick() => Ticks++;

        public bool Send(TextKind kind, string text)
        {
            if (Refuse)
            {
                return false;
            }

            Received.Add((kind, text));
            return true;
        }
    }

    private sealed class ListHookSink : ITextHookSink
    {
        public List<string> Lines { get; } = [];

        public bool Emit(string text)
        {
            Lines.Add(text);
            return true;
        }
    }

    private const string ChoiceScript =
        "_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);_SELR(0,$000003);_SELR(1,$000004);_SELR(2,$000005);_SELD(_D000,5);_GOTS(_D000,A,B,C);"
        + "_ZZ00101(A);_WKST(F10,eq,10);_END_();_ZZ00201(B);_WKST(F10,eq,20);_END_();_ZZ00301(C);_WKST(F10,eq,30);_END_();";

    private static void Run(AppRig rig, TextFeed feed, int frames, bool click = false)
    {
        for (int i = 0; i < frames; i++)
        {
            rig.Step(click && i % 3 == 0);
            feed.Step(rig.Host);
        }
    }

    [Fact]
    public void TheProducerDropsMarkupAndRubyJoinsLineBreaksAndPutsTheNamePlateAboveTheText()
    {
        Assert.Equal("Hello there", PlainText.Message("Hello^there"u8, Language.En));
        Assert.Equal("こんにちは世界", PlainText.Message("こんにちは^世界"u8, Language.Ja));
        Assert.Equal("漢字を読む", PlainText.Message("<漢字|かんじ>を読む"u8, Language.Ja));
        string waited = PlainText.Message("Slow@w30 words"u8, Language.En);
        Assert.DoesNotContain("@w", waited, StringComparison.Ordinal);                             // the wait code is gone (its digits are the parser's business, as in K21)
        Assert.StartsWith("Slow", waited, StringComparison.Ordinal);
        Assert.EndsWith("words", waited, StringComparison.Ordinal);
        Assert.Equal(string.Empty, PlainText.Message([], Language.Zc));

        Assert.Equal("Arcueid\nHello", PlainText.WithName("Arcueid"u8, "Hello", Language.En));
        Assert.Equal("アルクェイド\nこんにちは", PlainText.WithName("<アルクェイド|あるくぇいど>"u8, "こんにちは", Language.Ja));      // the plate's ruby goes too
        Assert.Equal("Hello", PlainText.WithName([], "Hello", Language.En));

        string choices = PlainText.Choices([Encoding.UTF8.GetBytes("1. Go^home"), Encoding.UTF8.GetBytes(""), Encoding.UTF8.GetBytes("2. Wait")], Language.En);
        Assert.Equal("1. Go home\n2. Wait", choices);
        string japanese = PlainText.Choices([Encoding.UTF8.GetBytes("1. <待つ|まつ>"), Encoding.UTF8.GetBytes("2. 帰る^か")], Language.Ja);      // the ruby reading dropped, the break joined with nothing
        Assert.Equal("1. 待つ\n2. 帰るか", japanese);
    }

    [Fact]
    public void EveryOutputThatIsOnGetsEachNewMessageOnceAndTheOthersNothing()
    {
        var rig = new AppRig(AppRig.ThreePages);
        bool clipOn = false;
        var clip = new ListOutput("clipboard", () => clipOn);
        var hook = new ListOutput("hooker", () => true);
        var socket = new ListOutput("websocket", () => false);
        var feed = new TextFeed(() => rig.Services.Language);
        feed.Add(clip);
        feed.Add(hook);
        feed.Add(socket);
        rig.Services.StartScene(100);
        Run(rig, feed, 60);
        Assert.Equal([(TextKind.Message, "alpha line")], hook.Received);
        Assert.Empty(clip.Received);
        Assert.Empty(socket.Received);
        Assert.Equal(1, feed.Produced);
        Assert.Equal("alpha line", feed.LastText);
        Assert.True(hook.Ticks >= 60 && clip.Ticks == hook.Ticks && socket.Ticks == hook.Ticks);     // every output is ticked each step, on or off

        clipOn = true;
        Run(rig, feed, 200, click: true);
        Assert.Contains((TextKind.Message, "beta line"), hook.Received);
        Assert.Contains((TextKind.Message, "beta line"), clip.Received);
        Assert.DoesNotContain(clip.Received, r => r.Text == "alpha line");                              // the page shown while it was off is not sent to it later
        Assert.Empty(socket.Received);
        Assert.Equal(hook.Received.Count, hook.Received.Distinct().Count());
        Assert.Equal(hook.Received.Count, feed.Produced);
    }

    [Fact]
    public void WithEverySwitchOffTheFeedFollowsThePageSoThatSwitchingOnSendsTheNextMessageOnly()
    {
        var rig = new AppRig(AppRig.ThreePages);
        bool on = false;
        var output = new ListOutput("hooker", () => on);
        var feed = new TextFeed(() => rig.Services.Language);
        feed.Add(output);
        rig.Services.StartScene(100);
        Run(rig, feed, 60);
        Assert.Empty(output.Received);
        Assert.Equal(0, feed.Produced);
        on = true;
        Run(rig, feed, 30);
        Assert.Empty(output.Received);
        Run(rig, feed, 200, click: true);
        Assert.Equal("beta line", output.Received[0].Text);
    }

    [Fact]
    public void AChoiceListGoesOutAsItsLinesAndTheSpeakersPlateAboveAMessage()
    {
        var rig = new AppRig(ChoiceScript);
        var output = new ListOutput("hooker", () => true);
        var feed = new TextFeed(() => rig.Services.Language);
        feed.Add(output);
        rig.Services.StartScene(100);
        Run(rig, feed, 60);
        Assert.Equal([(TextKind.Message, "alpha line")], output.Received);
        Run(rig, feed, 90, click: true);
        for (int i = 0; i < 120 && !rig.Host.ChoiceOpen; i++)
        {
            Run(rig, feed, 1);
        }

        Assert.True(rig.Host.ChoiceOpen);
        Run(rig, feed, 5);
        (TextKind kind, string lines) = Assert.Single(output.Received.Skip(1));
        Assert.Equal(TextKind.Choice, kind);
        string[] parts = lines.Split('\n');
        Assert.Equal(3, parts.Length);
        Assert.EndsWith("beta line", parts[0], StringComparison.Ordinal);
        Assert.EndsWith("delta line", parts[2], StringComparison.Ordinal);

        // a plate the game shows goes above the next message (retail never fills it: GameHost.NamePlate); the feed is reset as a rebuilt host would be
        var rig2 = new AppRig(AppRig.ThreePages);
        var output2 = new ListOutput("hooker", () => true);
        var feed2 = new TextFeed(() => rig2.Services.Language);
        feed2.Add(output2);
        rig2.Services.StartScene(100);
        rig2.Host.SetNamePlate("Arcueid"u8);
        Run(rig2, feed2, 60);
        Assert.Equal("Arcueid\nalpha line", output2.Received[0].Text);
        feed2.Reset();
        Run(rig2, feed2, 3);
        Assert.Single(output2.Received);                                                                   // the reset forgets the message, but the same text is never sent twice in a row (K21's rule)
        Assert.Equal(1, feed2.Produced);
    }

    [Fact]
    public void TheHookPointPassesEachLineToItsSinkOnceAndCountsThem()
    {
        var rig = new AppRig(AppRig.ThreePages);
        var sink = new ListHookSink();
        var log = new List<string>();
        var hook = new TextHookOutput(() => true, sink, log.Add);
        var feed = new TextFeed(() => rig.Services.Language, log.Add);
        feed.Add(hook);
        Assert.Equal("hooker", hook.Name);
        rig.Services.StartScene(100);
        Run(rig, feed, 60);
        Run(rig, feed, 200, click: true);
        Assert.Equal(["alpha line", "beta line", "gamma line"], sink.Lines);
        Assert.Equal(3, hook.Emitted);
        Assert.Equal(3, log.Count(l => l.StartsWith("hooker: message, ", StringComparison.Ordinal)));
        Assert.Contains("hooker: message, 10 chars", log);

        // the real sink: the GDI calls on a memory DC succeed on Windows and do nothing elsewhere
        Assert.Equal(OperatingSystem.IsWindows(), Tsuki.Platform.GdiTextHook.Emit("月姫 -A piece of blue glass moon-"));
        Assert.False(Tsuki.Platform.GdiTextHook.Emit(string.Empty));
        Assert.Equal(["ExtTextOutW", "TextOutW", "GetTextExtentPoint32W"], Tsuki.Platform.GdiTextHook.Apis);
    }

    [Fact]
    public void ARefusingOutputIsLoggedByTheFeedAndTheOthersStillGetTheText()
    {
        var rig = new AppRig(AppRig.ThreePages);
        var log = new List<string>();
        var bad = new ListOutput("websocket", () => true) { Refuse = true };
        var good = new ListOutput("hooker", () => true);
        var feed = new TextFeed(() => rig.Services.Language, log.Add);
        feed.Add(bad);
        feed.Add(good);
        rig.Services.StartScene(100);
        Run(rig, feed, 60);
        Assert.Empty(bad.Received);
        Assert.Single(good.Received);
        Assert.Contains("websocket: the message text could not be delivered", log);
    }
}
