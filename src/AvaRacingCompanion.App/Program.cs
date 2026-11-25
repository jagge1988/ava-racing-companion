using System.Threading;
using AvaRacingCompanion.Core.Versioning;
using Velopack;

namespace AvaRacingCompanion.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Pflicht für Velopack: Verarbeitet Install-, Update- und Uninstall-Hooks
        // noch bevor WPF oder Benutzerlogik startet.
        VelopackApp.Build().Run();

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: $"Local\\{AppInfo.AppId}",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            return;
        }

        var startMinimized = args.Any(arg =>
            string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        var app = new App(startMinimized);
        app.InitializeComponent();
        app.Run();
    }
}
