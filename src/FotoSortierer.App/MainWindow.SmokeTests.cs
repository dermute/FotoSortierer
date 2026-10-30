using System.IO;

namespace FotoSortierer.App;

public partial class MainWindow
{
    internal async Task SmokeTestAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "FotoSortierer-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var photo = Path.Combine(folder, "2025-07-18_14-32-05.png");
            await System.IO.File.WriteAllBytesAsync(photo, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aM1sAAAAASUVORK5CYII="));
            selections.Add(photo);
            await ScanAsync();
            Check(rows.Count == 1 && PreviewGrid.Items.Count == 1, "Single-file preview was not populated.");

            var correctedDate = new DateTime(2025, 8, 19, 10, 11, 12);
            rows[0].Correct(correctedDate);
            rows[0].Included = false;
            selections.Clear();
            selections.Add(folder);
            await ScanAsync();
            Check(rows.Count == 1 && PreviewGrid.Items.Count == 1, "Folder preview was not populated.");
            Check(rows[0].File.Date.Value == correctedDate && !rows[0].Included,
                "Rescanning lost the date correction or exclusion.");

            WarningsOnlyBox.IsChecked = true;
            Check(PreviewGrid.Items.Count == rows.Count(x => x.HasWarning), "Warning filter is incorrect.");
            WarningsOnlyBox.IsChecked = false;
            System.IO.File.Delete(photo);
            await ScanAsync();
            Check(rows.Count == 0 && PreviewGrid.Items.Count == 0, "Empty folder did not clear the preview.");
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        static void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
        }
    }
}
