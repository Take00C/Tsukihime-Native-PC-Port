using System.Buffers.Binary;

namespace Tsuki.Engine.Contracts;

/// <summary>What a <see cref="StateField"/> holds and which accessors the generated block has for it.</summary>
public enum StateFieldKind : byte
{
    /// <summary>One little-endian value; a property named <see cref="StateField.Name"/>.</summary>
    Scalar = 0,

    /// <summary>
    /// <see cref="StateField.Count"/> equal entries. Entries of one value have <c>Get{Name}(i)</c> /
    /// <c>Set{Name}(i, v)</c>; entries of several values (<see cref="StateField.PerEntry"/> &gt; 1) take a second
    /// index; opaque entries (type <c>bytes</c>) have <c>{Name}(i)</c>, which returns the entry's bytes.
    /// </summary>
    Array = 1,

    /// <summary>A bit set of <see cref="StateField.Count"/> bits (bit k = byte k / 8, bit k % 8): <c>Get{Name}(k)</c> / <c>Set{Name}(k, b)</c>.</summary>
    Bits = 2,

    /// <summary>Raw bytes; a <see cref="Span{T}"/> property named <see cref="StateField.Name"/>.</summary>
    Bytes = 3,

    /// <summary>A nested state block (for example the scene state inside a history record); a view property.</summary>
    Block = 4,

    /// <summary>Bytes the engine never addresses (gaps, padding, unused space); no accessor, kept by block copies.</summary>
    Unused = 5,
}

/// <summary>
/// One entry of a state block's field map (ARCHITECTURE 3.2). The generated blocks list every byte
/// of the block in exactly one field (<c>Fields</c>, in offset order), so a contract test can check
/// that the map tiles the block.
/// </summary>
/// <param name="Name">C# accessor name (see <see cref="StateFieldKind"/> for the accessor shapes).</param>
/// <param name="OracleName">The field's name in the Python oracle table or format document it was generated from.</param>
/// <param name="Offset">Byte offset in the block.</param>
/// <param name="Size">Size in bytes (all entries).</param>
/// <param name="Kind">Accessor shape.</param>
/// <param name="Type">Element type: <c>u8 i8 u16 i16 u32 i32 u64 i64 f32</c>, <c>bytes</c>, or a block class name.</param>
/// <param name="Count">Entries (<see cref="StateFieldKind.Array"/>), bits (<see cref="StateFieldKind.Bits"/>), else 1.</param>
/// <param name="PerEntry">Values per entry of an <see cref="StateFieldKind.Array"/> (for example 4 for the per-layer crop rectangle), else 1.</param>
public readonly record struct StateField(
    string Name,
    string OracleName,
    int Offset,
    int Size,
    StateFieldKind Kind,
    string Type,
    int Count = 1,
    int PerEntry = 1)
{
    /// <summary>First byte after the field.</summary>
    public int End => Offset + Size;

    /// <summary>Bytes per entry (<see cref="Size"/> / <see cref="Count"/> for arrays, else <see cref="Size"/>).</summary>
    public int EntrySize => Kind == StateFieldKind.Array ? Size / Count : Size;
}

/// <summary>
/// Base of the generated state blocks (ARCHITECTURE 3.2): a fixed-size window of a byte array with
/// typed little-endian accessors. A block either owns a fresh zeroed array or is a view into a larger
/// one (the scene state inside a history record, the config inside the system block), so the bytes
/// can be copied, saved and compared as the PS4 engine does with its own memcpy.
/// </summary>
public abstract class StateBlock
{
    /// <summary>Creates a block over a new zeroed array of <paramref name="size"/> bytes.</summary>
    protected StateBlock(int size)
        : this(new byte[size], 0, size)
    {
    }

    /// <summary>Creates a view of <paramref name="size"/> bytes of <paramref name="storage"/> at <paramref name="offset"/>.</summary>
    protected StateBlock(byte[] storage, int offset, int size)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (storage.Length - offset < size)
        {
            throw new ArgumentException($"the block needs 0x{size:X} bytes at offset 0x{offset:X} of a 0x{storage.Length:X}-byte array", nameof(storage));
        }

