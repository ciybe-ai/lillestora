using System.Text.Json.Serialization;

namespace Lillestora;

public record ManifestChunk(int Index, long OffsetBytes, string HashHex);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FileManifestEntry), "file")]
[JsonDerivedType(typeof(DirManifestEntry),  "dir")]
public abstract record ManifestEntry(string Name);

public sealed record FileManifestEntry(
    string Name, DateTimeOffset Created, DateTimeOffset Modified,
    long Size, List<ManifestChunk> Chunks) : ManifestEntry(Name);

public sealed record DirManifestEntry(string Name, string Hash) : ManifestEntry(Name);

public record DirManifest(List<ManifestEntry> Entries);

public record Snapshot(string SourcePath, DateTimeOffset Time, long TotalBytes, string RootHash);

public record ChunkResult(int Index, long OffsetBytes, string HashHex, bool Deduplicated, string RelativePath);
public record BackupResult(int TotalChunks, int Deduplicated, long TotalBytes, string RootHash, int FilesSkipped, int FilesErrored);

public enum ChunkVerifyStatus { Ok, Missing, Corrupt }
public record ChunkVerifyResult(int Index, long OffsetBytes, string HashHex, ChunkVerifyStatus Status, string RelativePath);
public record VerifyResult(int TotalChunks, int Missing, int Corrupt)
{
    public bool IsValid => Missing == 0 && Corrupt == 0;
}

public record CleanupResult(int ReferencedObjects, int Unreferenced, long FreedBytes, bool DryRun);
public record RestoreFileProgress(string RelativePath, long Bytes);
public record RestoreResult(int FilesRestored, long TotalBytes);
