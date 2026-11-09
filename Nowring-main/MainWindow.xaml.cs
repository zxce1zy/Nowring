using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Nowring;

public partial class MainWindow : Window
{
    readonly PresenceService _svc;
    readonly Settings _s;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    string? _coverKey, _lastTrackKey;
    bool _closing, _closeDone;
    string? _status;
    bool _init = true;
    readonly DispatcherTimer _customDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

    static readonly Brush Green = Frozen(0x3B, 0xA5, 0x5D);
    static readonly Brush Gray = Frozen(0x6B, 0x6B, 0x76);
    static readonly Brush Red = Frozen(0xED, 0x42, 0x45);

    static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    internal MainWindow(PresenceService svc, Settings s)
    {
        _svc = svc;
        _s = s;
        InitializeComponent();

        if (App.AppIcon != null)
        {
            try
            {
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    App.AppIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
        }

        SpotifyCb.IsChecked = s.Spotify;
        YandexCb.IsChecked = s.Yandex;
        SoundCloudCb.IsChecked = s.SoundCloud;
        YouTubeCb.IsChecked = s.YouTube;
        TelegramCb.IsChecked = s.Telegram;
        BrowserCb.IsChecked = s.Browser;
        EnabledCb.IsChecked = s.Enabled;
        ButtonCb.IsChecked = s.ShowButton;
        BadgeCb.IsChecked = s.ShowBadge;
        WebCb.IsChecked = s.WebBridge;
        WebhookBox.Text = s.Webhook;
        TaLineCb.IsChecked = s.TitleArtistOneLine;
        AaLineCb.IsChecked = s.ArtistAlbumOneLine;
        SwapCb.IsChecked = s.SwapOrder;
        AlbumNoArtistCb.IsChecked = s.AlbumNoArtist;
        ProgressCb.IsChecked = s.ShowProgress;
        HideInfoCb.IsChecked = s.HideInfo;
        ArtistCb.IsChecked = s.ShowArtist;
        AlbumCb.IsChecked = s.ShowAlbum;
        VideoMusicCb.IsChecked = s.VideoAsMusic;
        CoversCb.IsChecked = s.Covers;
        DarkCb.IsChecked = s.Dark;
        AutostartCb.IsChecked = s.Autostart;
        AutoUpdCb.IsChecked = s.AutoUpdate;

        CustomOnCb.IsChecked = s.CustomOn;
        CustomOverCb.IsChecked = s.CustomOver;
        CustomTimerCb.IsChecked = s.CustomTimer;
        CustomTitleBox.Text = s.CustomTitle;
        CustomTextBox.Text = s.CustomText;
        UpdatePreview();
        BuildTemplates();
        BuildLang();
        BuildPrefixes();
        BuildAccentPicker();
        BuildCustomTypes();
        foreach (var tb in new[] { CustomTitleBox, CustomTextBox })
            tb.PreviewMouseLeftButtonDown += (s2, _) => { if (s2 is TextBox x && !x.IsKeyboardFocusWithin) System.Windows.Input.Keyboard.Focus(x); };
        StateChanged += (_, _) =>
        {
            bool min = WindowState == WindowState.Minimized;
            HomeBg.Visibility = min ? Visibility.Collapsed : Visibility.Visible;   // свёрнуто: фон не рисуем
            if (!min && !_closing) PlayIn();
        };
        HomeBg.SizeChanged += (_, e) =>
            HomeBg.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 11, 11);
        _customDebounce.Tick += (_, _) => { _customDebounce.Stop(); ApplyCustomText(); };
        Loc.Apply(this);
        _init = false;

        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            if (_s.WinLeft is double l && _s.WinTop is double tp
                && l > vl - 50 && tp > vt - 10 && l < vl + vw - 100 && tp < vt + vh - 100)
            {
                Left = l;
                Top = tp;
            }
            else
            {
                Left = wa.Left + (wa.Width - ActualWidth) / 2;
                Top = wa.Top + (wa.Height - ActualHeight) / 2;
            }

            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            SlideT.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ease });

            _svc.Updated += OnUpdated;
            Theme.Changed += SyncTheme;
            Updater.Changed += OnUpdaterChanged;
            SyncUpdate();
            _timer.Tick += (_, _) => RenderProgress();
            _timer.Start();
            Render();
        };

        Closing += (_, e) =>
        {
            if (_closeDone) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            SavePosition();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) => { _closeDone = true; Close(); };
            Root.BeginAnimation(OpacityProperty, fade);
            SlideT.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, TimeSpan.FromMilliseconds(130)) { EasingFunction = Ease });
        };

        Closed += (_, _) =>
        {
            _svc.Updated -= OnUpdated;
            Theme.Changed -= SyncTheme;
            Updater.Changed -= OnUpdaterChanged;
            _timer.Stop();
            CoverBrush.ImageSource = null;
        };
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl)
        {
            var presenter = FindName("TabContentPresenter") as UIElement;
            if (presenter != null)
            {
                var fadeIn = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(180));
                presenter.BeginAnimation(OpacityProperty, fadeIn);
            }
        }
    }

    void SavePosition()
    {
        try
        {
            var b = RestoreBounds;
            if (double.IsNaN(b.Left) || double.IsNaN(b.Top) || double.IsInfinity(b.Left) || double.IsInfinity(b.Top)) return;
            _s.WinLeft = b.Left;
            _s.WinTop = b.Top;
            _s.Save();
        }
        catch { }
    }

    void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    bool _minimizing;

    // сворачивание: плавно гасим окно и только потом сворачиваем
    void OnMin(object sender, RoutedEventArgs e)
    {
        if (_minimizing || _closing) return;
        _minimizing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) => { _minimizing = false; WindowState = WindowState.Minimized; };
        Root.BeginAnimation(OpacityProperty, fade);
        SlideT.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(26, TimeSpan.FromMilliseconds(170)) { EasingFunction = Ease });
    }

    // разворачивание: окно плавно выплывает
    void PlayIn()
    {
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        SlideT.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = Ease });
    }

    // ---------- обновления ----------

    void OnUpdaterChanged() => Dispatcher.BeginInvoke(new Action(SyncUpdate));

    void SyncUpdate()
    {
        if (_closeDone) return;
        UpdStatus.Text = Updater.Status;
        UpdBtn.IsEnabled = !Updater.Busy;
        var v = Updater.LatestVersion;
        UpdateBar.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
        if (v == null) return;
        bool ready = Updater.ReadyPath != null;
        UpdText.Text = Loc.T("Доступна версия ") + v;
        UpdNow.Content = Loc.T(ready ? "Обновить" : Updater.Installed ? "Скачивается…" : "Скачать");
        UpdNow.IsEnabled = ready || !Updater.Installed;
    }

    void OnCheckUpdate(object sender, RoutedEventArgs e) => _ = Updater.CheckAsync(true);

    void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (Updater.ReadyPath != null)
        {
            if (Updater.Install()) Application.Current.Shutdown();
        }
        else Open(Updater.ReleasesUrl);
    }

    // ---------- справка и отзыв / GitHub ----------

    const string RepoUrl = "https://github.com/zxce1zy/Nowring";

    static void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }

    void OnGitHub(object sender, RoutedEventArgs e) => Open(RepoUrl);

    void OnHelp(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement anchor) return;
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Top, StaysOpen = false,
            AllowsTransparency = true, VerticalOffset = -6, PopupAnimation = PopupAnimation.Fade
        };
        var box = new StackPanel { Width = 250 };
        void Item(string text, Action act)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Margin = new Thickness(0, 2, 0, 2) };
            b.Click += (_, _) => { popup.IsOpen = false; act(); };
            box.Children.Add(b);
        }
        Item(Loc.T("🐞  Сообщить о проблеме"), () => Open(RepoUrl + "/issues/new?title=" + Uri.EscapeDataString("Проблема: ")));
        Item(Loc.T("💡  Предложить идею"), () => Open(RepoUrl + "/issues/new?title=" + Uri.EscapeDataString("Идея: ")));
        Item(Loc.T("📄  Открыть журнал ошибок"), () => Log.Open());
        Item(Loc.T("📋  Скопировать данные для отчёта"), () =>
        {
            try
            {
                Clipboard.SetText("Nowring 2.0.1 · " + Environment.OSVersion.VersionString + " · .NET " + Environment.Version);
            }
            catch { }
        });
        Item(Loc.T("⭐  Страница на GitHub"), () => Open(RepoUrl));
        var card = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(8), Child = box };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Edge");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black };
        popup.Child = card;
        popup.IsOpen = true;
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();

    void OnUpdated() => Dispatcher.BeginInvoke(new Action(Render));
    void SyncTheme() => DarkCb.IsChecked = Theme.Dark;

    void Render()
    {
        if (_closeDone) return;
        var t = _svc.Current;

        if (!_s.HasClientId)
        {
            DiscordDot.Fill = Red;
            DiscordText.Text = Loc.T("не задан Application ID (Config.cs)");
        }
        else if (!_s.Enabled)
        {
            DiscordDot.Fill = Gray;
            DiscordText.Text = Loc.T("статус выключен");
        }
        else
        {
            DiscordDot.Fill = _svc.DiscordConnected ? Green : Gray;
            DiscordText.Text = Loc.T(_svc.DiscordConnected ? "Discord подключён" : "Discord не найден");
        }

        if (_status != DiscordText.Text)
        {
            _status = DiscordText.Text;
            ShowStatus();
        }

        if (t == null)
        {
            TitleText.Text = Loc.T("Ничего не играет");
            ArtistText.Text = "";
            SourceChip.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Collapsed;
            CoverBrush.ImageSource = null;
            _coverKey = _lastTrackKey = null;
            Theme.SetCover(null, null);
            Progress.BeginAnimation(RangeBase.ValueProperty, null);
            Progress.Value = 0;
            PosText.Text = DurText.Text = "";
            return;
        }

        if (_lastTrackKey != t.Key)
        {
            _lastTrackKey = t.Key;
            InfoPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        TitleText.Text = t.Title;
        ArtistText.Text = t.ArtistsDisplay;
        SourceText.Text = Fmt.SourceName(t.Source) + (t.Playing ? "" : "  ⏸");
        SourceChip.Visibility = Visibility.Visible;
        Hint.Visibility = t.Source == "spotify" && !_s.HideHint ? Visibility.Visible : Visibility.Collapsed;

        var coverPath = CoverCache.GetPath(t.Key);
        if (_coverKey != coverPath)
        {
            var img = LoadCover(coverPath);
            CoverBrush.ImageSource = img;
            _coverKey = img != null ? coverPath : null;
            if (img != null)
                CoverBrush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            var cc = img != null ? CoverColors(img) : null;
            Theme.SetCover(cc?.Main, cc?.Second);
        }
        RenderProgress();
    }

    void ShowStatus()
    {
        var x = new DoubleAnimationUsingKeyFrames();
        x.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        x.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)), Ease));
        x.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        x.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3500)), Ease));
        var o = x.Clone();
        StatusScale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
        StatusWrap.BeginAnimation(OpacityProperty, o);
    }

    void RenderProgress()
    {
        var t = _svc.Current;
        if (t == null) return;
        var pos = t.Position;
        if (t.Playing) pos += DateTimeOffset.Now - t.At;
        if (t.Duration > TimeSpan.Zero && pos > t.Duration) pos = t.Duration;
        PosText.Text = Fmt.Time(pos);
        DurText.Text = t.Duration > TimeSpan.Zero ? Fmt.Time(t.Duration) : "";

        double v = t.Duration > TimeSpan.Zero ? Math.Clamp(pos / t.Duration, 0, 1) : 0;
        if (!t.Playing || Math.Abs(v - Progress.Value) > 0.03)
        {
            Progress.BeginAnimation(RangeBase.ValueProperty, null);
            Progress.Value = v;
        }
        else
        {
            Progress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(1000)));
        }
    }

    /// <summary>Два самых «цветных» оттенка обложки (для фона главной).</summary>
    static (Color Main, Color? Second)? CoverColors(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(32.0 / src.PixelWidth, 32.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, n = w * h;
            var px = new byte[n * 4];
            conv.CopyPixels(px, w * 4, 0);

            var sr = new double[12]; var sg = new double[12]; var sb = new double[12]; var sw = new double[12];
            double ar = 0, ag = 0, ab = 0;
            for (int i = 0; i < n; i++)
            {
                double b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
                ar += r; ag += g; ab += b;
                double mxv = Math.Max(r, Math.Max(g, b)), dv = mxv - Math.Min(r, Math.Min(g, b));
                double mx = mxv / 255.0, d = dv / 255.0;
                if (mx < 0.2 || d < 0.15) continue;
                double hue = (mxv == r ? ((g - b) / dv) % 6 : mxv == g ? (b - r) / dv + 2 : (r - g) / dv + 4) * 60;
                if (hue < 0) hue += 360;
                int k = (int)(hue / 30) % 12;
                double wgt = d;
                sr[k] += r * wgt; sg[k] += g * wgt; sb[k] += b * wgt; sw[k] += wgt;
            }

            int top = -1;
            for (int k = 0; k < 12; k++) if (sw[k] > 0 && (top < 0 || sw[k] > sw[top])) top = k;
            if (top < 0)
            {
                // серая обложка: берём средний цвет, чуть осветлив
                var avg = Color.FromRgb((byte)(ar / n), (byte)(ag / n), (byte)(ab / n));
                return (Theme.Vivid(avg), null);
            }

            Color Of(int k) => Theme.Vivid(Color.FromRgb((byte)(sr[k] / sw[k]), (byte)(sg[k] / sw[k]), (byte)(sb[k] / sw[k])));
            int second = -1;
            for (int k = 0; k < 12; k++)
            {
                int dist = Math.Min((k - top + 12) % 12, (top - k + 12) % 12);
                if (dist < 2 || sw[k] < sw[top] * 0.25) continue;
                if (second < 0 || sw[k] > sw[second]) second = k;
            }
            return (Of(top), second >= 0 ? Of(second) : null);
        }
        catch { return null; }
    }

    static BitmapImage? LoadCover(string? path)
    {
        if (path == null) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 192;
            bi.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    void OnToggle(object sender, RoutedEventArgs e)
    {
        _s.Spotify = SpotifyCb.IsChecked == true;
        _s.Yandex = YandexCb.IsChecked == true;
        _s.SoundCloud = SoundCloudCb.IsChecked == true;
        _s.YouTube = YouTubeCb.IsChecked == true;
        _s.Telegram = TelegramCb.IsChecked == true;
        _s.Browser = BrowserCb.IsChecked == true;
        _s.Covers = CoversCb.IsChecked == true;
        _s.Enabled = EnabledCb.IsChecked == true;
        _s.ShowButton = ButtonCb.IsChecked == true;
        _s.ShowBadge = BadgeCb.IsChecked == true;
        _s.WebBridge = WebCb.IsChecked == true;
        _s.TitleArtistOneLine = TaLineCb.IsChecked == true;
        _s.ArtistAlbumOneLine = AaLineCb.IsChecked == true;
        _s.SwapOrder = SwapCb.IsChecked == true;
        _s.AlbumNoArtist = AlbumNoArtistCb.IsChecked == true;
        _s.ShowProgress = ProgressCb.IsChecked == true;
        _s.HideInfo = HideInfoCb.IsChecked == true;
        _s.ShowArtist = ArtistCb.IsChecked == true;
        _s.ShowAlbum = AlbumCb.IsChecked == true;
        _s.VideoAsMusic = VideoMusicCb.IsChecked == true;
        bool customOn = CustomOnCb.IsChecked == true;
        if (customOn && !_s.CustomOn) _s.CustomStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _s.CustomOn = customOn;
        _s.CustomOver = CustomOverCb.IsChecked == true;
        _s.CustomTimer = CustomTimerCb.IsChecked == true;
        _s.AutoUpdate = AutoUpdCb.IsChecked == true;
        bool auto = AutostartCb.IsChecked == true;
        if (auto != _s.Autostart) { _s.Autostart = auto; Autostart.Set(auto); }
        bool dark = DarkCb.IsChecked == true;
        bool themeChanged = dark != _s.Dark;
        _s.Dark = dark;
        _s.Save();
        if (themeChanged) Theme.Apply(dark);
        _svc.Schedule();
    }

    // ---------- акцентный цвет ----------

    static readonly (string Name, string Hex)[] AccentColors =
    {
        ("Авто: под цвет трека", "auto"), ("Бирюзовый", "#14C9A3"), ("Синий", "#3B82F6"), ("Зелёный", "#22C55E"), ("Красный", "#EF4444"),
        ("Оранжевый", "#F97316"), ("Жёлтый", "#EAB308"), ("Розовый", "#EC4899"), ("Фиолетовый", "#8B5CF6"),
    };

    void BuildAccentPicker()
    {
        AccentPanel.Children.Clear();
        foreach (var (name, hex) in AccentColors)
        {
            var ring = new System.Windows.Shapes.Ellipse { Width = 36, Height = 36, StrokeThickness = 2, Stroke = Brushes.Transparent };
            if (string.Equals(hex, _s.Accent, StringComparison.OrdinalIgnoreCase))
                ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Title");
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 26, Height = 26,
                Fill = hex == "auto" ? AutoBrush() : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
            };
            var g = new Grid
            {
                Width = 36, Height = 36, Margin = new Thickness(0, 0, 8, 8), Tag = hex, ToolTip = Loc.T(name),
                Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand
            };
            g.Children.Add(ring);
            g.Children.Add(dot);
            g.MouseLeftButtonUp += OnAccent;
            AccentPanel.Children.Add(g);
        }
    }

    // ---------- вид карточки «Свой статус» ----------

    static readonly (string Name, int Type)[] CustomTypes = { ("Играет", 0), ("Слушает", 2), ("Смотрит", 3) };

    void BuildCustomTypes()
    {
        CustomTypePanel.Children.Clear();
        foreach (var (name, type) in CustomTypes)
        {
            var b = new Button { Content = Loc.T(name), Margin = new Thickness(0, 0, 8, 0), Tag = type };
            if (type == _s.CustomType)
            {
                b.SetResourceReference(Control.BackgroundProperty, "TabSel");
                b.SetResourceReference(Control.ForegroundProperty, "TabSelText");
            }
            b.Click += (s2, _) =>
            {
                if (s2 is not FrameworkElement { Tag: int tp }) return;
                _s.CustomType = tp;
                _s.Save();
                BuildCustomTypes();
                _svc.Schedule();
            };
            CustomTypePanel.Children.Add(b);
        }
    }

    void BuildTemplates()
    {
        TplPanel.Children.Clear();
        foreach (var t in Templates.All)
        {
            var b = new Button
            {
                Tag = t,
                Margin = new Thickness(0, 0, 8, 8),
                Content = new TextBlock { Text = t.Emoji + "  " + Loc.T(t.Name) }
            };
            b.Click += OnTemplate;
            TplPanel.Children.Add(b);
        }
    }

    // ---------- язык ----------

    void BuildLang()
    {
        LangPanel.Children.Clear();
        foreach (var (name, code) in new[] { ("Авто", "auto"), ("Русский", "ru"), ("English", "en") })
        {
            var b = new Button { Content = Loc.T(name), Margin = new Thickness(0, 0, 8, 0), Tag = code };
            if (code == _s.Lang)
            {
                b.SetResourceReference(Control.BackgroundProperty, "TabSel");
                b.SetResourceReference(Control.ForegroundProperty, "TabSelText");
            }
            b.Click += (s2, _) =>
            {
                if (s2 is not FrameworkElement { Tag: string c }) return;
                _s.Lang = c;
                _s.Save();
                Loc.Set(c);
                ApplyLang();
                Loc.Notify();
                _svc.Schedule();
            };
            LangPanel.Children.Add(b);
        }
    }

    void BuildPrefixes()
    {
        Chips(ArtistPrefixPanel, new[] { "", "от", "by" }, _s.ArtistPrefix, v => _s.ArtistPrefix = v);
        Chips(AlbumPrefixPanel, new[] { "", "в", "on" }, _s.AlbumPrefix, v => _s.AlbumPrefix = v);
    }

    void Chips(Panel panel, string[] values, string current, Action<string> set)
    {
        panel.Children.Clear();
        foreach (var v in values)
        {
            var b = new Button { Content = v.Length == 0 ? Loc.T("Нет") : v, Margin = new Thickness(0, 0, 8, 0), Tag = v };
            if (v == current)
            {
                b.SetResourceReference(Control.BackgroundProperty, "TabSel");
                b.SetResourceReference(Control.ForegroundProperty, "TabSelText");
            }
            b.Click += (s2, _) =>
            {
                if (s2 is not FrameworkElement { Tag: string val }) return;
                set(val);
                _s.Save();
                BuildPrefixes();
                _svc.Schedule();
            };
            panel.Children.Add(b);
        }
    }

    void ApplyLang()
    {
        Loc.Apply(this);
        BuildTemplates();
        BuildLang();
        BuildPrefixes();
        BuildAccentPicker();
        BuildCustomTypes();
        SyncUpdate();
        UpdatePreview();
        Render();
    }

    static Brush AutoBrush()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        string[] cs = { "#14C9A3", "#3B82F6", "#8B5CF6", "#EC4899", "#F97316" };
        for (int i = 0; i < cs.Length; i++)
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(cs[i]), i / (cs.Length - 1.0)));
        return b;
    }

    void OnAccent(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;
        _s.Accent = hex;
        _s.Save();
        Theme.Apply(_s.Dark, hex);
        BuildAccentPicker();
    }

    // ---------- свой статус ----------

    void OnWebhook(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        _s.Webhook = WebhookBox.Text.Trim();
        _s.Save();
    }

    void OnCustomText(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        _customDebounce.Stop();
        _customDebounce.Start();
    }

    void ApplyCustomText()
    {
        _s.CustomTitle = CustomTitleBox.Text;
        _s.CustomText = CustomTextBox.Text;
        _s.Save();
        _svc.Schedule();
    }

    void OnTemplate(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Tpl t) return;
        _customDebounce.Stop();
        _s.CustomTitle = Loc.T(t.Title);
        _s.CustomText = Loc.T(t.Text);
        _s.CustomImage = "tpl:" + t.Id;
        _s.CustomOn = true;
        _s.CustomStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _s.Save();
        _init = true;
        CustomTitleBox.Text = Loc.T(t.Title);
        CustomTextBox.Text = Loc.T(t.Text);
        CustomOnCb.IsChecked = true;
        _init = false;
        UpdatePreview();
        _svc.Schedule();
    }

    void OnPickImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Картинка для статуса"),
            Filter = Loc.T("Изображения") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // копируем в папку программы: статус не сломается, если исходный файл удалят
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nowring");
            Directory.CreateDirectory(dir);
            var dst = Path.Combine(dir, "custom_cover" + Path.GetExtension(dlg.FileName).ToLowerInvariant());
            foreach (var old in Directory.GetFiles(dir, "custom_cover.*")) { try { File.Delete(old); } catch { } }
            File.Copy(dlg.FileName, dst, true);
            _s.CustomImage = "file:" + dst;
        }
        catch (Exception ex) { Log.Write("Своя обложка: " + ex.Message); return; }
        _s.Save();
        UpdatePreview();
        _svc.Schedule();
    }

    void OnClearImage(object sender, RoutedEventArgs e)
    {
        _s.CustomImage = "";
        _s.Save();
        UpdatePreview();
        _svc.Schedule();
    }

    void UpdatePreview()
    {
        var img = _s.CustomImage ?? "";
        ImageSource? src = null;
        try
        {
            if (img.StartsWith("tpl:") && Templates.Find(img[4..]) is { } t) src = Templates.Render(t);
            else if (img.StartsWith("file:")) src = LoadCover(img[5..]);
        }
        catch { }
        CustomBrush.ImageSource = src;
    }

    void OnHint(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _s.HideHint = true;
        _s.Save();
        Hint.Visibility = Visibility.Collapsed;
    }

    void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
