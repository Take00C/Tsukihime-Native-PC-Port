using System.Buffers.Binary;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Save;

/// <summary>The local date and time stored in a save slot (slot +0x70C0..+0x70C6; Rtc_LocalTime 0xEAD50).</summary>
public readonly record struct SlotDate(int Year, int Month, int Day, int Hour, int Minute, int Second) : IComparable<SlotDate>
{
    /// <summary>The date of a local <see cref="DateTime"/>.</summary>
    public static SlotDate FromDateTime(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);

    /// <summary>True when the fields describe a real date (the save checker of adv_state.md 7.7: 2020..2100).</summary>
    public bool IsPlausible =>
        Year is >= 2000 and <= 2100 && Month is >= 1 and <= 12 && Day is >= 1 and <= 31 && Hour is >= 0 and < 24
        && Minute is >= 0 and < 60 && Second is >= 0 and < 60;

    /// <summary>The date as a <see cref="DateTime"/> (kind unspecified), or null when the fields are not a calendar date.</summary>
    public DateTime? ToDateTime()
    {
        if (!IsPlausible || Day > DateTime.DaysInMonth(Year, Month))
        {
            return null;
        }

        return new DateTime(Year, Month, Day, Hour, Minute, Second, DateTimeKind.Unspecified);
    }

    /// <inheritdoc/>
    public int CompareTo(SlotDate other)
    {
        int c = Year.CompareTo(other.Year);
        c = c != 0 ? c : Month.CompareTo(other.Month);
        c = c != 0 ? c : Day.CompareTo(other.Day);
        c = c != 0 ? c : Hour.CompareTo(other.Hour);
        c = c != 0 ? c : Minute.CompareTo(other.Minute);
        return c != 0 ? c : Second.CompareTo(other.Second);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Year:D4}-{Month:D2}-{Day:D2} {Hour:D2}:{Minute:D2}:{Second:D2}";
}

/// <summary>
/// One save slot as the PS4 stores it (0x7108 bytes; adv_state.md 7.4, tools/eboot/savedata.py SLOT_FIELDS): magic and
/// used flag, whether state B follows, state A (the newest page-start history record), state B (the first 0x36A8
/// bytes of the scene-stack top record), the date and the end marker. The same layout is the slot staging
/// (RAM 0x12031E0) that the history code keeps current while the game runs.
/// </summary>
public sealed class SlotImage
{
    /// <summary>Size of a slot in bytes.</summary>
    public const int Size = 0x7108;

    /// <summary>Size of state B (the first part of a history record).</summary>
    public const int StateBSize = 0x36A8;

    /// <summary>Offset of state A.</summary>
    public const int StateAOffset = 8;

    /// <summary>Offset of state B.</summary>
    public const int StateBOffset = 0x3A18;

    /// <summary>Offset of the date (u16 year, then month, day, hour, minute, second).</summary>
    public const int DateOffset = 0x70C0;

    /// <summary>Offset of the end marker (u16 0x2AF8).</summary>
    public const int EndMarkerOffset = 0x70C8;

    /// <summary>The magic word 0x2AF8 at +0 and at the end marker.</summary>
    public const ushort Magic = 0x2AF8;

    private HistoryRecord? _stateA;

    /// <summary>Creates an empty slot (all zero).</summary>
    public SlotImage()
        : this(new byte[Size])
    {
    }

