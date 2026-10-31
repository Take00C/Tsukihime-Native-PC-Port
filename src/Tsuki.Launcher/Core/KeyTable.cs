// The keys shown on the Controls and Help pages (package K17). The strings are the ones of Tsuki.Platform.Input.InputMapper.Bindings (a Synthetic test compares them), so
// the launcher does not need SDL to show the table.
namespace Tsuki.Launcher.Core;

/// <summary>One key (or group of keys) and what it does.</summary>
/// <param name="Keys">The key caps, as in InputMapper.Bindings.</param>
/// <param name="Action">What they do.</param>
public sealed record KeyRow(string Keys, string Action);

/// <summary>The keyboard and mouse table.</summary>
public static class KeyTable
{
    /// <summary>The rows, in the order the Controls page lists them.</summary>
    public static IReadOnlyList<KeyRow> Rows { get; } =
    [
        new("Enter / Space / Z / left click", "Advance the text, decide in menus"),
        new("Esc / right click", "System menu in the story; back in menus; on the title, asks to quit"),
        new("X", "Back in menus; hide or show the message window"),
        new("H", "Hide or show the message window"),
        new("Arrow keys", "Move the cursor; Up opens the mode panel in the story"),
        new("W / A / S / D", "Move the cursor in the menus (same as the arrow keys)"),
        new("Q / E", "Previous / next tab or page in menus"),
        new("Ctrl (held)", "Fast-forward"),
        new("Tab / middle click (wheel button)", "Toggle latched skip"),
        new("A", "Toggle auto mode"),
        new("Mouse wheel up / Page Up / I", "Backlog (in the story)"),
        new("Mouse wheel / Page Up / Page Down", "Scroll lists and pages in menus"),
        new("Backspace (held)", "Rewind (back in menus)"),
        new("F5 / F9", "Quick save / quick load"),
        new("N / B", "Next / previous scene"),
        new("V", "Replay the voice"),
        new("M", "System menu; copy a slot in the save list"),
        new("Delete", "Delete the slot under the cursor in the save list"),
        new("Alt+Enter / F11", "Fullscreen on / off (the same as Config › System › Display Mode)"),
        new("F12", "Screenshot: a PNG at the window's size into Pictures › Tsukihime (a short notice follows; Config › System › Screenshot Notice turns it off). Print Screen stays Windows' own"),
    ];

    /// <summary>The bindings a keyboard user can press, as the game names them: every one of them is a row (a Synthetic test checks it).</summary>
    public static bool IsKeyboardBinding(string input) =>
        !input.StartsWith("Mouse:", StringComparison.Ordinal) && !input.StartsWith("Gamepad", StringComparison.Ordinal);

    /// <summary>The few keys the Help page names.</summary>
    public static IReadOnlyList<KeyRow> Essentials { get; } =
        [.. Rows.Where(r => r.Keys is "Enter / Space / Z / left click" or "Esc / right click" or "Ctrl (held)" or "A" or "F5 / F9" or "Alt+Enter / F11")];
}
