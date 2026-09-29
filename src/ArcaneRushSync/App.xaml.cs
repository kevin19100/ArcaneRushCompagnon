using System.Threading;
using System.Windows;
using ArcaneRushSync.Services;

namespace ArcaneRushSync;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _isPrimaryInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (TryHandleUpdateMode(e.Args))
            return;

        _singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\ArcaneRushSync.Singleton.v1",
            createdNew: out var createdNew);
        _isPrimaryInstance = createdNew;

        if (!createdNew)
        {
            MessageBox.Show(
                "Arcane Rush Sync est déjà ouvert.",
                "Arcane Rush Sync",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        AppPaths.EnsureDirectories();
        ProxyStateGuard.RestoreIfPending();

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled UI exception", args.Exception);
            ProxyStateGuard.RestoreIfPending();
            args.Handled = true;

            var closing = Current?.MainWindow is MainWindow main && main.IsClosingForShutdown;
            var dispatcherStopping = Current?.Dispatcher?.HasShutdownStarted == true
                                     || Current?.Dispatcher?.HasShutdownFinished == true;
            if (!closing && !dispatcherStopping)
            {
                try
                {
                    MessageBox.Show(
                        "Arcane Rush Sync a rencontré une erreur. Les réglages réseau Windows ont été restaurés par sécurité.\n\n" + args.Exception.Message,
                        "Arcane Rush Sync",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch (Exception popupEx)
                {
                    AppLog.Warn("Error popup suppressed: " + popupEx.Message);
                }
            }

            Shutdown(-1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Error("Unhandled process exception", ex);
            ProxyStateGuard.RestoreIfPending();
        };

        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (e.Args.Length >= 3 && string.Equals(e.Args[0], "--cleanup-update", StringComparison.OrdinalIgnoreCase))
        {
            var updateRoot = e.Args[1];
            _ = int.TryParse(e.Args[2], out var updaterPid);
            UpdateService.CleanupStagingInBackground(updateRoot, updaterPid);
        }
    }

    private bool TryHandleUpdateMode(string[] args)
    {
        if (args.Length < 5 || !string.Equals(args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
            return false;

        var payloadDirectory = args[1];
        var installDirectory = args[2];
        _ = int.TryParse(args[3], out var oldProcessId);
        var updateRoot = args[4];
        var exitCode = UpdateService.ApplyUpdateAndRestart(payloadDirectory, installDirectory, oldProcessId, updateRoot);
        Environment.Exit(exitCode);
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_isPrimaryInstance)
        {
            ProxyStateGuard.RestoreIfPending();
            try { _singleInstance?.ReleaseMutex(); } catch { }
        }
        try { _singleInstance?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
