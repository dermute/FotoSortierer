using System.ComponentModel;
using System.Globalization;
using FotoSortierer.Core;

namespace FotoSortierer.App;

public sealed class PreviewRow(MediaFile file) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? PlanChanged;
    private bool included = true;
    private string newName = "";
    public MediaFile File { get; private set; } = file;
    public string OriginalName => System.IO.Path.GetFileName(File.Path);
    public string DisplayDate => File.Date.Value?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("de-DE")) ?? "Datum fehlt";
    public string DateSource => File.Date.Source;
    public string Warning => File.Date.Warning;
    public bool HasWarning => Warning.Length > 0;
    public bool Included
    {
        get => included;
        set { if (included == value) return; included = value; Notify(nameof(Included)); PlanChanged?.Invoke(); }
    }
    public string NewName { get => newName; set { newName = value; Notify(nameof(NewName)); } }
    public void Correct(DateTime date)
    {
        File = File with { Date = new(date, "Manuell korrigiert") };
        Notify(nameof(DisplayDate)); Notify(nameof(DateSource)); Notify(nameof(Warning)); Notify(nameof(HasWarning));
        PlanChanged?.Invoke();
    }
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}
