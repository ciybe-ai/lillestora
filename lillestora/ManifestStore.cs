using System.IO.Hashing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lillestora;

internal static class ManifestStore
{
    // Compact JSON for objects stored in packs
    private static readonly JsonSerializerOptions ObjectOptions = new()
    {
        WriteIndented = false,
        Converters    = { new JsonStringEnumConverter() }
    };

    // Indented JSON for the human-readable index.json
    private static readonly JsonSerializerOptions IndexOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() }
    };

    // Store a DirManifest as an object in the pack store; return its hash.
    public static async Task<string> WriteManifestAsync(PackStore store, DirManifest manifest, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, ObjectOptions);
        string hash = ComputeHash(json);
        await store.WriteAsync(json, hash, ct);
        return hash;
    }

    public static async Task<DirManifest> ReadManifestAsync(PackStore store, string hash)
    {
        byte[] data = await store.ReadAsync(hash);
        return JsonSerializer.Deserialize<DirManifest>(data, ObjectOptions)
               ?? throw new InvalidDataException($"Ungültiges Manifest: {hash}");
    }

    // Snapshot index operations on index.json

    public static async Task AppendSnapshotAsync(string backupDir, Snapshot snapshot)
    {
        var list = await LoadSnapshotsAsync(backupDir);
        list.Add(snapshot);
        await WriteSnapshotsAsync(Path.Combine(backupDir, "index.json"), list);
    }

    public static async Task<List<Snapshot>> LoadSnapshotsAsync(string backupDir)
    {
        string path = Path.Combine(backupDir, "index.json");
        if (!File.Exists(path)) return [];
        string json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<List<Snapshot>>(json, IndexOptions) ?? [];
    }

    public static async Task RemoveSnapshotAsync(string backupDir, string rootHash)
    {
        var list = await LoadSnapshotsAsync(backupDir);
        list.RemoveAll(s => s.RootHash.Equals(rootHash, StringComparison.OrdinalIgnoreCase));
        await WriteSnapshotsAsync(Path.Combine(backupDir, "index.json"), list);
    }

    // Build flat file lookup for metadata-based skip (size + mtime unchanged → skip).
    public static async Task<Dictionary<string, FileManifestEntry>> LoadPreviousEntriesAsync(
        PackStore store, string backupDir, string sourcePath)
    {
        var    snapshots  = await LoadSnapshotsAsync(backupDir);
        string fullSource = Path.GetFullPath(sourcePath);

        var latest = snapshots
            .Where(s => s.SourcePath.Equals(fullSource, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Time)
            .FirstOrDefault();

        if (latest == null || !store.Contains(latest.RootHash)) return [];

        var result = new Dictionary<string, FileManifestEntry>(StringComparer.OrdinalIgnoreCase);
        await CollectFilesAsync(store, latest.RootHash, "", result);
        return result;
    }

    // Collect all referenced object hashes (manifest hashes + chunk hashes) for cleanup.
    public static async Task CollectReferencedHashesAsync(
        PackStore store, string rootHash, HashSet<string> referenced)
    {
        if (!referenced.Add(rootHash)) return;
        if (!store.Contains(rootHash))  return;

        var manifest = await ReadManifestAsync(store, rootHash);
        foreach (var entry in manifest.Entries)
        {
            switch (entry)
            {
                case FileManifestEntry f:
                    foreach (var chunk in f.Chunks)
                        referenced.Add(chunk.HashHex);
                    break;
                case DirManifestEntry d:
                    await CollectReferencedHashesAsync(store, d.Hash, referenced);
                    break;
            }
        }
    }

    // Flatten all file entries with their relative paths (for testing and restore helpers).
    public static async Task<List<(string RelPath, FileManifestEntry Entry)>> FlattenFilesAsync(
        PackStore store, string rootHash)
    {
        var result = new List<(string, FileManifestEntry)>();
        await CollectFilesWithPathAsync(store, rootHash, "", result);
        return result;
    }

    private static async Task CollectFilesAsync(
        PackStore store, string hash, string prefix,
        Dictionary<string, FileManifestEntry> result)
    {
        var manifest = await ReadManifestAsync(store, hash);
        foreach (var entry in manifest.Entries)
        {
            switch (entry)
            {
                case FileManifestEntry f:
                    result[prefix + f.Name] = f;
                    break;
                case DirManifestEntry d:
                    await CollectFilesAsync(store, d.Hash,
                        prefix + d.Name + Path.DirectorySeparatorChar, result);
                    break;
            }
        }
    }

    private static async Task CollectFilesWithPathAsync(
        PackStore store, string hash, string prefix,
        List<(string, FileManifestEntry)> result)
    {
        var manifest = await ReadManifestAsync(store, hash);
        foreach (var entry in manifest.Entries)
        {
            switch (entry)
            {
                case FileManifestEntry f:
                    result.Add((prefix + f.Name, f));
                    break;
                case DirManifestEntry d:
                    await CollectFilesWithPathAsync(store, d.Hash,
                        prefix + d.Name + Path.DirectorySeparatorChar, result);
                    break;
            }
        }
    }

    private static async Task WriteSnapshotsAsync(string indexPath, List<Snapshot> snapshots)
    {
        string tmp = indexPath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshots, IndexOptions));
        File.Move(tmp, indexPath, overwrite: true);
    }

    private static string ComputeHash(ReadOnlySpan<byte> data)
        => $"{XxHash64.HashToUInt64(data):X16}";
}
