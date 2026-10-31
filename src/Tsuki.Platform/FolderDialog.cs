// Native folder dialog for the first run (ARCHITECTURE 4.1 step 1, 9.5): SDL3's
// SDL_ShowOpenFolderDialog. SDL may call back on another thread, so the result is handed over
// through a request object the main loop polls (pumping events) or waits on.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace Tsuki.Platform;

/// <summary>State of a folder-dialog request.</summary>
public enum FolderDialogOutcome
{
    /// <summary>The dialog is still open.</summary>
    Pending,

    /// <summary>The user chose a folder (<see cref="FolderDialogRequest.Path"/>).</summary>
    Chosen,

    /// <summary>The user cancelled.</summary>
    Cancelled,

    /// <summary>SDL reported an error (<see cref="FolderDialogRequest.Error"/>).</summary>
    Failed,
}

/// <summary>One open folder dialog.</summary>
public sealed class FolderDialogRequest
{
    private readonly ManualResetEventSlim _done = new(false);
    private volatile FolderDialogOutcome _outcome = FolderDialogOutcome.Pending;

    internal FolderDialogRequest()
    {
    }

    /// <summary>Current outcome.</summary>
    public FolderDialogOutcome Outcome => _outcome;

    /// <summary>True once the dialog has closed.</summary>
    public bool IsCompleted => _outcome != FolderDialogOutcome.Pending;

    /// <summary>The chosen folder (Chosen only).</summary>
    public string? Path { get; private set; }

    /// <summary>SDL's error text (Failed only).</summary>
    public string? Error { get; private set; }

    /// <summary>Blocks until the dialog closes or the timeout passes; true when it closed.</summary>
    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    internal void Complete(FolderDialogOutcome outcome, string? path, string? error)
    {
        Path = path;
        Error = error;
        _outcome = outcome;
        _done.Set();
    }
}

/// <summary>Shows the native "choose folder" dialog.</summary>
public static unsafe class FolderDialog
{
    /// <summary>
    /// Opens the dialog, modal to <paramref name="parent"/> when given, starting in
    /// <paramref name="defaultLocation"/>, with <paramref name="title"/> as its caption (the system's default when null; package K20: the first-run prompt names the
    /// game and the folder in the player's language). Returns at once; the request completes when the user
    /// closes the dialog. Keep pumping events on the main thread while it is open.
    /// </summary>
    public static FolderDialogRequest Show(GameWindow? parent, string? defaultLocation = null, string? title = null)
    {
        var request = new FolderDialogRequest();
        GCHandle handle = GCHandle.Alloc(request);
        SDL_Window* owner = parent != null ? parent.Handle : null;
        if (title is null)
        {
            SDL_ShowOpenFolderDialog(&OnResult, GCHandle.ToIntPtr(handle), owner, defaultLocation, false);
            return request;
        }

        SDL_PropertiesID props = SDL_CreateProperties();
        try
        {
            _ = SDL_SetStringProperty(props, SDL_PROP_FILE_DIALOG_TITLE_STRING, title);
            if (owner != null)
            {
                _ = SDL_SetPointerProperty(props, SDL_PROP_FILE_DIALOG_WINDOW_POINTER, (nint)owner);
            }

            if (defaultLocation is not null)
            {
                _ = SDL_SetStringProperty(props, SDL_PROP_FILE_DIALOG_LOCATION_STRING, defaultLocation);
            }

            SDL_ShowFileDialogWithProperties(SDL_FileDialogType.SDL_FILEDIALOG_OPENFOLDER, &OnResult, GCHandle.ToIntPtr(handle), props);
        }
        finally
        {
            SDL_DestroyProperties(props);               // SDL copied what it needs when the dialog was shown
        }

        return request;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResult(nint userdata, byte** filelist, int filter)
    {
        GCHandle handle = GCHandle.FromIntPtr(userdata);
        var request = (FolderDialogRequest)handle.Target!;
        handle.Free();
        if (filelist == null)
        {
            request.Complete(FolderDialogOutcome.Failed, null, SDL_GetError());
        }
        else if (filelist[0] == null)
        {
            request.Complete(FolderDialogOutcome.Cancelled, null, null);
        }
        else
        {
            request.Complete(FolderDialogOutcome.Chosen, Marshal.PtrToStringUTF8((nint)filelist[0]), null);
        }
    }
}
