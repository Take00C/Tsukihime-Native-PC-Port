// Text for text hookers (package K23). The game draws its glyphs with OpenGL from the PS4's glyph pages, so a hooker (LunaHook / LunaTranslator, Textractor) that
// hooks the Windows text APIs sees nothing. This passes each line once through the GDI text calls those tools hook by default (Textractor's
// texthook/engine/native/pchooks.cc, hookGDIFunctions, which LunaHook inherits: GetTextExtentPoint32W, ExtTextOutW, TextOutW among others), on a memory
// device context that is never shown: nothing is drawn anywhere, the call costs microseconds, and the hooker's thread list gains a thread per API that
// carries the current line.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tsuki.Platform;

/// <summary>The off-screen GDI text calls a text hooker can attach to.</summary>
public static partial class GdiTextHook
{
    /// <summary>The APIs called per line, in order (the names a hooker shows).</summary>
    public static IReadOnlyList<string> Apis { get; } = ["ExtTextOutW", "TextOutW", "GetTextExtentPoint32W"];

    /// <summary>
    /// Passes <paramref name="text"/> (UTF-16) through ExtTextOutW, TextOutW and GetTextExtentPoint32W on a fresh memory DC. True when the calls were made and
    /// reported success; false on another platform or when GDI refused (no DC).
    /// </summary>
    public static bool Emit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!OperatingSystem.IsWindows() || text.Length == 0)
        {
            return false;
        }

        return EmitWindows(text);
    }

    [SupportedOSPlatform("windows")]
    private static bool EmitWindows(string text)
    {
        nint dc = CreateCompatibleDC(0);                                   // a memory DC with a 1 x 1 monochrome bitmap: the text lands nowhere visible
        if (dc == 0)
        {
            return false;
        }

        try
        {
            bool drawn = ExtTextOutW(dc, 0, 0, 0, 0, text, (uint)text.Length, 0);
            bool drawn2 = TextOutW(dc, 0, 0, text, text.Length);
            bool measured = GetTextExtentPoint32W(dc, text, text.Length, out _);
            return drawn && drawn2 && measured;
        }
        finally
        {
            _ = DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Width;
        public int Height;
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ExtTextOutW(nint hdc, int x, int y, uint options, nint rect, string text, uint count, nint dx);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TextOutW(nint hdc, int x, int y, string text, int count);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTextExtentPoint32W(nint hdc, string text, int count, out Size size);
}
