// The display-list executor (ARCHITECTURE 3.2, 6.1, 6.3; docs/formats/compositing.md 6): runs the
// commands of one DisplayList in order on the GPU compositor. Textured quads resolve their TextureRef
// through an ITextureSource (the texture cache) or, for TextureKind.Capture, to a capture surface.
using Tsuki.Engine.Contracts;

namespace Tsuki.Render.Compositor;

/// <summary>Resolves the textures a display list names.</summary>
public interface ITextureSource
{
    /// <summary>
    /// The GL texture of <paramref name="key"/> and its transition parameters (texture +0xF0..+0xFC,
    /// compositing 2.2), or false when it is not resident.
    /// </summary>
    bool TryGetTexture(in TextureRef key, out uint texture, out TransitionParams? transition);
}

/// <summary>What one <see cref="DisplayListExecutor.Execute"/> did.</summary>
/// <param name="Commands">Commands read.</param>
/// <param name="Draws">Quads drawn.</param>
/// <param name="Culled">Quads skipped for vertex alpha 0.</param>
/// <param name="Captures">Capture copies.</param>
/// <param name="MissingTextures">Textured quads skipped because the texture was not resident.</param>
/// <param name="Blits">Backdrop copies.</param>
/// <param name="BlitPixels">Pixels copied into the backdrop.</param>
public readonly record struct ExecuteStats(int Commands, int Draws, int Culled, int Captures, int MissingTextures, int Blits, long BlitPixels);

/// <summary>Executes <see cref="DisplayList"/>s on a <see cref="GpuCompositor"/>.</summary>
/// <param name="compositor">The GPU compositor.</param>
/// <param name="textures">Texture resolver (normally the texture cache).</param>
public sealed class DisplayListExecutor(GpuCompositor compositor, ITextureSource textures)
{
    private readonly Vertex[] _quad = new Vertex[4];

    /// <summary>The compositor.</summary>
    public GpuCompositor Compositor { get; } = compositor ?? throw new ArgumentNullException(nameof(compositor));

    /// <summary>The texture resolver.</summary>
    public ITextureSource Textures { get; } = textures ?? throw new ArgumentNullException(nameof(textures));

    /// <summary>
    /// Runs every command of <paramref name="list"/> in order. The targets are not cleared first: the frame
    /// still holds the previous frame, which a transition start captures (compositing 14 open question 2).
    /// </summary>
    public ExecuteStats Execute(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        Compositor.BeginFrame();
        int missing = 0;
        foreach (ref readonly DrawCmd cmd in list.Commands)
        {
            switch (cmd.Kind)
            {
                case DrawKind.TexturedQuad:
                    if (!Resolve(cmd.Texture, out uint tex, out TransitionParams? tp))
                    {
                        missing++;
                        break;
                    }

                    Draw(cmd, tex, tp);
                    break;
                case DrawKind.FillQuad:
                    Draw(cmd, 0, null);
                    break;
                case DrawKind.Capture:
                    Compositor.Capture(cmd.Target, cmd.Texture.A);
                    break;
                case DrawKind.Clear:
                {
                    uint fill = cmd.V0.Rgba;
                    Compositor.Clear(cmd.Target, (byte)fill, (byte)(fill >> 8), (byte)(fill >> 16), (byte)(fill >> 24));
                    break;
                }

                default:
                    throw new InvalidOperationException($"unknown display-list command kind {cmd.Kind}");
            }
        }

        CompositorCounters c = Compositor.Counters;
        return new ExecuteStats(list.Count, c.Draws, c.Culled, c.Captures, missing, c.Blits, c.BlitPixels);
    }

    private bool Resolve(in TextureRef key, out uint texture, out TransitionParams? transition)
    {
        if (key.Kind == TextureKind.Capture)
        {
            texture = Compositor.CaptureSurface(key.A).Texture;
            transition = null;
            return true;
        }

        return Textures.TryGetTexture(key, out texture, out transition) && texture != 0;
    }

    private void Draw(in DrawCmd cmd, uint texture, TransitionParams? transition)
    {
        _quad[0] = cmd.V0;
        _quad[1] = cmd.V1;
        _quad[2] = cmd.V2;
        _quad[3] = cmd.V3;
        Compositor.DrawQuad(cmd.Target, texture, _quad, cmd.EffectWord, cmd.BlendState, transition, cmd.Clip, cmd.Fx);
    }
}
