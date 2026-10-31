using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>A small deterministic generator for the menu particles (the PS4 draws them from <c>rand()</c>; the port needs repeatable frames).</summary>
public sealed class UiRandom
{
    private ulong _state;

    /// <summary>Creates a generator.</summary>
    public UiRandom(ulong seed = 0x9E3779B97F4A7C15UL)
    {
        _state = seed | 1UL;
    }

    /// <summary>The next value in [0, 1).</summary>
    public float Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return (_state >> 40) / (float)(1 << 24);
    }

    /// <summary>A value in [<paramref name="a"/>, <paramref name="b"/>).</summary>
    public float Range(float a, float b) => a + ((b - a) * Next());

    /// <summary>An integer in [<paramref name="lo"/>, <paramref name="hi"/>] inclusive.</summary>
    public int Int(int lo, int hi) => lo + (int)(Next() * (hi - lo + 1));
}

/// <summary>Easing used by the menu fades and tweens (knife: ease-out quad <c>t (2 - t)</c>, ease-in quad <c>t t</c>).</summary>
public static class UiEase
{
    /// <summary>Ease-out quad.</summary>
    public static float Out(float t)
    {
        float u = Math.Clamp(t, 0f, 1f);
        return u * (2f - u);
    }

    /// <summary>Ease-in quad.</summary>
    public static float In(float t)
    {
        float u = Math.Clamp(t, 0f, 1f);
        return u * u;
    }

    /// <summary>The 1 - |((t / period) mod 2) - 1| triangle wave between 0 and 1 (the PRESS ANY BUTTON and cursor pulses).</summary>
    public static float Triangle(float t, float period = 1f)
    {
        float x = (t / period) % 2f;
        if (x < 0)
        {
            x += 2f;
        }

        return 1f - MathF.Abs(x - 1f);
    }
}

/// <summary>What the screens share: the game services, the sheets and tables, the text drawer, the frame counter and the sound shortcut.</summary>
public sealed class UiContext
{
    internal UiContext(UiRoot root, IGameServices services, UiAssets assets)
    {
        Root = root;
        Services = services;
        Assets = assets;
        Text = new UiText(assets);
        Random = new UiRandom();
        Backdrop = new UiBackdrop(Random);
    }

    /// <summary>The root that owns the screens.</summary>
    public UiRoot Root { get; }

    /// <summary>The game services.</summary>
    public IGameServices Services { get; }

    /// <summary>The sheets and eboot tables.</summary>
    public UiAssets Assets { get; }

    /// <summary>The glyph text drawer.</summary>
    public UiText Text { get; }

    /// <summary>The particle generator.</summary>
    public UiRandom Random { get; }

    /// <summary>The submenu backdrop shared by the title, the system menu and the screens opened from them.</summary>
    public UiBackdrop Backdrop { get; }

    /// <summary>The system menu tab under the cursor, kept across invocations (knife <c>sys_tab</c>).</summary>
    public int MenuTab { get; set; }

    /// <summary>The returning player's intro (TM_CI and the second caution) has been shown this power-on (docs/formats/system_flow.md 3.4: once per power-on).</summary>
    public bool TitleIntroShown { get; set; }

    /// <summary>What the gallery remembers between visits: the mode it closed on and the entry a replay started from (package K5b).</summary>
    public Screens.GalleryMemory Gallery { get; } = new();

    /// <summary>The 60 Hz step counter since the root was created.</summary>
    public long Frame { get; internal set; }

    /// <summary>The effective UI language.</summary>
    public Language Language => Assets.Language;

    /// <summary>Plays a system sound.</summary>
    public void Sound(UiSound sound) => Services.PlaySystemSound(sound);

    /// <summary>The <c>ui_text</c> string with the PS4 id <paramref name="id"/>.</summary>
    public string Message(int id) => Assets.Text(id);

    /// <summary>The <c>ui_text</c> string of knife's SYSMES number <paramref name="knifeId"/>.</summary>
    public string Sysmes(int knifeId) => Assets.Text(UiAssets.Sysmes(knifeId));
}
