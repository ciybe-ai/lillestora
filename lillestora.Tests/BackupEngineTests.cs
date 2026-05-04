using System.IO.Hashing;
using Lillestora;

namespace Lillestora.Tests;

public class BackupEngineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public BackupEngineTests() => Directory.CreateDirectory(_tempDir);

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

    private static string HashHex(ReadOnlySpan<byte> data)
        => $"{XxHash64.HashToUInt64(data):X16}";

    // ---------------------------------------------------------------
    // Backup tests

    [Fact]
    public async Task SmallFile_ProducesExactlyOneChunk()
    {
        var data    = RandomBytes(BackupEngine.MinChunkSize / 2);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        var result = await BackupEngine.RunAsync(srcFile, backup);

        Assert.Equal(1, result.TotalChunks);
        Assert.Equal(data.Length, (int)result.TotalBytes);
    }

    [Fact]
    public async Task SingleChunk_CanBeReadBack()
    {
        var data    = RandomBytes(BackupEngine.MinChunkSize / 2);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        var result = await BackupEngine.RunAsync(srcFile, backup);

        // The root manifest is a DirManifest with one FileManifestEntry
        var root  = await BackupEngine.LoadDirManifestAsync(backup, result.RootHash);
        var entry = (FileManifestEntry)root.Entries[0];
        byte[] recovered = await BackupEngine.ReadChunkAsync(backup, entry.Chunks[0].HashHex);
        Assert.Equal(data, recovered);
    }

    [Fact]
    public async Task ChunkContent_RoundTrips_AfterDecompression()
    {
        var data    = RandomBytes(BackupEngine.MinChunkSize / 2);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        await BackupEngine.RunAsync(srcFile, backup);

        byte[] recovered = await BackupEngine.ReadChunkAsync(backup, HashHex(data));
        Assert.Equal(data, recovered);
    }

    [Fact]
    public async Task LargeFile_ProducesMultipleChunks()
    {
        var data    = RandomBytes(3 * BackupEngine.MaxChunkSize);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        var result = await BackupEngine.RunAsync(srcFile, backup);

        Assert.True(result.TotalChunks >= 3);
        Assert.Equal(data.Length, (int)result.TotalBytes);
    }

    [Fact]
    public async Task TwoFilesWithSameContent_SecondIsFullyDeduplicated()
    {
        var data   = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 42);
        var backup = TempFile("backup");

        var src1 = TempFile("file1.bin");
        var src2 = TempFile("file2.bin");
        await WriteFileAsync(src1, data);
        await WriteFileAsync(src2, data);

        var r1 = await BackupEngine.RunAsync(src1, backup);
        var r2 = await BackupEngine.RunAsync(src2, backup);

        Assert.Equal(1, r1.TotalChunks);
        Assert.Equal(1, r2.TotalChunks);
        Assert.Equal(1, r2.Deduplicated);
    }

    [Fact]
    public async Task SecondRun_SameFile_AllChunksDeduplicated()
    {
        var data    = RandomBytes(BackupEngine.MaxChunkSize + BackupEngine.MinChunkSize);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        await BackupEngine.RunAsync(srcFile, backup);
        var result2 = await BackupEngine.RunAsync(srcFile, backup);

        Assert.Equal(result2.TotalChunks, result2.Deduplicated);
    }

    [Fact]
    public async Task BackupReportsCorrectTotalBytes()
    {
        var data    = RandomBytes(200_000);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        var result = await BackupEngine.RunAsync(srcFile, backup);

        Assert.Equal(data.Length, (int)result.TotalBytes);
    }

    [Fact]
    public async Task ChunkData_IsStoredCompressed()
    {
        // All-zeros data compresses very well — total pack bytes should be much smaller.
        var data    = new byte[BackupEngine.MinChunkSize / 2];
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        await BackupEngine.RunAsync(srcFile, backup);

        long packBytes = await BackupEngine.TotalPackBytesAsync(backup);
        Assert.True(packBytes < data.Length);
    }

    // ---------------------------------------------------------------
    // Snapshot / manifest tests

    [Fact]
    public async Task Backup_CreatesRootHashAndSnapshot()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(1024));

        var result = await BackupEngine.RunAsync(srcFile, backup);

        Assert.Equal(16, result.RootHash.Length);
        Assert.True(result.RootHash.All(c => "0123456789ABCDEFabcdef".Contains(c)));

        var snaps = await BackupEngine.ListSnapshotsAsync(backup);
        Assert.Single(snaps);
        Assert.Equal(result.RootHash, snaps[0].RootHash);
    }

    [Fact]
    public async Task Snapshot_ContainsCorrectMetadata()
    {
        var data    = RandomBytes(1024);
        var srcFile = TempFile("myfile.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        var before = DateTimeOffset.Now;
        var result = await BackupEngine.RunAsync(srcFile, backup);
        var after  = DateTimeOffset.Now;

        var snaps = await BackupEngine.ListSnapshotsAsync(backup);
        var snap  = snaps.Single(s => s.RootHash == result.RootHash);

        Assert.Equal(Path.GetFullPath(srcFile), snap.SourcePath);
        Assert.Equal(data.Length, (int)snap.TotalBytes);
        Assert.True(snap.Time >= before && snap.Time <= after);
    }

    [Fact]
    public async Task Manifest_ChunksAreOrderedAndCoverFullFile()
    {
        var data    = RandomBytes(BackupEngine.MaxChunkSize + BackupEngine.MinChunkSize);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        var result = await BackupEngine.RunAsync(srcFile, backup);
        var root   = await BackupEngine.LoadDirManifestAsync(backup, result.RootHash);
        var entry  = (FileManifestEntry)root.Entries[0];

        Assert.True(entry.Chunks.Count >= 2);
        Assert.Equal(0L, entry.Chunks[0].OffsetBytes);

        for (int i = 0; i < entry.Chunks.Count; i++)
            Assert.Equal(i, entry.Chunks[i].Index);

        long coveredBytes = entry.Chunks
            .Zip(entry.Chunks.Skip(1), (a, b) => b.OffsetBytes - a.OffsetBytes)
            .Sum();
        coveredBytes += entry.Size - entry.Chunks[^1].OffsetBytes;
        Assert.Equal(entry.Size, coveredBytes);
    }

    [Fact]
    public async Task MultipleRuns_EachCreatesOwnSnapshot()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(1024, seed: 7));
        var r1 = await BackupEngine.RunAsync(srcFile, backup);

        await WriteFileAsync(srcFile, RandomBytes(1024, seed: 8));
        var r2 = await BackupEngine.RunAsync(srcFile, backup);

        Assert.NotEqual(r1.RootHash, r2.RootHash);
        var snaps = await BackupEngine.ListSnapshotsAsync(backup);
        Assert.Equal(2, snaps.Count);
    }

    // ---------------------------------------------------------------
    // Verify tests

    [Fact]
    public async Task Verify_AfterCleanBackup_ReturnsAllOk()
    {
        var data    = RandomBytes(BackupEngine.MaxChunkSize + BackupEngine.MinChunkSize, seed: 10);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        await BackupEngine.RunAsync(srcFile, backup);
        var result = await BackupEngine.VerifyAsync(srcFile, backup);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.Corrupt);
    }

    [Fact]
    public async Task Verify_ViaRootHash_AfterCleanBackup_ReturnsAllOk()
    {
        var data    = RandomBytes(BackupEngine.MaxChunkSize + BackupEngine.MinChunkSize, seed: 11);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        var br     = await BackupEngine.RunAsync(srcFile, backup);
        var result = await BackupEngine.VerifyAsync(br.RootHash, backup);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Verify_WithDeletedPackFile_ReportsMissing()
    {
        var data    = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 20);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        await BackupEngine.RunAsync(srcFile, backup);

        // Delete the pack files — idx files remain so hashes are in the index but reads fail
        await BackupEngine.DeletePackFilesAsync(backup);

        var result = await BackupEngine.VerifyAsync(srcFile, backup);

        Assert.False(result.IsValid);
        Assert.Equal(1, result.Missing);
        Assert.Equal(0, result.Corrupt);
    }

    [Fact]
    public async Task Verify_WithCorruptChunk_ReportsCorrupt()
    {
        var data    = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 30);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        await BackupEngine.RunAsync(srcFile, backup);
        await BackupEngine.CorruptChunkForTestAsync(backup, HashHex(data));

        var result = await BackupEngine.VerifyAsync(srcFile, backup);

        Assert.False(result.IsValid);
        Assert.Equal(0, result.Missing);
        Assert.Equal(1, result.Corrupt);
    }

    [Fact]
    public async Task Verify_WithoutPriorBackup_ReportsAllMissing()
    {
        var data    = RandomBytes(BackupEngine.MaxChunkSize + 1024, seed: 40);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        var result = await BackupEngine.VerifyAsync(srcFile, backup);

        Assert.True(result.Missing >= 2);
        Assert.False(result.IsValid);
    }

    // ---------------------------------------------------------------
    // Metadata-skip tests

    [Fact]
    public async Task Backup_UnchangedFile_IsSkipped()
    {
        var data    = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 50);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        await BackupEngine.RunAsync(srcFile, backup);
        int objectsBefore = await BackupEngine.CountObjectsAsync(backup);

        var r2 = await BackupEngine.RunAsync(srcFile, backup);

        Assert.Equal(1, r2.FilesSkipped);
        Assert.Equal(r2.TotalChunks, r2.Deduplicated);
        // Unchanged file → same manifest hash → no new objects written
        int objectsAfter = await BackupEngine.CountObjectsAsync(backup);
        Assert.Equal(objectsBefore, objectsAfter);
    }

    [Fact]
    public async Task Backup_ChangedMtime_IsReread()
    {
        var data    = RandomBytes(BackupEngine.MinChunkSize / 2, seed: 51);
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, data);

        await BackupEngine.RunAsync(srcFile, backup);
        File.SetLastWriteTimeUtc(srcFile, File.GetLastWriteTimeUtc(srcFile).AddSeconds(1));

        var r2 = await BackupEngine.RunAsync(srcFile, backup);

        Assert.Equal(0, r2.FilesSkipped);
    }

    [Fact]
    public async Task Backup_ChangedSize_IsReread()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(BackupEngine.MinChunkSize / 2, seed: 52));

        await BackupEngine.RunAsync(srcFile, backup);
        await WriteFileAsync(srcFile, RandomBytes(BackupEngine.MinChunkSize / 2 + 1, seed: 53));

        var r2 = await BackupEngine.RunAsync(srcFile, backup);

        Assert.Equal(0, r2.FilesSkipped);
    }

    [Fact]
    public async Task Backup_FirstRun_NothingSkipped()
    {
        var srcFile = TempFile("source.bin");
        var backup  = TempFile("backup");
        await WriteFileAsync(srcFile, RandomBytes(BackupEngine.MinChunkSize / 2, seed: 54));

        var result = await BackupEngine.RunAsync(srcFile, backup);

        Assert.Equal(0, result.FilesSkipped);
    }
}
