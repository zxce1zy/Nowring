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
    static readonly Dictionary<string, string> Cache = new();
    static readonly Dictionary<string, DateTime> Neg = new();   // «не нашли»: не кэшируем надолго

    public static string Detect(string aumid, string title, string artist)
    {
        var key = aumid + "|" + title + "|" + artist;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var c)) return c;
            // не нашли пару секунд назад: не сканируем вкладки снова при каждом событии
            if (Neg.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalSeconds < 3) return "browser";
        }

        string site;
        try { site = Scan(aumid, title); }
        catch { return "browser"; }   // не кэшируем сбой, попробуем при следующем событии

        lock (Cache)
        {
            if (Cache.Count > 60) Cache.Clear();
            if (Neg.Count > 60) Neg.Clear();
            // «browser» запоминаем только на пару секунд: Chrome отдаёт список вкладок не сразу,
            // а позже (иначе один неудачный просмотр навсегда оставлял «Слушает Nowring»)
            if (site == "browser") Neg[key] = DateTime.UtcNow; else Cache[key] = site;
        }
        return site;
    }

    static string Scan(string aumid, string title)
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

        var t = title.ToLowerInvariant();
        bool yandex = false, soundcloud = false;
        foreach (var n in names)
        {
            var l = n.ToLowerInvariant();
            bool isY = l.Contains("яндекс музык") || l.Contains("yandex music") || l.Contains("music.yandex");
            bool isS = l.Contains("soundcloud");
            // вкладка с названием играющего трека и признаком сайта: однозначно
            if (t.Length > 2 && l.Contains(t))
            {
                if (isY) return "yandex";
                if (isS) return "soundcloud";
            }
            yandex |= isY;
            soundcloud |= isS;
        }
        if (yandex && !soundcloud) return "yandex";
        if (soundcloud && !yandex) return "soundcloud";
        Log.Write("Сайт в браузере не определён (найдено названий вкладок и окон: " + names.Count + ")");
        return "browser";
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
