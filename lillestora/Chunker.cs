using System.Buffers;
using System.Runtime.CompilerServices;

namespace Lillestora;

internal static class Chunker
{
    public const int   MinChunkSize = 80   * 1024;
    public const int   MaxChunkSize = 2560 * 1024;
    private const ulong ChunkMask   = 0x7FFFF;

    private static readonly ulong[] GearTable = BuildGearTable(seed: 0xC0FFEE_C0FFEEL);

    private static ulong[] BuildGearTable(long seed)
    {
        var rng   = new Random((int)(seed ^ (seed >> 32)));
        var table = new ulong[256];
        var buf   = new byte[8];
        for (int i = 0; i < 256; i++) { rng.NextBytes(buf); table[i] = BitConverter.ToUInt64(buf); }
        return table;
    }

    public static IEnumerable<string> EnumerateFiles(string sourcePath, bool isDir)
        => isDir
            ? Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).OrderBy(f => f)
            : [sourcePath];

    public static async IAsyncEnumerable<(int Index, long OffsetBytes, ReadOnlyMemory<byte> Data)> GetChunksAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 1 MB per read: fewer round trips on network shares, fits most chunks in a single read
        const int ReadSize = 1 * 1024 * 1024;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                      FileShare.Read, ReadSize, FileOptions.SequentialScan);

        var readBuf  = ArrayPool<byte>.Shared.Rent(ReadSize);
        var chunkBuf = ArrayPool<byte>.Shared.Rent(MaxChunkSize);
        try
        {
            int   pos        = 0;
            ulong gearHash   = 0;
            int   index      = 0;
            long  chunkStart = 0;

            while (true)
            {
                int n = await fs.ReadAsync(readBuf.AsMemory(0, ReadSize), ct);
                if (n == 0) break;

                int i = 0;
                while (i < n)
                {
                    // Phase 1: hash every byte but skip the cut check — same hash state as
                    // before the optimisation, so existing backups stay compatible.
                    while (pos < MinChunkSize && i < n)
                    {
                        byte b = readBuf[i++];
                        chunkBuf[pos++] = b;
                        gearHash = (gearHash << 1) + GearTable[b];
                    }
                    if (pos < MinChunkSize) break; // refill readBuf

                    // Phase 2: rolling-hash scan — pos >= MinChunkSize is guaranteed here,
                    // so the guard is gone from the hot-path inner loop.
                    for (; i < n; i++)
                    {
                        byte b = readBuf[i];
                        chunkBuf[pos++] = b;
                        gearHash = (gearHash << 1) + GearTable[b];

                        if ((gearHash & ChunkMask) == 0 || pos == MaxChunkSize)
                        {
                            // chunkBuf is valid until the next MoveNextAsync call;
                            // callers must finish using Data before then.
                            yield return (index++, chunkStart, chunkBuf.AsMemory(0, pos));
                            chunkStart += pos;
                            pos      = 0;
                            gearHash = 0;
                            i++;
                            break; // restart at phase 1 for next chunk
                        }
                    }
                }
            }

            if (pos > 0)
                yield return (index, chunkStart, chunkBuf.AsMemory(0, pos));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
            ArrayPool<byte>.Shared.Return(chunkBuf);
        }
    }
}
