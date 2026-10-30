using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FotoSortierer.Core;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.Xmp;
using XmpCore;
using Xunit;

namespace FotoSortierer.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fs-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Capture = new(2025, 7, 18, 14, 32, 5);
    private string JournalPath => Path.Combine(root, "state", "journal.json");
    public CoreTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string Write(string name, string content = "original")
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    private static MediaFile Media(string path, DateTime? date = null)
    {
        var info = new FileInfo(path);
        return new(path, info.Length, info.LastWriteTimeUtc, new(date ?? Capture, "Test"));
    }
    private sealed class InlineProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
    { public void Report(OperationProgress value) => action(value); }

    [Theory]
    [InlineData("IMG_20250718_143205.jpg", "2025-07-18T14:32:05")]
    [InlineData("2025-07-18_14-32-05_original.heic", "2025-07-18T14:32:05")]
    [InlineData("Screenshot_2025-07-18.png", "2025-07-18T00:00:00")]
    public void FilenameDates(string name, string expected)
    {
        var choice = Dates.FromFilename(name);
        Assert.NotNull(choice);
        Assert.Equal(DateTime.Parse(expected), choice.Value);
        Assert.NotEmpty(choice.Warning);
    }

    [Theory]
    [InlineData("IMG_20250230_143205.jpg")]
    [InlineData("IMG_00000000_000000.jpg")]
    [InlineData("IMG_1234.jpg")]
    public void InvalidFilenameDates(string name) => Assert.Null(Dates.FromFilename(name));

    [Fact]
    public void ExifOriginalWinsAndOffsetIsConvertedToTripTime()
    {
        var exif = new ExifSubIfdDirectory();
        exif.Set(0x9003, "2025:07:18 14:32:05");
        exif.Set(0x9011, "+02:00");
        exif.Set(0x9004, "2024:01:01 00:00:00");
        var zone = TimeZoneInfo.CreateCustomTimeZone("Trip", TimeSpan.FromHours(3), "Trip", "Trip");
        var date = Dates.FromMetadata([exif], zone)!;
        Assert.Equal(Capture.AddHours(1), date.Value);
        Assert.Equal("EXIF-Aufnahme", date.Source);
    }

    [Fact]
    public void LocalExifDoesNotChangeWithComputerTimeZone()
    {
        var exif = new ExifSubIfdDirectory();
        exif.Set(0x9003, "2025:07:18 14:32:05");
        Assert.Equal(Capture, Dates.FromMetadata([exif], TimeZoneInfo.Utc)!.Value);
    }

    [Fact]
    public void InvalidOriginalFallsBackToDigitized()
    {
        var exif = new ExifSubIfdDirectory();
        exif.Set(0x9003, "0000:00:00 00:00:00");
        exif.Set(0x9004, "2025:07:18 14:32:05");
        Assert.Equal("EXIF-Digitalisierung", Dates.FromMetadata([exif], TimeZoneInfo.Utc)!.Source);
    }

    [Fact]
    public void XmpCaptureBeatsExifDigitization()
    {
        var exif = new ExifSubIfdDirectory();
        exif.Set(0x9004, "2020:01:01 00:00:00");
        var xmp = new XmpDirectory();
        xmp.SetXmpMeta(XmpMetaFactory.ParseFromString("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" exif:DateTimeOriginal="2025-07-18T14:32:05" />
              </rdf:RDF>
            </x:xmpmeta>
            """));
        var result = Dates.FromMetadata([exif, xmp], TimeZoneInfo.Utc)!;
        Assert.Equal(Capture, result.Value);
        Assert.Equal("XMP-Aufnahme", result.Source);
    }

    [Fact]
    public void ExplicitUtcUsesDaylightSavingRules()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.Equal(new DateTime(2025, 7, 18, 14, 0, 0), Dates.Parse("2025-07-18T12:00:00Z", berlin));
        Assert.Equal(new DateTime(2025, 1, 18, 13, 0, 0), Dates.Parse("2025-01-18T12:00:00Z", berlin));
    }

    [Fact]
    public void VideoUtcUsesTripZoneAndWarnsAboutContainerAmbiguity()
    {
        var header = new QuickTimeMovieHeaderDirectory();
        header.Set(QuickTimeMovieHeaderDirectory.TagCreated, new DateTime(2025, 7, 18, 12, 32, 5));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Trip", TimeSpan.FromHours(2), "Trip", "Trip");
        var result = Dates.FromMetadata([header], zone)!;
        Assert.Equal(Capture, result.Value);
        Assert.NotEmpty(result.Warning);
        header.Set(QuickTimeMovieHeaderDirectory.TagCreated, new DateTime(1904, 1, 1));
        Assert.Null(Dates.FromMetadata([header], zone));
    }

    [Fact]
    public void RealJpegExifBeatsFilenameAndFilesystem()
    {
        var path = Path.Combine(root, "IMG_20200101_000000.jpg");
        File.WriteAllBytes(path, JpegFixture());
        var result = Dates.Read(path, TimeZoneInfo.Utc);
        Assert.Equal(Capture, result.Value);
        Assert.Equal("EXIF-Aufnahme", result.Source);
    }

    [Fact]
    public void RealMp4ContainerDateIsExtracted()
    {
        var path = Path.Combine(root, "video.mp4");
        using var stream = File.Create(path);
        void Atom(string type, byte[] payload)
        {
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)payload.Length + 8);
            stream.Write(size); stream.Write(Encoding.ASCII.GetBytes(type)); stream.Write(payload);
        }
        Atom("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isommp42"));
        var header = new byte[100];
        var seconds = (uint)(Capture - new DateTime(1904, 1, 1)).TotalSeconds;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), seconds);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), seconds);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 1000);
        var movie = new byte[108];
        BinaryPrimitives.WriteUInt32BigEndian(movie, 108);
        Encoding.ASCII.GetBytes("mvhd").CopyTo(movie, 4);
        header.CopyTo(movie, 8);
        Atom("moov", movie);
        stream.Dispose();
        var result = Dates.Read(path, TimeZoneInfo.Utc);
        Assert.Equal(Capture, result.Value);
        Assert.Equal("QuickTime-Erstellung", result.Source);
    }

    private static byte[] JpegFixture()
    {
        using var tiff = new MemoryStream();
        using var writer = new BinaryWriter(tiff, Encoding.ASCII, true);
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8u);
        writer.Write((ushort)1);
        writer.Write((ushort)0x8769); writer.Write((ushort)4); writer.Write(1u); writer.Write(26u); writer.Write(0u);
        writer.Write((ushort)2);
        foreach (var (tag, offset) in new[] { (0x9003, 56u), (0x9004, 76u) })
        { writer.Write((ushort)tag); writer.Write((ushort)2); writer.Write(20u); writer.Write(offset); }
        writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes("2025:07:18 14:32:05\0"));
        writer.Write(Encoding.ASCII.GetBytes("2020:01:01 00:00:00\0"));
        using var jpeg = new MemoryStream();
        jpeg.Write([0xff, 0xd8, 0xff, 0xe1]);
        var payload = tiff.ToArray();
        var size = payload.Length + 8;
        jpeg.WriteByte((byte)(size >> 8)); jpeg.WriteByte((byte)size);
        jpeg.Write(Encoding.ASCII.GetBytes("Exif\0\0")); jpeg.Write(payload); jpeg.Write([0xff, 0xd9]);
        return jpeg.ToArray();
    }

    [Fact]
    public void BadMetadataFallsBackToFilenameThenModificationTime()
    {
        var named = Write("IMG_20250718_143205.jpg");
        Assert.Equal("Dateiname", Dates.Read(named, TimeZoneInfo.Utc).Source);
        var plain = Write("camera.jpg");
        File.SetLastWriteTime(plain, Capture);
        var result = Dates.Read(plain, TimeZoneInfo.Utc);
        Assert.Equal("Dateiänderung", result.Source);
        Assert.Equal(Capture, result.Value);
        Assert.Contains("Metadaten", result.Warning);
    }

    [Fact]
    public void RecursiveScanDeduplicatesAndExcludesOutput()
    {
        var source = Write("trip/a.jpg");
        Write("trip/camera/b.heic"); Write("trip/output/c.jpg"); Write("trip/readme.txt");
        var result = Scanner.Scan([Path.Combine(root, "trip"), source], true, Path.Combine(root, "trip/output"), TimeZoneInfo.Utc, null, default);
        Assert.Equal(2, result.Files.Count);
        Assert.Single(result.Messages.Where(x => x.Contains("Nicht unterstützt")));
        var shallow = Scanner.Scan([Path.Combine(root, "trip")], false, null, TimeZoneInfo.Utc, null, default);
        Assert.Single(shallow.Files);
    }

    [Fact]
    public void NamingIsStableCaseInsensitiveAndChronological()
    {
        var a = Media(Write("a/IMG.JPG"));
        var b = Media(Write("b/img.jpg"));
        var c = Media(Write("c.jpg"), Capture.AddDays(-1));
        var output = Path.Combine(root, "out");
        var plan = Planner.Create([b, c, a], OperationMode.Copy, output);
        var again = Planner.Create([a, b, c], OperationMode.Copy, output);
        Assert.Equal(plan.Files.Select(x => x.Destination), again.Files.Select(x => x.Destination));
        Assert.Equal(c.Path, plan.Files[0].Source);
        Assert.EndsWith("2025-07-18_14-32-05_img_002.jpg", plan.Files[2].Destination);
        Assert.Equal(plan.Files.Select(x => x.CaptureDate).Order(), plan.Files.OrderBy(x => x.Destination, Paths.Comparer).Select(x => x.CaptureDate));
    }

    [Fact]
    public void RepeatedRenameReplacesPrefix()
    {
        var input = Media(Write("2024-01-01_00-00-00_Ürlaub.jpg"));
        var plan = Planner.Create([input], OperationMode.Rename, null);
        Assert.Equal("2025-07-18_14-32-05_Ürlaub.jpg", Path.GetFileName(plan.Files[0].Destination));
    }

    [Fact]
    public void MissingDatesAndLongPathsBlockPlanning()
    {
        var file = Media(Write("a.jpg"));
        Assert.Throws<IOException>(() => Planner.Create([file with { Date = new(null, "Unbekannt") }], OperationMode.Rename, null));
        Assert.Throws<IOException>(() => Planner.Create([file], OperationMode.Copy, Path.Combine(root, new string('x', 230))));
    }

    [Fact]
    public async Task CopyIsIdenticalPreservesTimestampAndDoesNotOverwrite()
    {
        var source = Media(Write("a.jpg", "unchanged content"));
        var plan = Planner.Create([source], OperationMode.Copy, Path.Combine(root, "out"));
        var operations = new FileOperations(JournalPath);
        var first = await operations.ExecuteAsync(plan, null, default);
        Assert.Empty(first.Errors); Assert.Equal(1, first.Completed);
        Assert.Equal(File.ReadAllBytes(source.Path), File.ReadAllBytes(plan.Files[0].Destination));
        Assert.Equal(source.ModifiedUtc, File.GetLastWriteTimeUtc(plan.Files[0].Destination));
        var second = await operations.ExecuteAsync(plan, null, default);
        Assert.NotEmpty(second.Errors); Assert.Equal(0, second.Completed);
        Assert.True(File.Exists(source.Path));
    }

    [Fact]
    public async Task ChangedSourceBlocksEntirePlan()
    {
        var source = Media(Write("a.jpg"));
        var plan = Planner.Create([source], OperationMode.Copy, Path.Combine(root, "out"));
        File.AppendAllText(source.Path, "changed");
        var result = await new FileOperations(JournalPath).ExecuteAsync(plan, null, default);
        Assert.NotEmpty(result.Errors);
        Assert.False(File.Exists(plan.Files[0].Destination));
    }

    [Fact]
    public async Task RenameCycleAndUndoSurviveRestart()
    {
        var a = Media(Write("a.jpg", "A")); var b = Media(Write("b.jpg", "B"));
        var plan = new OperationPlan(OperationMode.Rename, [new(a.Path, b.Path, a.Length, a.ModifiedUtc, Capture), new(b.Path, a.Path, b.Length, b.ModifiedUtc, Capture)]);
        var result = await new FileOperations(JournalPath).ExecuteAsync(plan, null, default);
        Assert.Empty(result.Errors);
        Assert.Equal("B", File.ReadAllText(a.Path));
        Assert.Equal("A", File.ReadAllText(b.Path));
        var restarted = new FileOperations(JournalPath);
        Assert.True(restarted.CanUndo); Assert.False(restarted.NeedsRecovery);
        var undo = restarted.Undo(null, default);
        Assert.Empty(undo.Errors);
        Assert.Equal("A", File.ReadAllText(a.Path)); Assert.Equal("B", File.ReadAllText(b.Path));
        Assert.False(restarted.CanUndo);
    }

    [Fact]
    public async Task CancellationDuringStagingCanBeRecovered()
    {
        var a = Media(Write("a.jpg")); var b = Media(Write("b.jpg"));
        var plan = Planner.Create([a, b], OperationMode.Rename, null);
        using var cancel = new CancellationTokenSource();
        var result = await new FileOperations(JournalPath).ExecuteAsync(plan, new InlineProgress(_ => cancel.Cancel()), cancel.Token);
        Assert.True(result.Cancelled);
        var restarted = new FileOperations(JournalPath);
        Assert.True(restarted.NeedsRecovery);
        Assert.Empty(restarted.Undo(null, default).Errors);
        Assert.True(File.Exists(a.Path)); Assert.True(File.Exists(b.Path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void CrashBetweenMoveAndJournalSaveIsRecovered()
    {
        var original = Write("a.jpg", "photo");
        var temp = Path.Combine(root, ".fotosortierer-interrupted.tmp");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
        File.Move(original, temp);
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        var journal = new FileOperations.Journal { Entries = [new() { Original = original, Current = original, Next = temp, Destination = Path.Combine(root, "new.jpg"), Hash = hash }] };
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(journal));
        var result = new FileOperations(JournalPath).Undo(null, default);
        Assert.Empty(result.Errors); Assert.Equal("photo", File.ReadAllText(original));
    }

    [Fact]
    public void AmbiguousJournalNeverMovesAnUnrelatedIdenticalFile()
    {
        var original = Write("a.jpg", "photo");
        var target = Write("target.jpg", "photo");
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        var journal = new FileOperations.Journal { Entries = [new() { Original = original, Current = original, Next = target, Destination = target, Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))) }] };
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(journal));
        Assert.NotEmpty(new FileOperations(JournalPath).Undo(null, default).Errors);
        Assert.Equal("photo", File.ReadAllText(original));
        Assert.Equal("photo", File.ReadAllText(target));
    }

    [Fact]
    public void PartialFinalWriteAheadRecordStillAllowsRecovery()
    {
        var original = Write("a.jpg", "photo");
        var temp = Path.Combine(root, ".fotosortierer-crash.tmp");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
        var journal = new FileOperations.Journal { Entries = [new() { Original = original, Current = original, Destination = Path.Combine(root, "new.jpg"), Hash = hash }] };
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(journal));
        var intent = new { journal.Id, Index = 0, Current = original, Next = temp };
        File.WriteAllText(JournalPath + ".wal", JsonSerializer.Serialize(intent) + "\n{\"partial\"");
        File.Move(original, temp);
        Assert.Empty(new FileOperations(JournalPath).Undo(null, default).Errors);
        Assert.Equal("photo", File.ReadAllText(original));
        Assert.False(File.Exists(JournalPath + ".wal"));
    }

    [Fact]
    public void ObsoleteWriteAheadLogCannotOverrideNewJournal()
    {
        var original = Path.Combine(root, "a.jpg");
        var current = Write("renamed.jpg", "photo");
        var unrelated = Write("unrelated.jpg", "photo");
        var journal = new FileOperations.Journal { Completed = true, Entries = [new() { Original = original, Current = current, Destination = current, Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(current))) }] };
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(journal));
        File.WriteAllText(JournalPath + ".wal", JsonSerializer.Serialize(new { Id = Guid.NewGuid(), Index = 0, Current = unrelated, Next = (string?)null }) + "\n");
        Assert.Empty(new FileOperations(JournalPath).Undo(null, default).Errors);
        Assert.True(File.Exists(original)); Assert.True(File.Exists(unrelated)); Assert.False(File.Exists(current));
    }

    [Fact]
    public async Task InterruptedUndoCanBeResumed()
    {
        var a = Media(Write("a.jpg", "A")); var b = Media(Write("b.jpg", "B"));
        var operations = new FileOperations(JournalPath);
        var plan = Planner.Create([a, b], OperationMode.Rename, null);
        Assert.Empty((await operations.ExecuteAsync(plan, null, default)).Errors);
        using var cancel = new CancellationTokenSource();
        Assert.True(operations.Undo(new InlineProgress(_ => cancel.Cancel()), cancel.Token).Cancelled);
        Assert.True(operations.NeedsRecovery);
        Assert.Empty(new FileOperations(JournalPath).Undo(null, default).Errors);
        Assert.Equal("A", File.ReadAllText(a.Path)); Assert.Equal("B", File.ReadAllText(b.Path));
    }

    [Fact]
    public async Task UndoRefusesModifiedFilesAndOccupiedOriginalNames()
    {
        var original = Media(Write("a.jpg"));
        var plan = Planner.Create([original], OperationMode.Rename, null);
        var operations = new FileOperations(JournalPath);
        Assert.Empty((await operations.ExecuteAsync(plan, null, default)).Errors);
        Write("a.jpg", "new unrelated photo");
        Assert.NotEmpty(operations.Undo(null, default).Errors);
        Assert.Equal("new unrelated photo", File.ReadAllText(original.Path));
        File.Delete(original.Path);
        File.AppendAllText(plan.Files[0].Destination, "edit");
        Assert.NotEmpty(operations.Undo(null, default).Errors);
        Assert.False(File.Exists(original.Path));
    }

    [Fact]
    public async Task TenThousandFilesProduceUniqueChronologicalNames()
    {
        var files = Enumerable.Range(0, 10000).Select(i => new MediaFile(Path.Combine(root, $"camera{i % 4}", $"IMG_{i}.jpg"), 0, Capture, new(Capture.AddSeconds(i / 3), "Test"))).ToArray();
        var plan = await Task.Run(() => Planner.Create(files.Reverse(), OperationMode.Copy, Path.Combine(root, "out")));
        Assert.Equal(10000, plan.Files.Select(x => x.Destination).Distinct(Paths.Comparer).Count());
        Assert.Equal(plan.Files.Select(x => x.CaptureDate).Order(), plan.Files.OrderBy(x => x.Destination, Paths.Comparer).Select(x => x.CaptureDate));
    }
}
