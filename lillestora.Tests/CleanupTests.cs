using Lillestora;

namespace Lillestora.Tests;

public class CleanupTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public CleanupTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string TempFile(string name) => Path.Combine(_tempDir, name);

    private static byte[] RandomBytes(int length, int? seed = null)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var buf = new byte[length];
        rng.NextBytes(buf);
        return buf;
    }

    private static async Task WriteFileAsync(string path, byte[] data)
        => await File.WriteAllBytesAsync(path, data);

    // ---------------------------------------------------------------

    [Fact]
    public async Task Cleanup_AfterNormalBackup_DeletesNothing()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(BackupEngine.MinChunkSize / 2));

        await BackupEngine.RunAsync(srcFile, backup);
        int objectsBefore = await BackupEngine.CountObjectsAsync(backup);

        var result = await BackupEngine.CleanupAsync(backup, dryRun: false);

        Assert.Equal(0, result.Unreferenced);
        Assert.Equal(objectsBefore, await BackupEngine.CountObjectsAsync(backup));
    }

    [Fact]
    public async Task Cleanup_DryRun_DoesNotDeleteObjects()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(BackupEngine.MinChunkSize / 2));

        var br = await BackupEngine.RunAsync(srcFile, backup);

        // Remove snapshot → all objects become orphaned
        await BackupEngine.RemoveSnapshotAsync(backup, br.RootHash);

        int objectsBefore = await BackupEngine.CountObjectsAsync(backup);
        var result        = await BackupEngine.CleanupAsync(backup, dryRun: true);

        Assert.True(result.Unreferenced > 0);
        Assert.Equal(objectsBefore, await BackupEngine.CountObjectsAsync(backup)); // nothing deleted
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task Cleanup_AfterSnapshotRemoved_RemovesOrphanedObjects()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(BackupEngine.MinChunkSize / 2));

        var br = await BackupEngine.RunAsync(srcFile, backup);
        int objectsBefore = await BackupEngine.CountObjectsAsync(backup);
        Assert.True(objectsBefore > 0);

        await BackupEngine.RemoveSnapshotAsync(backup, br.RootHash);

        var result = await BackupEngine.CleanupAsync(backup, dryRun: false);

        Assert.Equal(objectsBefore, result.Unreferenced);
        Assert.Equal(0, await BackupEngine.CountObjectsAsync(backup));
    }

    [Fact]
    public async Task Cleanup_SharedChunks_OnlyDeletesExclusiveObjects()
    {
        // Two files: one with shared data, one with unique data
        var shared = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 1);
        var unique = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 2);
        var backup = TempFile("backup");

        var src1 = TempFile("file1.bin");
        var src2 = TempFile("file2.bin");
        await WriteFileAsync(src1, shared);
        await WriteFileAsync(src2, unique);

        var r1 = await BackupEngine.RunAsync(src1, backup);
        var r2 = await BackupEngine.RunAsync(src2, backup);

        // Remove snapshot for file2 → its unique chunk and its manifest become orphaned
        await BackupEngine.RemoveSnapshotAsync(backup, r2.RootHash);

        var result = await BackupEngine.CleanupAsync(backup, dryRun: false);

        // 2 orphaned objects: unique chunk + r2's manifest
        Assert.Equal(2, result.Unreferenced);
        // 2 objects remain: shared chunk + r1's manifest
        Assert.Equal(2, await BackupEngine.CountObjectsAsync(backup));
    }

    [Fact]
    public async Task Cleanup_ReportsCorrectFreedBytes()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(BackupEngine.MinChunkSize / 2));

        var br             = await BackupEngine.RunAsync(srcFile, backup);
        long totalBefore   = await BackupEngine.TotalPackBytesAsync(backup);

        await BackupEngine.RemoveSnapshotAsync(backup, br.RootHash);

        var result = await BackupEngine.CleanupAsync(backup, dryRun: false);

        // All objects are orphaned, so FreedBytes == total compressed size before cleanup
        Assert.Equal(totalBefore, result.FreedBytes);
    }

    [Fact]
    public async Task Cleanup_WaitsForRunningBackup()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(BackupEngine.MinChunkSize / 2));

        await using var backupLock = await BackupLock.AcquireAsync(backup);

        var cleanupTask = BackupEngine.CleanupAsync(backup, dryRun: true);

        await Task.Delay(300);
        Assert.False(cleanupTask.IsCompleted);

        await backupLock.DisposeAsync();
        var result = await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.Unreferenced);
    }

    [Fact]
    public async Task Cleanup_Cancellable_ThrowsOnCancel()
    {
        var backup = TempFile("backup");
        Directory.CreateDirectory(backup);

        using var cts = new CancellationTokenSource();

        await using var held = await BackupLock.AcquireAsync(backup);

        var cleanupTask = BackupEngine.CleanupAsync(backup, dryRun: true, ct: cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanupTask);
    }
}
