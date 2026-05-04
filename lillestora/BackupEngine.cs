using System.Globalization;
using System.IO.Hashing;

namespace Lillestora;

public static class BackupEngine
{
    public const int MinChunkSize = Chunker.MinChunkSize;
    public const int MaxChunkSize = Chunker.MaxChunkSize;

    // ---------------------------------------------------------------
    // Backup

    public static async Task<BackupResult> RunAsync(
        string sourcePath,
        string backupDir,
        Action<ChunkResult>? onChunk = null,
        Action<string, Exception>? onError = null,
        CancellationToken ct = default)
    {
        await using var _ = await BackupLock.AcquireAsync(backupDir, ct);

        bool isDir = Directory.Exists(sourcePath);
        if (!isDir && !File.Exists(sourcePath))
            throw new FileNotFoundException($"Quelle nicht gefunden: {sourcePath}");

        await using var store = await PackStore.OpenAsync(backupDir);
        var prev              = await ManifestStore.LoadPreviousEntriesAsync(store, backupDir, sourcePath);
        var state             = new RunState();

        string rootHash;
        if (isDir)
        {
            string fullDir = Path.GetFullPath(sourcePath);
            rootHash = await BackupDirectoryAsync(fullDir, fullDir, store, prev, state, onChunk, onError, ct);
        }
        else
        {
            var fi    = new FileInfo(Path.GetFullPath(sourcePath));
            var entry = await BackupFileAsync(fi.FullName, fi.Name, fi, store, prev, state, onChunk, onError, ct);
            var mf    = new DirManifest(entry != null ? [entry] : []);
            rootHash  = await ManifestStore.WriteManifestAsync(store, mf, ct);
        }

        await store.SealAsync();
        var snapshot = new Snapshot(Path.GetFullPath(sourcePath), DateTimeOffset.Now, state.TotalBytes, rootHash);
        await ManifestStore.AppendSnapshotAsync(backupDir, snapshot);

        return new BackupResult(state.TotalChunks, state.Deduplicated, state.TotalBytes,
                                rootHash, state.FilesSkipped, state.FilesErrored);
    }

    private static async Task<string> BackupDirectoryAsync(
        string dirPath, string baseDir, PackStore store,
        Dictionary<string, FileManifestEntry> prev, RunState state,
        Action<ChunkResult>? onChunk, Action<string, Exception>? onError,
        CancellationToken ct)
    {
        var entries = new List<ManifestEntry>();

        foreach (string filePath in Directory.EnumerateFiles(dirPath).OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            var    fi      = new FileInfo(filePath);
            string relPath = Path.GetRelativePath(baseDir, filePath);
            var    entry   = await BackupFileAsync(filePath, relPath, fi, store, prev, state, onChunk, onError, ct);
            if (entry != null) entries.Add(entry);
        }

        foreach (string subDir in Directory.EnumerateDirectories(dirPath).OrderBy(d => d))
        {
            ct.ThrowIfCancellationRequested();
            string subName = Path.GetFileName(subDir);
            string subHash = await BackupDirectoryAsync(subDir, baseDir, store, prev, state, onChunk, onError, ct);
            entries.Add(new DirManifestEntry(subName, subHash));
        }

        return await ManifestStore.WriteManifestAsync(store, new DirManifest(entries), ct);
    }

