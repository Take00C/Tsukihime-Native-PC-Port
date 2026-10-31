using System.Text;
using Tsuki.App.Play;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K21: Copy Text to Clipboard over the hand-made game: each new message's plain text once when it starts, an appended <c>@n</c> continuation as its added part
/// only, a choice list as its lines one per line, nothing while the switch is off, and the markup gone.
/// </summary>
public sealed class TextCopierTests
{
    private sealed class ListSink : IClipboardSink
    {
        public List<string> Texts { get; } = [];

        public bool Refuse { get; set; }

        public bool SetText(string text)
        {
            if (Refuse)
            {
                return false;
            }

            Texts.Add(text);
            return true;
        }
    }

    private const string ChoiceScript =
        "_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);_SELR(0,$000003);_SELR(1,$000004);_SELR(2,$000005);_SELD(_D000,5);_GOTS(_D000,A,B,C);"
        + "_ZZ00101(A);_WKST(F10,eq,10);_END_();_ZZ00201(B);_WKST(F10,eq,20);_END_();_ZZ00301(C);_WKST(F10,eq,30);_END_();";

    private const string ContinueScript = "_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002@n);_MSAD($000003@e);_WKST(F823,eq,0);_WNTY(1);_ZM00102($000004);_END_();";

    // package K23: the copier is one output of the shared text feed; the feed watches the host, the copier takes what the feed produces
    private static (AppRig Rig, TextFeed Feed, TextCopier Copier, ListSink Sink, List<string> Log) Make(string script, bool enabled = true)
    {
        var rig = new AppRig(script);
        var sink = new ListSink();
        var log = new List<string>();
        bool on = enabled;
        var copier = new TextCopier(() => on, sink, log.Add);
        var feed = new TextFeed(() => rig.Services.Language, log.Add);
        feed.Add(copier);
        return (rig, feed, copier, sink, log);
    }

    private static void Run(AppRig rig, TextFeed feed, int frames, bool click = false)
    {
        for (int i = 0; i < frames; i++)
        {
            rig.Step(click && i % 3 == 0);
            feed.Step(rig.Host);
        }
    }

