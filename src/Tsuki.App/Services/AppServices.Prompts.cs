// The button prompts (package K9): the config screen's setting, the device the player used last, and the style the menus draw (state: PromptState).
using Tsuki.Platform.Input;
using Tsuki.Ui;

namespace Tsuki.App.Services;

public sealed partial class AppServices
{
    private PromptState? _promptState;

    private PromptState Prompts => _promptState ??= new PromptState(_parts.SettingsPath, _parts.PromptOverride, Log);

    /// <inheritdoc/>
    public PromptSetting PromptSetting
    {
        get => Prompts.Setting;
        set => Prompts.Setting = value;
    }

    /// <inheritdoc/>
    public PromptStyle PromptStyle => Prompts.Style;

    /// <summary>Takes the device the input source saw the player use last (what the Auto setting follows).</summary>
    public void ObserveDevice(InputDevice device) => Prompts.Observe(device);
}
