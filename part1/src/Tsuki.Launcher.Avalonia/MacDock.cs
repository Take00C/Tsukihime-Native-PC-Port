using System.Runtime.InteropServices;

namespace Tsuki.Launcher.Avalonia;

/// <summary>
/// The running launcher's Dock tile on macOS, where Avalonia's Window.Icon does nothing: NSApplication's applicationIconImage, set through the Objective-C runtime
/// (the game's own tile is SDL's). The app's Finder icon stays the bundle's: a signed bundle cannot carry a picture made from the player's files.
/// </summary>
internal static class MacDock
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>
    /// Shows <paramref name="png"/> in the Dock (null: the app's own icon again). False, with nothing changed, off macOS, off the main thread (AppKit's), for a
    /// picture macOS cannot read, or when the runtime cannot be reached.
    /// </summary>
    public static bool SetApplicationIcon(string? png)
    {
        if (!OperatingSystem.IsMacOS()) { return false; }
        try
        {
            if (SendBool(Class("NSThread"), Selector("isMainThread")) == 0) { return false; }
            nint image = 0;
            if (png is not null)
            {
                nint path = WithUtf8(png, text => Send(Class("NSString"), Selector("stringWithUTF8String:"), text));
                image = path == 0 ? 0 : Send(Send(Class("NSImage"), Selector("alloc")), Selector("initWithContentsOfFile:"), path);
                if (image == 0) { return false; }
            }

            nint application = Send(Class("NSApplication"), Selector("sharedApplication"));
            if (application != 0) { Send(application, Selector("setApplicationIconImage:"), image); }
            // the application keeps its own reference to the picture
            if (image != 0) { Send(image, Selector("release")); }
            return application != 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or BadImageFormatException)
        {
            return false;
        }
    }

    private static nint Class(string name) => WithUtf8(name, objc_getClass);

    private static nint Selector(string name) => WithUtf8(name, sel_registerName);

    private static nint WithUtf8(string text, Func<nint, nint> use)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try { return use(utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    // DllImport, not LibraryImport: the launcher compiles without unsafe code; every argument is a pointer, so nothing is marshalled
    [DllImport(ObjC)]
    private static extern nint objc_getClass(nint name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName(nint name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendBool(nint receiver, nint selector);
}