    private static async Task<ManifestEntry?> BackupFileAsync(
        string filePath, string relPath, FileInfo fi, PackStore store,
        Dictionary<string, FileManifestEntry> prev, RunState state,
        Action<ChunkResult>? onChunk, Action<string, Exception>? onError,
        CancellationToken ct)
    {
        // Fast path: size and mtime unchanged → reuse chunks from the previous snapshot
        if (prev.TryGetValue(relPath, out var p)
            && fi.Length           == p.Size
            && fi.LastWriteTimeUtc == p.Modified.UtcDateTime)
        {
            foreach (var chunk in p.Chunks)
            {
                onChunk?.Invoke(new ChunkResult(chunk.Index, chunk.OffsetBytes, chunk.HashHex, true, relPath));
                state.TotalChunks++;
                state.Deduplicated++;
            }
            state.TotalBytes += fi.Length;
            state.FilesSkipped++;
            return p; // reuse existing entry (same hash → manifest deduped too)
        }

        var chunks = new List<ManifestChunk>();
        try
        {
            await foreach (var (idx, offsetBytes, data) in Chunker.GetChunksAsync(filePath, ct))
            {
                ulong  hash    = XxHash64.HashToUInt64(data.Span);
                string hashHex = $"{hash:X16}";

                bool isNew = await store.WriteAsync(data, hashHex, ct);
                if (!isNew) state.Deduplicated++;

                chunks.Add(new ManifestChunk(idx, offsetBytes, hashHex));
                onChunk?.Invoke(new ChunkResult(idx, offsetBytes, hashHex, !isNew, relPath));

                state.TotalBytes += data.Length;
                state.TotalChunks++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(relPath, ex);
            state.FilesErrored++;
            return null;
        }

        return new FileManifestEntry(
            Name:     fi.Name,
            Created:  new DateTimeOffset(fi.CreationTimeUtc,  TimeSpan.Zero),
            Modified: new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
            Size:     fi.Length,
            Chunks:   chunks);
    }

    private sealed class RunState
    {
        public int   TotalChunks;
        public int   Deduplicated;
        public long  TotalBytes;
        public int   FilesSkipped;
        public int   FilesErrored;
    }

    // ---------------------------------------------------------------
    // Verify — auto-detects whether the first argument is a root hash (16 hex chars)
    // or a source path. Root-hash mode traverses the stored manifest tree without
    // needing the source files; source-path mode re-chunks the source for comparison.

    public static async Task<VerifyResult> VerifyAsync(
        string sourcePathOrRootHash,
        string backupDir,
        Action<ChunkVerifyResult>? onChunk = null,
        CancellationToken ct = default)
    {
        if (IsRootHash(sourcePathOrRootHash))
        {
            await using var _ = await BackupLock.AcquireAsync(backupDir, ct);
            await using var store = await PackStore.OpenAsync(backupDir);
            return await VerifyManifestAsync(store, sourcePathOrRootHash, "", onChunk, ct);
        }

        string sourcePath = sourcePathOrRootHash;
        await using var _2 = await BackupLock.AcquireAsync(backupDir, ct);
        await using var store2 = await PackStore.OpenAsync(backupDir);

        bool isDir = Directory.Exists(sourcePath);
        if (!isDir && !File.Exists(sourcePath))
            throw new FileNotFoundException($"Quelle nicht gefunden: {sourcePath}");

        string baseDir = isDir
            ? Path.GetFullPath(sourcePath)
            : Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;

        int total = 0, missing = 0, corrupt = 0;

        foreach (string filePath in Chunker.EnumerateFiles(sourcePath, isDir))
        {
            ct.ThrowIfCancellationRequested();
            string relPath = isDir
                ? Path.GetRelativePath(baseDir, filePath)
                : Path.GetFileName(filePath);

            await foreach (var (idx, offsetBytes, data) in Chunker.GetChunksAsync(filePath, ct))
            {
                ulong  sourceHash = XxHash64.HashToUInt64(data.Span);
                string hashHex    = $"{sourceHash:X16}";
                var    status     = await store2.VerifyAsync(hashHex, sourceHash);
                if (status == ChunkVerifyStatus.Missing) missing++;
                else if (status == ChunkVerifyStatus.Corrupt) corrupt++;
                onChunk?.Invoke(new ChunkVerifyResult(idx, offsetBytes, hashHex, status, relPath));
                total++;
            }
        }

        return new VerifyResult(total, missing, corrupt);
    }

    private static bool IsRootHash(string s)
        => s.Length == 16 && s.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f'));

    private static async Task<VerifyResult> VerifyManifestAsync(
        PackStore store, string manifestHash, string relPrefix,
        Action<ChunkVerifyResult>? onChunk, CancellationToken ct)
    {
        if (!store.Contains(manifestHash))
            return new VerifyResult(0, 1, 0); // manifest itself is missing

        var manifest = await ManifestStore.ReadManifestAsync(store, manifestHash);
        int total = 0, missing = 0, corrupt = 0;

        foreach (var entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry is FileManifestEntry f)
            {
                string relPath = relPrefix.Length > 0 ? Path.Combine(relPrefix, f.Name) : f.Name;
                foreach (var chunk in f.Chunks)
                {
                    ulong expected = ulong.Parse(chunk.HashHex, NumberStyles.HexNumber);
                    var   status   = await store.VerifyAsync(chunk.HashHex, expected);
                    if (status == ChunkVerifyStatus.Missing) missing++;
                    else if (status == ChunkVerifyStatus.Corrupt) corrupt++;
                    onChunk?.Invoke(new ChunkVerifyResult(chunk.Index, chunk.OffsetBytes, chunk.HashHex, status, relPath));
                    total++;
                }
            }
            else if (entry is DirManifestEntry d)
            {
                string newPrefix = relPrefix.Length > 0 ? Path.Combine(relPrefix, d.Name) : d.Name;
                var sub = await VerifyManifestAsync(store, d.Hash, newPrefix, onChunk, ct);
                total   += sub.TotalChunks;
                missing += sub.Missing;
                corrupt += sub.Corrupt;
            }
        }

        return new VerifyResult(total, missing, corrupt);
    }

