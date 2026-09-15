using TomLabs.AutoUpdate;
using Xunit;

namespace TomLabs.AutoUpdate.Tests;

public class UpdateApplierTests
{
    [Fact]
    public async Task CleanUpRemovesBackup()
    {
        var dir = Directory.CreateTempSubdirectory();
        var exe = Path.Combine(dir.FullName, "App.exe");
        File.WriteAllText(exe, "new");
        File.WriteAllText(exe + UpdateApplier.BackupSuffix, "old");

        await UpdateApplier.CleanUpAsync(exe);

        Assert.False(File.Exists(exe + UpdateApplier.BackupSuffix));
        dir.Delete(true);
    }

    [Fact]
    public void RollbackRestoresPreviousExecutable()
    {
        var dir = Directory.CreateTempSubdirectory();
        var exe = Path.Combine(dir.FullName, "App.exe");
        File.WriteAllText(exe, "broken");
        File.WriteAllText(exe + UpdateApplier.BackupSuffix, "good");

        UpdateApplier.Rollback(exe);

        Assert.Equal("good", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + UpdateApplier.BackupSuffix));
        dir.Delete(true);
    }

    [Fact]
    public void RefusesNonExecutable()
        => Assert.NotNull(UpdateApplier.CheckCanApply(Path.Combine(Path.GetTempPath(), "App.dll")));
}
