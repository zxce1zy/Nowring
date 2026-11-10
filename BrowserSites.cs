using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Nowring;

/// <summary>
/// Windows не сообщает, какой сайт играет в браузере. Смотрим названия окон и вкладок браузера
/// (один раз на трек) и ищем Яндекс Музыку / SoundCloud. Не нашли: обычный «browser».
/// </summary>
static class BrowserSites
{
    static readonly Dictionary<string, (string Site, bool Matched)> Cache = new();
    static readonly Dictionary<string, (DateTime At, string Site, bool Matched)> Neg = new();   // «не нашли»: не кэшируем надолго

    /// <summary>
    /// Site: сайт, который играет. Matched: у сайта есть вкладка с названием играющего ролика/трека
    /// (так отличаем открытое видео YouTube от превью, на которое просто навели мышь).
    /// </summary>
    public static (string Site, bool Matched) Detect(string aumid, string title, string artist)
    {
        var key = aumid + "|" + title + "|" + artist;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var c)) return c;
            // не нашли пару секунд назад: не сканируем вкладки снова при каждом событии
            if (Neg.TryGetValue(key, out var n) && (DateTime.UtcNow - n.At).TotalSeconds < 3) return (n.Site, n.Matched);
        }

        (string Site, bool Matched) r;
        try { r = Scan(aumid, title); }
        catch { return ("browser", false); }   // не кэшируем сбой, попробуем при следующем событии

        lock (Cache)
        {
            if (Cache.Count > 60) Cache.Clear();
            if (Neg.Count > 60) Neg.Clear();
            // «browser» и непроверенный YouTube запоминаем только на пару секунд: Chrome отдаёт список
            // вкладок не сразу, а название вкладки у видео может обновиться позже
            if (r.Site == "browser" || (r.Site == "youtube" && !r.Matched)) Neg[key] = (DateTime.UtcNow, r.Site, r.Matched);
            else Cache[key] = r;
        }
        return r;
    }

    static (string, bool) Scan(string aumid, string title)
    {
        var names = Names(aumid);

        var t = title.ToLowerInvariant();
        string? only = null;
        int kinds = 0;
        var seen = new HashSet<string>();
        foreach (var n in names)
        {
            var l = n.ToLowerInvariant();
            string? site =
                l.Contains("яндекс музык") || l.Contains("yandex music") || l.Contains("music.yandex") ? "yandex" :
                l.Contains("soundcloud") ? "soundcloud" :
                l.Contains("youtube") ? "youtube" :
                l.Contains("telegram") ? "telegram" : null;
            if (site == null) continue;
            // вкладка с названием играющего трека и признаком сайта: однозначно
            if (t.Length > 2 && l.Contains(t)) return (site, true);
            if (seen.Add(site)) { kinds++; only = site; }
        }
        if (kinds == 1) return (only!, only != "youtube");
        // рядом открыты и другие сайты, а название не совпало ни с одной вкладкой: это превью YouTube
        if (seen.Contains("youtube")) return ("youtube", false);
        Log.Write("Сайт в браузере не определён (найдено названий вкладок и окон: " + names.Count + ")");
        return ("browser", false);
    }

    static List<string> Names(string aumid)
    {
        var names = new List<string>();
        foreach (var h in FindWindows(ProcNames(aumid)))
        {
            var wt = WindowTitle(h);
            if (wt.Length > 0) names.Add(wt);
            try
            {
                var root = AutomationElement.FromHandle(h);
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
                {
                    var n = e.Current.Name;
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            catch { }
        }
        return names;
    }

    static string? _tabKey;
    static DateTime _tabAt;
    static bool _tabRes;

    /// <summary>Есть ли ещё вкладка с таким названием (результат на 3 секунды в кэше).</summary>
    public static bool HasTab(string aumid, string title)
    {
        var key = aumid + "|" + title;
        lock (Cache)
        {
            if (_tabKey == key && (DateTime.UtcNow - _tabAt).TotalSeconds < 3) return _tabRes;
        }
        bool res = false;
        try
        {
            var t = title.ToLowerInvariant();
            foreach (var n in Names(aumid))
                if (n.ToLowerInvariant().Contains(t)) { res = true; break; }
        }
        catch { res = true; }   // сбой UIA: не рвём статус
        lock (Cache) { _tabKey = key; _tabAt = DateTime.UtcNow; _tabRes = res; }
        return res;
    }

    /// <summary>
    /// Spotify отдаёт в системное управление мультимедиа только первого исполнителя, а в заголовке окна
    /// пишет всех («A, B - Название»). Берём полный список, если он начинается с того же исполнителя.
    /// </summary>
    public static string? SpotifyArtists(string title, string artist)
    {
        try
        {
            string suffix = " - " + title;
            foreach (var h in FindWindows(new[] { "Spotify" }))
            {
                var wt = WindowTitle(h);
                if (!wt.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var list = wt[..^suffix.Length].Trim();
                if (list.Length > artist.Length && list.StartsWith(artist, StringComparison.OrdinalIgnoreCase)) return list;
            }
        }
        catch { }
        return null;
    }

    static readonly Dictionary<string, (string? Url, DateTime At)> YtUrls = new();

    /// <summary>
    /// Адрес открытой вкладки с этим видео (из адресной строки браузера). Работает, пока вкладка с видео
    /// активна в своём окне (заголовок окна совпадает с названием видео), иначе null.
    /// </summary>
    public static string? YouTubeUrl(string aumid, string title)
    {
        var key = aumid + "|" + title;
        lock (YtUrls)
        {
            if (YtUrls.TryGetValue(key, out var c) && (c.Url != null || (DateTime.UtcNow - c.At).TotalSeconds < 5)) return c.Url;
        }
        string? url = null;
        try
        {
            var t = title.ToLowerInvariant();
            foreach (var h in FindWindows(ProcNames(aumid)))
            {
                if (!WindowTitle(h).ToLowerInvariant().Contains(t)) continue;
                var root = AutomationElement.FromHandle(h);
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
                {
                    if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var pat) && pat is ValuePattern vp)
                    {
                        var v = vp.Current.Value ?? "";
                        if (v.Contains("youtube.com/") || v.Contains("youtu.be/")) { url = v; break; }
                    }
                }
                if (url != null) break;
            }
        }
        catch { }
        lock (YtUrls)
        {
            if (YtUrls.Count > 60) YtUrls.Clear();
            YtUrls[key] = (url, DateTime.UtcNow);
        }
        return url;
    }

    static string[] ProcNames(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        if (a.Contains("chrome")) return new[] { "chrome" };
        if (a.Contains("msedge")) return new[] { "msedge" };
        if (a.Contains("firefox") || a.Contains("308046b0af4a39cb")) return new[] { "firefox" };
        if (a.Contains("opera")) return new[] { "opera" };
        if (a.Contains("brave")) return new[] { "brave" };
        if (a.Contains("vivaldi")) return new[] { "vivaldi" };
        if (a.Contains("yandex") || a.Contains("browser")) return new[] { "browser" };
        return Array.Empty<string>();
    }

    static List<IntPtr> FindWindows(string[] procNames)
    {
        var pids = new HashSet<uint>();
        foreach (var n in procNames)
        {
            foreach (var p in Process.GetProcessesByName(n))
            {
                pids.Add((uint)p.Id);
                p.Dispose();
            }
        }

        var res = new List<IntPtr>();
        if (pids.Count == 0) return res;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains(pid)) res.Add(h);
            return true;
        }, IntPtr.Zero);
        return res;
    }

    static string WindowTitle(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
}