    [Fact]
    public void EachNewMessageIsCopiedOnceAsPlainTextWhenItStarts()
    {
        (AppRig rig, TextFeed copier, TextCopier output, ListSink sink, List<string> log) = Make(AppRig.ThreePages);
        int announced = 0;
        output.FirstCopy += () => announced++;
        rig.Services.StartScene(100);
        Run(rig, copier, 60);
        Assert.Equal(["alpha line"], sink.Texts);
        Assert.Equal(1, announced);
        Run(rig, copier, 200);                                                  // the message waits for its click: nothing more
        Assert.Single(sink.Texts);

        Run(rig, copier, 200, click: true);                                     // the clicks turn the pages
        Assert.Contains("beta line", sink.Texts);
        Assert.Contains("gamma line", sink.Texts);
        Assert.Equal(sink.Texts.Count, sink.Texts.Distinct().Count());          // never the same message twice
        Assert.Equal(sink.Texts.Count, output.Copies);
        Assert.Equal(sink.Texts.Count, copier.Produced);
        Assert.Equal(1, announced);                                             // the toast once per run
        Assert.Contains(log, l => l.StartsWith("clipboard: message, ", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingIsCopiedWhileTheSwitchIsOffAndTurningItOnCopiesTheNextMessageOnly()
    {
        var rig = new AppRig(AppRig.ThreePages);
        var sink = new ListSink();
        bool on = false;
        var copier = new TextFeed(() => rig.Services.Language);
        copier.Add(new TextCopier(() => on, sink));
        rig.Services.StartScene(100);
        Run(rig, copier, 60);
        Assert.Empty(sink.Texts);
        on = true;
        Run(rig, copier, 30);
        Assert.Empty(sink.Texts);                                               // the page on show was seen with the switch off: not copied again
        Run(rig, copier, 200, click: true);
        Assert.NotEmpty(sink.Texts);
        Assert.DoesNotContain("alpha line", sink.Texts);
        Assert.Equal("beta line", sink.Texts[0]);
    }

    [Fact]
    public void AChoiceListIsCopiedAsItsLinesOnePerLine()
    {
        (AppRig rig, TextFeed copier, _, ListSink sink, _) = Make(ChoiceScript);
        rig.Services.StartScene(100);
        Run(rig, copier, 60);
        Assert.Equal(["alpha line"], sink.Texts);
        Run(rig, copier, 90, click: true);                                      // the click past the first page opens the choice
        for (int i = 0; i < 120 && !rig.Host.ChoiceOpen; i++)
        {
            Run(rig, copier, 1);
        }

        Assert.True(rig.Host.ChoiceOpen);
        Run(rig, copier, 5);
        string lines = Assert.Single(sink.Texts.Skip(1));
        string[] parts = lines.Split('\n');
        Assert.Equal(3, parts.Length);
        Assert.EndsWith("beta line", parts[0], StringComparison.Ordinal);
        Assert.EndsWith("gamma line", parts[1], StringComparison.Ordinal);
        Assert.EndsWith("delta line", parts[2], StringComparison.Ordinal);
        Run(rig, copier, 60);                                                   // an open choice is not copied again
        Assert.Equal(2, sink.Texts.Count);
    }

    [Fact]
    public void AnAppendedContinuationCopiesOnlyThePartThatWasAdded()
    {
        (AppRig rig, TextFeed copier, _, ListSink sink, _) = Make(ContinueScript);
        rig.Services.StartScene(100);
        Run(rig, copier, 60);
        Assert.Equal("alpha line", sink.Texts[0]);
        Run(rig, copier, 200, click: true);
        Assert.Contains("beta line", sink.Texts);                               // the _MSAD part alone, not "alpha line beta line"
        Assert.DoesNotContain(sink.Texts, t => t.Contains("alpha", StringComparison.Ordinal) && t.Contains("beta", StringComparison.Ordinal));
        Assert.Contains("gamma line", sink.Texts);
    }

    [Fact]
    public void TheMarkupAndTheLineBreaksAreRemovedAsTheBacklogShowsThem()
    {
        Assert.Equal("Hello there", PlainText.Message(Encoding.UTF8.GetBytes("Hello^there"), Language.En));
        Assert.Equal("こんにちは世界", PlainText.Message(Encoding.UTF8.GetBytes("こんにちは^世界"), Language.Ja));
        Assert.Equal("漢字", PlainText.Message(Encoding.UTF8.GetBytes("<漢字|かんじ>"), Language.Ja));             // the ruby reading is dropped
        Assert.Equal("", PlainText.Message([], Language.En));
        string waited = PlainText.Message(Encoding.UTF8.GetBytes("Slow@w30 words"), Language.En);
        Assert.DoesNotContain("@w", waited, StringComparison.Ordinal);
        Assert.StartsWith("Slow", waited, StringComparison.Ordinal);
        Assert.EndsWith("words", waited, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedClipboardIsLoggedAndCountsNoCopy()
    {
        (AppRig rig, TextFeed copier, TextCopier output, ListSink sink, List<string> log) = Make(AppRig.ThreePages);
        sink.Refuse = true;
        rig.Services.StartScene(100);
        Run(rig, copier, 60);
        Assert.Empty(sink.Texts);
        Assert.Equal(0, output.Copies);
        Assert.Contains("clipboard: the message text could not be set", log);
        Assert.Contains("clipboard: the message text could not be delivered", log);                 // the feed says so too
    }

    [Fact]
    public void TheFileSinkAppendsEveryTextWithASeparator()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-k21-clip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sink = new FileClipboardSink(Path.Combine(dir, "sub", "clip.txt"));
            Assert.True(sink.SetText("one"));
            Assert.True(sink.SetText("two"));
            Assert.Equal("one" + FileClipboardSink.Separator + "two" + FileClipboardSink.Separator, File.ReadAllText(Path.Combine(dir, "sub", "clip.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
