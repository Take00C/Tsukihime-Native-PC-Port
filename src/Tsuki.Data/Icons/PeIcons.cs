// Package K28: what a built program carries, read from the file without loading or running it: the icon group the C# compiler writes for a project's ApplicationIcon
// (RT_GROUP_ICON 32512 and its RT_ICON images, rebuilt into the .ico file they came from so that FaceIcon.DecodeIco reads it) and the assembly-level attributes of a
// managed assembly that mark a PERSONAL build (AssemblyMetadata "TsukiPersonalBuild", the informational version). No Win32 call and no reflection load: the PE headers
// come from System.Reflection.PortableExecutable, the resource tree is walked here (IMAGE_RESOURCE_DIRECTORY: type, id, language, data entry) and the attributes are
// decoded with System.Reflection.Metadata. The release check of Tsuki.Cli and the tests use it to tell a release build (the crescent, no mark) from a personal one.
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Tsuki.Data.Icons;

/// <summary>The assembly-level marks of a managed assembly: its <c>AssemblyMetadata</c> pairs and its informational version.</summary>
/// <param name="Metadata">Every <c>[assembly: AssemblyMetadata(key, value)]</c> of the assembly.</param>
/// <param name="InformationalVersion">The <c>AssemblyInformationalVersion</c>, or null when the assembly has none.</param>
public sealed record AssemblyMarks(IReadOnlyDictionary<string, string> Metadata, string? InformationalVersion)
{
    /// <summary>True when the assembly was built with <c>-p:TsukiPersonalIcon=...</c> (<see cref="PeIcons.PersonalBuildKey"/> is set to <c>true</c>).</summary>
    public bool IsPersonalBuild => Metadata.TryGetValue(PeIcons.PersonalBuildKey, out string? value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads icon groups and build marks out of executables and assemblies on disk.</summary>
public static class PeIcons
{
    /// <summary>
    /// The resource id of the icon group that the C# compiler writes for <c>ApplicationIcon</c> (Roslyn's <c>/win32icon</c>: RT_GROUP_ICON 32512, IDI_APPLICATION) and
    /// that the .NET apphost copies into the executable; the game window's class takes it (<c>GameWindow.ApplicationIconResource</c>).
    /// </summary>
    public const int ApplicationIconGroup = 32512;

    /// <summary>The <c>AssemblyMetadata</c> key a personal build carries with the value <c>true</c> (src/Tsuki.PersonalIcon.targets).</summary>
    public const string PersonalBuildKey = "TsukiPersonalBuild";

    private const uint RtIcon = 3;
    private const uint RtGroupIcon = 14;
    private const uint SubdirectoryBit = 0x8000_0000;

    /// <summary>The ids of the icon groups of <paramref name="path"/> (empty when it has resources but no icon group); null when the file is not a PE image.</summary>
    public static IReadOnlyList<int>? IconGroupIds(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        using var pe = OpenPe(stream);
        if (pe is null || Resources(pe) is not { } res)
        {
            return pe is null ? null : [];
        }

        var ids = new List<int>();
        if (FindEntry(res, 0, RtGroupIcon) is { } typeDir)
        {
            foreach ((uint id, _, bool isDir) in Entries(res, typeDir))
            {
                if (isDir && (id & SubdirectoryBit) == 0)
                {
                    ids.Add((int)id);                                                       // a named group (the bit set) has no id
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// The icon group <paramref name="groupId"/> of the executable <paramref name="path"/> as the bytes of a .ico file (the directory of the group with its images
    /// appended, the form <see cref="FaceIcon.DecodeIco"/> reads); null when the file is not a PE image or has no such group.
    /// </summary>
    public static byte[]? IconGroup(string path, int groupId = ApplicationIconGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        using var pe = OpenPe(stream);
        if (pe is null || Resources(pe) is not { } res)
        {
            return null;
        }

        ReadOnlySpan<byte> group = FirstData(pe, res, RtGroupIcon, (uint)groupId);
        if (group.Length < 6)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(group[4..]);
        if (group.Length < 6 + (14 * count))
        {
            return null;
        }

        // GRPICONDIR entries are 14 bytes (the image id instead of the file offset); the .ico directory entries are 16 (a file offset)
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)count);
        ms.Write(header);
        var images = new List<byte[]>(count);
        int offset = 6 + (16 * count);
        Span<byte> entry = stackalloc byte[16];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> g = group.Slice(6 + (14 * i), 14);
            uint imageId = BinaryPrimitives.ReadUInt16LittleEndian(g[12..]);
            byte[] image = FirstData(pe, res, RtIcon, imageId).ToArray();
            images.Add(image);
            g[..8].CopyTo(entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)image.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            ms.Write(entry);
            offset += image.Length;
        }

        foreach (byte[] image in images)
        {
            ms.Write(image);
        }

        return ms.ToArray();
    }

    /// <summary>The assembly-level marks of the managed assembly <paramref name="path"/>; null when the file is not a PE image or carries no managed metadata (an apphost).</summary>
    public static AssemblyMarks? Marks(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        using var pe = OpenPe(stream);
        if (pe is null || !pe.HasMetadata)
        {
            return null;
        }

        MetadataReader md = pe.GetMetadataReader();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        string? informational = null;
        foreach (CustomAttributeHandle handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = md.GetCustomAttribute(handle);
            string type = AttributeTypeName(md, attribute);
            if (type == "System.Reflection.AssemblyMetadataAttribute")
            {
                BlobReader blob = md.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() == 1)
                {
                    string key = blob.ReadSerializedString() ?? string.Empty;
                    metadata[key] = blob.ReadSerializedString() ?? string.Empty;
                }
            }
            else if (type == "System.Reflection.AssemblyInformationalVersionAttribute")
            {
                BlobReader blob = md.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() == 1)
                {
                    informational = blob.ReadSerializedString();
                }
            }
        }

        return new AssemblyMarks(metadata, informational);
    }

    // ---- the PE image ---------------------------------------------------------------------------------------------------------------------------------------

    private static PEReader? OpenPe(FileStream stream)
    {
        try
        {
            var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            _ = reader.PEHeaders;                                                           // throws for a file that is not a PE image
            return reader;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>The resource section from the resource table's RVA to the end of its section: offsets inside the resource tree count from its start.</summary>
    private static ImmutableArray<byte>? Resources(PEReader pe)
    {
        PEHeader? header = pe.PEHeaders.PEHeader;
        if (header is null || header.ResourceTableDirectory.Size == 0)
        {
            return null;
        }

        PEMemoryBlock block = pe.GetSectionData(header.ResourceTableDirectory.RelativeVirtualAddress);
        return block.Length == 0 ? null : block.GetContent();
    }

    /// <summary>The entries of the directory at <paramref name="at"/>: (name or id, offset of the child, whether the child is a directory).</summary>
    private static List<(uint Id, int Offset, bool IsDir)> Entries(ImmutableArray<byte> res, int at)
    {
        var list = new List<(uint, int, bool)>();
        if (at < 0 || at + 16 > res.Length)
        {
            return list;
        }

        ReadOnlySpan<byte> span = res.AsSpan();
        int named = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 12)..]);
        int ids = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 14)..]);
        for (int i = 0; i < named + ids; i++)
        {
            int e = at + 16 + (8 * i);
            if (e + 8 > span.Length)
            {
                break;
            }

            uint name = BinaryPrimitives.ReadUInt32LittleEndian(span[e..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(span[(e + 4)..]);
            list.Add((name, (int)(offset & ~SubdirectoryBit), (offset & SubdirectoryBit) != 0));
        }

        return list;
    }

    private static int? FindEntry(ImmutableArray<byte> res, int dir, uint id)
    {
        foreach ((uint entryId, int offset, bool isDir) in Entries(res, dir))
        {
            if (isDir && entryId == id)
            {
                return offset;
            }
        }

        return null;
    }

    /// <summary>The data of the first language of resource <paramref name="id"/> of <paramref name="type"/>; empty when it is not there.</summary>
    private static ReadOnlySpan<byte> FirstData(PEReader pe, ImmutableArray<byte> res, uint type, uint id)
    {
        int? typeDir = FindEntry(res, 0, type);
        int? nameDir = typeDir is { } t ? FindEntry(res, t, id) : null;
        if (nameDir is not { } n)
        {
            return default;
        }

        foreach ((_, int offset, bool isDir) in Entries(res, n))
        {
            if (isDir || offset + 8 > res.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> span = res.AsSpan();
            int rva = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 4)..]);
            if (rva <= 0 || size <= 0)
            {
                continue;
            }

            PEMemoryBlock block = pe.GetSectionData(rva);
            if (block.Length < size)
            {
                continue;
            }

            return block.GetContent(0, size).AsSpan();
        }

        return default;
    }

    private static string AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                MemberReference member = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (member.Parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
                    return md.GetString(type.Namespace) + "." + md.GetString(type.Name);
                }

                return string.Empty;
            }

            case HandleKind.MethodDefinition:
            {
                MethodDefinition method = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                TypeDefinition type = md.GetTypeDefinition(method.GetDeclaringType());
                return md.GetString(type.Namespace) + "." + md.GetString(type.Name);
            }

            default:
                return string.Empty;
        }
    }
}
