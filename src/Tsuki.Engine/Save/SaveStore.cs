namespace Tsuki.Engine.Save;

/// <summary>An image the app attaches to a slot (the thumbnail of the save list). The engine stores the bytes as given.</summary>
/// <param name="Format">A format id chosen by the caller (the engine does not interpret it), 1..65535.</param>
/// <param name="Data">The image bytes.</param>
public sealed record SlotThumbnail(ushort Format, byte[] Data);

/// <summary>
/// All persistent save state in memory: the <see cref="SystemData"/>, the 140 save slots and their thumbnails
/// (docs/formats/adv_state.md 7). Slots 0..99 are manual saves, 100..138 an auto-save FIFO (newest at 100), slot 139
/// the quick save. <see cref="SaveFile"/> reads and writes it.
/// </summary>
public sealed class SaveStore
{
    /// <summary>Number of slots (0x8C).</summary>
    public const int SlotCount = 140;

    /// <summary>Manual slots are 0..99.</summary>
    public const int ManualSlotCount = 100;

    /// <summary>First auto-save slot (the newest auto save).</summary>
    public const int AutoSaveFirst = 100;

    /// <summary>Last auto-save slot (the oldest one that is kept).</summary>
    public const int AutoSaveLast = 138;

    /// <summary>The quick-save slot.</summary>
    public const int QuickSlot = 139;

    private readonly SlotImage?[] _slots = new SlotImage?[SlotCount];
    private readonly SlotThumbnail?[] _thumbnails = new SlotThumbnail?[SlotCount];

    /// <summary>Creates an empty store with fresh system data.</summary>
    public SaveStore()
        : this(new SystemData())
    {
    }

    /// <summary>Creates an empty store over existing system data.</summary>
    public SaveStore(SystemData system)
    {
        SystemData = system ?? throw new ArgumentNullException(nameof(system));
    }

    /// <summary>The system data (config, global flags, unlock bits, read-text table).</summary>
    public SystemData SystemData { get; }

    /// <summary>True when slot <paramref name="slot"/> holds a save.</summary>
    public bool HasSave(int slot) => _slots[Check(slot)] is { IsStored: true };

    /// <summary>The slot's image, or null when it holds no save. Do not modify the returned object.</summary>
    public SlotImage? GetSlot(int slot) => _slots[Check(slot)] is { IsStored: true } s ? s : null;

    /// <summary>The slot's thumbnail, or null.</summary>
    public SlotThumbnail? GetThumbnail(int slot) => _thumbnails[Check(slot)];

    /// <summary>The slot's date, or null when it holds no save.</summary>
    public SlotDate? GetDate(int slot) => GetSlot(slot)?.Date;

    /// <summary>Slot indices that hold a save, ascending.</summary>
    public IEnumerable<int> StoredSlots()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (HasSave(i))
            {
                yield return i;
            }
        }
    }

    /// <summary>Replaces a slot with an image read from a file (used by <see cref="SaveFile"/>).</summary>
    internal void SetSlot(int slot, SlotImage image, SlotThumbnail? thumbnail)
    {
        _slots[Check(slot)] = image;
        _thumbnails[slot] = thumbnail;
    }

    /// <summary>
    /// Slot_Store (VA 0x737D0): stores the staging as a manual save in slot 0..99 (the menu accepts 0..138), marking the
    /// page with F1118, and updates the latest slot.
    /// </summary>
    /// <param name="slot">0..138.</param>
    /// <param name="staging">The history's staging (<see cref="AdvHistory.Staging"/>).</param>
    /// <param name="date">The local time of the save.</param>
    /// <param name="thumbnail">Optional thumbnail of the screen.</param>
    /// <returns>False when the staging holds no snapshot yet.</returns>
    public bool SaveManual(int slot, SlotImage staging, SlotDate date, SlotThumbnail? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if ((uint)slot > AutoSaveLast)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "manual saves use slots 0..138");
        }

        if (staging.Used == 0)
        {
            return false;
        }

        Put(slot, staging, date, markSaved: true, thumbnail);
        UpdateLatestSlot();
        return true;
    }

    /// <summary>
    /// QuickSave_Store (VA 0x73A70): stores the staging in slot 139 <i>without</i> the F1118 bit and without touching the
    /// latest slot. A quick save is refused for 300 frames after the last one on the PS4; that cooldown is the caller's.
    /// </summary>
    /// <returns>False when the staging holds no snapshot yet.</returns>
    public bool QuickSave(SlotImage staging, SlotDate date, SlotThumbnail? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (staging.Used == 0)
        {
            return false;
        }

        Put(QuickSlot, staging, date, markSaved: false, thumbnail);
        return true;
    }

    /// <summary>
    /// The auto save (VA 0x73B70): slots 100..137 move to 101..138 (the oldest is dropped), the staging becomes slot 100
    /// (marked with F1118) and the latest slot is updated.
    /// </summary>
    /// <returns>False when the staging holds no snapshot yet.</returns>
    public bool AutoSave(SlotImage staging, SlotDate date, SlotThumbnail? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (staging.Used == 0)
        {
            return false;
        }

        for (int i = AutoSaveLast; i > AutoSaveFirst; i--)
        {
            _slots[i] = _slots[i - 1];
            _thumbnails[i] = _thumbnails[i - 1];
        }

        _slots[AutoSaveFirst] = null;
        _thumbnails[AutoSaveFirst] = null;
        Put(AutoSaveFirst, staging, date, markSaved: true, thumbnail);
        UpdateLatestSlot();
        return true;
    }

    /// <summary>Deletes a save (not a PS4 feature; the slot is emptied).</summary>
    public void Delete(int slot)
    {
        _slots[Check(slot)] = null;
        _thumbnails[slot] = null;
        if (slot < QuickSlot)
        {
            UpdateLatestSlot();
        }
    }

    /// <summary>
    /// Loads a stored slot into the history and the live variables (<see cref="AdvHistory.LoadSlot"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The slot is empty.</exception>
    /// <exception cref="InvalidDataException">The slot is not a complete PS4-layout save.</exception>
    public SceneRestoreResult Load(int slot, AdvHistory history, AdvLiveState live)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(live);
        SlotImage image = GetSlot(slot) ?? throw new InvalidOperationException($"slot {slot} holds no save");
        return history.LoadSlot(image, live);
    }

    /// <summary>
    /// SaveImage_Commit mode 2 (VA 0x6FEC0): the latest slot is the slot with the newest date among 0..138 (a strictly
    /// newer date wins, so the lowest index wins a tie); 0 when none is stored.
    /// </summary>
    public void UpdateLatestSlot()
    {
        int best = 0;
        SlotDate? bestDate = null;
        for (int i = 0; i <= AutoSaveLast; i++)
        {
            if (GetSlot(i) is { } s && (bestDate is null || s.Date.CompareTo(bestDate.Value) > 0))
            {
                best = i;
                bestDate = s.Date;
            }
        }

        SystemData.Block.LatestSlot = (ushort)best;
    }

    private void Put(int slot, SlotImage staging, SlotDate date, bool markSaved, SlotThumbnail? thumbnail)
    {
        var image = _slots[slot] is { } existing && existing != staging ? existing : new SlotImage();
        image.StoreFrom(staging, date, markSaved);
        _slots[slot] = image;
        _thumbnails[slot] = thumbnail;
    }

    private static int Check(int slot)
    {
        if ((uint)slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"slot must be in 0..{SlotCount - 1}");
        }

        return slot;
    }
}
