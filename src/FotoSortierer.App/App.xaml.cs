using System.Globalization;
using System.Threading;
using System.Windows;

namespace FotoSortierer.App;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
        instance = new Mutex(true, "Local\\FotoSortierer-0E70E4CB", out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("FotoSortierer ist bereits geöffnet.", "FotoSortierer");
            Shutdown();
            return;
        }
        base.OnStartup(e);
        var window = new MainWindow();
        window.Show();
        if (e.Args.Contains("--smoke-test"))
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await window.SmokeTestAsync();
                    window.Close();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    Shutdown(1);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
