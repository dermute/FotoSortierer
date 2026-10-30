using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FotoSortierer.Core;
using Microsoft.Win32;

namespace FotoSortierer.App;

public partial class MainWindow : Window
{
    private readonly PreviewRows rows = [];
    private readonly List<string> selections = [];
    private readonly FileOperations operations = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FotoSortierer", "letzte-umbenennung.json"));
    private OperationPlan? plan;
    private CancellationTokenSource? cancellation;
    private bool ready;
    private bool busy;
    private bool journalHealthy = true;
    private bool recoveryRequired;
    private string report = "";
    private string? lastOutput;
    private OperationMode Mode => ModeBox.SelectedIndex == 0 ? OperationMode.Copy : OperationMode.Rename;
    private string? Destination => string.IsNullOrWhiteSpace(DestinationBox.Text) ? null : DestinationBox.Text;

    public MainWindow()
    {
        InitializeComponent();
        PreviewGrid.ItemsSource = rows;
        var view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = x => WarningsOnlyBox.IsChecked != true || ((PreviewRow)x).HasWarning;
        view.SortDescriptions.Add(new("File.Date.Value", ListSortDirection.Ascending));
        view.SortDescriptions.Add(new("File.Path", ListSortDirection.Ascending));
        ZoneBox.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        ZoneBox.SelectedItem = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(x => x.Id == TimeZoneInfo.Local.Id);
        ready = true;
        UpdateUndo();
    }

    private void UpdateUndo()
    {
        try
        {
            UndoButton.IsEnabled = !busy && operations.CanUndo;
            journalHealthy = true;
            recoveryRequired = operations.NeedsRecovery;
            if (recoveryRequired) StatusText.Text = "Eine Umbenennung wurde unterbrochen. Bitte zuerst rückgängig machen; das Protokoll enthält die Wiederherstellungspfade.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            journalHealthy = false;
            UndoButton.IsEnabled = false;
            StatusText.Text = "Wiederherstellungsprotokoll nicht lesbar. Originale können derzeit nicht umbenannt werden. " + ex.Message;
        }
    }

