// Loads SDL3.dll from the application folder by full path (ARCHITECTURE 1.11: native DLLs only from
// the app folder). .NET's default probing of runtimes/win-x64/native/SDL3.dll failed from an app
// folder whose path is longer than MAX_PATH (a 315-character DLL path: the process exited with code
// 127 at the first SDL call), while FFmpeg.AutoGen's full-path LoadLibrary from the same folder
// worked. AppContext.BaseDirectory carries the \\?\ prefix in that case, so a full-path load works.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tsuki.Platform;

/// <summary>The DllImport resolver for SDL3-CS.</summary>
internal static class NativeSdl
{
    private const string LibraryName = "SDL3";

    /// <summary>Where SDL3.dll was loaded from, or null when the default probing was used.</summary>
    internal static string? LoadedFrom { get; private set; }

    /// <summary>Installs the resolver when Tsuki.Platform is loaded, before any SDL call.</summary>
#pragma warning disable CA2255 // Tsuki.Platform owns SDL; its resolver must be in place before the first P/Invoke from any entry point.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() => NativeLibrary.SetDllImportResolver(typeof(SDL.SDL3).Assembly, Resolve);

    /// <summary>The app-folder candidates for SDL3.dll, most specific first.</summary>
    internal static IEnumerable<string> Candidates(string baseDirectory)
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };
        yield return Path.Combine(baseDirectory, "runtimes", rid, "native", LibraryName + ".dll");
        yield return Path.Combine(baseDirectory, LibraryName + ".dll");
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, LibraryName, StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows())
        {
            return 0;
        }

        foreach (string candidate in Candidates(AppContext.BaseDirectory))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint handle))
            {
                LoadedFrom = candidate;
                return handle;
            }
        }

        return 0; // not shipped next to the app: the runtime's default probing decides
    }
}
