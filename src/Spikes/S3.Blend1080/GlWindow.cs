// Spike S3: SDL3 window with an OpenGL 3.3 core context (ROADMAP M0 scope 7/8, ARCHITECTURE 6.5).
using SDL;
using Silk.NET.OpenGL;
using static SDL.SDL3;

namespace Tsuki.Spikes.S3;

/// <summary>An SDL3 window, its GL 3.3 core context and the Silk.NET bindings loaded from it.</summary>
internal sealed unsafe class GlWindow : IDisposable
{
    private SDL_Window* _window;
    private SDL_GLContextState* _context;

    public GL Gl { get; }
    public int SwapInterval { get; }
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public string Info { get; }

    private GlWindow(SDL_Window* window, SDL_GLContextState* context, GL gl, int swapInterval)
    {
        _window = window;
        _context = context;
        Gl = gl;
        SwapInterval = swapInterval;
        UpdateSize();
        int major, minor, profile;
        SDL_GL_GetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, &major);
        SDL_GL_GetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, &minor);
        SDL_GL_GetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, &profile);
        Info = $"requested 3.3 core; SDL reports {major}.{minor} profile mask {profile}; " +
               $"GL_VERSION \"{gl.GetStringS(StringName.Version)}\"; GL_RENDERER \"{gl.GetStringS(StringName.Renderer)}\"; " +
               $"GL_VENDOR \"{gl.GetStringS(StringName.Vendor)}\"; GLSL \"{gl.GetStringS(StringName.ShadingLanguageVersion)}\"";
    }

    public static GlWindow Create(int width, int height, bool hidden, int swapInterval)
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            throw new InvalidOperationException("SDL_Init: " + SDL_GetError());
        SDL_GL_ResetAttributes();
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, (int)SDL_GLProfile.SDL_GL_CONTEXT_PROFILE_CORE);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_DOUBLEBUFFER, 1);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_RED_SIZE, 8);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_GREEN_SIZE, 8);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_BLUE_SIZE, 8);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_ALPHA_SIZE, 0);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_DEPTH_SIZE, 0);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_STENCIL_SIZE, 0);
        SDL_WindowFlags flags = SDL_WindowFlags.SDL_WINDOW_OPENGL;
        if (hidden)
            flags |= SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        SDL_Window* window = SDL_CreateWindow("Tsuki spike S3.Blend1080", width, height, flags);
        if (window == null)
            throw new InvalidOperationException("SDL_CreateWindow: " + SDL_GetError());
        SDL_GLContextState* context = SDL_GL_CreateContext(window);
        if (context == null)
            throw new InvalidOperationException("SDL_GL_CreateContext: " + SDL_GetError());
        SDL_GL_MakeCurrent(window, context);
        if (!SDL_GL_SetSwapInterval(swapInterval))
            Console.Error.WriteLine($"SDL_GL_SetSwapInterval({swapInterval}) failed: {SDL_GetError()}");
        int actual;
        SDL_GL_GetSwapInterval(&actual);
        GL gl = GL.GetApi(name => SDL_GL_GetProcAddress(name));
        return new GlWindow(window, context, gl, actual);
    }

    private void UpdateSize()
    {
        int w, h;
        SDL_GetWindowSizeInPixels(_window, &w, &h);
        PixelWidth = w;
        PixelHeight = h;
    }

    /// <summary>Pumps events; false when the window was asked to close.</summary>
    public bool Pump()
    {
        SDL_Event ev;
        bool alive = true;
        while (SDL_PollEvent(&ev))
        {
            if (ev.type == (uint)SDL_EventType.SDL_EVENT_QUIT || ev.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                alive = false;
            if (ev.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED)
                UpdateSize();
        }
        return alive;
    }

    public void Swap() => SDL_GL_SwapWindow(_window);

    public void Dispose()
    {
        if (_context != null)
        {
            SDL_GL_DestroyContext(_context);
            _context = null;
        }
        if (_window != null)
        {
            SDL_DestroyWindow(_window);
            _window = null;
        }
        SDL_Quit();
    }
}
