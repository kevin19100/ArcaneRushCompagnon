using System.Net;
using System.Security.Cryptography.X509Certificates;
using ArcaneRushSync.Models;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace ArcaneRushSync.Services;

public sealed class GameScannerService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly DeckDetector _detector;
    private readonly GameSnapshot _snapshot = new();
    private readonly HashSet<string> _seenApiPaths = new(StringComparer.Ordinal);
    private readonly List<TrafficObservation> _trafficDiagnostics = new();
    private ProxyServer? _proxy;
    private ExplicitProxyEndPoint? _endpoint;
    private CancellationTokenSource? _scanCts;
    private bool _running;
    private int _stopping;
    private long _apiRequestsSeen;
    private long _apiResponsesSeen;
    private long _candidateBodiesScanned;
    private volatile bool _acceptGameTraffic = true;

    public event Action<GameSnapshot>? SnapshotChanged;
    public event Action<string>? StatusChanged;
    public event Action? Stopped;

    public bool IsRunning => _running;
    public int NumericReferenceCount => _detector.NumericReferenceCount;

    public IReadOnlyList<IdentityCandidateDiagnostic> GetIdentityDiagnostics() => _detector.GetIdentityDiagnostics();

    public IReadOnlyList<TrafficObservation> GetTrafficDiagnostics()
    {
        lock (_gate) return _trafficDiagnostics.ToArray();
    }

    public GameScannerService()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        _detector = new DeckDetector(dataDir);
        var local = PlayerLogReader.TryReadWithSource();
        if (!string.IsNullOrWhiteSpace(local.Name))
        {
            _snapshot.PlayerName = local.Name;
            _snapshot.PlayerNameConfidence = 55;
            _snapshot.PlayerNameSource = local.Source;
            AppLog.Info($"Initial weak player-name fallback from {local.Source}: {local.Name}");
        }
    }

    public GameSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot.Clone(); }
    }

    public async Task StartAsync(bool installCertificateIfNeeded, CancellationToken cancellationToken = default)
    {
        if (_running) return;
        if (Volatile.Read(ref _stopping) != 0)
            throw new InvalidOperationException("Le scanner est encore en cours de fermeture. Réessaie dans une seconde.");

        // Recover from a previous abnormal termination before touching the network stack again.
        // The dedicated CurrentUser root is intentionally reused between scans: Windows
        // displays a non-suppressible Root Store confirmation when a trusted root is removed.
        ProxyStateGuard.RestoreIfPending();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var gameProcessAtStart = GameLauncherService.TryGetRunningProcessId();
        _acceptGameTraffic = !gameProcessAtStart.HasValue;
        ResetDetectionState("scan-start");

        _proxy = CreateProxyServer();
        _endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, GetFreePort(), decryptSsl: true);
        _endpoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
        _proxy.BeforeRequest += OnBeforeRequest;
        _proxy.BeforeResponse += OnBeforeResponse;
        _proxy.AddEndPoint(_endpoint);

        // Create/load one dedicated CA and trust it only in CurrentUser.  Reusing the
        // same persisted certificate avoids install/delete prompts on every launch.
        _proxy.CertificateManager.EnsureRootCertificate();
        var alreadyTrusted = IsCurrentUserRootTrusted(_proxy.CertificateManager.RootCertificate);
        if (!alreadyTrusted && installCertificateIfNeeded)
            _proxy.CertificateManager.TrustRootCertificate();

        if (!IsCurrentUserRootTrusted(_proxy.CertificateManager.RootCertificate))
            throw new InvalidOperationException("Windows n'a pas approuvé le certificat local de synchronisation.");

        StatusChanged?.Invoke("Scanner prêt · activation réseau locale…");
        if (!ProxyStateGuard.Capture())
            throw new InvalidOperationException("Impossible de sauvegarder les réglages proxy Windows. Le scan a été annulé par sécurité.");

        try
        {
            _proxy.Start();
            _proxy.SetAsSystemProxy(_endpoint, ProxyProtocolType.AllHttp);
            _running = true;
            AppLog.Info($"Scanner started on 127.0.0.1:{_endpoint.Port}; target host={AppConfig.ApiHost}; gamePidAtStart={gameProcessAtStart?.ToString() ?? "none"}.");
            StatusChanged?.Invoke(gameProcessAtStart.HasValue
                ? "Scanner prêt ✓ · Arcane Rush est déjà ouvert. Ferme-le puis relance-le normalement : le scanner restera armé et récupérera la collection au prochain démarrage."
                : "Scanner prêt ✓ · lance Arcane Rush normalement. La collection et les decks seront lus pendant le chargement du jeu.");
            _ = Task.Run(() => WatchLoopAsync(_scanCts.Token, gameProcessAtStart), _scanCts.Token);
        }
        catch
        {
            // Never leave Windows pointing at a proxy which did not finish starting.
            ProxyStateGuard.RestoreIfPending();
            try { _proxy.Stop(); } catch { }
            try { _proxy.Dispose(); } catch { }
            _proxy = null;
            _endpoint = null;
            throw;
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        try
        {
            _running = false;
            try { _scanCts?.Cancel(); } catch { }

            // Let Titanium stop, then restore our exact WinINet registry snapshot. This preserves
            // a pre-existing corporate proxy, PAC URL or custom user configuration byte-for-byte
            // for the values we touched.
            try { _proxy?.Stop(); } catch (Exception ex) { AppLog.Warn("Proxy stop failed: " + ex.Message); }
            ProxyStateGuard.RestoreIfPending();

            try
            {
                if (_endpoint is not null)
                    _endpoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequest;
                if (_proxy is not null)
                {
                    _proxy.BeforeRequest -= OnBeforeRequest;
                    _proxy.BeforeResponse -= OnBeforeResponse;
                }
            }
            catch { }

            try { _proxy?.Dispose(); } catch { }
            _proxy = null;
            _endpoint = null;
            try { _scanCts?.Dispose(); } catch { }
            _scanCts = null;

            var final = Snapshot;
            AppLog.Info($"Scanner stopped. apiRequests={_apiRequestsSeen}, apiResponses={_apiResponsesSeen}, candidateBodies={_candidateBodiesScanned}, decks={final.Decks.Count}, collection={final.OwnedCardIds.Count}/safe={final.CollectionSyncSafe}, player={final.PlayerName}/{final.PlayerNameConfidence}, numericRefs={_detector.NumericReferenceCount}.");
            StatusChanged?.Invoke(final.IsComplete(AppConfig.ExpectedDeckCount)
                ? $"Scan terminé ✓ · {final.PlayerName} + {final.OwnedCardIds.Count} cartes + 13 decks · réseau Windows restauré."
                : "Scan arrêté · réseau Windows restauré. Le certificat local dédié reste approuvé pour les prochains scans.");
            Stopped?.Invoke();
            await Task.CompletedTask;
        }
        finally
        {
            Volatile.Write(ref _stopping, 0);
        }
    }

    private static ProxyServer CreateProxyServer()
    {
        var proxy = new ProxyServer(
            rootCertificateName: "Arcane Rush Sync Local CA",
            rootCertificateIssuerName: "Arcane Rush Sync",
            userTrustRootCertificate: false,
            machineTrustRootCertificate: false,
            trustRootCertificateAsAdmin: false)
        {
            ViaHeaderPseudonym = "",
            MaxBufferedBodyBytes = AppConfig.MaxCapturedBodyBytes,
            EnableHttp2 = true,
            EnableDecryptFailureBypass = false,
            // If the PC already uses a corporate/VPN upstream proxy, keep forwarding
            // through it instead of silently breaking the user's network setup.
            ForwardToUpstreamGateway = true
        };
        proxy.CertificateManager.PfxFilePath = AppPaths.ProxyCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        return proxy;
    }

    private Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
    {
        try
        {
            var host = e.HttpClient.Request.RequestUri.Host;
            // Everything else remains an opaque CONNECT tunnel. Its TLS content is never visible
            // to Arcane Rush Sync and no response body is requested for non-game domains.
            e.DecryptSsl = string.Equals(host, AppConfig.ApiHost, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            e.DecryptSsl = false;
        }
        return Task.CompletedTask;
    }

    private async Task OnBeforeRequest(object sender, SessionEventArgs e)
    {
        try
        {
            var uri = e.HttpClient.Request.RequestUri;
            if (!string.Equals(uri.Host, AppConfig.ApiHost, StringComparison.OrdinalIgnoreCase)) return;
            if (!_acceptGameTraffic) return;

            var path = uri.AbsolutePath;
            var method = e.HttpClient.Request.Method ?? "";
            Interlocked.Increment(ref _apiRequestsSeen);
            lock (_gate) _seenApiPaths.Add(path);

            byte[] body = Array.Empty<byte>();
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    body = await e.GetRequestBody();
                    if (body.Length > 2 * 1024 * 1024) body = Array.Empty<byte>();
                }
                catch { body = Array.Empty<byte>(); }
            }

            var observation = ScannerDiagnosticExtractor.Build(
                "request", method, path, 0, "", body);
            AddTrafficDiagnostic(observation);

            if (body.Length == 0) return;

            string accepted = "";
            IReadOnlyList<string> candidates = Array.Empty<string>();
            lock (_gate)
            {
                var probe = _detector.InspectPlayerName(body, path, "request");
                candidates = probe.Candidates;
                if (ApplyPlayerNameProbeLocked(probe, $"request:{path}"))
                {
                    accepted = probe.AcceptedName;
                    _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            if (candidates.Count > 0)
                AppLog.Info($"Player-name request candidates path={path}: {string.Join(",", candidates)}");

            if (!string.IsNullOrWhiteSpace(accepted))
            {
                AppLog.Info($"Player name detected from request {path}: {accepted}");
                var snap = Snapshot;
                SnapshotChanged?.Invoke(snap);
                StatusChanged?.Invoke(StatusForSnapshot(snap));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture request ignored: " + ex.Message);
        }
    }

    private async Task OnBeforeResponse(object sender, SessionEventArgs e)
    {
        try
        {
            var uri = e.HttpClient.Request.RequestUri;
            if (!string.Equals(uri.Host, AppConfig.ApiHost, StringComparison.OrdinalIgnoreCase)) return;
            if (!_acceptGameTraffic) return;

            var path = uri.AbsolutePath;
            Interlocked.Increment(ref _apiResponsesSeen);
            lock (_gate) _seenApiPaths.Add(path);

            // We read a game response only when it can contribute to the name or to equipped-deck
            // detection. /001003 is handled by a strict nested-deck extractor; live run/GDC
            // endpoints remain completely barred from deck use.
            var current = Snapshot;
            var needName = current.PlayerNameConfidence < 100 || string.Equals(path, "/001003", StringComparison.Ordinal);
            var deckEligiblePath = AppConfig.IsDeckEligibleEndpoint(path);
            var collectionEligiblePath = string.Equals(path, "/001003", StringComparison.Ordinal);
            if (!needName && !deckEligiblePath && !collectionEligiblePath) return;

            var body = await e.GetResponseBody();
            if (body.Length == 0 || body.Length > AppConfig.MaxCapturedBodyBytes) return;
            AddTrafficDiagnostic(ScannerDiagnosticExtractor.Build(
                "response", e.HttpClient.Request.Method ?? "", path, e.HttpClient.Response.StatusCode, "", body));
            if (deckEligiblePath) Interlocked.Increment(ref _candidateBodiesScanned);

            var changed = false;
            var acceptedDecks = new List<DetectedDeck>();

            lock (_gate)
            {
                if (needName)
                {
                    var probe = _detector.InspectPlayerName(body, path, "response");
                    if (probe.Candidates.Count > 0)
                        AppLog.Info($"Player-name response candidates path={path}: {string.Join(",", probe.Candidates)}");
                    if (ApplyPlayerNameProbeLocked(probe, $"response:{path}"))
                    {
                        changed = true;
                        AppLog.Info($"Player name accepted from response {path}: {probe.AcceptedName} confidence={probe.Confidence} evidence={probe.Evidence}");
                    }
                }

                if (deckEligiblePath)
                {
                    foreach (var deck in _detector.DetectDecks(body, path))
                    {
                        // Generic endpoints need stronger evidence. The two validated endpoints are
                        // already promoted to 100 by DeckDetector. This preserves /002001 discoveries
                        // without allowing collection/run-pool lookalikes to replace a real deck.
                        if (!AppConfig.ValidatedDeckEndpoints.Contains(path)
                            && deck.Confidence < AppConfig.GenericDeckConfidence)
                            continue;

                        if (!_snapshot.Decks.TryGetValue(deck.MerchantId, out var old)
                            || DeckRank(deck) > DeckRank(old)
                            || (DeckRank(deck) == DeckRank(old)
                                && !old.CardIds.SequenceEqual(deck.CardIds, StringComparer.OrdinalIgnoreCase)))
                        {
                            _snapshot.Decks[deck.MerchantId] = deck;
                            acceptedDecks.Add(deck);
                            changed = true;
                        }
                    }
                }

                if (collectionEligiblePath)
                {
                    var collection = _detector.DetectOwnedCollection(body, path);
                    if (collection.SyncSafe
                        && (collection.Confidence > _snapshot.CollectionConfidence
                            || !_snapshot.CollectionSyncSafe
                            || !_snapshot.OwnedCardIds.SequenceEqual(collection.OwnedCardIds, StringComparer.OrdinalIgnoreCase)))
                    {
                        _snapshot.OwnedCardIds = collection.OwnedCardIds.ToArray();
                        _snapshot.CollectionSyncSafe = true;
                        _snapshot.CollectionConfidence = collection.Confidence;
                        _snapshot.CollectionMethod = collection.Method;
                        _snapshot.CollectionAsciiCount = collection.AsciiCount;
                        _snapshot.CollectionNoiseCount = collection.NoiseCount;
                        _snapshot.CollectionNumericOnlyCount = collection.NumericOnlyCount;
                        _snapshot.CollectionUnresolvedRecords = collection.UnresolvedRecords;
                        _snapshot.CollectionCandidateRecords = collection.CandidateRecords;
                        _snapshot.CollectionNumericOnlyIds = collection.SafeNumericOnlyIds.ToArray();
                        _snapshot.CollectionInferredIds = collection.SafeInferredIds.ToArray();
                        _snapshot.CollectionNoiseIds = collection.SafeNoiseIds.ToArray();
                        _snapshot.CollectionRejectedNumericIds = collection.SafeRejectedNumericIds.ToArray();
                        _snapshot.CollectionDeckRecoveredIds = Array.Empty<string>();
                        changed = true;
                        AppLog.Info($"Owned collection accepted from {path}: count={collection.OwnedCardIds.Count}, confidence={collection.Confidence}, method={collection.Method}, ascii={collection.AsciiCount}, noise={collection.NoiseCount}, numericOnly={collection.NumericOnlyCount}, inferred={collection.SafeInferredIds.Count}, unresolved={collection.UnresolvedRecords}.");
                    }
                    else if (!collection.SyncSafe)
                    {
                        if (!_snapshot.CollectionSyncSafe)
                        {
                            _snapshot.CollectionConfidence = collection.Confidence;
                            _snapshot.CollectionMethod = collection.Method;
                            _snapshot.CollectionAsciiCount = collection.AsciiCount;
                            _snapshot.CollectionNoiseCount = collection.NoiseCount;
                            _snapshot.CollectionNumericOnlyCount = collection.NumericOnlyCount;
                            _snapshot.CollectionUnresolvedRecords = collection.UnresolvedRecords;
                            _snapshot.CollectionCandidateRecords = collection.CandidateRecords;
                            _snapshot.CollectionNumericOnlyIds = collection.SafeNumericOnlyIds.ToArray();
                            _snapshot.CollectionInferredIds = collection.SafeInferredIds.ToArray();
                            _snapshot.CollectionNoiseIds = collection.SafeNoiseIds.ToArray();
                            _snapshot.CollectionRejectedNumericIds = collection.SafeRejectedNumericIds.ToArray();
                        }
                        AppLog.Warn($"Owned collection not sync-safe from {path}: method={collection.Method}, ascii={collection.AsciiCount}, confidence={collection.Confidence}, noise={collection.NoiseCount}, rejectedNumeric={collection.SafeRejectedNumericIds.Count}.");
                    }
                }

                var local = PlayerLogReader.TryReadWithSource();
                if (!string.IsNullOrWhiteSpace(local.Name) && _snapshot.PlayerNameConfidence < 55)
                {
                    _snapshot.PlayerName = local.Name;
                    _snapshot.PlayerNameConfidence = 55;
                    _snapshot.PlayerNameSource = local.Source;
                    changed = true;
                    AppLog.Info($"Weak player-name fallback detected from {local.Source}: {local.Name}");
                }

                if (ReconcileCollectionWithDeckProofLocked())
                    changed = true;

                if (changed) _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            }

            foreach (var deck in acceptedDecks)
                AppLog.Info($"Deck accepted merchant={deck.MerchantId}, cards={deck.CardIds.Count}, confidence={deck.Confidence}, endpoint={path}.");

            if (changed)
            {
                var snap = Snapshot;
                SnapshotChanged?.Invoke(snap);
                StatusChanged?.Invoke(StatusForSnapshot(snap));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture response ignored: " + ex.Message);
        }
    }

    private async Task WatchLoopAsync(CancellationToken token, int? gameProcessAtStart)
    {
        var waitingForRestart = gameProcessAtStart.HasValue;
        var originalGameWasSeenClosed = !waitingForRestart;
        int? activeGamePid = null;
        DateTimeOffset? gameObservedAt = null;
        var warnedNoTraffic = false;
        var warnedTrafficNoDeck = false;
        var restartPromptLogged = false;

        while (!token.IsCancellationRequested && _running)
        {
            try
            {
                var local = PlayerLogReader.TryReadWithSource();
                if (!string.IsNullOrWhiteSpace(local.Name))
                {
                    var changed = false;
                    lock (_gate)
                    {
                        if (_snapshot.PlayerNameConfidence < 55)
                        {
                            _snapshot.PlayerName = local.Name;
                            _snapshot.PlayerNameConfidence = 55;
                            _snapshot.PlayerNameSource = local.Source;
                            _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
                            changed = true;
                            AppLog.Info($"Weak player-name fallback detected from {local.Source}: {local.Name}");
                        }
                    }
                    if (changed)
                    {
                        var snap = Snapshot;
                        SnapshotChanged?.Invoke(snap);
                        StatusChanged?.Invoke(StatusForSnapshot(snap));
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var currentPid = GameLauncherService.TryGetRunningProcessId();

                if (waitingForRestart)
                {
                    if (currentPid == gameProcessAtStart)
                    {
                        if (!restartPromptLogged)
                        {
                            restartPromptLogged = true;
                            AppLog.Info($"Scanner armed while existing Arcane Rush pid={gameProcessAtStart} is still open; waiting for a fresh game launch.");
                            StatusChanged?.Invoke(
                                "Arcane Rush était déjà ouvert avant le scan. Ferme-le puis relance-le normalement : aucune donnée partielle de cette ancienne session ne sera utilisée.");
                        }

                        try { await Task.Delay(250, token); } catch { return; }
                        continue;
                    }

                    if (currentPid is null)
                    {
                        if (!originalGameWasSeenClosed)
                        {
                            originalGameWasSeenClosed = true;
                            _acceptGameTraffic = false;
                            ResetDetectionState("existing-game-closed");
                            _acceptGameTraffic = true;
                            AppLog.Info("Existing Arcane Rush instance closed; scanner is armed before the next launch.");
                            StatusChanged?.Invoke("Arcane Rush fermé ✓ · scanner armé. Relance maintenant le jeu normalement.");
                        }

                        try { await Task.Delay(250, token); } catch { return; }
                        continue;
                    }

                    // The PID changed so quickly that the poll may not have observed a gap.
                    // Reset before accepting the new session to avoid mixing old-menu traffic
                    // with the fresh bootstrap.
                    if (!originalGameWasSeenClosed)
                    {
                        _acceptGameTraffic = false;
                        ResetDetectionState("fast-game-restart");
                        _acceptGameTraffic = true;
                    }

                    waitingForRestart = false;
                    activeGamePid = currentPid;
                    gameObservedAt = now;
                    warnedNoTraffic = false;
                    warnedTrafficNoDeck = false;
                    AppLog.Info($"Fresh Arcane Rush launch detected pid={currentPid}; scan timeout window begins now.");
                    StatusChanged?.Invoke("Nouveau lancement d’Arcane Rush détecté ✓ · lecture de la collection et des decks en cours…");
                }
                else if (activeGamePid is null)
                {
                    if (currentPid.HasValue)
                    {
                        activeGamePid = currentPid;
                        gameObservedAt = now;
                        warnedNoTraffic = false;
                        warnedTrafficNoDeck = false;
                        AppLog.Info($"Arcane Rush process detected pid={currentPid}; scan timeout window begins now.");
                        StatusChanged?.Invoke("Arcane Rush détecté ✓ · lecture de la collection et des decks en cours…");
                    }
                }
                else if (currentPid != activeGamePid)
                {
                    if (Snapshot.IsComplete(AppConfig.ExpectedDeckCount))
                    {
                        try { await Task.Delay(150, token); } catch { return; }
                        continue;
                    }

                    _acceptGameTraffic = false;
                    ResetDetectionState("game-session-changed");
                    _acceptGameTraffic = true;
                    activeGamePid = currentPid;
                    gameObservedAt = currentPid.HasValue ? now : null;
                    warnedNoTraffic = false;
                    warnedTrafficNoDeck = false;

                    if (currentPid.HasValue)
                    {
                        AppLog.Info($"Arcane Rush restarted with pid={currentPid}; starting a clean scan.");
                        StatusChanged?.Invoke("Arcane Rush relancé ✓ · nouvelle lecture complète en cours…");
                    }
                    else
                    {
                        AppLog.Info("Arcane Rush closed before scan completion; scanner remains armed.");
                        StatusChanged?.Invoke("Arcane Rush fermé avant la fin du scan · le scanner reste armé. Relance le jeu normalement.");
                        try { await Task.Delay(250, token); } catch { return; }
                        continue;
                    }
                }

                var elapsed = gameObservedAt is null
                    ? TimeSpan.Zero
                    : now - gameObservedAt.Value;
                var apiSeen = Interlocked.Read(ref _apiResponsesSeen);
                var deckCount = Snapshot.Decks.Count;

                if (!warnedNoTraffic && gameObservedAt is not null && elapsed > TimeSpan.FromSeconds(18) && apiSeen == 0)
                {
                    warnedNoTraffic = true;
                    AppLog.Warn("Arcane Rush is running after a fresh launch but no target API response reached the local scanner after 18 seconds.");
                    StatusChanged?.Invoke("Arcane Rush est lancé, mais aucun trafic du jeu n’atteint encore le scanner. Attends quelques secondes ; si ça persiste, ferme puis relance le jeu une fois.");
                }
                else if (!warnedTrafficNoDeck && gameObservedAt is not null && elapsed > TimeSpan.FromSeconds(30) && apiSeen > 0 && deckCount == 0)
                {
                    warnedTrafficNoDeck = true;
                    string paths;
                    lock (_gate) paths = string.Join(",", _seenApiPaths.OrderBy(x => x).Take(24));
                    AppLog.Warn($"Fresh game API traffic seen but no deck accepted after 30 seconds. paths={paths}");
                    StatusChanged?.Invoke("Le nouveau lancement est bien capturé, mais aucun deck sûr n’a encore été validé. Laisse le menu finir de charger quelques secondes.");
                }

                if (gameObservedAt is not null && elapsed > TimeSpan.FromMinutes(2))
                {
                    var snap = Snapshot;
                    string paths;
                    lock (_gate) paths = string.Join(",", _seenApiPaths.OrderBy(x => x).Take(40));
                    var missing = string.Join(",", AppConfig.ExpectedMerchantIds.Where(id => !snap.Decks.ContainsKey(id)));
                    AppLog.Warn($"Scanner timeout. apiResponses={apiSeen}, decks={snap.Decks.Count}, missing={missing}, numericRefs={_detector.NumericReferenceCount}, name={(string.IsNullOrWhiteSpace(snap.PlayerName) ? "no" : $"{snap.PlayerName}/{snap.PlayerNameConfidence}")}, collection={snap.OwnedCardIds.Count}/{snap.CollectionConfidence}/safe={snap.CollectionSyncSafe}, paths={paths}");
                    StatusChanged?.Invoke(
                        apiSeen == 0
                            ? "Scan arrêté : aucun trafic Arcane Rush n’a atteint le scanner après le nouveau lancement."
                            : snap.Decks.Count >= AppConfig.ExpectedDeckCount && snap.PlayerNameConfidence < 75
                                ? "Scan arrêté après 2 minutes · 13/13 decks détectés, mais le pseudo Arcane Rush n’est pas encore vérifié."
                                : snap.Decks.Count >= AppConfig.ExpectedDeckCount && !snap.CollectionSyncSafe
                                    ? "Scan arrêté après 2 minutes · 13/13 decks détectés, mais la collection exacte du démarrage n’a pas été reçue."
                                    : $"Scan arrêté après 2 minutes · {snap.Decks.Count}/{AppConfig.ExpectedDeckCount} decks sûrs détectés.");
                    await StopAsync();
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Scanner watch loop warning: " + ex.Message);
            }

            try { await Task.Delay(1000, token); } catch { return; }
        }
    }

    private void ResetDetectionState(string reason)
    {
        GameSnapshot snapshot;
        lock (_gate)
        {
            _detector.ResetTransientScanState();
            _snapshot.Decks.Clear();
            _snapshot.OwnedCardIds = Array.Empty<string>();
            _snapshot.CollectionSyncSafe = false;
            _snapshot.CollectionConfidence = 0;
            _snapshot.CollectionMethod = "";
            _snapshot.CollectionAsciiCount = 0;
            _snapshot.CollectionNoiseCount = 0;
            _snapshot.CollectionNumericOnlyCount = 0;
            _snapshot.CollectionUnresolvedRecords = 0;
            _snapshot.CollectionCandidateRecords = 0;
            _snapshot.CollectionNumericOnlyIds = Array.Empty<string>();
            _snapshot.CollectionInferredIds = Array.Empty<string>();
            _snapshot.CollectionNoiseIds = Array.Empty<string>();
            _snapshot.CollectionRejectedNumericIds = Array.Empty<string>();
            _snapshot.CollectionDeckRecoveredIds = Array.Empty<string>();

            var local = PlayerLogReader.TryReadWithSource();
            _snapshot.PlayerName = local.Name;
            _snapshot.PlayerNameConfidence = string.IsNullOrWhiteSpace(local.Name) ? 0 : 55;
            _snapshot.PlayerNameSource = string.IsNullOrWhiteSpace(local.Name) ? "" : local.Source;
            _snapshot.UpdatedAt = DateTimeOffset.UtcNow;

            _seenApiPaths.Clear();
            _trafficDiagnostics.Clear();
            _apiRequestsSeen = 0;
            _apiResponsesSeen = 0;
            _candidateBodiesScanned = 0;

            snapshot = _snapshot.Clone();
        }

        AppLog.Info($"Detection state reset: {reason}.");
        SnapshotChanged?.Invoke(snapshot);
    }

    private bool ReconcileCollectionWithDeckProofLocked()
    {
        if (!_snapshot.CollectionSyncSafe
            || !_snapshot.CollectionMethod.StartsWith("bootstrap-exact-ascii", StringComparison.Ordinal)
            || _snapshot.OwnedCardIds.Count == 0
            || _snapshot.Decks.Count == 0)
            return false;

        var owned = _snapshot.OwnedCardIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recovered = _snapshot.CollectionDeckRecoveredIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedNow = new List<string>();

        foreach (var deck in _snapshot.Decks.Values)
        {
            foreach (var rawId in deck.CardIds)
            {
                var cardId = (rawId ?? "").Trim().ToUpperInvariant();
                if (cardId.Length == 0 || _detector.IsStarterCard(cardId))
                    continue;

                // A non-starter card present in the player's equipped deck is direct,
                // independent proof of ownership. It is the only non-/001003 source that
                // is allowed to supplement ownedIds.
                if (owned.Add(cardId))
                {
                    recovered.Add(cardId);
                    addedNow.Add(cardId);
                }
            }
        }

        if (addedNow.Count == 0)
            return false;

        _snapshot.OwnedCardIds = owned
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _snapshot.CollectionDeckRecoveredIds = recovered
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _snapshot.CollectionRejectedNumericIds = _snapshot.CollectionRejectedNumericIds
            .Where(id => !recovered.Contains(id))
            .ToArray();
        _snapshot.CollectionMethod = "bootstrap-exact-ascii+deck-proof";
        _snapshot.CollectionConfidence = Math.Max(_snapshot.CollectionConfidence, 99);

        AppLog.Info(
            $"Collection supplemented by equipped-deck proof: +{addedNow.Count} card(s): " +
            string.Join(",", addedNow.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)));
        return true;
    }

    private void AddTrafficDiagnostic(TrafficObservation observation)
    {
        lock (_gate)
        {
            _trafficDiagnostics.Add(observation);
            if (_trafficDiagnostics.Count > 400)
                _trafficDiagnostics.RemoveRange(0, _trafficDiagnostics.Count - 400);
        }
    }

    private static string StatusForSnapshot(GameSnapshot snap)
    {
        if (snap.IsComplete(AppConfig.ExpectedDeckCount))
            return $"Scan terminé ✓ · {snap.PlayerName} + 13 decks + {snap.OwnedCardIds.Count} cartes détectées.";
        if (snap.Decks.Count >= AppConfig.ExpectedDeckCount && snap.CollectionSyncSafe && snap.PlayerNameConfidence < 75)
            return $"13/13 decks + {snap.OwnedCardIds.Count} cartes ✓ · recherche du pseudo Arcane Rush en cours…";
        if (snap.Decks.Count >= AppConfig.ExpectedDeckCount && !snap.CollectionSyncSafe)
            return "13/13 decks détectés ✓ · lecture sécurisée de la collection en cours…";
        if (snap.PlayerNameConfidence >= 75)
            return $"Pseudo {snap.PlayerName} détecté ✓ · {snap.Decks.Count}/{AppConfig.ExpectedDeckCount} decks · {snap.OwnedCardIds.Count} cartes.";
        return $"Scan en cours · {snap.Decks.Count}/{AppConfig.ExpectedDeckCount} decks · {snap.OwnedCardIds.Count} cartes · pseudo en recherche.";
    }

    private bool ApplyPlayerNameProbeLocked(PlayerNameProbe probe, string source)
    {
        if (string.IsNullOrWhiteSpace(probe.AcceptedName) || probe.Confidence <= 0) return false;
        var same = string.Equals(_snapshot.PlayerName, probe.AcceptedName, StringComparison.OrdinalIgnoreCase);
        if (!same && probe.Confidence <= _snapshot.PlayerNameConfidence) return false;
        if (same && probe.Confidence <= _snapshot.PlayerNameConfidence) return false;

        var previous = _snapshot.PlayerName;
        var previousConfidence = _snapshot.PlayerNameConfidence;
        _snapshot.PlayerName = probe.AcceptedName;
        _snapshot.PlayerNameConfidence = probe.Confidence;
        _snapshot.PlayerNameSource = string.IsNullOrWhiteSpace(probe.Evidence) ? source : probe.Evidence;
        AppLog.Info($"Player identity updated: '{previous}'({previousConfidence}) -> '{probe.AcceptedName}'({probe.Confidence}) via {_snapshot.PlayerNameSource}.");
        return true;
    }

    private static int DeckRank(DetectedDeck deck)
    {
        var endpointBonus = AppConfig.ValidatedDeckEndpoints.Contains(deck.SourcePath) ? 1000
            : AppConfig.ObservedDeckEndpoints.Contains(deck.SourcePath) ? 100
            : 0;
        return endpointBonus + deck.Confidence * 10 - Math.Abs(AppConfig.ExpectedCardsPerDeck - deck.CardIds.Count);
    }

    public static void RemoveTrustedCertificate()
    {
        var removed = 0;
        foreach (var storeName in new[] { StoreName.Root, StoreName.My })
        {
            try
            {
                using var store = new X509Store(storeName, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                var matches = store.Certificates
                    .Cast<X509Certificate2>()
                    .Where(cert =>
                        string.Equals(
                            cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                            "Arcane Rush Sync Local CA",
                            StringComparison.OrdinalIgnoreCase)
                        && cert.Issuer.Contains("Arcane Rush Sync", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var cert in matches)
                {
                    try { store.Remove(cert); removed++; } catch { }
                    cert.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Temporary scanner certificate cleanup failed in {storeName}: {ex.Message}");
            }
        }

        try
        {
            if (File.Exists(AppPaths.ProxyCertificate))
                File.Delete(AppPaths.ProxyCertificate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Temporary scanner PFX cleanup failed: " + ex.Message);
        }

        if (removed > 0)
            AppLog.Info($"Temporary scanner certificate removed ({removed} store entrie(s)).");
    }

    private static bool IsCurrentUserRootTrusted(X509Certificate2? certificate)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(certificate.Thumbprint)) return false;
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(
            X509FindType.FindByThumbprint,
            certificate.Thumbprint,
            validOnly: false).Count > 0;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
