// The PS4 param.sfo of the game folder (sce_sys/param.sfo): title id, version and the localized titles, read-only. The format is the public PSF layout: a header, 16-byte
// index entries, a key table and a data table. Package K17 wrote it for the launcher; package K20 moved it here so that the game reads the window title from it too.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Tsuki.Data;

/// <summary>The key / value pairs of a param.sfo.</summary>
public sealed class ParamSfo
{
    private readonly Dictionary<string, string> _values;

    private ParamSfo(Dictionary<string, string> values) => _values = values;

    /// <summary>The value of a key such as <c>TITLE_ID</c>, <c>APP_VER</c>, <c>VERSION</c> or <c>TITLE</c>; null when the key is not there.</summary>
    public string? this[string key] => _values.GetValueOrDefault(key);

    /// <summary>Every key.</summary>
    public IReadOnlyCollection<string> Keys => _values.Keys;

    /// <summary>A param.sfo is a few kilobytes; a file larger than this is not one (the data layer never reads a whole archive into memory).</summary>
    public const int MaxFileSize = 1 << 20;

    /// <summary>Reads a file; null when it is missing, too large to be a param.sfo, or is not one.</summary>
    public static ParamSfo? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            if (file.Length > MaxFileSize)
            {
                return null;
            }

            byte[] bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            return TryParse(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses the bytes; null when the header, an entry or an offset is wrong.</summary>
    public static ParamSfo? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46535000u)
        {
            return null;
        }

        uint keyTable = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint dataTable = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (count > 512 || keyTable > data.Length || dataTable > data.Length || 20 + (count * 16) > data.Length)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(20 + (i * 16), 16);
            int keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            long keyStart = keyTable + keyOffset;
            long valueStart = dataTable + dataOffset;
            if (keyStart >= data.Length || valueStart + length > data.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> keyBytes = data[(int)keyStart..];
            int keyEnd = keyBytes.IndexOf((byte)0);
            string key = Encoding.UTF8.GetString(keyBytes[..(keyEnd < 0 ? keyBytes.Length : keyEnd)]);
            ReadOnlySpan<byte> value = data.Slice((int)valueStart, (int)length);
            if (format == 0x0404 && length == 4)
            {
                values[key] = BinaryPrimitives.ReadUInt32LittleEndian(value).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                int end = value.IndexOf((byte)0);
                values[key] = Encoding.UTF8.GetString(value[..(end < 0 ? value.Length : end)]);
            }
        }

        return new ParamSfo(values);
    }
}
