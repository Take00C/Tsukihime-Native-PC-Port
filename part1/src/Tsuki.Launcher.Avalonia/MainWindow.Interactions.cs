using Avalonia.Platform.Storage;

namespace Tsuki.Launcher.Avalonia;

public partial class MainWindow
{
    private async Task BrowseForGame()
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the CUSA47999 folder (it holds eboot.bin and image/)", AllowMultiple = false });
            if (folders.Count == 1)
            {
                _model.FolderText = folders[0].Path.LocalPath;
                await _model.ApplyFolderText();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _model.Notify("The folder window is unavailable. Type the folder's path into the box and press Enter: " + e.Message);
        }
    }

    /// <summary>The file picker of "Choose a file…": the PS4 save image, or null.</summary>
    private async Task<string?> ChooseSaveImage()
    {
        try
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a PS4 save image (savedata000)", AllowMultiple = false });
            return files.Count == 1 ? files[0].Path.LocalPath : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _model.Notify("The file window is unavailable: " + e.Message);
            return null;
        }
    }
}
