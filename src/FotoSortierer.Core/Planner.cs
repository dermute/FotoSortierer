using System.Globalization;
using System.Text.RegularExpressions;

namespace FotoSortierer.Core;

public static partial class Planner
{
    [GeneratedRegex(@"^(?:\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_)+")]
    private static partial Regex OurPrefix();

    public static OperationPlan Create(IEnumerable<MediaFile> selected, OperationMode mode, string? destination)
    {
        if (mode == OperationMode.Copy && string.IsNullOrWhiteSpace(destination)) throw new IOException("Bitte einen Zielordner auswählen.");
        var used = new HashSet<string>(Paths.Comparer);
        var result = new List<PlannedFile>();
        foreach (var file in selected.OrderBy(x => x.Date.Value).ThenBy(x => x.Path, Paths.Comparer))
        {
            if (file.Date.Value is not { } date || !Dates.Valid(date)) throw new IOException($"Datum fehlt oder ist ungültig: {file.Path}");
            var folder = mode == OperationMode.Copy ? Path.GetFullPath(destination!) : Path.GetDirectoryName(file.Path)!;
            var stem = OurPrefix().Replace(Path.GetFileNameWithoutExtension(file.Path), "");
            if (stem.Length == 0) stem = "Datei";
            var prefix = date.ToString("yyyy-MM-dd_HH-mm-ss_", CultureInfo.InvariantCulture);
            var extension = Path.GetExtension(file.Path);
            var target = Path.Combine(folder, prefix + stem + extension);
            for (int i = 2; !used.Add(target); i++) target = Path.Combine(folder, $"{prefix}{stem}_{i:000}{extension}");
            Paths.CheckLength(target);
            result.Add(new(file.Path, target, file.Length, file.ModifiedUtc, date));
        }
        return new(mode, result.AsReadOnly());
    }
}