    private void Rebuild()
    {
        if (!ready) return;
        plan = null;
        ApplyButton.IsEnabled = false;
        foreach (var row in rows) row.NewName = row.Included ? "" : "Ausgeschlossen";
        var selected = rows.Where(x => x.Included).ToArray();
        CountText.Text = $"{rows.Count:N0} Dateien · {selected.Length:N0} ausgewählt · {rows.Count(x => x.HasWarning):N0} Hinweise";
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            if (selected.Length == 0) return;
            plan = Planner.Create(selected.Select(x => x.File), Mode, Destination);
            var targets = plan.Files.ToDictionary(x => x.Source, x => x.Destination, Paths.Comparer);
            foreach (var row in selected) row.NewName = Path.GetFileName(targets[row.File.Path]);
            ApplyButton.IsEnabled = !busy && (Mode == OperationMode.Copy || journalHealthy && !operations.NeedsRecovery);
            if (!busy) StatusText.Text = "Vorschau bereit. Bitte die Aufnahmedaten und Hinweise prüfen.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { plan = null; StatusText.Text = ex.Message; }
        CollectionViewSource.GetDefaultView(rows).Refresh();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true;
        cancellation = new();
        SettingsPanel.IsEnabled = false;
        PreviewGrid.IsEnabled = false;
        ReviewControls.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "Abgebrochen. Bereits abgeschlossene Schritte bleiben erhalten."; }
        catch (Exception ex)
        {
            report = "Der Vorgang konnte nicht abgeschlossen werden.\n\n" + ex.Message;
            ReportButton.IsEnabled = true;
            StatusText.Text = "Der Vorgang konnte nicht abgeschlossen werden. Details stehen im Protokoll.";
            ShowText("Vorgang fehlgeschlagen", report);
        }
        finally
        {
            busy = false;
            cancellation.Dispose(); cancellation = null;
            SettingsPanel.IsEnabled = true;
            PreviewGrid.IsEnabled = true;
            ReviewControls.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ProgressBar.Visibility = Visibility.Collapsed;
            UpdateUndo();
            if (plan is not null) ApplyButton.IsEnabled = Mode == OperationMode.Copy || journalHealthy && !recoveryRequired;
        }
    }

    private Progress<OperationProgress> MakeProgress() => new(p =>
    {
        StatusText.Text = p.Message;
        ProgressBar.IsIndeterminate = p.Total == 0;
        if (p.Total > 0) ProgressBar.Value = 100.0 * p.Done / p.Total;
    });

    private async Task ScanAsync()
    {
        if (!ready || selections.Count == 0 || busy) return;
        plan = null;
        ApplyButton.IsEnabled = false;
        var inputs = selections.ToArray();
        var recursive = RecursiveBox.IsChecked == true;
        var excluded = Mode == OperationMode.Copy ? Destination : null;
        var zone = ZoneBox.SelectedItem as TimeZoneInfo ?? TimeZoneInfo.Local;
        var corrections = rows.Where(x => x.DateSource == "Manuell korrigiert").ToDictionary(x => x.File.Path, x => x.File.Date.Value!.Value, Paths.Comparer);
        var exclusions = new HashSet<string>(rows.Where(x => !x.Included).Select(x => x.File.Path), Paths.Comparer);
        await RunAsync(async token =>
        {
            var progress = MakeProgress();
            var result = await Task.Run(() => Scanner.Scan(inputs, recursive, excluded, zone, progress, token), token);
            var scannedRows = new List<PreviewRow>(result.Files.Count);
            foreach (var file in result.Files)
            {
                var row = new PreviewRow(file);
                if (corrections.TryGetValue(file.Path, out var date)) row.Correct(date);
                row.Included = !exclusions.Contains(file.Path);
                row.PlanChanged += () => Dispatcher.InvokeAsync(() =>
                {
                    PreviewGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                    PreviewGrid.CommitEdit(DataGridEditingUnit.Row, true);
                    Rebuild();
                });
                scannedRows.Add(row);
            }
            rows.ReplaceAll(scannedRows);
            report = result.Messages.Count == 0 ? "Alle unterstützten Dateien wurden eingelesen." : string.Join(Environment.NewLine, result.Messages);
            ReportButton.IsEnabled = true;
            Rebuild();
            StatusText.Text = $"{rows.Count:N0} Dateien eingelesen. {result.Messages.Count:N0} Meldungen im Protokoll.";
        });
    }

    private async void FolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Ordner mit Reisefotos auswählen", Multiselect = true };
        if (dialog.ShowDialog(this) == true) { selections.AddRange(dialog.FolderNames); await ScanAsync(); }
    }
    private async void FilesClick(object sender, RoutedEventArgs e)
    {
        var glob = string.Join(';', Scanner.Extensions.Split(';').Select(x => "*" + x));
        var dialog = new OpenFileDialog { Title = "Fotos und Videos auswählen", Multiselect = true, Filter = $"Fotos und Videos|{glob}|Alle Dateien|*.*" };
        if (dialog.ShowDialog(this) == true) { selections.AddRange(dialog.FileNames); await ScanAsync(); }
    }
    private async void DestinationClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Zielordner für die Kopien auswählen" };
        if (dialog.ShowDialog(this) == true) { DestinationBox.Text = dialog.FolderName; Rebuild(); await ScanAsync(); }
    }
    private void ClearClick(object sender, RoutedEventArgs e) { selections.Clear(); rows.Clear(); Rebuild(); StatusText.Text = "Auswahl geleert."; }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await ScanAsync();
    private async void ScanSettingChanged(object sender, RoutedEventArgs e) { if (ready) await ScanAsync(); }
    private async void ZoneChanged(object sender, SelectionChangedEventArgs e) { if (ready) await ScanAsync(); }
    private async void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        DestinationBox.IsEnabled = DestinationButton.IsEnabled = Mode == OperationMode.Copy;
        ModeHint.Text = Mode == OperationMode.Copy ? "Die Originale bleiben erhalten. Alle Kopien landen gemeinsam im Zielordner." : "Die Dateien werden in ihren bisherigen Ordnern umbenannt. Die letzte Umbenennung kann rückgängig gemacht werden.";
        ApplyButton.Content = Mode == OperationMode.Copy ? "Kopien erstellen" : "Jetzt umbenennen";
        Rebuild();
        await ScanAsync();
    }
    private void FilterChanged(object sender, RoutedEventArgs e) { if (ready) CollectionViewSource.GetDefaultView(rows).Refresh(); }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] files) { selections.AddRange(files); await ScanAsync(); }
    }

    private void EditDateClick(object sender, RoutedEventArgs e)
    {
        if (PreviewGrid.SelectedItem is not PreviewRow row) { StatusText.Text = "Bitte zuerst eine Datei in der Vorschau auswählen."; return; }
        var dialog = new Window { Owner = this, Title = "Aufnahmedatum korrigieren", Width = 470, Height = 240, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = row.OriginalName, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = "Datum und Uhrzeit (TT.MM.JJJJ HH:MM:SS)", Margin = new Thickness(0, 15, 0, 6) });
        var input = new TextBox { Text = row.File.Date.Value?.ToString("dd.MM.yyyy HH:mm:ss") ?? "" };
        panel.Children.Add(input);
        var button = new Button { Content = "Datum übernehmen", IsDefault = true, Margin = new Thickness(0, 15, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) =>
        {
            if (!DateTime.TryParseExact(input.Text.Trim(), "dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.None, out var date) || !Dates.Valid(date))
            { MessageBox.Show(dialog, "Bitte ein gültiges Aufnahmedatum ab 1905 eingeben, z. B. 18.07.2026 14:32:05.", "Datum prüfen"); return; }
            row.Correct(date);
            dialog.Close();
        };
        panel.Children.Add(button); dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
    }

    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        PreviewGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PreviewGrid.CommitEdit(DataGridEditingUnit.Row, true);
        Rebuild();
        var approved = plan;
        if (approved is null || approved.Files.Count == 0) return;
        if (approved.Mode == OperationMode.Rename && MessageBox.Show(this,
            $"{approved.Files.Count:N0} Originaldateien gemäß Vorschau umbenennen?\n\nDas bisherige Rückgängig-Protokoll wird durch diesen Vorgang ersetzt.", "Originale umbenennen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync(async token =>
        {
            var progress = MakeProgress();
            var result = await Task.Run(() => operations.ExecuteAsync(approved, progress, token), token);
            var summary = $"{result.Completed:N0} Dateien abgeschlossen. " + (result.Cancelled ? "Vorgang abgebrochen. " : "") + $"{result.Errors.Count:N0} Fehler.";
            var outcomes = result.Outcomes ?? [];
            var handled = new HashSet<string>(outcomes.Select(x => x.Source), Paths.Comparer);
            report = summary + "\n\n" + string.Join("\n", result.Errors) + "\n\n" +
                string.Join("\n", outcomes.Select(x => $"{x.State}: {x.Source} → {x.Destination}")) + "\n" +
                string.Join("\n", approved.Files.Where(x => !handled.Contains(x.Source)).Select(x => $"Nicht abgeschlossen: {x.Source} → {x.Destination}"));
            ReportButton.IsEnabled = true;
            if (approved.Mode == OperationMode.Copy)
            {
                lastOutput = Path.GetDirectoryName(approved.Files[0].Destination);
                OpenButton.IsEnabled = lastOutput is not null && Directory.Exists(lastOutput);
            }
            else
            {
                rows.Clear(); selections.Clear(); Rebuild();
                if (operations.NeedsRecovery) summary += " Bitte die Umbenennung rückgängig machen.";
            }
            plan = null;
            StatusText.Text = summary;
            ShowText("Vorgang abgeschlossen", report);
        });
    }
    private async void UndoClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Die ursprünglichen Dateinamen wiederherstellen?", "Umbenennung rückgängig machen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync(async token =>
        {
            var progress = MakeProgress();
            var result = await Task.Run(() => operations.Undo(progress, token), token);
            rows.Clear(); selections.Clear(); Rebuild();
            report = $"{result.Completed:N0} Originalnamen wiederhergestellt." + (result.Cancelled ? " Abgebrochen; erneut rückgängig machen, um fortzufahren." : "") + "\n\n" + string.Join("\n", result.Errors);
            ReportButton.IsEnabled = true;
            StatusText.Text = result.Errors.Count == 0 ? report.Trim() : "Wiederherstellung nicht vollständig. Bitte Protokoll prüfen.";
            ShowText("Wiederherstellung", report);
        });
    }
    private void CancelClick(object sender, RoutedEventArgs e) { cancellation?.Cancel(); StatusText.Text = "Vorgang wird abgebrochen …"; }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; cancellation?.Cancel(); StatusText.Text = "Vorgang wird beendet. Danach können Sie das Fenster schließen."; }
    }
    private void OpenClick(object sender, RoutedEventArgs e)
    {
        if (lastOutput is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { lastOutput }, UseShellExecute = true }); }
        catch (Win32Exception) { StatusText.Text = "Der Zielordner konnte nicht geöffnet werden: " + lastOutput; }
    }
    private void ReportClick(object sender, RoutedEventArgs e) => ShowText("Protokoll", report);
    private void HelpClick(object sender, RoutedEventArgs e)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var contents = new StringBuilder();
        foreach (var suffix in new[] { "Anleitung.txt", "THIRD-PARTY-NOTICES.txt" })
        {
            var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith(suffix, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            contents.AppendLine(reader.ReadToEnd()).AppendLine();
        }
        if (assembly.GetManifestResourceStream("FotoSortierer.RuntimeNotices.txt") is { } notices)
        {
            using var reader = new StreamReader(notices);
            contents.AppendLine(reader.ReadToEnd());
        }
        ShowText("Hilfe und Lizenzen", contents.ToString());
    }
    private void ShowText(string title, string text)
    {
        new Window
        {
            Owner = this, Title = title, Width = 800, Height = 520, MinWidth = 500, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(18) }
        }.ShowDialog();
    }
}
