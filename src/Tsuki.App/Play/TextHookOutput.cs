// The hook point for text hookers (package K23): each produced line goes once through the GDI text calls LunaHook and Textractor hook by default, on an off-screen
// memory DC (Tsuki.Platform.GdiTextHook). Config > System > Text Hooker Output (On by default; settings.ini text_hooker=). The sink is an interface so the tests
// see the calls without GDI.
namespace Tsuki.App.Play;

/// <summary>Where a hook-point line goes: the GDI calls in the game, a list in the tests.</summary>
internal interface ITextHookSink
{
    /// <summary>Passes <paramref name="text"/> through the hooked calls; false when it could not.</summary>
    bool Emit(string text);
}

/// <summary>The real thing: ExtTextOutW, TextOutW and GetTextExtentPoint32W on a memory DC.</summary>
internal sealed class GdiTextHookSink : ITextHookSink
{
    /// <inheritdoc/>
    public bool Emit(string text) => Tsuki.Platform.GdiTextHook.Emit(text);
}

/// <summary>The hook-point output of the text feed.</summary>
internal sealed class TextHookOutput(Func<bool> enabled, ITextHookSink sink, Action<string>? log = null) : ITextOutput
{
    /// <inheritdoc/>
    public string Name => "hooker";

    /// <inheritdoc/>
    public bool Enabled => enabled();

    /// <summary>Lines passed through the calls so far.</summary>
    public int Emitted { get; private set; }

    /// <inheritdoc/>
    public void Tick()
    {
    }

    /// <inheritdoc/>
    public bool Send(TextKind kind, string text)
    {
        string what = kind == TextKind.Message ? "message" : "choice";
        if (!sink.Emit(text))
        {
            log?.Invoke($"hooker: the {what} text could not be passed to GDI");
            return false;
        }

        Emitted++;
        log?.Invoke($"hooker: {what}, {text.Length} chars");
        return true;
    }
}
