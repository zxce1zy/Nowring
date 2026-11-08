using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nowring;

static class Theme
{
    public const string DefaultAccent = "#14C9A3";

    public static bool Dark { get; private set; } = true;
    public static string AccentHex { get; private set; } = DefaultAccent;
    public static event Action? Changed;

    public static void Apply(bool dark, string? accent = null)
    {
        Dark = dark;
        if (!string.IsNullOrWhiteSpace(accent)) AccentHex = accent;
        var ac = ParseOr(AccentHex, DefaultAccent);
        var r = Application.Current.Resources;
        void Set(string key, string hex) => Put(key, new SolidColorBrush(Hex(hex)));
        void Put(string key, Brush b) { if (b.CanFreeze) b.Freeze(); r[key] = b; }

        if (dark)
        {
            Set("Bg", "#16161B"); Set("Edge", "#2A2A33"); Set("Text", "#E6E6EA"); Set("Title", "#FFFFFF");
            Set("Sub", "#A0A0AC"); Set("Muted", "#7C7C88"); Set("Card", "#22222A"); Set("CardHover", "#2E2E38");
            Set("Track", "#2C2C35"); Set("Off", "#3A3A44");
            Set("Divider", "#26262E"); Set("CoverBg", "#24242C");
            Set("Side", "#18181B"); Set("Panel", "#18181B");
            // главная: тёмная «плазма»
            Set("HomeBase", "#05060E"); Set("HomeSub", "#DCE7EE"); Set("HomeMuted", "#AEBFCB");
            Set("HomeTrack", "#33FFFFFF"); Set("HomeFill", "#F2FFFFFF");
            Set("Chip", "#33FFFFFF"); Set("ChipText", "#FFFFFF");
            Put("TabSel", new SolidColorBrush(Alpha(ac, 0x30)));
            Put("TabSelText", new SolidColorBrush(Mix(ac, Colors.White, 0.35)));
        }
        else
        {
            Set("Bg", "#F7F7FA"); Set("Edge", "#DADAE2"); Set("Text", "#1C1C22"); Set("Title", "#0E0E12");
            Set("Sub", "#5C5C68"); Set("Muted", "#84848F"); Set("Card", "#ECECF1"); Set("CardHover", "#E0E0E7");
            Set("Track", "#DCDCE4"); Set("Off", "#C4C4CE");
            Set("Divider", "#E4E4EA"); Set("CoverBg", "#E6E6EC");
            Set("Side", "#EFEFF4"); Set("Panel", "#FFFFFF");
            Set("HomeBase", "#F3F6FA"); Set("HomeSub", "#5C5C68"); Set("HomeMuted", "#84848F");
            Set("HomeTrack", "#DCDCE4");
            Put("HomeFill", new SolidColorBrush(ac));
            var dk = Mix(ac, Colors.Black, 0.3);
            Put("Chip", new SolidColorBrush(Alpha(ac, 0x28)));
            Put("ChipText", new SolidColorBrush(dk));
            Put("TabSel", new SolidColorBrush(Alpha(ac, 0x28)));
            Put("TabSelText", new SolidColorBrush(dk));
        }

        // акцент
        Put("Accent", new SolidColorBrush(ac));
        var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        grad.GradientStops.Add(new GradientStop(ac, 0));
        grad.GradientStops.Add(new GradientStop(Hue(ac, 25), 1));
        Put("AccentGrad", grad);

        _bgShown = false;   // смена темы/акцента: цвета фона ставим сразу
        UpdateBackground(false);

        var edge = dark ? Hex("#030308") : Hex("#F3F6FA");
        var vg = new RadialGradientBrush();
        vg.GradientStops.Add(new GradientStop(Alpha(edge, 0), 0));
        vg.GradientStops.Add(new GradientStop(Alpha(edge, 0), 0.5));
        vg.GradientStops.Add(new GradientStop(Alpha(edge, dark ? (byte)0xE6 : (byte)0xD9), 1));
        Put("Vignette", vg);

        Changed?.Invoke();
    }

    // ---------- цветовые помощники

    static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    static Color ParseOr(string s, string fallback)
    {
        try { return Hex(s); } catch { return Hex(fallback); }
    }

