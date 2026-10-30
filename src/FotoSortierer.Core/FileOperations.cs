using System.Security.Cryptography;
using System.Text.Json;

namespace FotoSortierer.Core;

public sealed class FileOperations(string journalPath)
{
    public sealed class Journal
    {
        public int Version { get; set; } = 1;
        public Guid Id { get; set; } = Guid.NewGuid();
        public bool Completed { get; set; }
        public bool UndoComplete { get; set; }
        public List<Entry> Entries { get; set; } = [];
    }
    public sealed class Entry
    {
        public string Original { get; set; } = "";
        public string Destination { get; set; } = "";
        public string Current { get; set; } = "";
        public string? Next { get; set; }
        public string Hash { get; set; } = "";
    }
    private sealed record MoveRecord(Guid Id, int Index, string Current, string? Next);
    private string WalPath => journalPath + ".wal";

    private static string Hash(string path, CancellationToken token = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private Journal? Load()
    {
        if (!File.Exists(journalPath)) return null;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath));
        if (journal is null || journal.Version != 1 || journal.Entries is null || journal.Entries.Any(x => x is null))
            throw new IOException("Wiederherstellungsprotokoll ist ungültig oder hat eine unbekannte Version.");
        if (File.Exists(WalPath))
        {
            using var reader = new StreamReader(WalPath);
            while (reader.ReadLine() is { } line)
            {
                MoveRecord? move;
                try { move = JsonSerializer.Deserialize<MoveRecord>(line); }
                // An incomplete final append is safe: moves only happen after a flushed intent.
                catch (JsonException) when (reader.Peek() == -1) { break; }
                if (move is null) throw new IOException("Wiederherstellungsprotokoll enthält einen ungültigen Schritt.");
                if (move.Id != journal.Id) continue;
                if (move.Index < 0 || move.Index >= journal.Entries.Count)
                    throw new IOException("Wiederherstellungsprotokoll enthält einen unbekannten Dateieintrag.");
                journal.Entries[move.Index].Current = move.Current;
                journal.Entries[move.Index].Next = move.Next;
            }
        }
        if (journal.Entries.Any(x => x is null ||
            string.IsNullOrWhiteSpace(x.Original) || !Path.IsPathFullyQualified(x.Original) ||
            string.IsNullOrWhiteSpace(x.Current) || !Path.IsPathFullyQualified(x.Current) ||
            string.IsNullOrWhiteSpace(x.Destination) || !Path.IsPathFullyQualified(x.Destination) ||
            x.Hash is null || x.Hash.Length != 64 || x.Next is not null && !Path.IsPathFullyQualified(x.Next)))
            throw new IOException("Wiederherstellungsprotokoll ist ungültig oder hat eine unbekannte Version.");
        return journal;
    }
    public bool CanUndo => Load() is { UndoComplete: false, Entries.Count: > 0 };
    public bool NeedsRecovery => Load() is { Completed: false, UndoComplete: false, Entries.Count: > 0 };

    private void Save(Journal journal)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        var temporary = journalPath + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal);
            stream.Flush(true);
        }
        File.Move(temporary, journalPath, true);
        if (File.Exists(WalPath)) File.Delete(WalPath);
    }

    private void SaveMove(Journal journal, Entry entry)
    {
        var record = new MoveRecord(journal.Id, journal.Entries.IndexOf(entry), entry.Current, entry.Next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        using var stream = new FileStream(WalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(true);
    }

    private static void CheckSource(PlannedFile file)
    {
        var info = new FileInfo(file.Source);
        if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc != file.ModifiedUtc)
            throw new IOException($"Quelldatei wurde verändert oder entfernt. Vorschau neu laden: {file.Source}");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Verknüpfung ist nicht zulässig: {file.Source}");
    }

    private static void CheckFolders(string folder)
    {
        for (var info = new DirectoryInfo(folder); info is not null; info = info.Parent)
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Zielpfad enthält eine Verknüpfung: {folder}");
    }

    public static void Preflight(OperationPlan plan)
    {
        var sources = new HashSet<string>(plan.Files.Select(x => x.Source), Paths.Comparer);
        var targets = new HashSet<string>(Paths.Comparer);
        foreach (var file in plan.Files)
        {
            CheckSource(file);
            Paths.CheckLength(file.Destination);
            CheckFolders(Path.GetDirectoryName(file.Destination)!);
            if (!targets.Add(file.Destination)) throw new IOException($"Doppelter Zielname: {file.Destination}");
            if (System.IO.Directory.Exists(file.Destination) || File.Exists(file.Destination) &&
                (plan.Mode == OperationMode.Copy || !sources.Contains(file.Destination)))
                throw new IOException($"Zieldatei existiert bereits. Bitte Zielordner ändern oder Vorschau neu erstellen: {file.Destination}");
            if (plan.Mode == OperationMode.Rename && (File.GetAttributes(file.Source) & FileAttributes.ReadOnly) != 0)
                throw new IOException($"Datei ist schreibgeschützt: {file.Source}");
        }
        if (plan.Mode == OperationMode.Copy)
        {
            foreach (var group in plan.Files.GroupBy(x => Path.GetPathRoot(x.Destination), Paths.Comparer))
            {
                var drive = new DriveInfo(group.Key!);
                if (drive.IsReady && drive.AvailableFreeSpace < group.Sum(x => x.Length))
                    throw new IOException("Auf dem Ziellaufwerk ist nicht genügend freier Speicherplatz.");
            }
        }
        foreach (var folder in plan.Files.Select(x => Path.GetDirectoryName(x.Destination)!).Distinct(Paths.Comparer))
        {
            System.IO.Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".fotosortierer-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
    }

    public async Task<OperationResult> ExecuteAsync(OperationPlan plan, IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var completed = 0;
        var errors = new List<string>();
        var outcomes = new List<FileOutcome>();
        try
        {
            token.ThrowIfCancellationRequested();
            Preflight(plan);
            if (plan.Mode == OperationMode.Copy)
            {
                foreach (var file in plan.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var temp = Path.Combine(Path.GetDirectoryName(file.Destination)!, ".fotosortierer-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        CheckSource(file);
                        using (var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                        {
                            await source.CopyToAsync(output, token);
                            await output.FlushAsync(token);
                            output.Flush(true);
                        }
                        CheckSource(file);
                        File.SetLastWriteTimeUtc(temp, file.ModifiedUtc);
                        File.Move(temp, file.Destination);
                        completed++;
                        outcomes.Add(new(file.Source, file.Destination, "Kopiert"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { errors.Add($"{file.Source}: {ex.Message}"); outcomes.Add(new(file.Source, file.Destination, "Fehlgeschlagen")); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    progress?.Report(new(completed, plan.Files.Count, Path.GetFileName(file.Destination)));
                }
            }
            else
            {
                if (NeedsRecovery) throw new IOException("Zuerst die unterbrochene Umbenennung rückgängig machen.");
                var journal = new Journal();
                foreach (var file in plan.Files.Where(x => Paths.Same(x.Source, x.Destination)))
                {
                    outcomes.Add(new(file.Source, file.Destination, "Unverändert"));
                    completed++;
                }
                foreach (var file in plan.Files.Where(x => !Paths.Same(x.Source, x.Destination)))
                {
                    token.ThrowIfCancellationRequested();
                    CheckSource(file);
                    var hash = Hash(file.Source, token);
                    CheckSource(file);
                    journal.Entries.Add(new() { Original = file.Source, Current = file.Source, Destination = file.Destination, Hash = hash });
                }
                if (journal.Entries.Count == 0) return new(completed, false, errors, outcomes);
                Save(journal);
                foreach (var entry in journal.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    Move(journal, entry, Temporary(entry.Original), token);
                    progress?.Report(new(0, journal.Entries.Count, "Originalnamen werden gesichert …"));
                }
                foreach (var entry in journal.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    Move(journal, entry, entry.Destination, token);
                    outcomes.Add(new(entry.Original, entry.Destination, "Umbenannt"));
                    progress?.Report(new(++completed, plan.Files.Count, Path.GetFileName(entry.Destination)));
                }
                journal.Completed = true;
                Save(journal);
            }
        }
        catch (OperationCanceledException) { return new(completed, true, errors, outcomes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { errors.Add(ex.Message); }
        return new(completed, false, errors, outcomes);
    }

    private static string Temporary(string path) => Path.Combine(Path.GetDirectoryName(path)!, ".fotosortierer-" + Guid.NewGuid().ToString("N") + ".tmp");

    // Write-ahead intent makes an interruption between File.Move and Save recoverable.
    private void Move(Journal journal, Entry entry, string target, CancellationToken token)
    {
        if (Hash(entry.Current, token) != entry.Hash) throw new IOException($"Datei wurde verändert: {entry.Current}");
        if (File.Exists(target) || System.IO.Directory.Exists(target)) throw new IOException($"Ziel existiert bereits: {target}");
        entry.Next = target;
        SaveMove(journal, entry);
        File.Move(entry.Current, target);
        entry.Current = target;
        entry.Next = null;
        SaveMove(journal, entry);
    }

    public OperationResult Undo(IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var completed = 0;
        try
        {
            var journal = Load() ?? throw new IOException("Keine Umbenennung vorhanden.");
            if (journal.UndoComplete) return new(0, false, []);
            foreach (var entry in journal.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Next is not null)
                {
                    if (File.Exists(entry.Next) && File.Exists(entry.Current))
                        throw new IOException($"Wiederherstellung mehrdeutig: Beide Pfade sind belegt: {entry.Current} und {entry.Next}");
                    if (File.Exists(entry.Next) && Hash(entry.Next, token) == entry.Hash) entry.Current = entry.Next;
                    else if (!File.Exists(entry.Current) || Hash(entry.Current, token) != entry.Hash)
                        throw new IOException($"Datei nicht sicher auffindbar: {entry.Original}");
                    entry.Next = null;
                }
                if (!File.Exists(entry.Current) || Hash(entry.Current, token) != entry.Hash)
                    throw new IOException($"Datei fehlt oder wurde verändert: {entry.Current}");
            }
            var current = new HashSet<string>(journal.Entries.Select(x => x.Current), Paths.Comparer);
            foreach (var entry in journal.Entries)
                if (System.IO.Directory.Exists(entry.Original) || File.Exists(entry.Original) && !current.Contains(entry.Original))
                    throw new IOException($"Ursprünglicher Name ist inzwischen belegt: {entry.Original}");
            journal.Completed = false;
            Save(journal);
            foreach (var entry in journal.Entries)
            {
                token.ThrowIfCancellationRequested();
                Move(journal, entry, Temporary(entry.Original), token);
            }
            foreach (var entry in journal.Entries)
            {
                token.ThrowIfCancellationRequested();
                Move(journal, entry, entry.Original, token);
                progress?.Report(new(++completed, journal.Entries.Count, "Originalnamen werden wiederhergestellt …"));
            }
            journal.UndoComplete = true;
            Save(journal);
            return new(completed, false, []);
        }
        catch (OperationCanceledException) { return new(completed, true, []); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(completed, false, [ex.Message]); }
    }
}
