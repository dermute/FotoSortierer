namespace FotoSortierer.Core;

public static class Scanner
{
    public const string Extensions = ".jpg;.jpeg;.heic;.heif;.png;.tif;.tiff;.webp;.dng;.arw;.crw;.cr2;.cr3;.nef;.orf;.pef;.raf;.rw2;.srw;.mov;.mp4;.avi";
    private static readonly HashSet<string> Supported = new(Extensions.Split(';'), StringComparer.OrdinalIgnoreCase);

    public static ScanResult Scan(IEnumerable<string> selections, bool recursive, string? excludedFolder, TimeZoneInfo zone,
        IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var files = new List<MediaFile>();
        var messages = new List<string>();
        var seen = new HashSet<string>(Paths.Comparer);
        var seenDirs = new HashSet<string>(Paths.Comparer);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        void Visit(string input)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(input);
            if (excludedFolder is not null && Paths.Within(path, excludedFolder)) return;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { messages.Add($"Verknüpfung übersprungen: {path}"); return; }
                if (System.IO.Directory.Exists(path))
                {
                    if (!seenDirs.Add(path)) return;
                    foreach (var file in System.IO.Directory.EnumerateFiles(path)) Visit(file);
                    if (recursive) foreach (var folder in System.IO.Directory.EnumerateDirectories(path)) Visit(folder);
                }
                else if (seen.Add(path))
                {
                    if (!Supported.Contains(Path.GetExtension(path))) { messages.Add($"Nicht unterstützt: {path}"); return; }
                    var info = new FileInfo(path);
                    var date = Dates.Read(path, zone);
                    files.Add(new(path, info.Length, info.LastWriteTimeUtc, date));
                    if (timer.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new(files.Count, 0, $"{files.Count:N0} Dateien gelesen …"));
                        timer.Restart();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { messages.Add($"Nicht lesbar: {path} – {ex.Message}"); }
        }
        foreach (var selection in selections) Visit(selection);
        return new(files, messages);
    }
}
