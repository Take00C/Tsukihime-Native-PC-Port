// The one plain-text producer of the port (packages K21 and K23) and the feed that hands every new line to the outputs that want it: the clipboard (K21), the GDI
// hook point for text hookers and the WebSocket feed (K23). The text is the line as the backlog shows it: the markup codes and the ruby readings removed
// (Markup.PlainText), a `^` line break joined (a space in English, nothing in the Asian languages, where it breaks mid-word), the speaker's name plate above it
// when the game shows one (never in retail: GameHost.NamePlate), and a choice list as its lines one per line. A message appended to with _MSAD (`@n`) yields
// only the part that was added.
using System.Text;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;

namespace Tsuki.App.Play;

/// <summary>What a produced text is.</summary>
internal enum TextKind
{
    /// <summary>A message (or the part added to one).</summary>
    Message,

    /// <summary>A choice list, one line per choice.</summary>
    Choice,
}

/// <summary>The readable text of the game's bytes.</summary>
internal static class PlainText
{
    /// <summary>The readable text of message bytes in <paramref name="lang"/>: the markup and the ruby removed, the line breaks joined.</summary>
    public static string Message(ReadOnlySpan<byte> text, Language lang)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        string s = Encoding.UTF8.GetString(Markup.PlainText(text, lang, RubyRendering.Drop, icons: false));
        string joiner = lang == Language.En ? " " : string.Empty;
        string[] lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(joiner, lines);
    }

    /// <summary>The message under its speaker's name as the game shows them (the plate above the text): "name\ntext"; the text alone when the plate is empty.</summary>
    public static string WithName(ReadOnlySpan<byte> namePlate, string text, Language lang)
    {
        string name = Message(namePlate, lang);
        return name.Length == 0 ? text : name + "\n" + text;
    }

    /// <summary>A choice list: the readable text of each line (the number prefix as shown), one per line, blank ones left out.</summary>
    public static string Choices(IEnumerable<ReadOnlyMemory<byte>> lines, Language lang)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return string.Join('\n', lines.Select(l => Message(l.Span, lang)).Where(t => t.Length > 0));
    }
}

/// <summary>Where produced text goes: the clipboard, the GDI hook point, the WebSocket. Each has its own switch.</summary>
internal interface ITextOutput
{
    /// <summary>The name in the log ("clipboard", "hooker", "websocket").</summary>
    string Name { get; }

    /// <summary>True while the player wants this output.</summary>
    bool Enabled { get; }

    /// <summary>Called once per step on the game thread, before any text: the output syncs its state (a server to start or stop) and drains what its threads logged.</summary>
    void Tick();

    /// <summary>Takes a produced text; false when it could not be delivered.</summary>
    bool Send(TextKind kind, string text);
}

/// <summary>Watches a game host for new text and hands each message and choice list to the outputs that are on.</summary>
internal sealed class TextFeed(Func<Language> language, Action<string>? log = null)
{
    private readonly List<ITextOutput> _outputs = [];
    private PageMessage? _lastMessage;
    private string _lastPlain = string.Empty;
    private int _lastLength;
    private bool _choiceWasOpen;

    /// <summary>The outputs, in the order they were added.</summary>
    public IReadOnlyList<ITextOutput> Outputs => _outputs;

    /// <summary>Texts produced so far (with at least one output on).</summary>
    public int Produced { get; private set; }

    /// <summary>The last text produced (the tests).</summary>
    public string LastText { get; private set; } = string.Empty;

    /// <summary>Adds an output.</summary>
    public void Add(ITextOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _outputs.Add(output);
    }

    /// <summary>Forgets the message it last saw: a host rebuilt for a language change starts its page over.</summary>
    public void Reset()
    {
        _lastMessage = null;
        _lastPlain = string.Empty;
        _lastLength = 0;
        _choiceWasOpen = false;
    }

    /// <summary>Called after every step of the host: a message that started (or grew) and a choice list that opened go to the outputs that are on.</summary>
    public void Step(GameHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        foreach (ITextOutput output in _outputs)
        {
            output.Tick();
        }

        bool any = false;
        foreach (ITextOutput output in _outputs)
        {
            any |= output.Enabled;
        }

        Language lang = language();
        if (!any)
        {
            // every switch is off: keep following what is on show, so that turning one on sends the next message and not the current page over again
            _lastMessage = host.CurrentMessage;
            _lastPlain = _lastMessage is { } m0 ? PlainText.Message(m0.Text, lang) : string.Empty;
            _lastLength = _lastMessage?.Text.Length ?? 0;
            _choiceWasOpen = host.ChoiceOpen;
            return;
        }

        PageMessage? m = host.CurrentMessage;
        if (m is not null && (!ReferenceEquals(m, _lastMessage) || m.Text.Length != _lastLength))
        {
            string plain = PlainText.Message(m.Text, lang);
            bool continued = ReferenceEquals(m, _lastMessage) && plain.StartsWith(_lastPlain, StringComparison.Ordinal);
            string fresh = continued ? plain[_lastPlain.Length..] : plain;
            _lastMessage = m;
            _lastPlain = plain;
            _lastLength = m.Text.Length;
            if (fresh.Length > 0)
            {
                // a whole message carries the speaker's plate when the game shows one; the added tail of a continuation does not repeat it
                string text = continued ? fresh : PlainText.WithName(host.NamePlateShown ? host.NamePlateText : [], fresh, lang);
                if (text != LastText)
                {
                    Deliver(TextKind.Message, text);
                }
            }
        }
        else if (m is null)
        {
            _lastMessage = null;
            _lastPlain = string.Empty;
            _lastLength = 0;
        }

        bool choice = host.ChoiceOpen && host.ChoiceKind == ChoiceKind.List;
        if (choice && !_choiceWasOpen && host.ChoiceLines.Count > 0)
        {
            string lines = PlainText.Choices(host.ChoiceLines.Select(l => (ReadOnlyMemory<byte>)l.Text), lang);
            if (lines.Length > 0)
            {
                Deliver(TextKind.Choice, lines);
            }
        }

        _choiceWasOpen = choice;
    }

    private void Deliver(TextKind kind, string text)
    {
        LastText = text;
        Produced++;
        foreach (ITextOutput output in _outputs)
        {
            if (output.Enabled && !output.Send(kind, text))
            {
                log?.Invoke($"{output.Name}: the {(kind == TextKind.Message ? "message" : "choice")} text could not be delivered");
            }
        }
    }
}
