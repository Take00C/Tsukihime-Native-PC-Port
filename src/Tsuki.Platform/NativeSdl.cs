// Prefer bundled SDL libraries by full path, including long Windows paths.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tsuki.Platform;

/// <summary>The DllImport resolver for SDL3-CS.</summary>
internal static class NativeSdl
{
    private const string LibraryName = "SDL3";

    /// <summary>Where SDL was loaded from, or null when default probing was used.</summary>
    internal static string? LoadedFrom { get; private set; }

    /// <summary>Installs the resolver when Tsuki.Platform is loaded, before any SDL call.</summary>
#pragma warning disable CA2255 // Tsuki.Platform owns SDL; its resolver must be in place before the first P/Invoke from any entry point.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() => NativeLibrary.SetDllImportResolver(typeof(SDL.SDL3).Assembly, Resolve);

    /// <summary>The bundled SDL candidates, most specific first.</summary>
    internal static IEnumerable<string> Candidates(string baseDirectory) => Candidates(
        baseDirectory,
        OperatingSystem.IsLinux() ? OSPlatform.Linux : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Windows,
        RuntimeInformation.ProcessArchitecture);

    /// <summary>The candidates for one platform and architecture (tests can check the Mac names without a Mac).</summary>
    internal static IEnumerable<string> Candidates(string baseDirectory, OSPlatform platform, Architecture architecture)
    {
        string suffix = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => architecture.ToString().ToLowerInvariant(),
        };
        string rid = (platform == OSPlatform.Linux ? "linux-" : platform == OSPlatform.OSX ? "osx-" : "win-") + suffix;
        string fileName = platform == OSPlatform.Linux ? "libSDL3.so" : platform == OSPlatform.OSX ? "libSDL3.dylib" : LibraryName + ".dll";
        yield return Path.Combine(baseDirectory, "runtimes", rid, "native", fileName);
        yield return Path.Combine(baseDirectory, fileName);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, LibraryName, StringComparison.OrdinalIgnoreCase) || (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()))
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
