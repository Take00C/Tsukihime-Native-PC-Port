// Copy Text to Clipboard (package K21): for text hookers and dictionary tools (Yomitan, Textractor), each new message's plain text goes to the clipboard once when it
// starts displaying, and a choice's lines, one per line, when the list opens. Since package K23 the text comes from the shared producer (TextFeed / PlainText), which
// also feeds the GDI hook point and the WebSocket; this is the clipboard output.
using System.Text;

namespace Tsuki.App.Play;

/// <summary>Where the text goes: the desktop clipboard in the game, a file or a list in the verification runs and the tests.</summary>
internal interface IClipboardSink
{
    /// <summary>Puts <paramref name="text"/> on the clipboard; false when it could not.</summary>
    bool SetText(string text);
}

/// <summary>The desktop clipboard through SDL.</summary>
internal sealed class SdlClipboardSink : IClipboardSink
{
    /// <inheritdoc/>
    public bool SetText(string text) => Tsuki.Platform.Clipboard.SetText(text);
}

/// <summary>A file that gets every copied text, each followed by a separator line (<c>--clipboard-file</c>: the hidden verification runs do not touch the desktop clipboard).</summary>
internal sealed class FileClipboardSink(string path) : IClipboardSink
{
    /// <summary>The line between two texts.</summary>
    public const string Separator = "\n---\n";

    /// <inheritdoc/>
    public bool SetText(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, text + Separator, new UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The clipboard output of the text feed: copies each text it is handed while the switch is on.</summary>
internal sealed class TextCopier(Func<bool> enabled, IClipboardSink sink, Action<string>? log = null) : ITextOutput
{
    private bool _announced;

    /// <inheritdoc/>
    public string Name => "clipboard";

    /// <inheritdoc/>
    public bool Enabled => enabled();

    /// <summary>Texts copied so far.</summary>
    public int Copies { get; private set; }

    /// <summary>The last text handed over (copied or not; the tests).</summary>
    public string LastText { get; private set; } = string.Empty;

    /// <summary>Raised once per run when the first text was copied with the switch on (the app shows "Text copied to the clipboard" then, and never per message).</summary>
    public event Action? FirstCopy;

    /// <inheritdoc/>
    public void Tick()
    {
    }

    /// <inheritdoc/>
    public bool Send(TextKind kind, string text)
    {
        string what = kind == TextKind.Message ? "message" : "choice";
        LastText = text;
        if (!sink.SetText(text))
        {
            log?.Invoke($"clipboard: the {what} text could not be set");
            return false;
        }

        Copies++;
        log?.Invoke($"clipboard: {what}, {text.Length} chars");
        if (!_announced)
        {
            _announced = true;
            FirstCopy?.Invoke();
        }

        return true;
    }
}