    // ---------------------------------------------------------------
    // Cleanup — repack referenced objects, discard orphans

    public static async Task<CleanupResult> CleanupAsync(
        string backupDir,
        bool dryRun = true,
        Action<string>? onUnreferenced = null,
        CancellationToken ct = default)
    {
        await using var _ = await BackupLock.AcquireAsync(backupDir, ct);
        await using var store = await PackStore.OpenAsync(backupDir);

        var snapshots  = await ManifestStore.LoadSnapshotsAsync(backupDir);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var snap in snapshots)
        {
            ct.ThrowIfCancellationRequested();
            await ManifestStore.CollectReferencedHashesAsync(store, snap.RootHash, referenced);
        }

        var allEntries    = store.AllEntries().ToList();
        var orphaned      = allEntries.Where(e => !referenced.Contains(e.Hash)).ToList();
        long freedBytes   = orphaned.Sum(e => (long)e.Len);

        foreach (var e in orphaned)
            onUnreferenced?.Invoke(e.Hash);

        if (!dryRun && orphaned.Count > 0)
        {
            // Read all referenced blobs before touching the pack files
            var repack = new List<(string Hash, byte[] Compressed)>();
            foreach (var e in allEntries.Where(e => referenced.Contains(e.Hash)))
            {
                ct.ThrowIfCancellationRequested();
                repack.Add((e.Hash, await store.ReadCompressedAsync(e.Hash)));
            }

            // Delete old packs + idx files
            string packsDir = Path.Combine(backupDir, "packs");
            foreach (string f in Directory.GetFiles(packsDir))
                File.Delete(f);

            // Write new packs with only referenced objects
            await using var newStore = await PackStore.OpenAsync(backupDir);
            foreach (var (hash, compressed) in repack)
            {
                ct.ThrowIfCancellationRequested();
                await newStore.WriteCompressedAsync(hash, compressed, ct);
            }
        }