        Storage = storage;
        Offset = offset;
        Length = size;
    }

    /// <summary>The array the block lives in (shared with every view of it).</summary>
    public byte[] Storage { get; }

    /// <summary>Offset of the block in <see cref="Storage"/>.</summary>
    public int Offset { get; }

    /// <summary>Size of the block in bytes.</summary>
    public int Length { get; }

    /// <summary>The block's bytes.</summary>
    public Span<byte> Bytes => Storage.AsSpan(Offset, Length);

    /// <summary>Copies <paramref name="source"/> (exactly <see cref="Length"/> bytes) over the block.</summary>
    public void CopyFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length != Length)
        {
            throw new ArgumentException($"expected 0x{Length:X} bytes, got 0x{source.Length:X}", nameof(source));
        }

        source.CopyTo(Bytes);
    }

    /// <summary>Copies another block of the same size over this one (overlapping views are handled like memmove).</summary>
    public void CopyFrom(StateBlock source)
    {
        ArgumentNullException.ThrowIfNull(source);
        CopyFrom(source.Bytes);
    }

    /// <summary>True when both blocks hold the same bytes.</summary>
    public bool ContentEquals(StateBlock other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Bytes.SequenceEqual(other.Bytes);
    }

    /// <summary>Sets every byte of the block to zero.</summary>
    public void Clear() => Bytes.Clear();

    /// <summary>Returns <paramref name="index"/> when it is in 0..<paramref name="count"/>-1, else throws.</summary>
    protected static int CheckIndex(int index, int count)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"index must be in 0..{count - 1}");
        }

        return index;
    }

    /// <summary>The bytes <paramref name="offset"/>..<paramref name="offset"/>+<paramref name="length"/>-1 of the block.</summary>
    protected Span<byte> Slice(int offset, int length) => Storage.AsSpan(Offset + offset, length);

    /// <summary>Reads a u8.</summary>
    protected byte ReadU8(int offset) => Storage[Offset + offset];

    /// <summary>Writes a u8.</summary>
    protected void WriteU8(int offset, byte value) => Storage[Offset + offset] = value;

    /// <summary>Reads an i8.</summary>
    protected sbyte ReadI8(int offset) => (sbyte)Storage[Offset + offset];

    /// <summary>Writes an i8.</summary>
    protected void WriteI8(int offset, sbyte value) => Storage[Offset + offset] = (byte)value;

    /// <summary>Reads a little-endian u16.</summary>
    protected ushort ReadU16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));

    /// <summary>Writes a little-endian u16.</summary>
    protected void WriteU16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Slice(offset, 2), value);

    /// <summary>Reads a little-endian i16.</summary>
    protected short ReadI16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(Slice(offset, 2));

    /// <summary>Writes a little-endian i16.</summary>
    protected void WriteI16(int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(Slice(offset, 2), value);

    /// <summary>Reads a little-endian u32.</summary>
    protected uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));

    /// <summary>Writes a little-endian u32.</summary>
    protected void WriteU32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Slice(offset, 4), value);

    /// <summary>Reads a little-endian i32.</summary>
    protected int ReadI32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Slice(offset, 4));

    /// <summary>Writes a little-endian i32.</summary>
    protected void WriteI32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Slice(offset, 4), value);

    /// <summary>Reads a little-endian u64.</summary>
    protected ulong ReadU64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8));

    /// <summary>Writes a little-endian u64.</summary>
    protected void WriteU64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Slice(offset, 8), value);

    /// <summary>Reads a little-endian i64.</summary>
    protected long ReadI64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(Slice(offset, 8));

    /// <summary>Writes a little-endian i64.</summary>
    protected void WriteI64(int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(Slice(offset, 8), value);

    /// <summary>Reads a little-endian IEEE float32 (bit-exact; NaN payloads are kept).</summary>
    protected float ReadF32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Slice(offset, 4));

    /// <summary>Writes a little-endian IEEE float32 (bit-exact).</summary>
    protected void WriteF32(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(Slice(offset, 4), value);

    /// <summary>Bit <paramref name="bit"/> of the bit set at <paramref name="offset"/> (byte bit / 8, bit bit % 8).</summary>
    protected bool GetBit(int offset, int bit) => ((Storage[Offset + offset + (bit >> 3)] >> (bit & 7)) & 1) != 0;

    /// <summary>Sets or clears bit <paramref name="bit"/> of the bit set at <paramref name="offset"/>.</summary>
    protected void SetBit(int offset, int bit, bool value)
    {
        ref byte b = ref Storage[Offset + offset + (bit >> 3)];
        byte mask = (byte)(1 << (bit & 7));
        b = value ? (byte)(b | mask) : (byte)(b & ~mask);
    }
}
