using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ArcaneRushSync.Models;
using ArcaneRushSync.Services;

namespace ArcaneRushSync;

public partial class MainWindow : Window
{
    private readonly SecureSessionStore _sessionStore = new();
    private readonly FirebaseAuthService _auth;
    private readonly FirestoreSyncService _sync;
    private readonly GameScannerService _scanner = new();
    private readonly UpdateService _updates = new();
    private UpdateRelease? _availableUpdate;
    private bool _updateStarting;
    private bool _closing;
    private bool _closeCleanupComplete;

    internal bool IsClosingForShutdown => _closing;

    public MainWindow()
    {
        InitializeComponent();
        _auth = new FirebaseAuthService(_sessionStore);
        _sync = new FirestoreSyncService(_auth);
        _scanner.SnapshotChanged += snap => QueueUi(() => UpdateSnapshotUi(snap));
        _scanner.StatusChanged += text => QueueUi(() => SetStatus(text, "working"));
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = CheckForUpdatesAsync();
        SetLoginBusy(true, "Restauration de la session…");
        try
        {
            if (await _auth.TryRestoreAsync())
                ShowHome();
            else
                ShowLogin();
        }
        finally
        {
            SetLoginBusy(false, "");
        }
    }


    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updates.CheckForUpdateAsync();
            if (update is null) return;
            _availableUpdate = update;
            QueueUi(() =>
            {
                UpdateButton.Content = "MAJ DISPONIBLE";
                UpdateButton.ToolTip = $"Version {update.Version} disponible · cliquer pour mettre à jour";
                UpdateButton.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Update check failed: " + ex.Message);
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var update = _availableUpdate;
        if (update is null) return;

        var answer = MessageBox.Show(
            $"La version {update.Version} d’Arcane Rush Sync est disponible.\n\nLa télécharger et l’installer maintenant ?",
            "Mise à jour disponible",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(percent => UpdateButton.Content = $"MAJ {percent}%");
            var payload = await _updates.DownloadAndPrepareAsync(update, progress);

            UpdateButton.Content = "INSTALLATION…";
            await _scanner.StopAsync();
            ProxyStateGuard.RestoreIfPending();

            UpdateService.StartApplyProcess(payload);
            _updateStarting = true;
            _closeCleanupComplete = true;
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            UpdateButton.Content = "MAJ DISPONIBLE";
            UpdateButton.IsEnabled = true;
            AppLog.Warn("Update failed: " + ex.Message);
            MessageBox.Show(
                "La mise à jour n’a pas pu être installée. L’addon actuel n’a pas été modifié.\n\n" + ex.Message,
                "Mise à jour Arcane Rush Sync",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void EmailLogin_Click(object sender, RoutedEventArgs e)
    {
        SetLoginBusy(true, "Connexion au site…");
        try
        {
            await _auth.LoginEmailAsync(EmailBox.Text, PasswordBox.Password);
            PasswordBox.Clear();
            ShowHome();
        }
        catch (Exception ex)
        {
            LoginStatusText.Text = ex.Message;
        }
        finally
        {
            SetLoginBusy(false, LoginStatusText.Text);
        }
    }

    private async void GoogleLogin_Click(object sender, RoutedEventArgs e)
    {
        SetLoginBusy(true, "Ouverture du navigateur…");
        try
        {
            await _auth.LoginGoogleAsync(text => Dispatcher.Invoke(() => LoginStatusText.Text = text));
            ShowHome();
        }
        catch (Exception ex)
        {
            LoginStatusText.Text = ex.Message;
        }
        finally
        {
            SetLoginBusy(false, LoginStatusText.Text);
        }
    }

    private async void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        LaunchButton.IsEnabled = false;
        try
        {
            if (!ScannerConsentStore.HasConsent)
            {
                var choice = MessageBox.Show(
                    "Pour lire uniquement les données utiles à la synchronisation Arcane Rush (pseudo, collection et decks), le Sync utilise un certificat local dédié dans TON profil Windows.\n\n" +
                    "• aucun droit administrateur\n" +
                    "• seul api-overhaul.cbg.alleylabs.com est déchiffré\n" +
                    "• aucun paquet brut n'est conservé sur le disque\n" +
                    "• le proxy Windows est restauré après chaque scan\n" +
                    "• le certificat est créé une seule fois puis réutilisé, pour éviter les fenêtres Windows à chaque fermeture\n" +
                    "• REPARER_RESEAU_WINDOWS.bat permet de le retirer manuellement\n\n" +
                    "Autoriser ce scanner local ?",
                    "Sécurité · Arcane Rush Sync",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (choice != MessageBoxResult.Yes) return;
                ScannerConsentStore.Grant();
            }

            SetStatus("Préparation du scanner sécurisé…", "working");
            await _scanner.StartAsync(installCertificateIfNeeded: true);
            if (!GameLauncherService.IsRunning())
            {
                GameLauncherService.Launch();
                SetStatus("Arcane Rush lancé · scan du pseudo, de la collection et des decks en cours…", "working");
            }
            else
            {
                SetStatus("Arcane Rush est déjà ouvert · scan des prochains échanges en cours. Si rien n'arrive, ferme le jeu puis relance-le avec ce bouton.", "working");
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, "error");
            await _scanner.StopAsync();
        }
        finally
        {
            LaunchButton.IsEnabled = true;
        }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        try
        {
            SetStatus("Synchronisation sécurisée avec le site…", "working");
            await _sync.SyncAsync(_scanner.Snapshot);
            SetStatus($"Synchronisé et vérifié ✓ · { _scanner.Snapshot.PlayerName } + { _scanner.Snapshot.OwnedCardIds.Count } cartes + 13 decks.", "ok");
            SyncSubText.Text = "Synchronisation terminée ✓";
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, "error");
        }
        finally
        {
            UpdateSnapshotUi(_scanner.Snapshot);
        }
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = DiagnosticReportService.Generate(_scanner, _sync, _auth.Session);
            SetStatus("Rapport créé ✓ · tu peux me l'envoyer si le pseudo ou la synchro pose encore problème.", "ok");
            DiagnosticReportService.Reveal(path);
            MessageBox.Show(
                "Le rapport a été créé et son dossier vient de s'ouvrir.\n\n" +
                "Il contient les endpoints vus, les candidats pseudo, l'état des 13 decks et le diagnostic Firebase.\n" +
                "Il ne contient aucun mot de passe, token Firebase ni corps réseau brut.",
                "Rapport Arcane Rush Sync",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Impossible de créer le rapport : " + ex.Message, "error");
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        await _scanner.StopAsync();
        _auth.Logout();
        ShowLogin();
    }

    private void ShowHome()
    {
        LoginView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        HomeActions.Visibility = Visibility.Visible;
        LogoutButton.Visibility = Visibility.Visible;
        ReportButton.Visibility = Visibility.Visible;
        var who = _auth.Session?.DisplayName;
        if (string.IsNullOrWhiteSpace(who)) who = _auth.Session?.Email;
        WelcomeText.Text = string.IsNullOrWhiteSpace(who) ? "Compte connecté" : $"Bonjour, {who}";
        UpdateSnapshotUi(_scanner.Snapshot);
        SetStatus("Clique sur « Lancer Arcane Rush » pour démarrer le scan.", "idle");
    }

    private void ShowLogin()
    {
        LoginView.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        HomeActions.Visibility = Visibility.Collapsed;
        LogoutButton.Visibility = Visibility.Collapsed;
        ReportButton.Visibility = Visibility.Collapsed;
        LoginStatusText.Text = "";
    }

    private void UpdateSnapshotUi(GameSnapshot snap)
    {
        PlayerNameText.Text = snap.PlayerNameConfidence >= 75 && !string.IsNullOrWhiteSpace(snap.PlayerName)
            ? snap.PlayerName
            : "En attente…";
        CollectionCountText.Text = snap.CollectionSyncSafe
            ? $"{snap.OwnedCardIds.Count} cartes"
            : snap.CollectionAsciiCount > 0
                ? "Analyse exacte…"
                : "En attente…";
        DeckCountText.Text = $"{snap.Decks.Count} / {AppConfig.ExpectedDeckCount}";
        var complete = snap.IsComplete(AppConfig.ExpectedDeckCount);
        SyncButton.IsEnabled = complete && _auth.Session is not null;
        if (complete)
            SyncSubText.Text = $"{snap.PlayerName} · {snap.OwnedCardIds.Count} cartes · 13 decks prêts";
        else if (snap.Decks.Count >= AppConfig.ExpectedDeckCount && !snap.CollectionSyncSafe)
            SyncSubText.Text = "13 decks ✓ · collection exacte en attente";
        else if (snap.Decks.Count >= AppConfig.ExpectedDeckCount && snap.CollectionSyncSafe && snap.PlayerNameConfidence < 75)
            SyncSubText.Text = $"13 decks ✓ · {snap.OwnedCardIds.Count} cartes ✓ · pseudo en attente";
        else if (snap.PlayerNameConfidence >= 75)
            SyncSubText.Text = $"Pseudo {snap.PlayerName} ✓ · {snap.OwnedCardIds.Count} cartes · {Math.Max(0, AppConfig.ExpectedDeckCount - snap.Decks.Count)} deck(s) manquant(s)";
        else
            SyncSubText.Text = $"Scan en cours · {snap.OwnedCardIds.Count} cartes · {snap.Decks.Count}/{AppConfig.ExpectedDeckCount} decks";
    }

    private void SetLoginBusy(bool busy, string status)
    {
        EmailLoginButton.IsEnabled = !busy;
        GoogleLoginButton.IsEnabled = !busy;
        EmailBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status)) LoginStatusText.Text = status;
    }

    private void SetStatus(string text, string state)
    {
        StatusText.Text = text;
        StatusDot.Fill = state switch
        {
            "ok" => new SolidColorBrush(Color.FromRgb(113, 203, 133)),
            "error" => new SolidColorBrush(Color.FromRgb(222, 104, 112)),
            _ => new SolidColorBrush(Color.FromRgb(230, 183, 93))
        };
    }

    private void QueueUi(Action action)
    {
        if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                action();
            }));
        }
        catch (Exception ex)
        {
            AppLog.Warn("UI update ignored during shutdown: " + ex.Message);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_updateStarting || _closeCleanupComplete) return;

        // WPF forbids calling Close()/Show()/ShowDialog() while the current Closing
        // event is still executing. The previous implementation called Close() after
        // awaiting the scanner cleanup from inside this event, which produced the
        // popup seen in 1.0.6. Cancel this first close, finish the network cleanup,
        // then queue a second Close on the dispatcher after this event has returned.
        e.Cancel = true;
        if (_closing) return;

        _closing = true;
        try
        {
            await _scanner.StopAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Scanner cleanup during window close failed: " + ex.Message);
            ProxyStateGuard.RestoreIfPending();
        }

        try { _sync.Dispose(); } catch (Exception ex) { AppLog.Warn("Sync dispose failed: " + ex.Message); }
        try { _auth.Dispose(); } catch (Exception ex) { AppLog.Warn("Auth dispose failed: " + ex.Message); }

        _closeCleanupComplete = true;
        try
        {
            _ =             Dispatcher.BeginInvoke(new Action(() =>
            {
                try { Close(); }
                catch (Exception ex)
                {
                    AppLog.Warn("Final window close failed: " + ex.Message);
                    Application.Current.Shutdown();
                }
            }));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Unable to queue final window close: " + ex.Message);
            Application.Current.Shutdown();
        }
    }
}