        return new CleanupResult(referenced.Count, orphaned.Count, freedBytes, dryRun);
    }

    // ---------------------------------------------------------------
    // Restore

    public static async Task<RestoreResult> RestoreAsync(
        string backupDir,
        string rootHash,
        string targetDir,
        Action<RestoreFileProgress>? onFile = null,
        CancellationToken ct = default)
    {
        await using var _ = await BackupLock.AcquireAsync(backupDir, ct);
        await using var store = await PackStore.OpenAsync(backupDir);
        string fullTarget = Path.GetFullPath(targetDir);
        return await RestoreManifestAsync(store, rootHash, fullTarget, "", onFile, ct);
    }

    public static Task<RestoreResult> RestoreAsync(
        Snapshot snapshot,
        string backupDir,
        string targetDir,
        Action<RestoreFileProgress>? onFile = null,
        CancellationToken ct = default)
        => RestoreAsync(backupDir, snapshot.RootHash, targetDir, onFile, ct);

    // Internal overload: restore directly from a DirManifest object (used in path-traversal tests).
    internal static async Task<RestoreResult> RestoreAsync(
        string backupDir,
        DirManifest rootManifest,
        string targetDir,
        Action<RestoreFileProgress>? onFile = null,
        CancellationToken ct = default)
    {
        await using var _ = await BackupLock.AcquireAsync(backupDir, ct);
        await using var store = await PackStore.OpenAsync(backupDir);
        string fullTarget = Path.GetFullPath(targetDir);
        return await RestoreManifestAsync(store, rootManifest, fullTarget, "", onFile, ct);
    }

    private static async Task<RestoreResult> RestoreManifestAsync(
        PackStore store, string manifestHash, string fullTarget, string relPrefix,
        Action<RestoreFileProgress>? onFile, CancellationToken ct)
    {
        var manifest = await ManifestStore.ReadManifestAsync(store, manifestHash);
        return await RestoreManifestAsync(store, manifest, fullTarget, relPrefix, onFile, ct);
    }

    private static async Task<RestoreResult> RestoreManifestAsync(
        PackStore store, DirManifest manifest, string fullTarget, string relPrefix,
        Action<RestoreFileProgress>? onFile, CancellationToken ct)
    {
        int restored = 0; long totalBytes = 0;

        foreach (var entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry is FileManifestEntry f)
            {
                string relPath  = relPrefix.Length > 0 ? Path.Combine(relPrefix, f.Name) : f.Name;
                string destPath = Path.GetFullPath(Path.Combine(fullTarget, relPath));

                if (!destPath.StartsWith(fullTarget + Path.DirectorySeparatorChar,    StringComparison.OrdinalIgnoreCase)
                 && !destPath.StartsWith(fullTarget + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Pfad außerhalb des Zielverzeichnisses: {destPath}");

                if (File.Exists(destPath))
                    throw new IOException($"Datei existiert bereits: {destPath}");

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                await using (var outStream = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write))
                    foreach (var chunk in f.Chunks.OrderBy(c => c.Index))
                    {
                        ct.ThrowIfCancellationRequested();
                        byte[] data = await store.ReadAsync(chunk.HashHex);
                        await outStream.WriteAsync(data, ct);
                    }

                File.SetCreationTimeUtc(destPath,  f.Created.UtcDateTime);
                File.SetLastWriteTimeUtc(destPath, f.Modified.UtcDateTime);

                onFile?.Invoke(new RestoreFileProgress(relPath, f.Size));
                restored++;
                totalBytes += f.Size;
            }
            else if (entry is DirManifestEntry d)
            {
                string newPrefix = relPrefix.Length > 0 ? Path.Combine(relPrefix, d.Name) : d.Name;
                var sub = await RestoreManifestAsync(store, d.Hash, fullTarget, newPrefix, onFile, ct);
                restored   += sub.FilesRestored;
                totalBytes += sub.TotalBytes;
            }
        }

        return new RestoreResult(restored, totalBytes);
    }

    // ---------------------------------------------------------------
    // Public helpers

    public static Task<List<Snapshot>> ListSnapshotsAsync(string backupDir)
        => ManifestStore.LoadSnapshotsAsync(backupDir);

    public static async Task<DirManifest> LoadDirManifestAsync(string backupDir, string rootHash)
    {
        await using var store = await PackStore.OpenAsync(backupDir);
        return await ManifestStore.ReadManifestAsync(store, rootHash);
    }

    // Still exposed for tests that round-trip chunks directly
    public static async Task<byte[]> ReadChunkAsync(string backupDir, string hashHex)
    {
        await using var store = await PackStore.OpenAsync(backupDir);
        return await store.ReadAsync(hashHex);
    }

    // ---------------------------------------------------------------
    // Internal test helpers

    internal static Task RemoveSnapshotAsync(string backupDir, string rootHash)
        => ManifestStore.RemoveSnapshotAsync(backupDir, rootHash);

    internal static async Task<int> CountObjectsAsync(string backupDir)
    {
        await using var store = await PackStore.OpenAsync(backupDir);
        return store.ObjectCount;
    }

    internal static async Task<long> TotalPackBytesAsync(string backupDir)
    {
        await using var store = await PackStore.OpenAsync(backupDir);
        return store.TotalCompressedBytes;
    }

    internal static async Task CorruptChunkForTestAsync(string backupDir, string hashHex)
    {
        await using var store = await PackStore.OpenAsync(backupDir);
        await store.CorruptEntryAsync(hashHex);
    }

    internal static async Task DeletePackFilesAsync(string backupDir)
    {
        string packsDir = Path.Combine(backupDir, "packs");
        if (!Directory.Exists(packsDir)) return;
        foreach (string f in Directory.GetFiles(packsDir, "*.pack"))
            File.Delete(f);
    }
}
