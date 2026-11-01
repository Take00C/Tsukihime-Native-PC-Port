using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Save;

public sealed class SaveFileSecurityTests
{
    [Fact]
    public void OversizedSaveFilesAreRejectedBeforeReadingAndStillAllowBackupRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tsuki-save-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "save.dat");
            using (var sparse = File.Create(path))
            {
                sparse.SetLength((long)int.MaxValue + 1);
            }

            SaveFormatException error = Assert.Throws<SaveFormatException>(() => SaveFile.ReadWithFallback(path, out _));
            Assert.Contains("larger than any valid save", error.Message, StringComparison.Ordinal);
            byte[] valid = SaveFile.ToBytes(new SaveStore(new SystemData()));
            File.WriteAllBytes(path + ".bak", valid);
            Assert.NotNull(SaveFile.ReadWithFallback(path, out bool recovered));
            Assert.True(recovered);

            File.WriteAllText(path, "damaged");
            using (var sparse = File.Create(path + ".bak"))
            {
                sparse.SetLength((long)int.MaxValue + 1);
            }

            error = Assert.Throws<SaveFormatException>(() => SaveFile.ReadWithFallback(path, out _));
            Assert.Contains("the backup: the file is larger than any valid save", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
