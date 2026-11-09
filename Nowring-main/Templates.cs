using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nowring;

sealed record Tpl(string Id, string Emoji, string Name, string Title, string Text, string C1, string C2);

/// <summary>Готовые шаблоны своего статуса: обложка рисуется на лету (градиент + эмодзи).</summary>
static class Templates
{
    public static readonly Tpl[] All =
    {
        new("code",   "💻", "Пишу код",    "Пишу код",      "Сижу в редакторе",      "#1E3A8A", "#3B82F6"),
        new("game",   "🎮", "Играю",       "Играю",         "Катаю с друзьями",      "#5B21B6", "#A855F7"),
        new("study",  "📚", "Учусь",       "Учусь",         "Грызу гранит науки",    "#047857", "#34D399"),
        new("movie",  "🎬", "Смотрю кино", "Смотрю кино",   "Вечер с фильмом",       "#9F1239", "#FB7185"),
        new("draw",   "🎨", "Рисую",       "Рисую",         "Творю красоту",         "#C2410C", "#FBBF24"),
        new("work",   "💼", "Работаю",     "Работаю",       "Не беспокоить",         "#334155", "#94A3B8"),
        new("stream", "📡", "Стримлю",     "Стримлю",       "Заходи в эфир",         "#7C3AED", "#EC4899"),
        new("read",   "📖", "Читаю",       "Читаю",         "Погружаюсь в книгу",    "#78350F", "#D97706"),
        new("gym",    "🏋", "Тренируюсь",  "Тренируюсь",    "Качаю железо",          "#7F1D1D", "#EF4444"),
        new("sleep",  "😴", "Сплю",        "Сплю",          "Не будить",             "#1E1B4B", "#6366F1"),
    };

    public static Tpl? Find(string id)
    {
        foreach (var t in All) if (t.Id == id) return t;
        return null;
    }

    public static BitmapSource Render(Tpl t)
    {
        const int S = 512;
        var rect = new Rect(0, 0, S, S);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(t.C1),
                (Color)ColorConverter.ConvertFromString(t.C2), 45);
            dc.DrawRectangle(bg, null, rect);
            var glow = new RadialGradientBrush(Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
            {
                Center = new Point(0.3, 0.25), GradientOrigin = new Point(0.3, 0.25), RadiusX = 0.8, RadiusY = 0.8
            };
            dc.DrawRectangle(glow, null, rect);
            var ft = new FormattedText(t.Emoji, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Emoji"), 240, Brushes.White, 1.0);
            dc.DrawText(ft, new Point((S - ft.Width) / 2, (S - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static byte[] Png(Tpl t)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Render(t)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Картинка для статуса: «tpl:код» (шаблон) или «file:путь» (своя).</summary>
    public static async Task<byte[]?> LoadAsync(string img)
    {
        try
        {
            if (img.StartsWith("tpl:") && Find(img[4..]) is { } t)
                return await Application.Current.Dispatcher.InvokeAsync(() => Png(t));
            if (img.StartsWith("file:") && File.Exists(img[5..]))
                return await File.ReadAllBytesAsync(img[5..]);
        }
        catch { }
        return null;
    }
}
