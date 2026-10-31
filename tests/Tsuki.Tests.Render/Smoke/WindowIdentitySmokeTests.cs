using Tsuki.Platform;

namespace Tsuki.Tests.Render.Smoke;

/// <summary>Package K20: the window's caption and icon on a real (hidden) SDL window: a Japanese title is taken, the RGBA icon is accepted, a wrong size is refused.</summary>
[Collection(SdlCollection.Name)]
public sealed class WindowIdentitySmokeTests
{
    [Fact]
    public void TheTitleCanBeChangedToTheGamesJapaneseNameAndTheIconIsTakenFromRgbaPixels()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render identity", 320, 180, Hidden: true, Resizable: false, IconResource: GameWindow.ApplicationIconResource));
        Assert.Equal("Tsuki.Tests.Render identity", window.Title);
        Assert.True(window.SetTitle("月姫 -A piece of blue glass moon-"));
        Assert.Equal("月姫 -A piece of blue glass moon-", window.Title);

        byte[] rgba = new byte[64 * 64 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 0x5E;
            rgba[i + 1] = 0x8B;
            rgba[i + 2] = 0xFF;
            rgba[i + 3] = 0xFF;
        }

        Assert.True(window.SetIcon(64, 64, rgba));
        Assert.Throws<ArgumentException>(() => window.SetIcon(32, 32, rgba));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetIcon(0, 64, rgba));

        // the test host is not Tsukihime.exe: the executable's icon group is not there, so the game would take the embedded picture
        Assert.False(GameWindow.ExecutableHasApplicationIcon());
        Assert.Equal("Tsukihime", new WindowOptions().Title);
    }

    [Fact]
    public void TheWindowTakesThePerSizeFramesOfAnIconFileOnWindows()
    {
        // package K23: the Arcueid .ico of the profile goes on the window through LoadImage + WM_SETICON (SDL's one-bitmap icon is set first, as the game does)
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki.Tests.Render icon file", 320, 180, Hidden: true, Resizable: false));
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-k23-icofile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var frames = new List<Tsuki.Data.Icons.IconFrame>();
            foreach (int size in Tsuki.Data.Icons.FaceIcon.Sizes)
            {
                byte[] rgba = new byte[size * size * 4];
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    rgba[i] = 0x9C;
                    rgba[i + 1] = 0xD2;
                    rgba[i + 2] = 0xFF;
                    rgba[i + 3] = 0xFF;
                }

                frames.Add(new Tsuki.Data.Icons.IconFrame(size, rgba));
            }

            string path = Path.Combine(dir, "tsukihime.ico");
            File.WriteAllBytes(path, Tsuki.Data.Icons.FaceIcon.EncodeIco(frames));
            Assert.True(window.SetIcon(256, 256, frames[^1].Rgba));
            Assert.Equal(OperatingSystem.IsWindows(), window.SetIconFile(path));
            Assert.Equal(OperatingSystem.IsWindows(), window.SetIconFile(path));                 // again: the earlier handles are released, the new ones set
            Assert.False(window.SetIconFile(Path.Combine(dir, "missing.ico")));
            Assert.Throws<ArgumentException>(() => window.SetIconFile(" "));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