    /// <summary>Wraps <paramref name="bytes"/> (exactly <see cref="Size"/> bytes, not copied).</summary>
    public SlotImage(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"a slot is 0x{Size:X} bytes, got 0x{bytes.Length:X}", nameof(bytes));
        }

        Bytes = bytes;
    }

    /// <summary>The slot's bytes.</summary>
    public byte[] Bytes { get; }

    /// <summary>u16 at +0 (0x2AF8 in a stored slot).</summary>
    public ushort MagicWord
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Bytes);
        set => BinaryPrimitives.WriteUInt16LittleEndian(Bytes, value);
    }

    /// <summary>u16 at +2: non-zero when the slot holds a save (the staging sets it to 1 with its first snapshot).</summary>
    public ushort Used
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(2), value);
    }

    /// <summary>i32 at +4: 1 when state B follows; 0 (or less) when the scene stack was empty.</summary>
    public int HasStateB
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(4));
        set => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(4), value);
    }

    /// <summary>u16 at the end marker (0x2AF8 in a stored slot).</summary>
    public ushort EndMarker
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(EndMarkerOffset));
        set => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(EndMarkerOffset), value);
    }

    /// <summary>State A as a history record (a view of the slot's bytes).</summary>
    public HistoryRecord StateA => _stateA ??= new HistoryRecord(Bytes, StateAOffset);

    /// <summary>State B: the first <see cref="StateBSize"/> bytes of a history record.</summary>
    public Span<byte> StateB => Bytes.AsSpan(StateBOffset, StateBSize);

    /// <summary>True when the slot holds a save: magic, used flag and end marker are all present.</summary>
    public bool IsStored => MagicWord == Magic && Used != 0 && EndMarker == Magic;

    /// <summary>
    /// F22 of state A: the scene number the save list shows as the slot's title (text id F22 + 0x18C, gallery.md 9; the list and the
    /// system menu read <c>F022 + 0x18C</c>).
    /// </summary>
    public int SceneVariable => StateA.GetFU16(HistoryRecord.FU16Indices.IndexOf(22));

    /// <summary>The slot's date.</summary>
    public SlotDate Date
    {
        get
        {
            ReadOnlySpan<byte> d = Bytes.AsSpan(DateOffset, 7);
            return new SlotDate(BinaryPrimitives.ReadUInt16LittleEndian(d), d[2], d[3], d[4], d[5], d[6]);
        }

        set
        {
            Span<byte> d = Bytes.AsSpan(DateOffset, 7);
            BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)value.Year);
            d[2] = (byte)value.Month;
            d[3] = (byte)value.Day;
            d[4] = (byte)value.Hour;
            d[5] = (byte)value.Minute;
            d[6] = (byte)value.Second;
        }
    }

    /// <summary>A copy of the slot.</summary>
    public SlotImage Clone() => new((byte[])Bytes.Clone());

    /// <summary>Overwrites this slot with <paramref name="other"/>.</summary>
    public void CopyFrom(SlotImage other)
    {
        ArgumentNullException.ThrowIfNull(other);
        other.Bytes.CopyTo(Bytes, 0);
    }

    /// <summary>Sets every byte to zero.</summary>
    public void Clear() => Array.Clear(Bytes);

    /// <summary>
    /// Staging_SetSnapshot (VA 0x73980): copies <paramref name="record"/> over state A, sets used = 1 and the state-B flag
    /// to <c>min(n, 1)</c>, and copies the first 0x36A8 bytes of <paramref name="stackTop"/> over state B when
    /// <paramref name="n"/> &gt; 0 and the record is given.
    /// </summary>
    /// <param name="record">The page-start record.</param>
    /// <param name="n">Scene-stack top + 1 (0 when the stack is empty).</param>
    /// <param name="stackTop">The scene-stack top record, or null.</param>
    public void SetSnapshot(HistoryRecord record, int n, HistoryRecord? stackTop)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Bytes.CopyTo(Bytes.AsSpan(StateAOffset, HistoryRecord.Size));
        Used = 1;
        HasStateB = n > 0 ? 1 : n;
        if (n > 0 && stackTop is not null)
        {
            stackTop.Bytes[..StateBSize].CopyTo(StateB);
        }
    }

    /// <summary>
    /// Stores the staging into a slot the way Slot_Store (0x737D0) and QuickSave_Store (0x73A70) do: the whole staging is
    /// copied, the first dword becomes 0x12AF8 (magic + used = 1), the date and the end marker are written, and for a
    /// manual or auto save (<paramref name="markSaved"/>) the F1118 bit of state A is set.
    /// </summary>
    public void StoreFrom(SlotImage staging, SlotDate date, bool markSaved)
    {
        ArgumentNullException.ThrowIfNull(staging);
        CopyFrom(staging);
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes, 0x12AF8);
        Date = date;
        EndMarker = Magic;
        if (markSaved)
        {
            // 0x76500(state A, 1118, 1): bit k of the record's bool list where the list holds F1118.
            int k = HistoryRecord.FBoolIndices.IndexOf(AdvLiveState.FIndex.SavedMarker);
            StateA.SetFBoolBit(k, true);
        }
    }
}
