// Load the FFmpeg 9 ABI from bundled libraries or the Linux system installation.
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Tsuki.Media.Ffmpeg;

/// <summary>One decoder that libavcodec has.</summary>
/// <param name="Name">The short name (<c>atrac9</c>, <c>h264</c>, <c>aac</c>).</param>
/// <param name="LongName">The descriptive name.</param>
/// <param name="CodecId">The AVCodecID.</param>
/// <param name="MediaType">audio or video.</param>
public sealed record DecoderInfo(string Name, string LongName, int CodecId, string MediaType);

/// <summary>Versions and licence of the loaded libraries.</summary>
/// <param name="Build">av_version_info.</param>
/// <param name="Avcodec">libavcodec major.minor.micro.</param>
/// <param name="Avutil">libavutil major.minor.micro.</param>
/// <param name="License">avcodec_license (ARCHITECTURE 0.5: "LGPL version 3 or later").</param>
public sealed record FfmpegVersion(string Build, string Avcodec, string Avutil, string License);

/// <summary>
/// The FFmpeg DLLs are not in the folder the game loads them from (<see cref="FfmpegLibrary.Load"/>). The message names the folder and the files and nothing else: the
/// game shows it to a player in its own words (review R1). A developer fills <c>third_party/ffmpeg/bin</c> with <c>tools/setup/ffmpeg_dlls.ps1</c> (ARCHITECTURE 0.5).
/// </summary>
public sealed class FfmpegMissingException(string folder, IReadOnlyList<string> missingFiles)
    : FileNotFoundException($"FFmpeg {(OperatingSystem.IsLinux() ? "shared libraries" : "DLLs")} missing from {folder}: {string.Join(", ", missingFiles)}", missingFiles.Count > 0 ? missingFiles[0] : null)
{
    /// <summary>The folder searched.</summary>
    public string Folder { get; } = folder;

    /// <summary>The DLLs of <see cref="FfmpegLibrary.RequiredDlls"/> that are not there, in that order.</summary>
    public IReadOnlyList<string> MissingFiles { get; } = missingFiles;
}

/// <summary>Loads the pinned FFmpeg native libraries and looks up decoders.</summary>
public static unsafe class FfmpegLibrary
{
    /// <summary>The libavcodec DLL of the pinned build (FFmpeg 9.0, ARCHITECTURE 0.5).</summary>
    public const string AvcodecDll = "avcodec-63.dll";

    /// <summary>The platform's library names for the pinned FFmpeg ABI.</summary>
    public static IReadOnlyList<string> RequiredDlls { get; } = OperatingSystem.IsLinux()
        ? ["libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63"]
        : ["avutil-61.dll", "swresample-7.dll", AvcodecDll, "avformat-63.dll"];

    /// <summary>The decoders the game needs: voices, SE and BGM (atrac9), movies (h264 + aac).</summary>
    public static IReadOnlyList<string> RequiredDecoders { get; } = ["atrac9", "h264", "aac"];

    private static readonly Lock Gate = new();
    private static string? _root;

    /// <summary>True once <see cref="Load"/> has succeeded.</summary>
    public static bool IsLoaded => _root is not null;

    /// <summary>The folder the native libraries were loaded from.</summary>
    public static string RootPath => _root ?? throw new InvalidOperationException("FfmpegLibrary.Load has not been called");

    /// <summary>
    /// Points the bindings at <paramref name="folder"/> or the bundled or system library folder and loads
    /// libavcodec. Idempotent; a second call with a different folder throws.
    /// </summary>
    public static FfmpegVersion Load(string? folder = null)
    {
        lock (Gate)
        {
            if (_root is not null)
            {
                if (folder is not null && !string.Equals(Path.TrimEndingDirectorySeparator(_root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"FFmpeg is already loaded from {_root}");
                }

                return Version();
            }

            string root = Path.GetFullPath(folder ?? DefaultFolder());
            var missing = RequiredDlls.Where(d => !File.Exists(Path.Combine(root, d))).ToList();
            if (missing.Count > 0)
            {
                throw new FfmpegMissingException(root, missing);
            }

            ffmpeg.RootPath = root;
            DynamicallyLoadedBindings.Initialize();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            _ = ffmpeg.avcodec_version(); // forces libavcodec (and its dependencies) to load now
            _ = ffmpeg.avformat_version();
            _ = ffmpeg.swresample_version();
            _root = root;
            return Version();
        }
    }

    /// <summary>Versions and licence of the loaded libraries.</summary>
    public static FfmpegVersion Version()
    {
        EnsureLoaded();
        return new FfmpegVersion(
            ffmpeg.av_version_info() ?? "",
            FormatVersion(ffmpeg.avcodec_version()),
            FormatVersion(ffmpeg.avutil_version()),
            ffmpeg.avcodec_license() ?? "");
    }

    /// <summary>The decoder registered under <paramref name="name"/>, or null.</summary>
    public static DecoderInfo? FindDecoder(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureLoaded();
        AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(name);
        if (codec == null || ffmpeg.av_codec_is_decoder(codec) == 0)
        {
            return null;
        }

        return new DecoderInfo(
            Marshal.PtrToStringUTF8((nint)codec->name) ?? "",
            Marshal.PtrToStringUTF8((nint)codec->long_name) ?? "",
            (int)codec->id,
            ffmpeg.av_get_media_type_string(codec->type) ?? "");
    }

    /// <summary>
    /// The files the process has loaded for the FFmpeg DLLs, so a caller can prove that they all come
    /// from <see cref="RootPath"/> (ARCHITECTURE 1.11).
    /// </summary>
    public static IReadOnlyList<string> LoadedModulePaths()
    {
        EnsureLoaded();
        var names = new HashSet<string>(RequiredDlls, StringComparer.OrdinalIgnoreCase);
        using var process = Process.GetCurrentProcess();
        var paths = new List<string>();
        foreach (ProcessModule m in process.Modules)
        {
            if (names.Contains(m.ModuleName) || (OperatingSystem.IsLinux() && RequiredDlls.Any(n => m.ModuleName.StartsWith(n + ".", StringComparison.Ordinal))))
            {
                paths.Add(m.FileName);
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    private static string DefaultFolder()
    {
        string app = AppContext.BaseDirectory;
        if (!OperatingSystem.IsLinux() || RequiredDlls.All(n => File.Exists(Path.Combine(app, n))))
        {
            return app;
        }

        // Let the system loader resolve distribution and custom library paths.
        if (!NativeLibrary.TryLoad(RequiredDlls[0], out nint handle))
        {
            throw new FfmpegMissingException("the system library path", RequiredDlls);
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (module.ModuleName == RequiredDlls[0] || module.ModuleName.StartsWith(RequiredDlls[0] + ".", StringComparison.Ordinal))
                {
                    return Path.GetDirectoryName(module.FileName)!;
                }
            }
        }
        finally
        {
            NativeLibrary.Free(handle);
        }

        throw new FfmpegMissingException("the system library path", RequiredDlls);
    }

    private static void EnsureLoaded()
    {
        if (_root is null)
        {
            throw new InvalidOperationException("FfmpegLibrary.Load has not been called");
        }
    }

    private static string FormatVersion(uint v) => $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";
}
