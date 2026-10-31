using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Ui;

namespace Tsuki.App.Services;

// Saving and loading (IUiSaveServices) over SaveStore and the history ring.
public sealed partial class AppServices
{
    private const int QuickSaveCooldownFrames = 300;
    private readonly Dictionary<int, (UiThumbnail Thumbnail, SlotThumbnail Source)> _thumbnails = [];
    private int _slotsVersion;
    private int _cachedVersion = -1;
    private List<UiSlot> _slotList = [];
    private long _lastQuickSave = long.MinValue / 2;
    private int _thumbVersion;

    /// <inheritdoc/>
    public bool SavingDisabled => Host.F(25) != 0 || !StoryActive || Host.Phase != GamePhase.Running;

    /// <inheritdoc/>
    public IReadOnlyList<UiSlot> Slots
    {
        get
        {
            if (_cachedVersion != _slotsVersion || _slotList.Count != SaveStore.SlotCount)
            {
                _slotList = BuildSlots();
                _cachedVersion = _slotsVersion;
            }

            return _slotList;
        }
    }

    private List<UiSlot> BuildSlots()
    {
        var list = new List<UiSlot>(SaveStore.SlotCount);
        for (int i = 0; i < SaveStore.SlotCount; i++)
        {
            SlotImage? image = Store.GetSlot(i);
            if (image is null)
            {
                list.Add(new UiSlot(i, false, default, 0, 0, string.Empty));
                continue;
            }

            HistoryRecord a = image.StateA;
            int phase = AdvHistory.RecordF8(a, 23);
            DateTime date = image.Date.ToDateTime() ?? default;
            list.Add(new UiSlot(i, true, date, Math.Clamp(phase, 0, 37), image.SceneVariable, Notes(a)));
        }

        return list;
    }

    /// <summary>The note text of a slot: the message of the saved page without its markup, lines joined by <c>^</c>.</summary>
    internal static string Notes(HistoryRecord record)
    {
        ReadOnlySpan<byte> raw = record.MessageText;
        int nul = raw.IndexOf((byte)0);
        if (nul >= 0)
        {
            raw = raw[..nul];
        }

        string text = Encoding.UTF8.GetString(raw);
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '@' && i + 1 < text.Length)
            {
                i++;                                                // @x markup
                continue;
            }

            if (c == '\n' || c == '\r')
            {
                sb.Append('^');
            }
            else if (c >= ' ')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private void SlotsChanged() => _slotsVersion++;

    /// <inheritdoc/>
    public UiThumbnail? Thumbnail(int slot)
    {
        if ((uint)slot >= SaveStore.SlotCount)
        {
            return null;
        }

        SlotThumbnail? stored = Store.GetThumbnail(slot);
        if (stored is null)
        {
            return null;
        }

        if (_thumbnails.TryGetValue(slot, out var cached) && ReferenceEquals(cached.Source, stored))
        {
            return cached.Thumbnail;
        }

        if (ThumbnailCodec.Decode(stored) is not { } t)
        {
            return null;
        }

        var ui = new UiThumbnail(t.Width, t.Height, ++_thumbVersion, t.Rgba);
        _thumbnails[slot] = (ui, stored);
        return ui;
    }

    /// <inheritdoc/>
    public bool Save(int slot)
    {
        if (SavingDisabled || (uint)slot >= SaveStore.AutoSaveFirst)
        {
            return false;
        }

        AdvHistory history = Host.History!;
        bool ok = Store.SaveManual(slot, history.Staging, Date(), CaptureThumbnail());
        if (ok)
        {
            // the slot stays in memory either way; a write that failed is reported as a failed save (the save screen's failure text), not as a success
            SlotsChanged();
            ok = Commit();
        }

        return ok;
    }

    /// <inheritdoc/>
    public bool Load(int slot)
    {
        if ((uint)slot >= SaveStore.SlotCount || !Store.HasSave(slot))
        {
            return false;
        }

        try
        {
            StopMenuAudio();
            StopIntroMovie();
            StopTitleMovie();
            Host.LoadSlot(Store, slot);
            StoryActive = true;
            Host.SetSkipLatch(false);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            Log($"slot {slot} could not be loaded: {e.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Delete(int slot)
    {
        if ((uint)slot >= SaveStore.AutoSaveFirst || !Store.HasSave(slot))
        {
            return false;
        }

        Store.Delete(slot);
        SlotsChanged();
        return Commit();
    }

    /// <inheritdoc/>
    public bool Copy(int slot)
    {
        SlotImage? image = Store.GetSlot(slot);
        if (image is null)
        {
            return false;
        }

        for (int free = 0; free < SaveStore.ManualSlotCount; free++)
        {
            if (!Store.HasSave(free))
            {
                SlotThumbnail? thumb = Store.GetThumbnail(slot);
                if (!Store.SaveManual(free, image.Clone(), image.Date, thumb))
                {
                    return false;
                }

                SlotsChanged();
                return Commit();
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public bool QuickSave()
    {
        if (SavingDisabled || _frame - _lastQuickSave < QuickSaveCooldownFrames)
        {
            return false;
        }

        if (!Store.QuickSave(Host.History!.Staging, Date(), CaptureThumbnail()))
        {
            return false;
        }

        _lastQuickSave = _frame;
        SlotsChanged();
        return Commit();
    }

    /// <inheritdoc/>
    public bool QuickLoad() => Load(SaveStore.QuickSlot);

    /// <inheritdoc/>
    public bool HasQuickSave => Store.HasSave(SaveStore.QuickSlot);

    /// <summary>The auto save of <c>_ATSV(8)</c> and the title auto save changed the slot list: the runtime calls this after each story step.</summary>
    public void NoteStoryStep()
    {
        if (Host.HistorySink is { Store: not null } && Store.HasSave(SaveStore.AutoSaveFirst))
        {
            SlotImage s = Store.GetSlot(SaveStore.AutoSaveFirst)!;
            if (!ReferenceEquals(s, _lastAutoSlot) || s.Date != _lastAutoDate)
            {
                _lastAutoSlot = s;
                _lastAutoDate = s.Date;
                SlotsChanged();
            }
        }
    }

    private SlotImage? _lastAutoSlot;
    private SlotDate _lastAutoDate;

    private static bool SamePoint(HistoryRecord a, HistoryRecord b) =>
        a.Script == b.Script && a.CmdOffset == b.CmdOffset && a.ResumeOffset == b.ResumeOffset && a.Kind == b.Kind;

    /// <inheritdoc/>
    public bool AutoSaveForTitle()
    {
        if (SavingDisabled || Host.History is not { } history || history.Staging.Used == 0)
        {
            return false;
        }

        if (Store.GetSlot(SaveStore.AutoSaveFirst) is { } newest && SamePoint(newest.StateA, history.Staging.StateA))
        {
            return false;
        }

        if (!Store.AutoSave(history.Staging, Date(), CaptureThumbnail()))
        {
            return false;
        }

        SlotsChanged();
        return Commit();
    }
}
