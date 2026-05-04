using Lillestora;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  lillestora backup  <quelle> <backup-verzeichnis>");
    Console.Error.WriteLine("  lillestora verify  <quelle|root-hash> <backup-verzeichnis>");
    Console.Error.WriteLine("  lillestora restore <backup-verzeichnis> <root-hash> <ziel-verzeichnis>");
    Console.Error.WriteLine("  lillestora list    <backup-verzeichnis>");
    Console.Error.WriteLine("  lillestora cleanup <backup-verzeichnis>          (dry-run)");
    Console.Error.WriteLine("  lillestora cleanup <backup-verzeichnis> --force  (tatsächlich löschen)");
    return 1;
}

string command = args[0].ToLowerInvariant();

if (command == "list")
{
    string backupDir = args[1];
    var snapshots    = await BackupEngine.ListSnapshotsAsync(backupDir);
    if (snapshots.Count == 0)
    {
        Console.WriteLine("Keine Sicherungen gefunden.");
        return 0;
    }
    foreach (var s in Enumerable.Reverse(snapshots))
        Console.WriteLine($"{s.RootHash}  {s.Time:yyyy-MM-dd HH:mm:ss}  {s.SourcePath}  ({s.TotalBytes / (1024.0 * 1024.0):F2} MB)");
    return 0;
}

if (command == "cleanup")
{
    string backupDir = args[1];
    bool   force     = args.Length > 2 && args[2].Equals("--force", StringComparison.OrdinalIgnoreCase);

    Console.WriteLine(force
        ? "Aufräumen (echt) …"
        : "Aufräumen (dry-run — kein --force, nichts wird gelöscht) …");

    var result = await BackupEngine.CleanupAsync(backupDir, dryRun: !force, onUnreferenced: hash =>
        Console.WriteLine($"  verwaist: {hash}"));

    Console.WriteLine($"{result.Unreferenced} verwaiste(s) Objekt(e), " +
                      $"{result.FreedBytes / 1024.0 / 1024.0:F2} MB " +
                      $"{(result.DryRun ? "würden freigegeben" : "freigegeben")}.");
    Console.WriteLine($"{result.ReferencedObjects} Objekt(e) referenziert.");
    return 0;
}

if (args.Length < 3)
{
    Console.Error.WriteLine("Zu wenige Argumente.");
    return 1;
}

string firstArg   = args[1];
string backupDir2 = args[2];

if (command == "backup")
{
    bool isDir = Directory.Exists(firstArg);
    if (!isDir && !File.Exists(firstArg))
    {
        Console.Error.WriteLine($"Quelle nicht gefunden: {firstArg}");
        return 2;
    }

    Console.WriteLine($"{"Chunk",-8} {"Offset (MB)",-14} {"xxHash64",-18} {"Datei",-36} Status");
    Console.WriteLine(new string('-', 84));

    var result = await BackupEngine.RunAsync(firstArg, backupDir2, chunk =>
    {
        double offsetMb  = chunk.OffsetBytes / (1024.0 * 1024.0);
        string status    = chunk.Deduplicated ? "skip" : "written";
        string shortPath = chunk.RelativePath.Length > 34
            ? "…" + chunk.RelativePath[^33..]
            : chunk.RelativePath;
        Console.WriteLine($"{chunk.Index,-8} {offsetMb,-14:F1} {chunk.HashHex,-18} {shortPath,-36} {status}");
    }, onError: (relPath, ex) =>
    {
        Console.Error.WriteLine($"ÜBERSPRUNGEN (gesperrt/kein Zugriff): {relPath}");
    });

    Console.WriteLine(new string('-', 84));
    string errorNote = result.FilesErrored > 0 ? $", {result.FilesErrored} nicht lesbar" : "";
    Console.WriteLine($"Done. {result.TotalChunks} chunk(s), {result.TotalBytes / (1024.0 * 1024.0 * 1024.0):F3} GB, " +
                      $"{result.Deduplicated} dedupliziert, {result.FilesSkipped} Datei(en) übersprungen{errorNote}.");
    Console.WriteLine($"Root-Hash: {result.RootHash}");
    return 0;
}

if (command == "verify")
{
    Console.WriteLine($"{"Chunk",-8} {"Offset (MB)",-14} {"xxHash64",-18} Status");
    Console.WriteLine(new string('-', 56));

    bool isRootHash = firstArg.Length == 16
        && firstArg.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f'));

    VerifyResult result;
    if (isRootHash)
    {
        result = await BackupEngine.VerifyAsync(firstArg, backupDir2, PrintChunk);
    }
    else
    {
        if (!File.Exists(firstArg) && !Directory.Exists(firstArg))
        {
            Console.Error.WriteLine($"Quelle nicht gefunden: {firstArg}");
            return 2;
        }
        result = await BackupEngine.VerifyAsync(firstArg, backupDir2, PrintChunk);
    }

    Console.WriteLine(new string('-', 56));
    Console.WriteLine($"Done. {result.TotalChunks} chunk(s), {result.Missing} fehlend, {result.Corrupt} korrupt.");
    return result.IsValid ? 0 : 3;
}

if (command == "restore")
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: lillestora restore <backup-verzeichnis> <root-hash> <ziel-verzeichnis>");
        return 1;
    }

    string backupDir3  = args[1];
    string rootHash    = args[2];
    string targetDir   = args[3];

    var snapshots = await BackupEngine.ListSnapshotsAsync(backupDir3);
    var snap      = snapshots.FirstOrDefault(s =>
        s.RootHash.StartsWith(rootHash, StringComparison.OrdinalIgnoreCase));

    if (snap == null)
    {
        Console.Error.WriteLine($"Kein Snapshot mit Hash gefunden: {rootHash}");
        return 2;
    }

    Console.WriteLine($"Wiederherstellen von: {snap.SourcePath}");
    Console.WriteLine($"Sicherungszeitpunkt:  {snap.Time:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"Zielverzeichnis:      {Path.GetFullPath(targetDir)}");
    Console.WriteLine(new string('-', 60));

    try
    {
        var result = await BackupEngine.RestoreAsync(backupDir3, snap.RootHash, targetDir, progress =>
        {
            double mb = progress.Bytes / (1024.0 * 1024.0);
            Console.WriteLine($"  {mb,8:F2} MB  {progress.RelativePath}");
        });

        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"Done. {result.FilesRestored} Datei(en), {result.TotalBytes / (1024.0 * 1024.0):F2} MB wiederhergestellt.");
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"Fehler: {ex.Message}");
        return 4;
    }

    return 0;
}

Console.Error.WriteLine($"Unbekannter Befehl: {command}");
return 1;

static void PrintChunk(ChunkVerifyResult chunk)
{
    double offsetMb = chunk.OffsetBytes / (1024.0 * 1024.0);
    string status   = chunk.Status switch
    {
        ChunkVerifyStatus.Ok      => "ok",
        ChunkVerifyStatus.Missing => "FEHLT",
        ChunkVerifyStatus.Corrupt => "KORRUPT",
        _                         => "?"
    };
    Console.WriteLine($"{chunk.Index,-8} {offsetMb,-14:F1} {chunk.HashHex,-18} {status}");
}
