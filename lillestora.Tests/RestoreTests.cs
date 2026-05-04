using Lillestora;

namespace Lillestora.Tests;

public class RestoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RestoreTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private static byte[] RandomBytes(int length, int? seed = null)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var buf = new byte[length];
        rng.NextBytes(buf);
        return buf;
    }

    private static async Task WriteFileAsync(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data);
    }

    // ---------------------------------------------------------------

    [Fact]
    public async Task Restore_SingleFile_ContentMatchesOriginal()
    {
        var data    = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 1);
        var srcFile = TempPath("src/file.bin");
        var backup  = TempPath("backup");
        var target  = TempPath("restore");
        await WriteFileAsync(srcFile, data);

        var br = await BackupEngine.RunAsync(srcFile, backup);
        await BackupEngine.RestoreAsync(backup, br.RootHash, target);

        byte[] restored = await File.ReadAllBytesAsync(Path.Combine(target, "file.bin"));
        Assert.Equal(data, restored);
    }

    [Fact]
    public async Task Restore_MultiChunkFile_ContentMatchesOriginal()
    {
        var data    = RandomBytes(BackupEngine.MaxChunkSize * 2 + 1024, seed: 2);
        var srcFile = TempPath("src/big.bin");
        var backup  = TempPath("backup");
        var target  = TempPath("restore");
        await WriteFileAsync(srcFile, data);

        var br = await BackupEngine.RunAsync(srcFile, backup);
        await BackupEngine.RestoreAsync(backup, br.RootHash, target);

        byte[] restored = await File.ReadAllBytesAsync(Path.Combine(target, "big.bin"));
        Assert.Equal(data, restored);
    }

    [Fact]
    public async Task Restore_Directory_RecreatesFullStructure()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");
        string target = TempPath("restore");

        var data1 = RandomBytes(512, seed: 10);
        var data2 = RandomBytes(512, seed: 11);
        var data3 = RandomBytes(512, seed: 12);
        await WriteFileAsync(Path.Combine(srcDir, "a.bin"),        data1);
        await WriteFileAsync(Path.Combine(srcDir, "b.bin"),        data2);
        await WriteFileAsync(Path.Combine(srcDir, "sub", "c.bin"), data3);

        var br = await BackupEngine.RunAsync(srcDir, backup);
        await BackupEngine.RestoreAsync(backup, br.RootHash, target);

        Assert.Equal(data1, await File.ReadAllBytesAsync(Path.Combine(target, "a.bin")));
        Assert.Equal(data2, await File.ReadAllBytesAsync(Path.Combine(target, "b.bin")));
        Assert.Equal(data3, await File.ReadAllBytesAsync(Path.Combine(target, "sub", "c.bin")));
    }

    [Fact]
    public async Task Restore_TimestampsAreRestored()
    {
        var srcFile = TempPath("src/ts.bin");
        var backup  = TempPath("backup");
        var target  = TempPath("restore");
        await WriteFileAsync(srcFile, RandomBytes(512, seed: 20));

        var fi               = new FileInfo(srcFile);
        var expectedCreated  = fi.CreationTimeUtc;
        var expectedModified = fi.LastWriteTimeUtc;

        var br = await BackupEngine.RunAsync(srcFile, backup);
        await BackupEngine.RestoreAsync(backup, br.RootHash, target);

        var rfi = new FileInfo(Path.Combine(target, "ts.bin"));
        Assert.Equal(expectedCreated.TruncateToSeconds(),  rfi.CreationTimeUtc.TruncateToSeconds());
        Assert.Equal(expectedModified.TruncateToSeconds(), rfi.LastWriteTimeUtc.TruncateToSeconds());
    }

    [Fact]
    public async Task Restore_ExistingFile_ThrowsIOException()
    {
        var data    = RandomBytes(512, seed: 30);
        var srcFile = TempPath("src/file.bin");
        var backup  = TempPath("backup");
        var target  = TempPath("restore");
        await WriteFileAsync(srcFile, data);

        var br = await BackupEngine.RunAsync(srcFile, backup);
        await WriteFileAsync(Path.Combine(target, "file.bin"), new byte[] { 0xFF });

        await Assert.ThrowsAsync<IOException>(() =>
            BackupEngine.RestoreAsync(backup, br.RootHash, target));
    }

    [Fact]
    public async Task Restore_PathTraversal_ThrowsInvalidOperationException()
    {
        var manifest = new DirManifest([new FileManifestEntry(
            Name:     "../outside.bin",
            Created:  DateTimeOffset.Now,
            Modified: DateTimeOffset.Now,
            Size:     0,
            Chunks:   [])]);

        var backup = TempPath("backup");
        var target = TempPath("restore");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(target);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BackupEngine.RestoreAsync(backup, manifest, target));
    }

    [Fact]
    public async Task Restore_ReportsCorrectProgress()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");
        string target = TempPath("restore");

        await WriteFileAsync(Path.Combine(srcDir, "x.bin"), RandomBytes(512, seed: 40));
        await WriteFileAsync(Path.Combine(srcDir, "y.bin"), RandomBytes(512, seed: 41));

        var br       = await BackupEngine.RunAsync(srcDir, backup);
        var progress = new List<RestoreFileProgress>();

        var result = await BackupEngine.RestoreAsync(backup, br.RootHash, target,
            p => progress.Add(p));

        Assert.Equal(2, result.FilesRestored);
        Assert.Equal(2, progress.Count);
        Assert.Equal(1024, (int)result.TotalBytes);
    }

    [Fact]
    public async Task Restore_ViaSnapshot_Works()
    {
        var data    = RandomBytes(512, seed: 50);
        var srcFile = TempPath("src/file.bin");
        var backup  = TempPath("backup");
        var target  = TempPath("restore");
        await WriteFileAsync(srcFile, data);

        var br        = await BackupEngine.RunAsync(srcFile, backup);
        var snapshots = await BackupEngine.ListSnapshotsAsync(backup);
        var snap      = snapshots.Single(s => s.RootHash == br.RootHash);

        await BackupEngine.RestoreAsync(snap, backup, target);

        byte[] restored = await File.ReadAllBytesAsync(Path.Combine(target, "file.bin"));
        Assert.Equal(data, restored);
    }

    [Fact]
    public async Task Restore_Cancellable_ThrowsOnCancel()
    {
        var data    = RandomBytes(512, seed: 60);
        var srcFile = TempPath("src/file.bin");
        var backup  = TempPath("backup");
        var target  = TempPath("restore");
        await WriteFileAsync(srcFile, data);

        var br = await BackupEngine.RunAsync(srcFile, backup);

        using var cts = new CancellationTokenSource();
        await using var held = await BackupLock.AcquireAsync(backup);

        var restoreTask = BackupEngine.RestoreAsync(backup, br.RootHash, target, ct: cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restoreTask);
    }
}

internal static class DateTimeExtensions
{
    public static DateTime TruncateToSeconds(this DateTime dt)
        => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, dt.Kind);
}
