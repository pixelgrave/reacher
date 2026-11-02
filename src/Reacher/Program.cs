using System;
using System.Net.Sockets;
using System.Threading;
using System.Windows;

namespace Reacher;

internal static class Program
{
    public const int DefaultPort = 47322;

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(true, @"Local\Reacher.Singleton", out var first);
        if (!first) return 0;

        var port = int.TryParse(Environment.GetEnvironmentVariable("REACHER_PORT"), out var p) ? p : DefaultPort;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log.Write($"ui error: {e.Exception}"); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");

        var coordinator = new Coordinator(app.Dispatcher);
        try
        {
            new ReachServer(coordinator, port).Start();
        }
        catch (SocketException ex)
        {
            Log.Write($"cannot listen on {port}: {ex.Message}");
            MessageBox.Show($"Reacher can't listen on port {port}: {ex.Message}", "Reacher", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        using var tray = new Tray(coordinator, port, app);
        Log.Write($"started on 127.0.0.1:{port}");
        app.Run();
        Log.Write("stopped");
        return 0;
    }
}
