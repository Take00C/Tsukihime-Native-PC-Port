// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (cGalleryMovie: movie_list, Screen::Movies, play_gallery_movie, gal_movie_step, gal_movie_skip)
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The movie gallery (docs/formats/gallery.md 7; knife <c>cGalleryMovie</c>): the prologue movie and each opening once it was watched, as cards in a row of 248 x 144
/// at (384 + 240 c, 104 + 160 r); a card plays its movie in a <see cref="GalleryMoviePlayer"/>.
/// </summary>
internal sealed class GalleryMoviesPanel : GalleryPanel
{
    // the card rows of GALLERY_PARTS_MOVIE child 0 by item: the prologue is the moon (row 3), the openings Arcueid and Ciel
    private static readonly float[] CardY = [312f, 8f, 160f];

    private int[] _items = [];
    private int _cur = -1;

    /// <inheritdoc/>
    public override GalleryMode Mode => GalleryMode.Movies;

    /// <inheritdoc/>
    public override bool Selectable => _items.Length > 0;

    /// <summary>The movie items shown (0 prologue, 1 and 2 the openings).</summary>
    public IReadOnlyList<int> Items => _items;

    /// <summary>The cursor (index into <see cref="Items"/>), or -1.</summary>
    public int Cursor => _cur;

    /// <summary>The card rectangle of entry <paramref name="k"/>.</summary>
    public static RectF Rect(int k) => new(384f + ((k % 6) * 240f), 104f + ((k / 6) * 160f), 248f, 144f);

    /// <inheritdoc/>
    public override void Show()
    {
        _items = [.. Enumerable.Range(0, GalleryModel.Movies.Count).Where(i => GalleryModel.MovieUnlocked(i, Host.Services))];
        _cur = -1;
    }

    /// <inheritdoc/>
    public override bool TakeFocus()
    {
        if (_items.Length == 0)
        {
            return false;
        }

        // the cursor comes back on the first entry
        _cur = 0;
        return true;
    }

    /// <inheritdoc/>
    public override void LoseFocus() => _cur = -1;

    /// <inheritdoc/>
    public override void Tick()
    {
    }

    /// <inheritdoc/>
    public override bool PointerOver(float x, float y)
    {
        for (int k = 0; k < _items.Length; k++)
        {
            if (Rect(k).Contains(x, y))
            {
                if (k != _cur)
                {
                    _cur = k;
                    Host.Sound(UiSound.Cursor);
                }

                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public override void Move(int dx, int dy, bool fresh)
    {
        if (_cur < 0)
        {
            _cur = 0;
            return;
        }

        if (dx < 0)
        {
            if (_cur == 0)
            {
                Host.ToSide();
            }
            else
            {
                _cur--;
                Host.Sound(UiSound.Cursor);
            }
        }
        else if (dx > 0 && _cur + 1 < _items.Length)
        {
            _cur++;
            Host.Sound(UiSound.Cursor);
        }
    }

    /// <inheritdoc/>
    public override void Activate()
    {
        if (_cur < 0 || _cur >= _items.Length)
        {
            return;
        }

        // A plays the movie at once (the confirmation of the exe's state 3 is never entered)
        Host.Sound(UiSound.Decide);
        Host.Root.Push(new GalleryMoviePlayer(_items[_cur]));
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p, float alpha, bool focused)
    {
        for (int k = 0; k < _items.Length; k++)
        {
            float sx = focused && _cur == k ? 264f : 8f;
            p.Sprite(GallerySheets.MovieCards, new RectF(sx, CardY[_items[k]], 248, 144), Rect(k));
        }
    }
}

/// <summary>
/// The gallery's movie player (knife <c>GalMovie</c>): a curtain fades in over the gallery in half a second (white for the first opening), the movie plays
/// full screen and one press of Cancel or Options stops it, then the curtain fades out and the BGM that was playing comes back. The prologue starts BGM 2 at
/// frame 360, as in the story. The movie is the language's copy of table entries 4 to 6 (the host applies the language remap).
/// </summary>
public sealed class GalleryMoviePlayer : UiScreen
{
    private const float CurtainFrames = 30f;
    private readonly int _item;
    private int _restore;
    private int _phase;
    private float _t;
    private float _frames;
    private bool _skip = true;
    private bool _bgm2;
    private bool _started;

    /// <summary>Creates the player for movie gallery item <paramref name="item"/> (0 prologue, 1 and 2 the openings).</summary>
    public GalleryMoviePlayer(int item)
    {
        _item = item;
    }

    /// <summary>0 curtain in, 1 the movie plays, 2 curtain out.</summary>
    public int Phase => _phase;

    /// <summary>The movie item.</summary>
    public int Item => _item;

    private bool White => _item == 1 && _frames < 30f && _phase < 2;

    /// <inheritdoc/>
    public override bool Opaque => _phase == 1;

    /// <inheritdoc/>
    public override void OnOpened()
    {
        // the BGM that plays is faded out and comes back after the movie
        IGameServices s = Ctx.Services;
        _restore = s.PlayingBgm;
        s.FadeOutBgm(0.5f);
    }

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        IGameServices s = Ctx.Services;
        switch (_phase)
        {
            case 0:
                _t++;
                if (_t >= CurtainFrames)
                {
                    int movie = GalleryModel.Movies[_item].Movie;
                    s.StartIntroMovie(movie);
                    _started = true;
                    _phase = 1;
                    _t = 0f;
                }

                break;
            case 1:
                _frames++;
                if (_item == 0 && _frames >= 360f && !_bgm2)
                {
                    _bgm2 = true;
                    s.PlayBgm(2);
                }

                // B or Options stop the movie, once (state 2, mask 0x4008)
                if (_skip && (input.Cancel || input.Options) && s.IntroMoviePlaying)
                {
                    _skip = false;
                    s.StopIntroMovie();
                }

                if (_started && !s.IntroMoviePlaying && _frames > 2f)
                {
                    _phase = 2;
                    _t = 0f;
                }

                break;
            default:
                _t++;
                if (_t >= CurtainFrames)
                {
                    if (_restore > 0)
                    {
                        s.PlayBgm(_restore);
                    }

                    Close();
                }

                break;
        }
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        float u = Math.Clamp(_t / CurtainFrames, 0f, 1f);
        float a = _phase switch
        {
            0 => u * (2f - u),
            1 => 1f,
            _ => (1f - u) * (1f - u),
        };
        float c = White ? 1f : 0f;
        p.Cover(a, c);
        if (_phase == 1 && Ctx.Services.IntroMoviePlaying)
        {
            p.Quad(TextureRef.ForMovieFrame(Ctx.Services.IntroMovieSlot), 0, 0, UiMetrics.Width, UiMetrics.Height, 0, 0, 1, 1, UiPainter.Pack(1f));
        }
    }
}
