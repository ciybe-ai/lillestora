using System.IO.Compression;
using System.IO.Hashing;
using System.Text.Json;

namespace Lillestora;

// Content-addressable object store backed by pack files.
// All objects (file chunks and directory manifests) are stored here.
// Format: pack files are binary concatenations of gzip-compressed blobs;
// each pack has a sidecar .idx file that maps hash → [offset, compressed_length].
internal sealed class PackStore : IAsyncDisposable
{
    private const long MaxPackBytes = 64L * 1024 * 1024; // 64 MB per pack

    private readonly string _packsDir;
    private readonly Dictionary<string, (string Pack, long Offset, int Len)> _index
        = new(StringComparer.OrdinalIgnoreCase);

    // State of the currently-open write pack (lazily created on first write)
    private FileStream? _ws;
    private string      _wsPath = "";
    private Dictionary<string, (long Offset, int Len)> _wsIdx = new();

    private PackStore(string packsDir) => _packsDir = packsDir;

    public static async Task<PackStore> OpenAsync(string backupDir)
    {
        string packsDir = Path.Combine(backupDir, "packs");
        Directory.CreateDirectory(packsDir);
        var store = new PackStore(packsDir);

        foreach (string idxPath in Directory.GetFiles(packsDir, "*.idx"))
        {
            string packPath = Path.ChangeExtension(idxPath, ".pack");
            if (!File.Exists(packPath)) continue;

            await using var s   = File.OpenRead(idxPath);
            var entries         = await JsonSerializer.DeserializeAsync<Dictionary<string, long[]>>(s);
            if (entries == null) continue;
            foreach (var (hash, arr) in entries)
                store._index[hash] = (packPath, arr[0], (int)arr[1]);
        }
        return store;
    }

    public bool Contains(string hashHex) => _index.ContainsKey(hashHex);

    // Returns true if the object was written, false if it already existed (deduplicated).
    public async Task<bool> WriteAsync(ReadOnlyMemory<byte> raw, string hashHex, CancellationToken ct)
    {
        if (_index.ContainsKey(hashHex)) return false;

        byte[] compressed = await CompressAsync(raw, ct);
        await EnsureWriteStreamAsync();

        long offset      = _ws!.Position;
        await _ws.WriteAsync(compressed, ct);

        _wsIdx[hashHex] = (offset, compressed.Length);
        _index[hashHex] = (_wsPath, offset, compressed.Length);

        if (_ws.Position >= MaxPackBytes)
            await SealCurrentAsync();

        return true;
    }

    // Write pre-compressed bytes directly (used during repack to avoid double-compression).
    public async Task WriteCompressedAsync(string hashHex, byte[] compressed, CancellationToken ct)
    {
        if (_index.ContainsKey(hashHex)) return;

        await EnsureWriteStreamAsync();
        long offset      = _ws!.Position;
        await _ws.WriteAsync(compressed, ct);

        _wsIdx[hashHex] = (offset, compressed.Length);
        _index[hashHex] = (_wsPath, offset, compressed.Length);

        if (_ws.Position >= MaxPackBytes)
            await SealCurrentAsync();
    }

    public async Task<byte[]> ReadAsync(string hashHex)
    {
        if (!_index.TryGetValue(hashHex, out var loc))
            throw new KeyNotFoundException($"Objekt nicht gefunden: {hashHex}");

        byte[] compressed = await ReadCompressedAsync(hashHex);
        return await DecompressAsync(compressed);
    }

    public async Task<byte[]> ReadCompressedAsync(string hashHex)
    {
        if (!_index.TryGetValue(hashHex, out var loc))
            throw new KeyNotFoundException($"Objekt nicht gefunden: {hashHex}");

        using var fs       = new FileStream(loc.Pack, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(loc.Offset, SeekOrigin.Begin);
        var compressed     = new byte[loc.Len];
        await fs.ReadExactlyAsync(compressed);
        return compressed;
    }

    public async Task<ChunkVerifyStatus> VerifyAsync(string hashHex, ulong expectedHash)
    {
        if (!_index.ContainsKey(hashHex)) return ChunkVerifyStatus.Missing;
        try
        {
            byte[] data       = await ReadAsync(hashHex);
            ulong  storedHash = XxHash64.HashToUInt64(data);
            return storedHash == expectedHash ? ChunkVerifyStatus.Ok : ChunkVerifyStatus.Corrupt;
        }
        catch (FileNotFoundException)      { return ChunkVerifyStatus.Missing; }
        catch (DirectoryNotFoundException) { return ChunkVerifyStatus.Missing; }
        catch (InvalidDataException)       { return ChunkVerifyStatus.Corrupt; }
        catch (IOException)                { return ChunkVerifyStatus.Corrupt; }
    }

    public IEnumerable<(string Hash, string Pack, long Offset, int Len)> AllEntries()
        => _index.Select(kv => (kv.Key, kv.Value.Pack, kv.Value.Offset, kv.Value.Len));

    public async Task SealAsync()
    {
        if (_ws != null) await SealCurrentAsync();
    }

    // --- Internal test helpers ---

    internal (string Pack, long Offset, int Len) GetEntry(string hashHex)
        => _index[hashHex];

    internal async Task CorruptEntryAsync(string hashHex)
    {
        if (!_index.TryGetValue(hashHex, out var loc)) return;
        using var fs = new FileStream(loc.Pack, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(loc.Offset, SeekOrigin.Begin);
        var buf = new byte[Math.Min(loc.Len, 16)];
        await fs.ReadExactlyAsync(buf);
        for (int i = 0; i < buf.Length; i++) buf[i] ^= 0xFF;
        fs.Seek(loc.Offset, SeekOrigin.Begin);
        await fs.WriteAsync(buf);
    }

    internal int ObjectCount => _index.Count;

    internal long TotalCompressedBytes => _index.Values.Sum(v => (long)v.Len);

    // --- Private helpers ---

    private async Task EnsureWriteStreamAsync()
    {
        if (_ws != null) return;
        int next  = Directory.GetFiles(_packsDir, "*.pack").Length + 1;
        _wsPath   = Path.Combine(_packsDir, $"{next:D10}.pack");
        _ws       = new FileStream(_wsPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _wsIdx    = new Dictionary<string, (long, int)>();
    }

    private async Task SealCurrentAsync()
    {
        if (_ws == null) return;
        await _ws.FlushAsync();
        await _ws.DisposeAsync();
        _ws = null;

        string idxPath = Path.ChangeExtension(_wsPath, ".idx");
        var    idxData = _wsIdx.ToDictionary(kv => kv.Key, kv => new long[] { kv.Value.Offset, kv.Value.Len });
        await File.WriteAllTextAsync(idxPath, JsonSerializer.Serialize(idxData));
        _wsIdx = new Dictionary<string, (long, int)>();
    }

    private static async Task<byte[]> CompressAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            await gz.WriteAsync(data, ct);
        return ms.ToArray();
    }

    private static async Task<byte[]> DecompressAsync(byte[] compressed)
    {
        using var inMs  = new MemoryStream(compressed);
        using var gz    = new GZipStream(inMs, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        await gz.CopyToAsync(outMs);
        return outMs.ToArray();
    }

    public async ValueTask DisposeAsync() => await SealAsync();
}
