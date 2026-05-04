using Lillestora;

namespace Lillestora.Tests;

public class DirectoryBackupTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public DirectoryBackupTests() => Directory.CreateDirectory(_tempDir);

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
    public async Task Directory_MultipleFiles_AllFilesInManifest()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");

        await WriteFileAsync(Path.Combine(srcDir, "a.bin"),          RandomBytes(1024, seed: 1));
        await WriteFileAsync(Path.Combine(srcDir, "b.bin"),          RandomBytes(1024, seed: 2));
        await WriteFileAsync(Path.Combine(srcDir, "sub", "c.bin"),   RandomBytes(1024, seed: 3));

        var result = await BackupEngine.RunAsync(srcDir, backup);
        var files  = await ManifestStore_FlatFiles(backup, result.RootHash);

        Assert.Equal(3, files.Count);
        Assert.Equal(3, result.TotalChunks); // each < MinChunkSize → 1 chunk per file
    }

    [Fact]
    public async Task Directory_RelativePaths_StoredCorrectly()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");

        await WriteFileAsync(Path.Combine(srcDir, "root.bin"),        RandomBytes(512, seed: 1));
        await WriteFileAsync(Path.Combine(srcDir, "sub", "deep.bin"), RandomBytes(512, seed: 2));

        var result = await BackupEngine.RunAsync(srcDir, backup);
        var files  = await ManifestStore_FlatFiles(backup, result.RootHash);
        var paths  = files.Select(f => f.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("root.bin", paths);
        Assert.Contains(Path.Combine("sub", "deep.bin"), paths);
    }

    [Fact]
    public async Task Directory_Timestamps_StoredInManifest()
    {
        string srcDir   = TempPath("src");
        string backup   = TempPath("backup");
        string filePath = Path.Combine(srcDir, "ts.bin");
        await WriteFileAsync(filePath, RandomBytes(512, seed: 5));

        var fi               = new FileInfo(filePath);
        var expectedCreated  = new DateTimeOffset(fi.CreationTimeUtc,  TimeSpan.Zero);
        var expectedModified = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);

        var result = await BackupEngine.RunAsync(srcDir, backup);
        var files  = await ManifestStore_FlatFiles(backup, result.RootHash);
        var entry  = files.Single().Entry;

        Assert.Equal(expectedCreated,  entry.Created);
        Assert.Equal(expectedModified, entry.Modified);
        Assert.Equal(fi.Length, entry.Size);
    }

    [Fact]
    public async Task Directory_FileSizes_StoredCorrectly()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");

        var data1 = RandomBytes(1000, seed: 10);
        var data2 = RandomBytes(2000, seed: 11);
        await WriteFileAsync(Path.Combine(srcDir, "small.bin"), data1);
        await WriteFileAsync(Path.Combine(srcDir, "large.bin"), data2);

        var result = await BackupEngine.RunAsync(srcDir, backup);
        var files  = await ManifestStore_FlatFiles(backup, result.RootHash);
        var sizes  = files.ToDictionary(f => f.RelPath, f => f.Entry.Size);

        Assert.Equal(data1.Length, sizes["small.bin"]);
        Assert.Equal(data2.Length, sizes["large.bin"]);
    }

    [Fact]
    public async Task Directory_Verify_AfterCleanBackup_ReturnsAllOk()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");

        await WriteFileAsync(Path.Combine(srcDir, "f1.bin"), RandomBytes(512, seed: 20));
        await WriteFileAsync(Path.Combine(srcDir, "f2.bin"), RandomBytes(512, seed: 21));

        await BackupEngine.RunAsync(srcDir, backup);
        var result = await BackupEngine.VerifyAsync(srcDir, backup);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.Corrupt);
    }

    [Fact]
    public async Task Directory_Verify_ViaRootHash_ReturnsAllOk()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");

        await WriteFileAsync(Path.Combine(srcDir, "x.bin"), RandomBytes(512, seed: 30));

        var br     = await BackupEngine.RunAsync(srcDir, backup);
        var result = await BackupEngine.VerifyAsync(br.RootHash, backup);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Directory_SharedChunksAcrossFiles_AreDeduplicated()
    {
        var data  = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 42);
        string srcDir = TempPath("src");
        string backup = TempPath("backup");

        await WriteFileAsync(Path.Combine(srcDir, "copy1.bin"), data);
        await WriteFileAsync(Path.Combine(srcDir, "copy2.bin"), data);

        var result = await BackupEngine.RunAsync(srcDir, backup);

        Assert.Equal(2, result.TotalChunks);
        Assert.Equal(1, result.Deduplicated);
    }

    [Fact]
    public async Task Directory_SourcePathInSnapshot_IsAbsolute()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");
        await WriteFileAsync(Path.Combine(srcDir, "file.bin"), RandomBytes(256, seed: 99));

        var result = await BackupEngine.RunAsync(srcDir, backup);
        var snaps  = await BackupEngine.ListSnapshotsAsync(backup);
        var snap   = snaps.Single(s => s.RootHash == result.RootHash);

        Assert.Equal(Path.GetFullPath(srcDir), snap.SourcePath);
    }

    [Fact]
    public async Task Directory_Cleanup_WorksAfterDirectoryBackup()
    {
        string srcDir = TempPath("src");
        string backup = TempPath("backup");

        await WriteFileAsync(Path.Combine(srcDir, "a.bin"), RandomBytes(512, seed: 50));
        await WriteFileAsync(Path.Combine(srcDir, "b.bin"), RandomBytes(512, seed: 51));

        var br = await BackupEngine.RunAsync(srcDir, backup);

        // Nothing should be unreferenced after a clean backup
        var r1 = await BackupEngine.CleanupAsync(backup, dryRun: false);
        Assert.Equal(0, r1.Unreferenced);

        // After removing the snapshot, all objects become orphaned
        await BackupEngine.RemoveSnapshotAsync(backup, br.RootHash);
        var r2 = await BackupEngine.CleanupAsync(backup, dryRun: false);
        Assert.True(r2.Unreferenced > 0);
    }

    // ---------------------------------------------------------------
    // Helper: flatten all file entries from a manifest tree

    private static async Task<List<(string RelPath, FileManifestEntry Entry)>> ManifestStore_FlatFiles(
        string backupDir, string rootHash)
        => await ManifestStore.FlattenFilesAsync(
               await OpenStore(backupDir), rootHash);

    private static async Task<PackStore> OpenStore(string backupDir)
        => await PackStore.OpenAsync(backupDir);
}
