using System;
using System.IO;
using System.Windows;
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
        BrowserCb.IsChecked = s.Browser;
        EnabledCb.IsChecked = s.Enabled;
        ButtonCb.IsChecked = s.ShowButton;
        CoversCb.IsChecked = s.Covers;
        DarkCb.IsChecked = s.Dark;
        AutostartCb.IsChecked = s.Autostart;

        Loaded += (_, _) =>
        {
            // позиция: где окно стояло в прошлый раз, иначе по центру экрана
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

            // появление: плавно проявляется и выезжает снизу
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            SlideT.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ease });

            _svc.Updated += OnUpdated;
            Theme.Changed += SyncTheme;
            _timer.Tick += (_, _) => RenderProgress();
            _timer.Start();
            Render();
        };

        // закрытие: сначала анимация, потом настоящее закрытие
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
            _timer.Stop();
            CoverBrush.ImageSource = null;
        };
    }

    void SavePosition()
    {
        try
        {
            var b = RestoreBounds;   // верно и для свёрнутого окна
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

    void OnMin(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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
            DiscordText.Text = "не задан Application ID (Config.cs)";
        }
        else if (!_s.Enabled)
        {
            DiscordDot.Fill = Gray;
            DiscordText.Text = "статус выключен";
        }
        else
        {
            DiscordDot.Fill = _svc.DiscordConnected ? Green : Gray;
            DiscordText.Text = _svc.DiscordConnected ? "Discord подключён" : "Discord не найден";
        }

        if (t == null)
        {
            TitleText.Text = "Ничего не играет";
            ArtistText.Text = "";
            SourceChip.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Collapsed;
            CoverBrush.ImageSource = null;
            _coverKey = _lastTrackKey = null;
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

        if (_coverKey != t.Key)
        {
            var img = LoadCover(CoverCache.GetPath(t.Key));
            CoverBrush.ImageSource = img;
            _coverKey = img != null ? t.Key : null;
            if (img != null)
                CoverBrush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        }
        RenderProgress();
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
            Progress.BeginAnimation(RangeBase.ValueProperty, null);   // перемотка / смена трека: сразу
            Progress.Value = v;
        }
        else
        {
            Progress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(1000)));
        }
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
        _s.Browser = BrowserCb.IsChecked == true;
        _s.Covers = CoversCb.IsChecked == true;
        _s.Enabled = EnabledCb.IsChecked == true;
        _s.ShowButton = ButtonCb.IsChecked == true;
        bool auto = AutostartCb.IsChecked == true;
        if (auto != _s.Autostart) { _s.Autostart = auto; Autostart.Set(auto); }
        bool dark = DarkCb.IsChecked == true;
        bool themeChanged = dark != _s.Dark;
        _s.Dark = dark;
        _s.Save();
        if (themeChanged) Theme.Apply(dark);
        _svc.Schedule();
    }

    void OnHint(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _s.HideHint = true;
        _s.Save();
        Hint.Visibility = Visibility.Collapsed;
    }

    void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
