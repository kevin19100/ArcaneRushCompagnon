using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ArcaneRushSync.Models;
using ArcaneRushSync.Services;

namespace ArcaneRushSync;

public partial class GameOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    private readonly CardCatalogService _catalog;
    private readonly DispatcherTimer _trackingTimer;
    private LiveGameState? _state;
    private int _selectedTier;
    private string _runSignature = "";
    private bool _collapsed;
    private bool _allowClose;

    public GameOverlayWindow()
    {
        InitializeComponent();

        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        _catalog = new CardCatalogService(dataDir);

        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        _trackingTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _trackingTimer.Tick += (_, _) => TrackGameWindow();
        _trackingTimer.Start();

        RenderState();
    }

    public void UpdateState(LiveGameState? state)
    {
        _state = state;
        RenderState();
        TrackGameWindow();
    }

    public void CloseForAppShutdown()
    {
        _allowClose = true;
        _trackingTimer.Stop();
        try { Close(); } catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ExpandedBody.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = _collapsed ? "+" : "−";
        CollapseButton.ToolTip = _collapsed ? "Agrandir l’affichage" : "Réduire l’affichage";
        TrackGameWindow();
    }

    private void TierButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || !int.TryParse(button.Tag?.ToString(), out var tier)
            || tier is < 1 or > 5)
            return;

        _selectedTier = tier;
        RenderState();
    }

    private void TrackGameWindow()
    {
        if (!GameWindowLocator.TryGetBounds(out var game)
            || !game.IsForeground)
        {
            if (IsVisible)
                Hide();
            return;
        }

        var width = _collapsed
            ? Math.Min(340, Math.Max(280, game.Width * 0.28))
            : Math.Min(470, Math.Max(410, game.Width * 0.31));
        var height = _collapsed
            ? 54
            : Math.Min(760, Math.Max(420, game.Height - 96));

        Width = width;
        Height = height;
        Left = game.Left + game.Width - width - 14;
        Top = game.Top + 66;

        if (!IsVisible)
            Show();

        Topmost = true;
    }

    private void RenderState()
    {
        FactionPanel.Children.Clear();
        ShopPanel.Children.Clear();
        BoardPanel.Children.Clear();
        HandPanel.Children.Clear();
        TierCardsPanel.Children.Clear();

        if (_state is null)
        {
            HeaderStatusText.Text = "En attente de la partie…";
            HeaderTierText.Text = "Taverne —";
            LiveStatusText.Text =
                "Le launcher est prêt. Les informations apparaîtront dès qu’une partie sera détectée.";
            AddEmptyText(FactionPanel, "—");
            AddEmptyText(ShopPanel, "Boutique en attente");
            AddEmptyText(BoardPanel, "—");
            AddEmptyText(HandPanel, "—");
            UpdateTierButtons();
            AddEmptyText(TierCardsPanel, "Cartes de la partie en attente");
            return;
        }

        var state = _state;
        HeaderTierText.Text = state.TavernTier is >= 1 and <= 5
            ? $"Taverne {state.TavernTier}"
            : "Taverne —";
        HeaderStatusText.Text = state.ActiveFactionDealers.Count >= 3
            ? $"{state.ActiveFactionDealers.Count} factions · {state.Shop.Count} carte(s) en boutique"
            : "Partie détectée";
        LiveStatusText.Text =
            "Affichage direct des données de la partie. Aucune prédiction ni calcul de future boutique.";

        foreach (var faction in _catalog.GetFactions(state.ActiveFactionDealers))
            FactionPanel.Children.Add(CreateFactionBadge(faction, neutral: false));

        var neutralFaction = _catalog.TryGetFaction(state.NeutralDealer);
        if (neutralFaction is not null)
            FactionPanel.Children.Add(CreateFactionBadge(neutralFaction, neutral: true));

        RenderCards(ShopPanel, state.Shop, 93, 124);
        RenderCards(BoardPanel, state.Board, 64, 90);
        RenderCards(HandPanel, state.Hand, 64, 90);

        if (state.Shop.Count == 0) AddEmptyText(ShopPanel, "Boutique en attente");
        if (state.Board.Count == 0) AddEmptyText(BoardPanel, "Vide");
        if (state.Hand.Count == 0) AddEmptyText(HandPanel, "Vide");

        var signature = string.Join("|", state.ActiveFactionDealers.Append(state.NeutralDealer));
        if (!string.Equals(signature, _runSignature, StringComparison.OrdinalIgnoreCase))
        {
            _runSignature = signature;
            _selectedTier = state.TavernTier is >= 1 and <= 5 ? state.TavernTier : 1;
        }
        else if (_selectedTier is < 1 or > 5)
        {
            _selectedTier = state.TavernTier is >= 1 and <= 5 ? state.TavernTier : 1;
        }

        UpdateTierButtons();
        RenderTierCards(state);
    }

    private UIElement CreateFactionBadge(FactionDisplayInfo faction, bool neutral)
    {
        var text = neutral
            ? $"{faction.Faction} · {faction.DealerName}"
            : $"{faction.Faction} · {faction.DealerName}";

        return new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(neutral
                ? Color.FromArgb(35, 255, 255, 255)
                : Color.FromArgb(34, 231, 198, 107)),
            BorderBrush = new SolidColorBrush(neutral
                ? Color.FromArgb(52, 255, 255, 255)
                : Color.FromArgb(70, 231, 198, 107)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 7, 7),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(neutral
                    ? Color.FromRgb(190, 190, 187)
                    : Color.FromRgb(240, 226, 184)),
                FontSize = 10,
                FontWeight = neutral ? FontWeights.Normal : FontWeights.SemiBold
            }
        };
    }

    private void UpdateTierButtons()
    {
        foreach (var button in TierButtonPanel.Children.OfType<Button>())
        {
            var tier = int.TryParse(button.Tag?.ToString(), out var parsed) ? parsed : 0;
            var selected = tier == _selectedTier;
            var current = _state?.TavernTier == tier;

            button.Background = new SolidColorBrush(selected
                ? Color.FromArgb(72, 231, 198, 107)
                : Color.FromArgb(26, 255, 255, 255));
            button.BorderBrush = new SolidColorBrush(current
                ? Color.FromRgb(231, 198, 107)
                : Color.FromArgb(56, 255, 255, 255));
            button.BorderThickness = new Thickness(current ? 2 : 1);
            button.Foreground = new SolidColorBrush(selected
                ? Color.FromRgb(255, 244, 210)
                : Color.FromRgb(235, 232, 225));
            button.ToolTip = current
                ? $"Taverne {tier} · niveau actuel"
                : $"Afficher les cartes de taverne {tier}";
        }
    }

    private void RenderTierCards(LiveGameState state)
    {
        var dealerIds = state.ActiveFactionDealers
            .Concat(new[] { state.NeutralDealer })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var rendered = 0;
        foreach (var dealerId in dealerIds)
        {
            if (!state.RunPools.TryGetValue(dealerId, out var pool))
                continue;

            var cards = _catalog.GetCards(pool)
                .Where(card => card.TavernTier == _selectedTier)
                .OrderBy(card => FamilyNumber(card.Id))
                .ThenBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (cards.Length == 0)
                continue;

            var faction = _catalog.TryGetFaction(dealerId);
            TierCardsPanel.Children.Add(CreateFactionCardGroup(
                faction?.Faction ?? dealerId,
                faction?.DealerName ?? dealerId,
                cards,
                dealerId.Equals(state.NeutralDealer, StringComparison.OrdinalIgnoreCase)));
            rendered += cards.Length;
        }

        if (rendered == 0)
            AddEmptyText(TierCardsPanel, $"Aucune carte détectée pour la taverne {_selectedTier}");
    }

    private UIElement CreateFactionCardGroup(
        string faction,
        string dealerName,
        IReadOnlyList<CardDisplayInfo> cards,
        bool neutral)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{faction} · {dealerName} · {cards.Count} carte(s)",
            Foreground = new SolidColorBrush(neutral
                ? Color.FromRgb(196, 196, 191)
                : Color.FromRgb(240, 226, 184)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 10,
            Margin = new Thickness(1, 0, 0, 7)
        });

        var panel = new WrapPanel();
        foreach (var card in cards)
            panel.Children.Add(CreateCardTile(card, 58, 82));
        stack.Children.Add(panel);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(120, 34, 36, 41)),
            BorderBrush = new SolidColorBrush(neutral
                ? Color.FromArgb(34, 255, 255, 255)
                : Color.FromArgb(45, 231, 198, 107)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9),
            Margin = new Thickness(0, 0, 0, 9),
            Child = stack
        };
    }

    private void RenderCards(Panel panel, IEnumerable<string> ids, double width, double height)
    {
        foreach (var id in ids)
        {
            var card = _catalog.TryGetCard(id);
            panel.Children.Add(card is null
                ? CreateUnknownCardTile(id, width, height)
                : CreateCardTile(card, width, height));
        }
    }

    private UIElement CreateCardTile(CardDisplayInfo card, double width, double height)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(18, height * 0.24)) });

        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            SnapsToDevicePixels = true
        };
        Grid.SetRow(image, 0);
        grid.Children.Add(image);

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(card.ImageUrl, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnDemand;
            bitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
            bitmap.EndInit();
            image.Source = bitmap;
        }
        catch
        {
            // The name below remains visible as a safe text fallback.
        }

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 15, 16, 19)),
            Padding = new Thickness(3, 2, 3, 2),
            Child = new TextBlock
            {
                Text = card.Name,
                Foreground = Brushes.WhiteSmoke,
                FontSize = width <= 55 ? 8 : 9,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetRow(label, 1);
        grid.Children.Add(label);

        var tooltip = new StackPanel { MaxWidth = 310 };
        tooltip.Children.Add(new TextBlock
        {
            Text = card.Name,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        tooltip.Children.Add(new TextBlock
        {
            Text = $"{card.Faction} · Taverne {card.TavernTier} · Coût {card.Cost}",
            Margin = new Thickness(0, 0, 0, 5)
        });
        tooltip.Children.Add(new TextBlock
        {
            Text = card.Effect,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(30, 32, 36)),
            BorderBrush = RarityBrush(card.Rarity),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = tooltip,
            Child = grid
        };
    }

    private static UIElement CreateUnknownCardTile(string id, double width, double height) =>
        new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(30, 32, 36)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = id,
                Foreground = Brushes.Gainsboro,
                FontSize = 8,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4)
            }
        };

    private static int FamilyNumber(string cardId)
    {
        var parts = (cardId ?? "").Split('_');
        return parts.Length >= 4 && int.TryParse(parts[2], out var family)
            ? family
            : int.MaxValue;
    }

    private static Brush RarityBrush(string rarity) =>
        (rarity ?? "").ToLowerInvariant() switch
        {
            "legendary" => new SolidColorBrush(Color.FromRgb(220, 172, 74)),
            "epic" => new SolidColorBrush(Color.FromRgb(161, 113, 210)),
            "rare" => new SolidColorBrush(Color.FromRgb(78, 151, 211)),
            _ => new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))
        };

    private static void AddEmptyText(Panel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(145, 145, 141)),
            FontSize = 10,
            Margin = new Thickness(1, 2, 0, 7)
        });
    }

    private void ApplyNoActivateStyle()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            _ = SetWindowLongPtr(
                handle,
                GwlExStyle,
                new nint(style | WsExNoActivate | WsExToolWindow));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Overlay no-activate style could not be applied: " + ex.Message);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
