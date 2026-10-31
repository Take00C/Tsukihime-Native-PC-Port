// The state behind the button prompts (package K9): the config screen's setting (kept in settings.ini), the device the player used last, and the style they
// resolve to. AppServices exposes it to the menus; it is a class of its own so that the rule can be tested without a game host.
using Tsuki.Platform.Input;
using Tsuki.Ui;

namespace Tsuki.App.Services;

/// <summary>The prompt setting and the last input device.</summary>
public sealed class PromptState
{
    private readonly string? _path;
    private readonly Action<string>? _log;
    private PromptSetting _setting;

    /// <summary>Reads the saved setting.</summary>
    /// <param name="settingsPath">The settings file, or null: nothing is read or written.</param>
    /// <param name="overrideSetting">A setting that replaces the saved one for this run (the <c>--prompts</c> option), or null.</param>
    /// <param name="log">Receives a line when the file cannot be written.</param>
    public PromptState(string? settingsPath, PromptSetting? overrideSetting = null, Action<string>? log = null)
    {
        _path = settingsPath;
        _log = log;
        _setting = overrideSetting ?? AppSettings.Load(settingsPath).Prompts;
    }

    /// <summary>The setting; setting it saves the settings file.</summary>
    public PromptSetting Setting
    {
        get => _setting;
        set
        {
            if (_setting == value)
            {
                return;
            }

            _setting = value;
            if (_path is not null && !AppSettings.Update(_path, saved => saved with { Prompts = value }))   // the launcher's keys in the same file stay (package K17)
            {
                _log?.Invoke($"could not write {_path}");
            }
        }
    }

    /// <summary>The style of the device the player used last: the keyboard until a pad is touched.</summary>
    public PromptStyle LastDevice { get; private set; } = PromptStyle.Keyboard;

    /// <summary>The style the menus draw: the setting, or for Auto the last device's.</summary>
    public PromptStyle Style => PromptStyles.Resolve(_setting, LastDevice);

    /// <summary>Takes the device the input source saw the player use last.</summary>
    public void Observe(InputDevice device) => LastDevice = StyleOf(device);

    /// <summary>The prompt style of an input device: the keyboard and the mouse show key caps, a PlayStation pad the PlayStation icons, any other pad the Xbox glyphs.</summary>
    public static PromptStyle StyleOf(InputDevice device) => device switch
    {
        InputDevice.PlayStationPad => PromptStyle.PlayStation,
        InputDevice.XboxPad => PromptStyle.Xbox,
        _ => PromptStyle.Keyboard,
    };
}
