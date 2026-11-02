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
    private bool _syncRequested;
    private bool _syncInProgress;
    private readonly System.Windows.Threading.DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    internal bool IsClosingForShutdown => _closing;

    public MainWindow()
    {
        InitializeComponent();
        _auth = new FirebaseAuthService(_sessionStore);
        _sync = new FirestoreSyncService(_auth);
        _scanner.SnapshotChanged += snap => QueueUi(() =>
        {
            UpdateSnapshotUi(snap);
            EvaluatePendingSync();
        });
        _syncTimer.Tick += (_, _) => EvaluatePendingSync();
        _scanner.StatusChanged += text => QueueUi(() => SetStatus(text, "working"));
        _scanner.Stopped += () => QueueUi(OnScannerStopped);
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

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_auth.Session is null || _syncInProgress || _syncRequested) return;

        SyncButton.IsEnabled = false;
        try
        {
            if (!EnsureScannerConsent())
            {
                SyncButton.IsEnabled = true;
                return;
            }

            // If the game is already open, its startup data has already gone by. Offer to close
            // it normally; the scanner then waits for the fresh launch below.
            if (GameLauncherService.IsRunning())
            {
                var answer = MessageBox.Show(
                    "Arcane Rush est déjà ouvert.\n\nTa collection n’est envoyée par le jeu qu’au démarrage : il faut le relancer.\n\n" +
                    "Fermer Arcane Rush maintenant et le relancer automatiquement ?",
                    "Arcane Rush Sync",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    SyncButton.IsEnabled = true;
                    return;
                }
            }

            _syncRequested = true;
            SetStatus("Préparation du scanner local…", "working");
            await _scanner.StartAsync(installCertificateIfNeeded: true);
            _syncTimer.Start();

            if (GameLauncherService.IsRunning())
            {
                SetStatus("Fermeture d’Arcane Rush…", "working");
                var closed = await GameLauncherService.TryCloseRunningGameAsync(TimeSpan.FromSeconds(20));
                if (!closed)
                {
                    SetStatus("Arcane Rush ne s’est pas fermé tout seul. Ferme-le, puis relance-le : le scanner reste armé.", "working");
                    SyncSubText.Text = "Scanner actif · en attente du relancement";
                    return;
                }
                await Task.Delay(1500);
            }

            var launch = GameLauncherService.Launch();
            if (launch.Started)
            {
                SyncSubText.Text = "Arcane Rush démarre · lecture de la collection…";
                SetStatus(
                    launch.Method == "steam"
                        ? "Lancement d’Arcane Rush via Steam… Laisse le jeu charger jusqu’au menu : la synchronisation se fera toute seule."
                        : "Lancement d’Arcane Rush… Laisse le jeu charger jusqu’au menu : la synchronisation se fera toute seule.",
                    "working");
            }
            else
            {
                SyncSubText.Text = "Scanner actif · lance Arcane Rush";
                SetStatus("Impossible de lancer Arcane Rush automatiquement (Steam introuvable). Lance-le toi-même : le scanner est prêt.", "working");
            }
        }
        catch (Exception ex)
        {
            _syncRequested = false;
            _syncTimer.Stop();
            SetStatus(ex.Message, "error");
            await _scanner.StopAsync();
            SyncButton.IsEnabled = _auth.Session is not null;
            SyncSubText.Text = "Clique pour réessayer";
        }
    }

    private void EvaluatePendingSync()
    {
        if (!_syncRequested || _syncInProgress || _closing) return;
        var snap = _scanner.Snapshot;
        if (snap.IsComplete(AppConfig.ExpectedDeckCount))
        {
            _ = CompletePendingSyncAsync(snap);
            return;
        }

        // Exact collection received but not every merchant deck: some players do not own all
        // merchants. Wait for a quiet period (nothing new detected) and sync what we have.
        if (snap.IsCollectionReady
            && snap.LastDetectionChangeAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - snap.LastDetectionChangeAt >= AppConfig.PartialSyncQuietPeriod)
        {
            _ = CompletePendingSyncAsync(snap);
        }
    }

    private bool EnsureScannerConsent()
    {
        if (ScannerConsentStore.HasConsent) return true;

        var choice = MessageBox.Show(
            "Pour lire ta collection et tes decks pendant le démarrage d’Arcane Rush, le Sync utilise un petit relais réseau local et un certificat dédié dans TON profil Windows.\n\n" +
            "• aucun droit administrateur\n" +
            "• seul le trafic du jeu (api-overhaul.cbg.alleylabs.com) est lu ; tout le reste passe sans être déchiffré\n" +
            "• aucun paquet brut n'est conservé sur le disque\n" +
            "• le proxy Windows est restauré dès que la collection est lue\n" +
            "• Windows va afficher une fenêtre « Avertissement de sécurité » : réponds Oui (une seule fois)\n" +
            "• le bouton « Réparer le réseau » retire tout en un clic\n\n" +
            "Autoriser ce scanner local ?",
            "Sécurité · Arcane Rush Sync",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (choice != MessageBoxResult.Yes) return false;
        ScannerConsentStore.Grant();
        return true;
    }

    private async Task CompletePendingSyncAsync(GameSnapshot snapshot)
    {
        if (!_syncRequested || _syncInProgress || _auth.Session is null) return;

        _syncRequested = false;
        _syncTimer.Stop();
        _syncInProgress = true;
        SyncButton.IsEnabled = false;
        var success = false;

        try
        {
            SetStatus("Collection Arcane Rush lue ✓ · restauration du réseau…", "working");
            await _scanner.StopAsync();

            SetStatus("Synchronisation sécurisée avec le site…", "working");
            await _sync.SyncAsync(snapshot);

            success = true;
            var who = snapshot.HasVerifiedPlayerName ? snapshot.PlayerName + " · " : "";
            SetStatus(
                $"Synchronisé et vérifié ✓ · {who}{snapshot.OwnedCardIds.Count} cartes + {snapshot.ValidDecks.Count()} decks. Ta collection est à jour sur le site.",
                "ok");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, "error");
        }
        finally
        {
            _syncInProgress = false;
            UpdateSnapshotUi(_scanner.Snapshot);
            if (success)
                SyncSubText.Text = "Synchronisation terminée ✓";
        }
    }

    private void OnScannerStopped()
    {
        if (_closing || _updateStarting || _syncInProgress || !_syncRequested) return;

        var snap = _scanner.Snapshot;
        if (snap.IsCollectionReady)
        {
            _ = CompletePendingSyncAsync(snap);
            return;
        }

        _syncRequested = false;
        _syncTimer.Stop();
        SyncButton.IsEnabled = _auth.Session is not null;
        SyncSubText.Text = "Scan incomplet · clique pour réessayer";
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Restaurer les réglages réseau Windows et retirer le certificat local d’Arcane Rush Sync ?\n\n" +
            "À utiliser si Internet ne marche plus après un scan, ou avant de désinstaller. " +
            "Windows peut demander de confirmer la suppression du certificat : réponds Oui.\n\n" +
            "Le prochain scan redemandera simplement l’autorisation.",
            "Réparer le réseau",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        RepairButton.IsEnabled = false;
        try
        {
            _syncRequested = false;
            _syncTimer.Stop();
            await _scanner.StopAsync();
            // UI thread on purpose: Windows shows its own confirmation when a root certificate is removed.
            var result = NetworkRepairService.Repair(removeCertificates: true);
            SetStatus(
                $"Réseau vérifié ✓ · proxy {(result.ProxyRestored || result.ProxyDisabled ? "restauré" : "déjà normal")} · {result.CertificatesRemoved} certificat(s) retiré(s).",
                "ok");
        }
        catch (Exception ex)
        {
            SetStatus("Réparation incomplète : " + ex.Message, "error");
        }
        finally
        {
            RepairButton.IsEnabled = true;
            UpdateSnapshotUi(_scanner.Snapshot);
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _syncRequested = false;
        _syncTimer.Stop();
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
        var who = _auth.Session?.DisplayName;
        if (string.IsNullOrWhiteSpace(who)) who = _auth.Session?.Email;
        WelcomeText.Text = string.IsNullOrWhiteSpace(who) ? "Compte connecté" : $"Bonjour, {who}";
        UpdateSnapshotUi(_scanner.Snapshot);
        SetStatus("Clique sur « Lancer Arcane Rush » : le jeu démarre et ta collection est envoyée sur ton compte du site.", "idle");
    }

    private void ShowLogin()
    {
        LoginView.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        HomeActions.Visibility = Visibility.Collapsed;
        LogoutButton.Visibility = Visibility.Collapsed;
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

        if (_syncInProgress)
        {
            SyncButton.IsEnabled = false;
            SyncSubText.Text = "Envoi sécurisé au site…";
            return;
        }

        if (_syncRequested)
        {
            SyncButton.IsEnabled = false;
            if (complete)
                SyncSubText.Text = "Données complètes · synchronisation…";
            else if (snap.IsCollectionReady)
                SyncSubText.Text = $"{snap.OwnedCardIds.Count} cartes ✓ · {snap.Decks.Count}/{AppConfig.ExpectedDeckCount} decks · finalisation…";
            else
                SyncSubText.Text = $"Lecture en cours · {snap.Decks.Count}/{AppConfig.ExpectedDeckCount} decks";
            return;
        }

        SyncButton.IsEnabled = _auth.Session is not null;
        SyncSubText.Text = snap.IsCollectionReady
            ? $"Dernière lecture : {snap.OwnedCardIds.Count} cartes · {snap.Decks.Count} decks"
            : "Démarre le jeu et envoie ta collection au site";
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
