using System.Buffers.Binary;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Save;

/// <summary>A save file or image that cannot be read: truncated, damaged, from a newer version or not a save at all.</summary>
public sealed class SaveFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    public SaveFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Our own save file (<c>%APPDATA%/Tsuki/…</c>, the path is the app's choice) and the PS4 save image import / export.
/// </summary>
/// <remarks>
/// <para>File layout, version 1, all integers little-endian:</para>
/// <code>
/// 0x00  char[8] "TSUKISAV"
/// 0x08  u32 version (1)       0x0C u32 flags (0)
/// 0x10  u32 systemSize (0x18AC)   0x14 u32 slotSize (0x7108)
/// 0x18  u32 slotRecords           0x1C u32 CRC-32 of bytes 0x00..0x1B
/// 0x20  system block (0x18AC bytes, the PS4 layout of adv_state.md 7.5) + u32 CRC-32 of those bytes
/// then slotRecords records:  u16 slot index (strictly ascending, 0..139), u16 thumbnail format (0 = none),
///       u32 thumbnail length, slot (0x7108 bytes, the PS4 layout of adv_state.md 7.4), thumbnail bytes,
///       u32 CRC-32 of the whole record before the CRC
/// footer: char[8] "TSUKIEND", u32 total file length, u32 CRC-32 of every byte before the footer's CRC
/// </code>
/// <para>
/// The system block and each slot are byte-for-byte the PS4 blocks, so a PS4 save image maps onto this file
/// (<see cref="ImportPs4Image"/>) and back (<see cref="ExportPs4Image"/>). Only stored slots are written.
/// </para>
/// </remarks>
public static class SaveFile
{
    /// <summary>The file magic.</summary>
    public static ReadOnlySpan<byte> Magic => "TSUKISAV"u8;

    private static ReadOnlySpan<byte> FooterMagic => "TSUKIEND"u8;

    /// <summary>The format version this code writes and reads.</summary>
    public const int Version = 1;

    private const int HeaderSize = 0x20;
    private const int MaxThumbnailBytes = 8 * 1024 * 1024;
    private const int MaxFileBytes = 256 * 1024 * 1024;

    /// <summary>Size of a PS4 save image (4 MiB).</summary>
    public const int Ps4ImageSize = 0x400000;

    private const int Ps4SlotBase = 0x1A00;
    private const int Ps4SlotStride = 0x7140;

    // -- writing -----------------------------------------------------------------------------------------------------

    /// <summary>The file's bytes.</summary>
    public static byte[] ToBytes(SaveStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        using var ms = new MemoryStream();
        Write(ms, store);
        return ms.ToArray();
    }

    /// <summary>Writes the store to a stream (not atomic: use <see cref="WriteAtomic"/> for files).</summary>
    public static void Write(Stream stream, SaveStore store)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(store);
        var body = new MemoryStream();
        var slots = store.StoredSlots().ToList();

        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x10..], SystemBlock.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x14..], SlotImage.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x18..], (uint)slots.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x1C..], Crc32.Compute(header[..0x1C]));
        body.Write(header);

        SystemBlock system = store.SystemData.Block;
        system.Magic = SystemBlock.MagicValue;
        if (system.Used == 0)
        {
            system.Used = 1;
        }

        body.Write(system.Bytes);
        WriteU32(body, Crc32.Compute(system.Bytes));

        byte[] headBytes = new byte[8];
        foreach (int slot in slots)
        {
            SlotImage image = store.GetSlot(slot)!;
            SlotThumbnail? thumb = store.GetThumbnail(slot);
            if (thumb is { Data.Length: > MaxThumbnailBytes } || thumb is { Format: 0 })
            {
                throw new ArgumentException($"slot {slot}: a thumbnail needs a non-zero format and at most {MaxThumbnailBytes} bytes");
            }

            long start = body.Position;
            Span<byte> head = headBytes;
            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)slot);
            BinaryPrimitives.WriteUInt16LittleEndian(head[2..], thumb?.Format ?? 0);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)(thumb?.Data.Length ?? 0));
            body.Write(head);
            body.Write(image.Bytes);
            if (thumb is not null)
            {
                body.Write(thumb.Data);
            }

            uint crc = Crc32.Compute(body.GetBuffer().AsSpan((int)start, (int)(body.Position - start)));
            WriteU32(body, crc);
        }

        body.Write(FooterMagic);
        WriteU32(body, checked((uint)(body.Length + 8)));
        uint total = Crc32.Compute(body.GetBuffer().AsSpan(0, (int)body.Length));
        WriteU32(body, total);
        stream.Write(body.GetBuffer().AsSpan(0, (int)body.Length));
    }

    /// <summary>
    /// Writes the store to <paramref name="path"/> atomically: the bytes go to a temporary file in the same folder, are
    /// flushed to disk, and replace the old file in one step; the previous generation stays as <c>path + ".bak"</c>.
    /// </summary>
    public static void WriteAtomic(string path, SaveStore store)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(store);
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Write(fs, store);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                File.Replace(tmp, full, full + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, full);
            }
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// SaveImage_Commit: copies G[0..499] of the interpreter's variables into the system data, then writes the file
    /// atomically (<see cref="WriteAtomic"/>). Call it after every manual, quick or auto save and when the config closes.
    /// </summary>
    /// <param name="path">The save file.</param>
    /// <param name="store">The save state.</param>
    /// <param name="vars">The interpreter's variables, or null when G did not change.</param>
    public static void Commit(string path, SaveStore store, Vars? vars)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (vars is not null)
        {
            store.SystemData.CaptureGlobalsFrom(vars);
        }

        WriteAtomic(path, store);
    }

    // -- reading -----------------------------------------------------------------------------------------------------

    /// <summary>Reads a store from a stream (it is read to its end).</summary>
    /// <exception cref="SaveFormatException">The data is not a complete, undamaged save file of a known version.</exception>
    public static SaveStore Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length - stream.Position > MaxFileBytes)
        {
            throw new SaveFormatException("the file is larger than any valid save");
        }

        using var ms = new MemoryStream();
        byte[] chunk = new byte[81920];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (ms.Length > MaxFileBytes - n)
            {
                throw new SaveFormatException("the file is larger than any valid save");
            }

            ms.Write(chunk, 0, n);
        }

        return FromBytes(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>Parses the bytes of a save file.</summary>
    /// <exception cref="SaveFormatException">The data is not a complete, undamaged save file of a known version.</exception>
    public static SaveStore FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxFileBytes)
        {
            throw new SaveFormatException("the file is larger than any valid save");
        }

        if (data.Length < HeaderSize + 4)
        {
            throw new SaveFormatException($"the file is too short ({data.Length} bytes)");
        }

        if (!data[..8].SequenceEqual(Magic))
        {
            throw new SaveFormatException("not a Tsuki save file (bad magic)");
        }

        if (Crc32.Compute(data[..0x1C]) != BinaryPrimitives.ReadUInt32LittleEndian(data[0x1C..]))
        {
            throw new SaveFormatException("the header is damaged (CRC mismatch)");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (version != Version)
        {
            throw new SaveFormatException(version > Version
                ? $"the save file has version {version}, newer than this game understands ({Version})"
                : $"unknown save file version {version}");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]) != SystemBlock.Size
            || BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]) != SlotImage.Size)
        {
            throw new SaveFormatException("the block sizes in the header do not match this version");
        }

        uint records = BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]);
        if (records > SaveStore.SlotCount)
        {
            throw new SaveFormatException($"{records} slot records (at most {SaveStore.SlotCount})");
        }

        // footer first: the whole-file CRC catches truncation and damage anywhere
        const int footerSize = 16;
        if (data.Length < HeaderSize + SystemBlock.Size + 4 + footerSize)
        {
            throw new SaveFormatException("the file is truncated");
        }

        ReadOnlySpan<byte> footer = data[^footerSize..];
        if (!footer[..8].SequenceEqual(FooterMagic))
        {
            throw new SaveFormatException("the file is truncated (no end marker)");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]) != (uint)data.Length)
        {
            throw new SaveFormatException("the file length differs from the length it records");
        }

        if (Crc32.Compute(data[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]))
        {
            throw new SaveFormatException("the file is damaged (CRC mismatch)");
        }

        int pos = HeaderSize;
        ReadOnlySpan<byte> systemBytes = data.Slice(pos, SystemBlock.Size);
        pos += SystemBlock.Size;
        if (Crc32.Compute(systemBytes) != BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]))
        {
            throw new SaveFormatException("the system block is damaged (CRC mismatch)");
        }

        pos += 4;
        var system = new SystemBlock();
        system.CopyFrom(systemBytes);
        ValidateSystem(system);
        var store = new SaveStore(new SystemData(system));

        int end = data.Length - footerSize;
        int previous = -1;
        for (uint r = 0; r < records; r++)
        {
            if (pos + 8 > end)
            {
                throw new SaveFormatException("a slot record is truncated");
            }

            int start = pos;
            int index = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            uint thumbLength = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            pos += 8;
            if (index >= SaveStore.SlotCount || index <= previous)
            {
                throw new SaveFormatException($"slot record {r} has the bad or repeated index {index}");
            }

            previous = index;
            if (thumbLength > MaxThumbnailBytes || (format == 0) != (thumbLength == 0)
                || pos + SlotImage.Size + (long)thumbLength + 4 > end)
            {
                throw new SaveFormatException($"slot {index}: bad thumbnail header or truncated record");
            }

            byte[] slotBytes = data.Slice(pos, SlotImage.Size).ToArray();
            pos += SlotImage.Size;
            byte[]? thumbBytes = thumbLength == 0 ? null : data.Slice(pos, (int)thumbLength).ToArray();
            pos += (int)thumbLength;
            if (Crc32.Compute(data[start..pos]) != BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]))
            {
                throw new SaveFormatException($"slot {index} is damaged (CRC mismatch)");
            }

            pos += 4;
            var image = new SlotImage(slotBytes);
            ValidateSlot(index, image);
            store.SetSlot(index, image, thumbBytes is null ? null : new SlotThumbnail(format, thumbBytes));
        }

        if (pos != end)
        {
            throw new SaveFormatException("unexpected bytes after the last slot record");
        }

        return store;
    }

    /// <summary>
    /// Reads a save file; when it is damaged, tries the previous generation (<c>path + ".bak"</c>).
    /// </summary>
    /// <param name="path">The save file.</param>
    /// <param name="usedBackup">True when the backup was read.</param>
    /// <returns>The store, or null when there is no save file at all.</returns>
    /// <exception cref="SaveFormatException">The file (and the backup, if any) is damaged.</exception>
    public static SaveStore? ReadWithFallback(string path, out bool usedBackup)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        usedBackup = false;
        string backup = path + ".bak";
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (SaveFormatException primary) when (File.Exists(backup))
        {
            try
            {
                using FileStream stream = File.OpenRead(backup);
                SaveStore store = Read(stream);
                usedBackup = true;
                return store;
            }
            catch (SaveFormatException e)
            {
                // the file's own fault is the one to report; the backup's goes with it (R1-app)
                throw new SaveFormatException($"{primary.Message}; the backup: {e.Message}");
            }
        }
    }

    private static void ValidateSystem(SystemBlock system, bool checkLatestSlot = true)
    {
        if (system.Magic != SystemBlock.MagicValue || system.Used == 0)
        {
            throw new SaveFormatException("the system block has no magic");
        }

        if (checkLatestSlot && system.LatestSlot > SaveStore.AutoSaveLast)
        {
            throw new SaveFormatException($"latest slot {system.LatestSlot} is out of range");
        }
    }

    private static void ValidateSlot(int index, SlotImage slot)
    {
        if (!slot.IsStored)
        {
            throw new SaveFormatException($"slot {index} is not a complete save (magic or end marker missing)");
        }

        if (slot.HasStateB is < 0 or > 1)
        {
            throw new SaveFormatException($"slot {index}: state-B flag {slot.HasStateB}");
        }

        if (!slot.Date.IsPlausible)
        {
            throw new SaveFormatException($"slot {index}: implausible date {slot.Date}");
        }

        if (slot.StateA.Serial < 0)
        {
            throw new SaveFormatException($"slot {index}: state A has a negative serial");
        }
    }

    // -- PS4 images (adv_state.md 7, tools/eboot/savedata.py) ------------------------------------------------------------

    /// <summary>
    /// Imports a PS4 save image (<c>savedata000</c>, 4 MiB: the system block at 0, slot i at <c>0x1A00 + i * 0x7140</c>).
    /// Only slots with the magic, a used flag and the end marker are taken. The thumbnails are not part of the image. A latest-slot
    /// word that names no stored slot is recomputed from the dates.
    /// </summary>
    /// <exception cref="SaveFormatException">The image has the wrong size or no system block.</exception>
    public static SaveStore ImportPs4Image(ReadOnlySpan<byte> image)
    {
        if (image.Length != Ps4ImageSize)
        {
            throw new SaveFormatException($"a PS4 save image is 0x{Ps4ImageSize:X} bytes, got 0x{image.Length:X}");
        }

        var system = new SystemBlock();
        system.CopyFrom(image[..SystemBlock.Size]);
        ValidateSystem(system, checkLatestSlot: false);
        var store = new SaveStore(new SystemData(system));
        for (int i = 0; i < SaveStore.SlotCount; i++)
        {
            int off = Ps4SlotBase + (i * Ps4SlotStride);
            var slot = new SlotImage(image.Slice(off, SlotImage.Size).ToArray());
            if (slot.IsStored)
            {
                ValidateSlot(i, slot);
                store.SetSlot(i, slot, null);
            }
        }

        // a console image can carry a stale latest-slot word (it is only refreshed by the saves that set it): "Continue" must not point nowhere
        if (system.LatestSlot > SaveStore.AutoSaveLast || !store.HasSave(system.LatestSlot))
        {
            store.UpdateLatestSlot();
        }

        return store;
    }

    /// <summary>The PS4 image of a store (4 MiB; slots that hold no save carry the magic only, as the console writes them).</summary>
    public static byte[] ExportPs4Image(SaveStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        byte[] image = new byte[Ps4ImageSize];
        store.SystemData.Block.Bytes.CopyTo(image);
        for (int i = 0; i < SaveStore.SlotCount; i++)
        {
            int off = Ps4SlotBase + (i * Ps4SlotStride);
            if (store.GetSlot(i) is { } slot)
            {
                slot.Bytes.CopyTo(image.AsSpan(off));
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off), SlotImage.Magic);
            }
        }

        return image;
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static void WriteU32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320), the checksum of the file's blocks.</summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        /// <summary>CRC-32 of <paramref name="data"/>.</summary>
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] BuildTable()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                t[i] = c;
            }

            return t;
        }
    }
}
