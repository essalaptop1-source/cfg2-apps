using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Configuration2App.Controls;
using Configuration2App.Models;
using Configuration2App.Services;
using Path = System.Windows.Shapes.Path;

namespace Configuration2App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<EmbedDraft> _embeds = new();
    private readonly List<Button> _swatches = new();
    private readonly Dictionary<DependencyObject, double> _fontOriginals = new();

    private int _selected;
    private bool _loading;
    private bool _initialized;
    private bool _restoring;
    private string _fontFamily = "Segoe UI";
    private double _fontScale = 1.0;
    private UpdateInfo? _pendingUpdate;

    // Resolve brushes on demand so code-set colors follow theme changes.
    private static Brush Accent => (Brush)Application.Current.FindResource("AccentBrush");
    private static Brush AccentGradient => (Brush)Application.Current.FindResource("AccentGradientBrush");
    private static Brush OnAccent => (Brush)Application.Current.FindResource("OnAccentBrush");
    private static Brush Text => (Brush)Application.Current.FindResource("TextBrush");
    private static Brush TextSecondary => (Brush)Application.Current.FindResource("TextSecondaryBrush");
    private static Brush TextTertiary => (Brush)Application.Current.FindResource("TextTertiaryBrush");
    private static Brush SurfaceAlt => (Brush)Application.Current.FindResource("SurfaceAltBrush");

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        _initialized = true;
        FontCombo.ItemsSource = ThemeService.FontChoices;
        ApplyThemeSettings();
        LoadUpdateUi();

        // Window icon comes from the embedded app.ico (multi-size, crisp on the
        // taskbar); the in-app title logo tile uses the full-quality PNG.
        var logo = App.TryLoadLogo();
        if (logo != null)
            TitleLogo.Source = logo;

        foreach (var child in SwatchRow.Children)
            if (child is Button { Tag: string tag } button && tag.Length == 6)
                _swatches.Add(button);

        RestoreState();
        RebuildTabs();
        LoadSelectedIntoForm();
        RebuildPreview();
        SetStatus("Ready");
        ToastService.ToastRequested += OnToastRequested;
        Closed += (_, _) => SaveSettings();

        // Modern entrance: fade the window in once it's shown.
        Opacity = 0;
        Loaded += (_, _) =>
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            BeginAnimation(UIElement.OpacityProperty, fade);
            _ = CheckForUpdatesSilently();
        };
    }

    // ================================================================ State

    private void RestoreState()
    {
        WebhookUrlBox.Text = _settings.WebhookUrl;
        UsernameBox.Text = _settings.Username;
        AvatarBox.Text = _settings.AvatarUrl;
        ContentBox.Text = _settings.EmbedContent;
        TtsCheck.IsChecked = _settings.Tts;

        // Only restore embeds that actually have content — empty drafts from
        // previous sessions (or taps on "add") should not pile up on launch.
        foreach (var embed in _settings.Embeds.Where(IsMeaningful))
        {
            var copy = CloneEmbed(embed);
            HookFields(copy);
            _embeds.Add(copy);
        }
        if (_embeds.Count == 0)
        {
            var fresh = new EmbedDraft();
            HookFields(fresh);
            _embeds.Add(fresh);
        }
        _selected = 0;
    }

    private static bool IsMeaningful(EmbedDraft e) =>
        !string.IsNullOrWhiteSpace(e.Title) ||
        !string.IsNullOrWhiteSpace(e.Url) ||
        !string.IsNullOrWhiteSpace(e.Description) ||
        !string.IsNullOrWhiteSpace(e.AuthorName) ||
        !string.IsNullOrWhiteSpace(e.AuthorUrl) ||
        !string.IsNullOrWhiteSpace(e.AuthorIcon) ||
        !string.IsNullOrWhiteSpace(e.ThumbnailUrl) ||
        !string.IsNullOrWhiteSpace(e.ImageUrl) ||
        !string.IsNullOrWhiteSpace(e.FooterText) ||
        !string.IsNullOrWhiteSpace(e.FooterIcon) ||
        (e.ColorHex != null && !e.ColorHex.Equals("5865F2", StringComparison.OrdinalIgnoreCase)) ||
        e.Fields.Count > 0;

    private void SaveSettings()
    {
        _settings.WebhookUrl = WebhookUrlBox.Text.Trim();
        _settings.Username = UsernameBox.Text.Trim();
        _settings.AvatarUrl = AvatarBox.Text.Trim();
        _settings.EmbedContent = ContentBox.Text;
        _settings.Tts = TtsCheck.IsChecked == true;
        _settings.Embeds = _embeds.Select(CloneEmbed).ToList();
        _settings.GitHubRepo = GitHubRepoBox.Text.Trim();
        _settings.UpdateUrl = UpdateUrlBox.Text.Trim();
        _settings.CheckUpdatesOnStartup = UpdateOnStartupCheck.IsChecked == true;
        SettingsService.Save(_settings);
    }

    private MessageDraft BuildMessage() => new()
    {
        Content = ContentBox.Text,
        Username = UsernameBox.Text,
        AvatarUrl = AvatarBox.Text,
        Tts = TtsCheck.IsChecked == true,
        Embeds = _embeds.Select(CloneEmbed).ToList(),
    };

    private static EmbedDraft CloneEmbed(EmbedDraft src) => new()
    {
        Title = src.Title,
        Url = src.Url,
        Description = src.Description,
        ColorHex = src.ColorHex,
        AuthorName = src.AuthorName,
        AuthorUrl = src.AuthorUrl,
        AuthorIcon = src.AuthorIcon,
        ThumbnailUrl = src.ThumbnailUrl,
        ImageUrl = src.ImageUrl,
        FooterText = src.FooterText,
        FooterIcon = src.FooterIcon,
        IncludeTimestamp = src.IncludeTimestamp,
        Fields = src.Fields
            .Select(f => new EmbedField { Name = f.Name, Value = f.Value, IsInline = f.IsInline })
            .ToList(),
    };

    private void HookFields(EmbedDraft embed)
    {
        foreach (var field in embed.Fields)
            field.PropertyChanged += (_, _) => RebuildPreview();
    }

    // ================================================================ Embed tabs

    private void RebuildTabs()
    {
        EmbedTabs.Children.Clear();
        for (var i = 0; i < _embeds.Count; i++)
        {
            var active = i == _selected;
            var tab = new Button
            {
                Content = $"Embed {i + 1}",
                Style = (Style)FindResource("EmbedTabButton"),
                Tag = i,
                Margin = new Thickness(0, 0, 6, 0),
                Background = active ? AccentGradient : SurfaceAlt,
                Foreground = active ? OnAccent : TextSecondary,
                BorderBrush = active ? (Brush)FindResource("AccentBorderBrush") : (Brush)FindResource("BorderBrush"),
            };
            tab.Click += EmbedTab_Click;
            EmbedTabs.Children.Add(tab);
        }

        var add = new Button
        {
            Content = "＋  Add embed",
            ToolTip = "Add embed (max 10)",
            Style = (Style)FindResource("GhostButton"),
            Width = double.NaN,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("AccentTextBrush"),
        };
        add.Click += AddEmbed_Click;
        EmbedTabs.Children.Add(add);

        var remove = new Button
        {
            Content = "✕",
            ToolTip = "Remove this embed",
            Style = (Style)FindResource("GhostButton"),
            Width = 28,
            Height = 28,
            FontSize = 12,
            Foreground = TextSecondary,
        };
        remove.Click += RemoveEmbed_Click;
        EmbedTabs.Children.Add(remove);
    }

    private void EmbedTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int i } && i != _selected && i >= 0 && i < _embeds.Count)
        {
            _selected = i;
            RebuildTabs();
            LoadSelectedIntoForm();
            RebuildPreview();
        }
    }

    private void AddEmbed_Click(object sender, RoutedEventArgs e)
    {
        if (_embeds.Count >= 10)
        {
            ToastService.Error("Discord allows up to 10 embeds per message");
            return;
        }
        var embed = new EmbedDraft();
        HookFields(embed);
        _embeds.Add(embed);
        _selected = _embeds.Count - 1;
        RebuildTabs();
        LoadSelectedIntoForm();
        RebuildPreview();
    }

    private void RemoveEmbed_Click(object sender, RoutedEventArgs e)
    {
        if (_embeds.Count <= 1)
        {
            _embeds[0] = new EmbedDraft();
            HookFields(_embeds[0]);
            LoadSelectedIntoForm();
            RebuildPreview();
            return;
        }
        _embeds.RemoveAt(_selected);
        if (_selected >= _embeds.Count) _selected = _embeds.Count - 1;
        RebuildTabs();
        LoadSelectedIntoForm();
        RebuildPreview();
    }

    // ================================================================ Form <-> selected embed

    private void LoadSelectedIntoForm()
    {
        if (_embeds.Count == 0) return;
        var embed = _embeds[_selected];
        _loading = true;
        EmbedTitleBox.Text = embed.Title;
        EmbedUrlBox.Text = embed.Url;
        EmbedDescBox.Text = embed.Description;
        ColorHexBox.Text = string.IsNullOrWhiteSpace(embed.ColorHex) ? "5865F2" : embed.ColorHex;
        AuthorNameBox.Text = embed.AuthorName;
        AuthorUrlBox.Text = embed.AuthorUrl;
        AuthorIconBox.Text = embed.AuthorIcon;
        ThumbUrlBox.Text = embed.ThumbnailUrl;
        ImageUrlBox.Text = embed.ImageUrl;
        FooterTextBox.Text = embed.FooterText;
        FooterIconBox.Text = embed.FooterIcon;
        TimestampCheck.IsChecked = embed.IncludeTimestamp;
        _loading = false;
        RebindFields();
    }

    private void RebindFields()
    {
        if (_embeds.Count == 0) return;
        FieldsList.ItemsSource = null;
        FieldsList.ItemsSource = _embeds[_selected].Fields;
    }

    private void UpdateSelectedFromForm()
    {
        if (_embeds.Count == 0) return;
        var embed = _embeds[_selected];
        embed.Title = EmbedTitleBox.Text;
        embed.Url = EmbedUrlBox.Text;
        embed.Description = EmbedDescBox.Text;
        embed.ColorHex = ColorHexBox.Text.Trim().TrimStart('#');
        embed.AuthorName = AuthorNameBox.Text;
        embed.AuthorUrl = AuthorUrlBox.Text;
        embed.AuthorIcon = AuthorIconBox.Text;
        embed.ThumbnailUrl = ThumbUrlBox.Text;
        embed.ImageUrl = ImageUrlBox.Text;
        embed.FooterText = FooterTextBox.Text;
        embed.FooterIcon = FooterIconBox.Text;
        embed.IncludeTimestamp = TimestampCheck.IsChecked == true;
    }

    // ================================================================ Composer events

    private void Composer_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _loading) return;
        UpdateSelectedFromForm();
        RebuildPreview();
    }

    private void ColorHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _loading) return;
        SetSwatchSelection(ColorHexBox.Text.Trim().TrimStart('#'));
        UpdateSelectedFromForm();
        RebuildPreview();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && hex.Length == 6)
        {
            ColorHexBox.Text = hex;
            SetSwatchSelection(hex);
            UpdateSelectedFromForm();
            RebuildPreview();
        }
    }

    private void SetSwatchSelection(string hex)
    {
        foreach (var swatch in _swatches)
        {
            var check = swatch.Template.FindName("Check", swatch) as FrameworkElement;
            if (check != null)
                check.Visibility = string.Equals((string?)swatch.Tag, hex, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void TimestampCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loading) return;
        UpdateSelectedFromForm();
        RebuildPreview();
    }

    private void WebhookUrl_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        var hasUrl = !string.IsNullOrWhiteSpace(WebhookUrlBox.Text);
        TestWebhookButton.IsEnabled = hasUrl;
        WebhookDot.Fill = hasUrl
            ? (Brush)FindResource("OnlineBrush")
            : (Brush)FindResource("TextDisabledBrush");
        WebhookStatusText.Text = hasUrl ? "Webhook ready to send" : "No webhook set — paste a URL above";
        WebhookStatusText.Foreground = hasUrl
            ? (Brush)FindResource("OnlineBrush")
            : (Brush)FindResource("TextTertiaryBrush");
    }

    private void AddField_Click(object sender, RoutedEventArgs e)
    {
        if (_embeds.Count == 0) return;
        var field = new EmbedField();
        field.PropertyChanged += (_, _) => RebuildPreview();
        _embeds[_selected].Fields.Add(field);
        RebindFields();
        RebuildPreview();
    }

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EmbedField field })
        {
            _embeds[_selected].Fields.Remove(field);
            RebindFields();
            RebuildPreview();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ContentBox.Clear();
        UsernameBox.Clear();
        AvatarBox.Clear();
        TtsCheck.IsChecked = false;

        _embeds.Clear();
        var fresh = new EmbedDraft();
        HookFields(fresh);
        _embeds.Add(fresh);
        _selected = 0;

        RebuildTabs();
        LoadSelectedIntoForm();
        RebuildPreview();
        SetStatus("Draft cleared");
        ToastService.Info("Draft cleared");
    }

    // ================================================================ Preview

    private void RebuildPreview()
    {
        PruneFontOriginals(PreviewStack);
        PreviewStack.Children.Clear();
        var draft = BuildMessage();

        foreach (var embed in draft.Embeds)
        {
            var preview = new EmbedPreview { Margin = new Thickness(0, 0, 0, 12) };
            preview.Show(embed);
            PreviewStack.Children.Add(preview);
        }
        ApplyFontToTree(PreviewStack);

        var isEmpty = string.IsNullOrWhiteSpace(draft.Content) &&
                      draft.Embeds.All(e =>
                          string.IsNullOrWhiteSpace(e.Title) &&
                          string.IsNullOrWhiteSpace(e.Description) &&
                          !e.Fields.Any(f =>
                              !string.IsNullOrWhiteSpace(f.Name) || !string.IsNullOrWhiteSpace(f.Value)));
        PreviewEmpty.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        PreviewTime.Text = DateTime.Now.ToString("Today at h:mm tt");

        var total = draft.Content.Length + draft.Embeds.Sum(e =>
            e.Title.Length + e.Description.Length + e.FooterText.Length + e.AuthorName.Length +
            e.Fields.Sum(f => f.Name.Length + f.Value.Length));
        CharCountText.Text = $"{total:N0} / 6,000 chars";
        CharCountText.Foreground = total > 5000
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextTertiaryBrush");
    }

    // ================================================================ Sending

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var url = WebhookUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            SetStatus("No webhook URL", error: true);
            ToastService.Error("Paste your Discord webhook URL at the top first", "Missing webhook");
            WebhookUrlBox.Focus();
            return;
        }

        var draft = BuildMessage();
        var hasEmbed = draft.Embeds.Any(em =>
            !string.IsNullOrWhiteSpace(em.Title) ||
            !string.IsNullOrWhiteSpace(em.Description) ||
            em.Fields.Any(f => !string.IsNullOrWhiteSpace(f.Name) || !string.IsNullOrWhiteSpace(f.Value)));
        if (string.IsNullOrWhiteSpace(draft.Content) && !hasEmbed)
        {
            ToastService.Error("Add some content before sending", "Empty message");
            return;
        }

        SendButton.IsEnabled = false;
        SetStatus("Sending…", busy: true);
        try
        {
            var (ok, message) = await WebhookService.SendMessageAsync(url, draft);
            if (ok)
            {
                SaveSettings();
                SetStatus($"Sent to {WebhookHost(url)}");
                ToastService.Success("Message sent — check the channel");
            }
            else
            {
                SetStatus("Send failed", error: true);
                ToastService.Error(message, "Discord rejected the request");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Send failed", error: true);
            ToastService.Error(ex.Message, "Could not reach Discord");
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private async void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        var url = WebhookUrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return;

        var test = new MessageDraft
        {
            Embeds =
            {
                new EmbedDraft
                {
                    Title = "✅ Test embed",
                    Description = "If you can see this, your webhook works!",
                    ColorHex = "57F287",
                },
            },
        };

        TestWebhookButton.IsEnabled = false;
        SetStatus("Testing webhook…", busy: true);
        try
        {
            var (ok, message) = await WebhookService.SendMessageAsync(url, test);
            if (ok)
            {
                SetStatus("Webhook works");
                ToastService.Success("Test embed sent — check the channel");
            }
            else
            {
                SetStatus("Test failed", error: true);
                ToastService.Error(message, "Webhook test failed");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Test failed", error: true);
            ToastService.Error(ex.Message, "Webhook test failed");
        }
        finally
        {
            TestWebhookButton.IsEnabled = !string.IsNullOrWhiteSpace(WebhookUrlBox.Text);
        }
    }

    private static string WebhookHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }

    // ================================================================ Window chrome

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc closes the settings panel.
        if (e.Key == Key.Escape && SettingsOverlay.Visibility == Visibility.Visible)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        // Ctrl+Enter sends the current draft, even while a text box has focus.
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Send_Click(sender, e);
            e.Handled = true;
        }
    }

    // ================================================================ Settings

    private void ApplyThemeSettings()
    {
        _restoring = true;
        try
        {
            ThemeService.ApplyAccent(_settings.Theme);
            _fontFamily = string.IsNullOrWhiteSpace(_settings.FontName) ? "Segoe UI" : _settings.FontName;
            _fontScale = ThemeService.ScaleForPreset(_settings.FontSizePreset);
            ApplyFontToTree(this);
            UpdateSettingsUiState();
        }
        finally
        {
            _restoring = false;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSettingsUiState();
        if (_pendingUpdate != null)
        {
            CheckUpdatesButton.Content = "Download & install";
            AutomationProperties.SetName(CheckUpdatesButton, "Download & install");
            CheckUpdatesButton.IsEnabled = true;
            UpdateStatusText.Text = $"Version {_pendingUpdate.Version} is available.";
            UpdateStatusText.Foreground = (Brush)FindResource("AccentTextBrush");
            SkipUpdateButton.Visibility = Visibility.Visible;
        }
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void ThemeSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key } && ThemeService.ApplyAccent(key))
        {
            _settings.Theme = key;
            RebuildTabs();
            SetStatus(StatusText.Text);
            UpdateSettingsUiState();
            SaveSettings();
            ToastService.Info($"Theme: {key}");
        }
    }

    private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _restoring || FontCombo.SelectedItem is not string f) return;
        _settings.FontName = f;
        _fontFamily = f;
        ApplyFontToTree(this);
        SaveSettings();
    }

    private void SizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string p })
        {
            _settings.FontSizePreset = p;
            _fontScale = ThemeService.ScaleForPreset(p);
            ApplyFontToTree(this);
            UpdateSettingsUiState();
            SaveSettings();
        }
    }

    private void UpdateSettingsUiState()
    {
        foreach (var child in ThemeRow.Children)
        {
            if (child is Button { Tag: string key } b &&
                b.Template.FindName("Check", b) is System.Windows.Shapes.Path check)
            {
                check.Visibility = key == _settings.Theme ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        if (!Equals(FontCombo.SelectedItem, _settings.FontName))
            FontCombo.SelectedItem = _settings.FontName;

        SetSizeActive(SizeSButton, _settings.FontSizePreset == "S");
        SetSizeActive(SizeMButton, _settings.FontSizePreset == "M");
        SetSizeActive(SizeLButton, _settings.FontSizePreset == "L");
    }

    private void SetSizeActive(Button b, bool active)
    {
        b.Background = active ? Accent : Brushes.Transparent;
        b.Foreground = active ? OnAccent : TextSecondary;
    }

    // ================================================================ Updates

    private void LoadUpdateUi()
    {
        VersionText.Text = UpdateService.LocalVersion.ToString(3);
        GitHubRepoBox.Text = _settings.GitHubRepo;
        UpdateUrlBox.Text = _settings.UpdateUrl;
        UpdateOnStartupCheck.IsChecked = _settings.CheckUpdatesOnStartup;
    }

    private async Task CheckForUpdatesSilently()
    {
        if (!_settings.CheckUpdatesOnStartup) return;
        if (string.IsNullOrWhiteSpace(_settings.GitHubRepo) && string.IsNullOrWhiteSpace(_settings.UpdateUrl))
            return;
        var info = await UpdateService.CheckAsync(_settings);
        if (info == null || info.Version == _settings.SkippedVersion) return;
        _pendingUpdate = info;
        SetStatus($"Update {info.Version} available — open Settings to install");
        ToastService.Info(
            $"Version {info.Version} is available — open Settings (gear) and press Download & install",
            "Update available");
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate != null)
        {
            await DownloadAndInstallAsync(_pendingUpdate);
            return;
        }

        _settings.GitHubRepo = GitHubRepoBox.Text.Trim();
        _settings.UpdateUrl = UpdateUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(_settings.GitHubRepo) && string.IsNullOrWhiteSpace(_settings.UpdateUrl))
        {
            UpdateStatusText.Text = "Set a GitHub repo or update URL first.";
            UpdateStatusText.Foreground = (Brush)FindResource("TextTertiaryBrush");
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";
        UpdateStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        var info = await UpdateService.CheckAsync(_settings);
        if (info == null)
        {
            CheckUpdatesButton.IsEnabled = true;
            UpdateStatusText.Text = "You're on the latest version.";
            UpdateStatusText.Foreground = (Brush)FindResource("OnlineBrush");
            return;
        }
        _pendingUpdate = info;
        UpdateStatusText.Text = $"Version {info.Version} is available.";
        UpdateStatusText.Foreground = (Brush)FindResource("AccentTextBrush");
        CheckUpdatesButton.Content = "Download & install";
        AutomationProperties.SetName(CheckUpdatesButton, "Download & install");
        CheckUpdatesButton.IsEnabled = true;
        SkipUpdateButton.Visibility = Visibility.Visible;
    }

    private async Task DownloadAndInstallAsync(UpdateInfo info)
    {
        CheckUpdatesButton.IsEnabled = false;
        SkipUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = $"Downloading {info.Version}…";
        UpdateStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        var ok = await UpdateService.InstallAsync(info);
        if (ok)
        {
            // The batch script swaps the exe and relaunches; close so it can.
            SaveSettings();
            Close();
        }
        else
        {
            CheckUpdatesButton.IsEnabled = true;
            SkipUpdateButton.IsEnabled = true;
            UpdateStatusText.Text = "Download failed — check the update URL and try again.";
            UpdateStatusText.Foreground = (Brush)FindResource("DangerBrush");
            ToastService.Error("Could not download the update", "Update failed");
        }
    }

    private void SkipUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate != null) _settings.SkippedVersion = _pendingUpdate.Version;
        _pendingUpdate = null;
        SkipUpdateButton.Visibility = Visibility.Collapsed;
        CheckUpdatesButton.Content = "Check for updates";
        AutomationProperties.SetName(CheckUpdatesButton, "Check for updates");
        UpdateStatusText.Text = "Skipped — the next version will remind you again.";
        UpdateStatusText.Foreground = (Brush)FindResource("TextTertiaryBrush");
        SaveSettings();
    }

    private void UpdateOnStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.CheckUpdatesOnStartup = UpdateOnStartupCheck.IsChecked == true;
        SaveSettings();
    }

    private void ApplyFontToTree(DependencyObject root) =>
        ThemeService.ApplyFontToTree(root, _fontFamily, _fontScale, _fontOriginals);

    private void PruneFontOriginals(DependencyObject node)
    {
        _fontOriginals.Remove(node);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            PruneFontOriginals(VisualTreeHelper.GetChild(node, i));
    }

    // ================================================================ Status

    private void SetStatus(string text, bool busy = false, bool error = false)
    {
        StatusText.Text = text;
        StatusDot.Fill = error ? TextTertiary : (busy ? TextTertiary : Accent);
        StatusDot.Width = busy ? 8 : 6;
        StatusDot.Height = busy ? 8 : 6;
    }

    // ================================================================ Toasts

    private void OnToastRequested(ToastType type, string message, string? title)
    {
        Dispatcher.Invoke(() => ShowToast(type, message, title));
    }

    private void ShowToast(ToastType type, string message, string? title)
    {
        var (icon, stroke) = type switch
        {
            ToastType.Success => ((Geometry)FindResource("IconCircleCheck2"), Accent),
            ToastType.Error => ((Geometry)FindResource("IconAlert"), Text),
            _ => ((Geometry)FindResource("IconInfo"), TextSecondary),
        };

        var toast = new Border
        {
            Style = (Style)FindResource("ToastBorder"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBorder = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = SurfaceAlt,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        iconBorder.Child = new Path
        {
            Style = (Style)FindResource("StrokeIcon"),
            Data = icon,
            Width = 14,
            Height = 14,
            Stroke = stroke,
            StrokeThickness = 2,
        };
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        var textPanel = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        if (!string.IsNullOrWhiteSpace(title))
        {
            textPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Text,
            });
        }
        textPanel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 11.5,
            Foreground = TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, title != null ? 2 : 0, 0, 0),
        });
        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        var duration = title != null ? 3800 : 3000;

        var progressBar = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(0, 0, 12, 12),
            Background = Accent,
            Opacity = 0.35,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransformOrigin = new Point(0, 0.5),
        };
        var progressScale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        progressBar.RenderTransform = progressScale;

        var toastGrid = new Grid();
        toastGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toastGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(grid, 0);
        Grid.SetRow(progressBar, 1);
        toastGrid.Children.Add(grid);
        toastGrid.Children.Add(progressBar);
        toast.Child = toastGrid;
        ToastHost.Children.Insert(0, toast);

        var transform = new TranslateTransform { X = 340 };
        toast.RenderTransform = transform;
        toast.Opacity = 0;

        var inAnim = new DoubleAnimation(340, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260));
        transform.BeginAnimation(TranslateTransform.XProperty, inAnim);
        toast.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(duration) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            var slideOut = new DoubleAnimation(0, 340, TimeSpan.FromMilliseconds(220));
            fadeOut.Completed += (_, _) => ToastHost.Children.Remove(toast);
            toast.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            transform.BeginAnimation(TranslateTransform.XProperty, slideOut);
        };
        timer.Start();
    }
}
