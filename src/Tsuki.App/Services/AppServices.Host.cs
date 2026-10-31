using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;

namespace Tsuki.App.Services;

// Replacing the host: the interpreter's language is fixed per GameHost (K3.md open issue 12), so a language change builds a new host over the same profile and,
// when a story runs, loads the page it was on into it (the same restore a save slot gets).
public sealed partial class AppServices
{
    private void RebuildHost(Language language)
    {
        GameHost old = Host;
        bool running = StoryActive && old.Phase == GamePhase.Running;
        SlotImage? carry = null;
        if (running && old.History is { Staging.Used: not 0 } history)
        {
            carry = history.Staging.Clone();
        }

        GameProfile profile = old.Profile;
        GameHost host = _parts.HostFactory(language, profile);
        HostLanguage = language;
        Host = host;
        AttachHost(host);
        if (carry is not null)
        {
            var temp = new SaveStore();
            temp.SaveManual(0, carry, Date());
            host.LoadSlot(temp, 0);
            host.SetSkipLatch(false);
        }
        else if (running)
        {
            // a story that has not recorded a page yet (the prologue movie scene records nothing before its _TOMP): there is no page to carry, so the new host
            // starts the same scene over, as the title would; a left-alone host would stay NotStarted under a story that counts as active, a black screen no
            // key can leave (package R1). A gallery replay restarts its ARCHIVE scene on the same G008.
            if (old.ReplayRequested)
            {
                host.BeginReplay(profile.Vars.GetG(8));
            }
            else
            {
                host.BeginStory(old.SceneId);
            }

            host.SetSkipLatch(false);
        }

        HostReplaced?.Invoke(host);
    }

    /// <summary>Sets the language the first host was built with (the runtime calls it once).</summary>
    public void SetInitialLanguage(Language language) => HostLanguage = language;
}