    static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    static RadialGradientBrush Glow(Color c, byte a, double f)
    {
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Alpha(c, (byte)(a * f)), 0));
        b.GradientStops.Add(new GradientStop(Alpha(c, (byte)(a * f * 0.5)), 0.5));
        b.GradientStops.Add(new GradientStop(Alpha(c, 0), 1));
        return b;
    }

    static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    static Color FromHsv(double h, double s, double v)
    {
        h %= 360;
        if (h < 0) h += 360;
        double k = v * s, x = k * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - k;
        (double R, double G, double B) = h < 60 ? (k, x, 0.0) : h < 120 ? (x, k, 0.0) : h < 180 ? (0.0, k, x)
            : h < 240 ? (0.0, x, k) : h < 300 ? (x, 0.0, k) : (k, 0.0, x);
        return Color.FromRgb((byte)Math.Round((R + m) * 255), (byte)Math.Round((G + m) * 255), (byte)Math.Round((B + m) * 255));
    }

    /// <summary>Поворот оттенка (в градусах).</summary>
    static Color Hue(Color c, double deg) { var (h, s, v) = ToHsv(c); return FromHsv(h + deg, s, v); }

    /// <summary>Делает цвет обложки сочным, чтобы он красиво светился на тёмном фоне.</summary>
    public static Color Vivid(Color c)
    {
        var (h, s, v) = ToHsv(c);
        return s < 0.12 ? FromHsv(h, s, Math.Max(v, 0.6)) : FromHsv(h, Math.Max(s, 0.55), Math.Max(v, 0.8));
    }

    // ---------- фон главной: цвета от обложки трека (или от акцента), смена цвета плавная
    // Кисти в ресурсах замораживаются, поэтому анимируем сами: по таймеру пересобираем кисти.

    static readonly double[] GlowAlpha = { 0xE6, 0xA0, 0xC8, 0xD0, 0xD0 };
    static readonly string[] GlowKeys = { "GlowMain", "GlowHi", "GlowB", "GlowD", "GlowC" };
    static Color? _cvMain, _cvSecond;
    static bool _bgShown;
    static Color[] _cur = new Color[6];    // 5 пятен + цвет лучей
    static Color[] _from = new Color[6];
    static Color[] _to = new Color[6];
    static DateTime _t0;
    static DispatcherTimer? _tm;
    const double FadeMs = 1100;

    /// <summary>Задать цвета фона по обложке (null: вернуться к акцентному цвету).</summary>
    public static void SetCover(Color? main, Color? second)
    {
        _cvMain = main;
        _cvSecond = second;
        if (_bgShown && Application.Current != null) UpdateBackground(true);
    }

    static void SetRes(string key, Brush b)
    {
        b.Freeze();
        Application.Current.Resources[key] = b;
    }

    static void Publish()
    {
        double f = Dark ? 1.0 : 0.55;
        for (int i = 0; i < GlowKeys.Length; i++) SetRes(GlowKeys[i], Glow(_cur[i], (byte)GlowAlpha[i], f));
        var sb = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        byte sa = Dark ? (byte)0xC0 : (byte)0x70;
        sb.GradientStops.Add(new GradientStop(Alpha(_cur[5], 0), 0));
        sb.GradientStops.Add(new GradientStop(Alpha(_cur[5], sa), 0.5));
        sb.GradientStops.Add(new GradientStop(Alpha(_cur[5], 0), 1));
        SetRes("Streak", sb);
    }

    static void UpdateBackground(bool animate)
    {
        var ac = ParseOr(AccentHex, DefaultAccent);
        var m = _cvMain ?? ac;
        Color blue, deep, violet;
        if (_cvMain != null)
        {
            var s2 = _cvSecond ?? Hue(m, 40);
            blue = s2;
            deep = Mix(s2, Colors.Black, 0.45);
            violet = Mix(Hue(m, -45), s2, 0.4);
        }
        else
        {
            blue = Mix(ac, Hex("#2A3FE0"), 0.6);
            deep = Mix(ac, Hex("#0B1E8C"), 0.7);
            violet = Mix(ac, Hex("#B01CC8"), 0.6);
        }
        _to = new[] { m, Mix(m, Colors.White, 0.4), blue, deep, violet, Dark ? Mix(m, Colors.White, 0.75) : m };

        _tm?.Stop();
        if (!animate || !_bgShown)
        {
            _cur = (Color[])_to.Clone();
            Publish();
            _bgShown = true;
            return;
        }

        _from = (Color[])_cur.Clone();
        _t0 = DateTime.UtcNow;
        _tm ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _tm.Tick -= Tick;
        _tm.Tick += Tick;
        _tm.Start();
    }

    static void Tick(object? sender, EventArgs e)
    {
        double p = Math.Min(1, (DateTime.UtcNow - _t0).TotalMilliseconds / FadeMs);
        double k = p * p * (3 - 2 * p);   // плавный старт и финиш
        for (int i = 0; i < _cur.Length; i++) _cur[i] = Mix(_from[i], _to[i], k);
        Publish();
        if (p >= 1) _tm?.Stop();
    }
}
