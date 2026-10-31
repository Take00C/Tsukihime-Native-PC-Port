namespace Tsuki.Ui;

/// <summary>One selectable item of a screen: its hit rectangle in canvas pixels, an id the screen understands, and whether it can be selected.</summary>
/// <param name="Rect">The hit rectangle.</param>
/// <param name="Id">The screen's own id.</param>
/// <param name="Enabled">False items are drawn but skipped by the cursor.</param>
public readonly record struct UiItem(RectF Rect, int Id, bool Enabled = true);

/// <summary>
/// The cursor of a menu: items with hit rectangles, a focused index (-1 = none), keyboard moves that wrap and skip disabled items, and the pointer rule of
/// the PS4 menus: the pointer moving over an item gives it the focus (docs/formats/system_flow.md 4.4 "pointer hover selects"), a click on an item acts on it.
/// </summary>
public sealed class UiItemList
{
    private readonly List<UiItem> _items = [];

    /// <summary>The items in cursor order.</summary>
    public IReadOnlyList<UiItem> Items => _items;

    /// <summary>The focused item index, or -1.</summary>
    public int Cursor { get; set; } = -1;

    /// <summary>The id of the focused item, or -1.</summary>
    public int FocusedId => Cursor >= 0 && Cursor < _items.Count ? _items[Cursor].Id : -1;

    /// <summary>Number of items.</summary>
    public int Count => _items.Count;

    /// <summary>Removes every item (the cursor stays where it was).</summary>
    public void Clear() => _items.Clear();

    /// <summary>Adds an item.</summary>
    public void Add(RectF rect, int id, bool enabled = true) => _items.Add(new UiItem(rect, id, enabled));

    /// <summary>The index of the item with <paramref name="id"/>, or -1.</summary>
    public int IndexOf(int id) => _items.FindIndex(i => i.Id == id);

    /// <summary>The index of the enabled item under the point, or -1.</summary>
    public int HitTest(float x, float y)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Enabled && _items[i].Rect.Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Applies the pointer: when it moved over an item the cursor goes there. A pointer that is over nothing leaves the cursor where it is (the keys' focus is
    /// not taken away).
    /// </summary>
    /// <returns>True when the cursor changed.</returns>
    public bool Hover(in UiInput input)
    {
        if (!input.PointerMoved)
        {
            return false;
        }

        int hit = HitTest(input.PointerX, input.PointerY);
        if (hit < 0 || hit == Cursor)
        {
            return false;
        }

        Cursor = hit;
        return true;
    }

    /// <summary>The index of the enabled item the pointer clicked, or -1.</summary>
    public int Clicked(in UiInput input) => input.Click && input.PointerActive ? HitTest(input.PointerX, input.PointerY) : -1;

    /// <summary>Moves the cursor by <paramref name="delta"/> (+1 / -1) over the enabled items. From no focus it goes to the first (down) or last (up).</summary>
    /// <param name="delta">+1 next, -1 previous.</param>
    /// <param name="wrap">Wrap around the ends.</param>
    /// <returns>True when the cursor changed.</returns>
    public bool Move(int delta, bool wrap = true)
    {
        int n = _items.Count;
        if (n == 0 || delta == 0)
        {
            return false;
        }

        int step = Math.Sign(delta);
        int at = Cursor;
        if (at < 0 || at >= n)
        {
            at = step > 0 ? -1 : n;
        }

        for (int k = 0; k < n; k++)
        {
            at += step;
            if (at < 0 || at >= n)
            {
                if (!wrap)
                {
                    return false;
                }

                at = (at + n) % n;
            }

            if (_items[at].Enabled)
            {
                bool changed = at != Cursor;
                Cursor = at;
                return changed;
            }
        }

        return false;
    }

    /// <summary>Moves the cursor to the first enabled item.</summary>
    public bool First()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Enabled)
            {
                bool changed = Cursor != i;
                Cursor = i;
                return changed;
            }
        }

        return false;
    }

    /// <summary>Moves the cursor to the last enabled item.</summary>
    public bool Last()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Enabled)
            {
                bool changed = Cursor != i;
                Cursor = i;
                return changed;
            }
        }

        return false;
    }
}
