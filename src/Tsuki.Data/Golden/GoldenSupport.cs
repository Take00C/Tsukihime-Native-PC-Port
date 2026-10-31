#if TSUKI_DEV
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Golden;

/// <summary>
/// The golden export on disk is inconsistent or unreadable: a manifest that does not follow the schema
/// <c>tsuki-golden-manifest/1</c>, a JSON header with a missing field, a file of the wrong size.
/// </summary>
public sealed class GoldenFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GoldenFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the underlying error.</summary>
    public GoldenFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The requested item is not part of the golden slice (<c>samples/golden/slice</c>): a script other than 2 and 3, a text line
/// other than 0..7, a picture that no traced statement displays, a UI sheet. It is a <see cref="KeyNotFoundException"/>, so a
/// caller that probes can tell "not exported" from a bad argument (<see cref="ArgumentOutOfRangeException"/>).
/// </summary>
public sealed class GoldenSliceMissingException : KeyNotFoundException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="what">What was asked for, for example <c>script 7</c>.</param>
    /// <param name="available">What the slice has instead, or null.</param>
    public GoldenSliceMissingException(string what, string? available = null)
        : base(available is null
            ? $"{what} is not part of the golden slice"
            : $"{what} is not part of the golden slice (the slice has {available})")
    {
        What = what;
    }

    /// <summary>What was asked for.</summary>
    public string What { get; }
}

/// <summary>The names the golden export uses for the game's archives, banks and languages.</summary>
public static class GoldenNames
{
    private static readonly string[] Archives = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4"];

    /// <summary>Number of image archives (archives_ui_movies.md 2.1).</summary>
    public static int ArchiveCount => Archives.Length;

    /// <summary>Name of image archive <paramref name="archive"/> (0 allpac0 .. 5 allpac4, archives_ui_movies.md 2.1).</summary>
    public static string Archive(int archive)
    {
        if ((uint)archive >= (uint)Archives.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(archive), archive, "image archive ids are 0 (allpac0) to 5 (allpac4)");
        }

        return Archives[archive];
    }

    /// <summary>The archive id of <paramref name="name"/> (<c>allpac1_2</c> is 2), or -1.</summary>
    public static int ArchiveId(string name) => Array.IndexOf(Archives, name);

    /// <summary>The HED name of a bank (<c>bgm</c>, <c>vse</c>, <c>voice</c>, <c>voice2</c>, <c>vag</c>; audio.md 1).</summary>
    public static string Bank(AudioBank bank) => bank switch
    {
        AudioBank.Bgm => "bgm",
        AudioBank.Se => "vse",
        AudioBank.Voice => "voice",
        AudioBank.Voice2 => "voice2",
        AudioBank.Vag => "vag",
        _ => throw new ArgumentOutOfRangeException(nameof(bank), bank, "unknown audio bank"),
    };

    /// <summary>The bank of an HED name, or null.</summary>
    public static AudioBank? BankOf(string name) => name switch
    {
        "bgm" => AudioBank.Bgm,
        "vse" => AudioBank.Se,
        "voice" => AudioBank.Voice,
        "voice2" => AudioBank.Voice2,
        "vag" => AudioBank.Vag,
        _ => null,
    };

    /// <summary>Lower-case language code used in file names and JSON (<c>ja</c>, <c>en</c>, <c>zc</c>, <c>zt</c>, <c>ko</c>; text.md 3).</summary>
    public static string Code(Language lang) => lang switch
    {
        Language.Ja => "ja",
        Language.En => "en",
        Language.Zc => "zc",
        Language.Zt => "zt",
        Language.Ko => "ko",
        _ => throw new ArgumentOutOfRangeException(nameof(lang), lang, "unknown language"),
    };

    /// <summary>The language of a code, or null.</summary>
    public static Language? LanguageOf(string code) => code switch
    {
        "ja" => Language.Ja,
        "en" => Language.En,
        "zc" => Language.Zc,
        "zt" => Language.Zt,
        "ko" => Language.Ko,
        _ => null,
    };
}

/// <summary>Small helpers over <see cref="JsonElement"/> that name the file and the property when something is missing.</summary>
internal static class GoldenJson
{
    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8, string source)
    {
        try
        {
            return JsonDocument.Parse(utf8);
        }
        catch (JsonException e)
        {
            throw new GoldenFormatException($"{source}: not valid JSON: {e.Message}", e);
        }
    }

    public static JsonElement Prop(this JsonElement element, string name, string source)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            throw new GoldenFormatException($"{source}: property \"{name}\" is missing");
        }

        return value;
    }

    public static bool TryProp(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static string Str(this JsonElement element, string name, string source)
    {
        JsonElement v = element.Prop(name, source);
        if (v.ValueKind != JsonValueKind.String)
        {
            throw new GoldenFormatException($"{source}: property \"{name}\" is {v.ValueKind}, expected a string");
        }

        return v.GetString()!;
    }

    public static string? StrOrNull(this JsonElement element, string name, string source)
    {
        if (!element.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (v.ValueKind != JsonValueKind.String)
        {
            throw new GoldenFormatException($"{source}: property \"{name}\" is {v.ValueKind}, expected a string or null");
        }

        return v.GetString();
    }

    public static int Int(this JsonElement element, string name, string source)
    {
        JsonElement v = element.Prop(name, source);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n))
        {
            throw new GoldenFormatException($"{source}: property \"{name}\" is not a 32-bit integer ({v.ValueKind})");
        }

        return n;
    }

    public static long Long(this JsonElement element, string name, string source)
    {
        JsonElement v = element.Prop(name, source);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long n))
        {
            throw new GoldenFormatException($"{source}: property \"{name}\" is not a 64-bit integer ({v.ValueKind})");
        }

        return n;
    }

    public static bool Bool(this JsonElement element, string name, string source)
    {
        JsonElement v = element.Prop(name, source);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new GoldenFormatException($"{source}: property \"{name}\" is {v.ValueKind}, expected true or false"),
        };
    }

    public static JsonElement Arr(this JsonElement element, string name, string source)
    {
        JsonElement v = element.Prop(name, source);
        if (v.ValueKind != JsonValueKind.Array)
        {
            throw new GoldenFormatException($"{source}: property \"{name}\" is {v.ValueKind}, expected an array");
        }

        return v;
    }

    public static JsonElement Obj(this JsonElement element, string name, string source)
    {
        JsonElement v = element.Prop(name, source);
        if (v.ValueKind != JsonValueKind.Object)
        {
            throw new GoldenFormatException($"{source}: property \"{name}\" is {v.ValueKind}, expected an object");
        }

        return v;
    }

    public static int AsInt(this JsonElement element, string source)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int n))
        {
            throw new GoldenFormatException($"{source}: {element.ValueKind} is not a 32-bit integer");
        }

        return n;
    }

    public static byte[] Hex(string text, string source)
    {
        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException e)
        {
            throw new GoldenFormatException($"{source}: not a hex string", e);
        }
    }

    public static string Sha1Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA1.HashData(data));

    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
#endif
