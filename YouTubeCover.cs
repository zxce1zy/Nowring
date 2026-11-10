using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nowring;

/// <summary>
/// Обложка видео YouTube, когда браузер не отдал превью через системное управление мультимедиа:
/// ищем id видео (адресная строка вкладки, иначе поиск YouTube по названию) и берём картинку с i.ytimg.com.
/// </summary>
static class YouTubeCover
{
    static readonly HttpClient Http = Create();
    static readonly Dictionary<string, string?> Ids = new();

    static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        h.DefaultRequestHeaders.Add("Cookie", "CONSENT=YES+1; SOCS=CAI");
        return h;
    }

    static string? IdFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = Regex.Match(url, @"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/)([\w-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Ищем видео по названию в поиске YouTube, берём только точное совпадение названия.</summary>
    static async Task<string?> SearchIdAsync(string title, string channel)
    {
        var key = title + "|" + channel;
        lock (Ids) if (Ids.TryGetValue(key, out var c)) return c;
        string? id = null;
        try
        {
            var q = Uri.EscapeDataString((title + " " + channel).Trim());
            var html = await Http.GetStringAsync("https://www.youtube.com/results?sp=EgIQAQ%253D%253D&search_query=" + q);
            var want = Norm(title);
            foreach (Match m in Regex.Matches(html, "\"videoRenderer\":\\{\"videoId\":\"([\\w-]{11})\".{0,2500}?\"title\":\\{\"runs\":\\[\\{\"text\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Singleline))
            {
                string text;
                try { text = JsonSerializer.Deserialize<string>("\"" + m.Groups[2].Value + "\"") ?? ""; }
                catch { continue; }
                if (Norm(text) == want) { id = m.Groups[1].Value; break; }
            }
        }
        catch (Exception ex) { Log.Write("Обложка YouTube (поиск): " + ex.Message); }
        lock (Ids)
        {
            if (Ids.Count > 100) Ids.Clear();
            Ids[key] = id;
        }
        return id;
    }

    public static async Task<byte[]?> GetAsync(string aumid, string title, string channel)
    {
        string? id = null;
        try { id = IdFromUrl(await Task.Run(() => BrowserSites.YouTubeUrl(aumid, title))); } catch { }
        id ??= await SearchIdAsync(title, channel);
        if (id == null) return null;

        // maxresdefault: 16:9 без чёрных полос (есть не у всех видео), mqdefault есть всегда
        foreach (var name in new[] { "maxresdefault", "mqdefault" })
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync($"https://i.ytimg.com/vi/{id}/{name}.jpg");
                if (bytes.Length > 3000) return bytes;
            }
            catch { }
        }
        return null;
    }
}
