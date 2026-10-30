namespace FotoSortierer.Core;

public enum OperationMode { Copy, Rename }
public sealed record DateChoice(DateTime? Value, string Source, string Warning = "");
public sealed record MediaFile(string Path, long Length, DateTime ModifiedUtc, DateChoice Date);
public sealed record ScanResult(IReadOnlyList<MediaFile> Files, IReadOnlyList<string> Messages);
public sealed record PlannedFile(string Source, string Destination, long Length, DateTime ModifiedUtc, DateTime CaptureDate);
public sealed record OperationPlan(OperationMode Mode, IReadOnlyList<PlannedFile> Files);
public sealed record OperationProgress(int Done, int Total, string Message);
public sealed record FileOutcome(string Source, string Destination, string State);
public sealed record OperationResult(int Completed, bool Cancelled, IReadOnlyList<string> Errors, IReadOnlyList<FileOutcome>? Outcomes = null);

public static class Paths
{
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
    public static bool Same(string a, string b) => Comparer.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b));
    public static bool Within(string path, string folder) => Same(path, folder) ||
        System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void CheckLength(string path)
    {
        if (System.IO.Path.GetFileName(path).Length > 255 || path.Length > 240)
            throw new IOException($"Pfad zu lang (höchstens 240 Zeichen): {path}");
    }
}
