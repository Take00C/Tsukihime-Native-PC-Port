// SHA256SUMS.txt, the checksum file every release attaches beside its zip (package K40): one line per file, "<64 hex digits>  <file name>", the format of GNU
// sha256sum (a '*' before the name, sha256sum's binary marker, is accepted too). `Tsuki.Cli release-sums <zip>` writes it; the launcher refuses an update whose zip is
// not listed or does not match.
using System.Security.Cryptography;

namespace Tsuki.Launcher.Core;

/// <summary>What the check of a downloaded file found.</summary>
public enum SumCheck
{
    /// <summary>The file's SHA-256 is the listed one.</summary>
    Match,

    /// <summary>The list names the file with another checksum.</summary>
    Mismatch,

    /// <summary>The list does not name the file (or it is no checksum list at all).</summary>
    Missing,
}

/// <summary>Reads SHA256SUMS.txt and checks files against it.</summary>
public static class ReleaseSums
{
    /// <summary>The largest checksum file read (a real one is a few lines).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The file names and their checksums (lower-case hex), in the order of the file; lines that are not "hex  name" are ignored.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r').TrimStart('﻿');
            if (line.Length < 66 || line.StartsWith('#'))
            {
                continue;
            }

            string hex = line[..64];
            if (!IsHex(hex) || line[64] != ' ' || line[65] is not (' ' or '*'))
            {
                continue;
            }

            string name = line[66..].Trim();
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];                       // sha256sum ./file
            }

            if (name.Length > 0)
            {
                map.TryAdd(name, hex.ToLowerInvariant());
            }
        }

        return map;
    }

    /// <summary>"&lt;hex&gt;  &lt;name&gt;" (two spaces: sha256sum's text-mode line).</summary>
    public static string FormatLine(string hex, string name) => $"{hex.ToLowerInvariant()}  {name}";

    /// <summary>The SHA-256 of a file as lower-case hex.</summary>
    public static string Sha256Hex(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    /// <summary>The SHA-256 of a file as lower-case hex, cancellable (a release zip is about 105 MB).</summary>
    public static async Task<string> Sha256HexAsync(string path, CancellationToken cancel)
    {
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false));
    }

    /// <summary>Checks <paramref name="actualHex"/> (the downloaded file's checksum) against the line for <paramref name="name"/>.</summary>
    public static SumCheck Check(string? sumsText, string name, string actualHex)
    {
        IReadOnlyDictionary<string, string> map = Parse(sumsText);
        if (!map.TryGetValue(name, out string? expected))
        {
            return SumCheck.Missing;
        }

        return string.Equals(expected, actualHex, StringComparison.OrdinalIgnoreCase) ? SumCheck.Match : SumCheck.Mismatch;
    }

    private static bool IsHex(string s) => s.All(char.IsAsciiHexDigit);
}
