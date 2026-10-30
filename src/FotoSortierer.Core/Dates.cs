using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.Xmp;

namespace FotoSortierer.Core;

public static partial class Dates
{
    [GeneratedRegex(@"(?<!\d)(?<y>20\d{2}|19\d{2})[-_]?(?<m>\d{2})[-_]?(?<d>\d{2})(?:[T _-]?(?<h>\d{2})[-_:]?(?<n>\d{2})[-_:]?(?<s>\d{2}))?(?!\d)")]
    private static partial Regex FilenameDate();
    [GeneratedRegex(@"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffset();

    public static bool Valid(DateTime value) => value.Year >= 1905 && value.Year <= DateTime.Now.Year + 1;

    public static DateTime? Parse(string? text, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimEnd('\0');
        if (Regex.IsMatch(text, @"^\d{4}:\d{2}:\d{2}"))
            text = text[..4] + "-" + text.Substring(5, 2) + "-" + text[8..];
        DateTime parsed;
        if (HasOffset().IsMatch(text))
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)) return null;
            parsed = TimeZoneInfo.ConvertTime(offset, zone).DateTime;
        }
        else if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)) return null;
        return Valid(parsed) ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified) : null;
    }

    public static DateChoice? FromFilename(string path)
    {
        foreach (Match m in FilenameDate().Matches(System.IO.Path.GetFileNameWithoutExtension(path)))
        {
            try
            {
                int Part(string name) => int.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture);
                var timed = m.Groups["h"].Success;
                var date = new DateTime(Part("y"), Part("m"), Part("d"), timed ? Part("h") : 0, timed ? Part("n") : 0, timed ? Part("s") : 0);
                if (Valid(date)) return new(date, "Dateiname", timed ? "Datum aus Dateiname – bitte prüfen." : "Dateiname enthält keine Uhrzeit; 00:00 angenommen.");
            }
            catch (ArgumentOutOfRangeException) { }
        }
        return null;
    }

    public static DateChoice Read(string path, TimeZoneInfo zone)
    {
        var warning = "";
        try
        {
            var embedded = FromMetadata(ImageMetadataReader.ReadMetadata(path), zone);
            if (embedded is not null) return embedded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warning = "Metadaten konnten nicht gelesen werden. ";
        }
        var filename = FromFilename(path);
        if (filename is not null) return filename with { Warning = warning + filename.Warning };
        var info = new FileInfo(path);
        if (Valid(info.LastWriteTime)) return new(info.LastWriteTime, "Dateiänderung", warning + "Kein Aufnahmedatum: Dateiänderung verwendet – bitte prüfen.");
        if (Valid(info.CreationTime)) return new(info.CreationTime, "Dateierstellung", warning + "Kein Aufnahmedatum: Dateierstellung verwendet – bitte prüfen.");
        return new(null, "Unbekannt", warning + "Datum eingeben oder Datei ausschließen.");
    }

    public static DateChoice? FromMetadata(IEnumerable<MetadataExtractor.Directory> metadata, TimeZoneInfo zone)
    {
        var directories = metadata.ToArray();
        DateTime? Exif(int tag, int offsetTag)
        {
            foreach (var dir in directories.OfType<ExifSubIfdDirectory>())
            {
                var raw = dir.GetString(tag)?.Trim().TrimEnd('\0');
                var offset = dir.GetString(offsetTag)?.Trim().TrimEnd('\0');
                var parsed = Parse(raw is null ? null : raw + offset, zone);
                if (parsed is not null) return parsed;
            }
            return null;
        }
        DateTime? Xmp(params string[] keys)
        {
            foreach (var key in keys)
                foreach (var dir in directories.OfType<XmpDirectory>())
                    if (dir.GetXmpProperties().TryGetValue(key, out var value) && Parse(value, zone) is { } d) return d;
            return null;
        }
        if (Exif(0x9003, 0x9011) is { } original) return new(original, "EXIF-Aufnahme");
        if (Xmp("exif:DateTimeOriginal", "photoshop:DateCreated") is { } xmp) return new(xmp, "XMP-Aufnahme");
        foreach (var dir in directories.OfType<QuickTimeMetadataHeaderDirectory>())
        {
            var value = dir.GetString(QuickTimeMetadataHeaderDirectory.TagCreationDate);
            if (Parse(value, zone) is { } date)
                return new(date, "Video-Aufnahme", HasOffset().IsMatch(value ?? "") ? "" : "Video-Zeitzone unbekannt; lokale Zeit angenommen.");
        }
        if (Exif(0x9004, 0x9012) is { } digitized) return new(digitized, "EXIF-Digitalisierung", "Digitalisierungsdatum – bitte prüfen.");
        if (Xmp("xmp:CreateDate", "exif:DateTimeDigitized") is { } created) return new(created, "XMP-Erstellung", "Erstellungsdatum – bitte prüfen.");
        foreach (var dir in directories.OfType<QuickTimeMovieHeaderDirectory>())
        {
            if (dir.GetObject(QuickTimeMovieHeaderDirectory.TagCreated) is DateTime date && Valid(date))
                return new(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(date, DateTimeKind.Utc), zone), "QuickTime-Erstellung", "Video-Containerzeit als UTC interpretiert – bitte prüfen.");
        }
        return null;
    }
}
