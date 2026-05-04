namespace Lillestora;

/// <summary>
/// Dateibasierter exklusiver Lock für das Backup-Verzeichnis.
/// Blockierte Prozesse warten (pollend) bis der Lock frei ist.
/// DeleteOnClose stellt sicher dass kein Stale-Lock zurückbleibt,
/// auch wenn der Prozess abstürzt.
/// </summary>
internal sealed class BackupLock : IAsyncDisposable
{
    private readonly FileStream _stream;

    private BackupLock(FileStream stream) => _stream = stream;

    public static async Task<BackupLock> AcquireAsync(
        string backupDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(backupDir);
        string lockPath = Path.Combine(backupDir, ".lock");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fs = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
                return new BackupLock(fs);
            }
            catch (IOException)
            {
                await Task.Delay(500, ct);
            }
        }
    }

    public async ValueTask DisposeAsync() => await _stream.DisposeAsync();
}
