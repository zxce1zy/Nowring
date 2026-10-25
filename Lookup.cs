using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nowring;

/// <summary>Что удалось узнать о треке из открытых API: обложка, все артисты, ссылки.</summary>
sealed record TrackMeta(string? CoverUrl, string[]? Artists, Dictionary<string, string>? Links)
{
    public static readonly TrackMeta Empty = new(null, null, null);
}

/// <summary>
/// Поиск трека без ключей, несколько источников параллельно:
///  • Яндекс Музыка (работает в России без VPN): обложка, артисты, ссылка;
///  • Deezer + song.link: обложка, артисты, ссылка на Spotify и другие сервисы;
///  • iTunes: запасной источник обложки.
/// Любой сбой это просто «ничего не нашли», статус от этого не ломается.
/// </summary>
static class Lookup
{
    static readonly HttpClient Http = Create();
    static readonly string MetaFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nowring", "covers", "meta.json");
    static readonly object L = new();
    static readonly Dictionary<string, TrackMeta> Cache = Load();

    static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Nowring/1.0");
        return h;
    }

    // то, что уже узнали о прослушанных треках, лежит на диске: при повторе всё готово сразу
    static Dictionary<string, TrackMeta> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, TrackMeta>>(File.ReadAllText(MetaFile)) ?? new(); }
        catch { return new(); }
    }

    public static TrackMeta? Get(string key)
    {
        lock (L) return Cache.TryGetValue(key, out var m) ? m : null;
    }

    public static void Put(string key, TrackMeta m)
    {
        lock (L)
        {
            if (Cache.Count > 500)
                foreach (var k in Cache.Keys.Take(100).ToList()) Cache.Remove(k);
            Cache[key] = m;

            // пустой результат (трек не нашли или сбой сети) на диск не пишем, чтобы попробовать ещё раз позже
            if (ReferenceEquals(m, TrackMeta.Empty)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MetaFile)!);
                var disk = Cache.Where(p => !ReferenceEquals(p.Value, TrackMeta.Empty))
                                .ToDictionary(p => p.Key, p => p.Value);
                File.WriteAllText(MetaFile, JsonSerializer.Serialize(disk));
            }
            catch { }
        }
    }

    public static async Task<TrackMeta> ResolveAsync(TrackInfo t)
    {
        try
        {
            var first = FirstArtist(t.Artist);
            var title = Clean(t.Title);
            if (first.Length == 0 || title.Length == 0) return TrackMeta.Empty;

            var yaTask = YandexAsync(first, title);
            var dzTask = DeezerAsync(first, title);
            await Task.WhenAll(yaTask, dzTask);
            var ya = yaTask.Result;
            var dz = dzTask.Result;

            string? cover = ya?.CoverUrl ?? dz?.CoverUrl ?? await ITunesCoverAsync(first, title);

            // исполнителей берём из того источника, где их больше (фиты)
            string[]? artists = ya?.Artists;
            if (dz?.Artists != null && (artists == null || dz.Artists.Length > artists.Length)) artists = dz.Artists;

            Dictionary<string, string>? links = null;
            if (dz?.Links != null) links = new Dictionary<string, string>(dz.Links);
            if (ya?.Links != null)
            {
                links ??= new Dictionary<string, string>();
                foreach (var p in ya.Links) links[p.Key] = p.Value;
            }

            Log.Write($"Поиск «{first} — {title}»: Яндекс {(ya != null ? "нашёл" : "нет")}, Deezer {(dz != null ? "нашёл" : "нет")}, обложка {(cover != null ? "есть" : "нет")}");

            if (cover == null && artists == null && links == null) return TrackMeta.Empty;
            return new TrackMeta(cover, artists, links);
        }
        catch (Exception ex)
        {
            Log.Write("Lookup: " + ex.Message);
            return TrackMeta.Empty;
        }
    }

    /// <summary>
    /// Исполнители: написание берём у плеера (регистр как в Spotify), а недостающих (фиты) добавляем из сервисов.
    /// null = оставить строку плеера как есть.
    /// </summary>
    public static string[]? MergeArtists(string player, string[]? found)
    {
        var parts = Regex.Split(player, @"\s*(?:,|;|\sfeat\.?\s|\sft\.?\s)\s*", RegexOptions.IgnoreCase)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (found == null || found.Length == 0) return null;
        if (parts.Length >= found.Length) return parts.Length > 0 ? parts : null;   // плеер уже отдал всех
        return found.Select(d => parts.FirstOrDefault(p => Norm(p) == Norm(d)) ?? d).ToArray();
    }

    /// <summary>Ссылка для кнопки и клика по обложке: точная, если нашли, иначе поиск на сервисе.</summary>
    public static string? UrlFor(TrackMeta m, TrackInfo t)
    {
        string? plat = t.Source switch
        {
            "spotify" => "spotify",
            "yandex" => "yandex",
            "soundcloud" => "soundcloud",
            _ => null
        };
        if (plat != null && m.Links != null && m.Links.TryGetValue(plat, out var exact)) return exact;

        var q = Uri.EscapeDataString(Trunc((FirstArtist(t.Artist) + " " + t.Title).Trim(), 100));
        string? fallback = t.Source switch
        {
            "spotify" => "https://open.spotify.com/search/" + q,
            "yandex" => "https://music.yandex.ru/search?text=" + q,
            "soundcloud" => "https://soundcloud.com/search?q=" + q,
            _ => null
        };
        if (fallback != null) return fallback;
        if (m.Links != null && m.Links.TryGetValue("page", out var page)) return page;
        return null;
    }

    // ---------- Яндекс Музыка ----------

    static async Task<TrackMeta?> YandexAsync(string first, string title)
    {
        const string host = "api.music.yandex.net";
        if (!Net.IsUp(host)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.music.yandex.net/search?type=track&page=0&nocorrect=false&text=" +
                Uri.EscapeDataString(first + " " + title));
            req.Headers.Add("X-Yandex-Music-Client", "YandexMusicAndroid/24023231");
            using var r = await Http.SendAsync(req);
            if (!r.IsSuccessStatusCode)
            {
                Net.Fail(host, "HTTP " + (int)r.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("result", out var res)
                || !res.TryGetProperty("tracks", out var tracks)
                || !tracks.TryGetProperty("results", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return null;

            string nt = Norm(title), na = Norm(first);
            if (nt.Length == 0 || na.Length == 0) return null;

            foreach (var it in arr.EnumerateArray())
            {
                if (Norm(Str(it, "title")) != nt) continue;

                var names = new List<string>();
                if (it.TryGetProperty("artists", out var ars) && ars.ValueKind == JsonValueKind.Array)
                    foreach (var a in ars.EnumerateArray())
                    {
                        var n = Str(a, "name");
                        if (n.Length > 0) names.Add(n);
                    }
                if (!names.Any(n => { var x = Norm(n); return x.Length > 0 && (x == na || x.Contains(na) || na.Contains(x)); }))
                    continue;

                string? id = IdOf(it, "realId") ?? IdOf(it, "id");
                if (id != null) id = id.Split(':')[0];
                string? albumId = null;
                if (it.TryGetProperty("albums", out var albs) && albs.ValueKind == JsonValueKind.Array && albs.GetArrayLength() > 0)
                    albumId = IdOf(albs[0], "id");

                string? cover = null;
                var cu = Str(it, "coverUri");
                if (cu.Length > 0) cover = "https://" + cu.Replace("%%", "400x400");

                Dictionary<string, string>? links = null;
                if (id != null && albumId != null)
                    links = new Dictionary<string, string> { ["yandex"] = $"https://music.yandex.ru/album/{albumId}/track/{id}" };

                return new TrackMeta(cover, names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), links);
            }
            return null;
        }
        catch (Exception ex)
        {
            Net.Fail(host, ex);
            return null;
        }
    }

    // ---------- Deezer + song.link ----------

    readonly record struct Hit(long Id, string Link, string? Cover, string Artist);

    static async Task<TrackMeta?> DeezerAsync(string first, string title)
    {
        const string host = "api.deezer.com";
        if (!Net.IsUp(host)) return null;
        try
        {
            var hit = await SearchDeezer($"artist:\"{Q(first)}\" track:\"{Q(title)}\"", first, title)
                   ?? await SearchDeezer($"{Q(first)} {Q(title)}", first, title);
            if (hit == null) return null;

            string[]? artists = null;
            try
            {
                var json = await Http.GetStringAsync("https://api.deezer.com/track/" + hit.Value.Id);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("contributors", out var c) && c.ValueKind == JsonValueKind.Array)
                {
                    artists = c.EnumerateArray()
                        .Select(x => Str(x, "name"))
                        .Where(x => x.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
            catch { }
            if (artists == null || artists.Length == 0)
                artists = hit.Value.Artist.Length > 0 ? new[] { hit.Value.Artist } : null;

            var links = await SongLink(hit.Value.Link) ?? new Dictionary<string, string>();
            if (hit.Value.Link.StartsWith("https://")) links["deezer"] = hit.Value.Link;

            return new TrackMeta(hit.Value.Cover, artists, links);
        }
        catch (Exception ex)
        {
            Net.Fail(host, ex);
            return null;
        }
    }

    static async Task<Hit?> SearchDeezer(string q, string first, string title)
    {
        var json = await Http.GetStringAsync("https://api.deezer.com/search?limit=8&q=" + Uri.EscapeDataString(q));
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        string nt = Norm(title), na = Norm(first);
        if (nt.Length == 0 || na.Length == 0) return null;

        foreach (var it in data.EnumerateArray())
        {
            string ct = Norm(Str(it, "title")), cs = Norm(Str(it, "title_short"));
            if (ct != nt && cs != nt) continue;

            string artistName = it.TryGetProperty("artist", out var ar) ? Str(ar, "name") : "";
            string an = Norm(artistName);
            if (an.Length == 0 || !(an == na || an.Contains(na) || na.Contains(an))) continue;

            if (!it.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;

            string? cover = null;
            if (it.TryGetProperty("album", out var al))
            {
                foreach (var k in new[] { "cover_big", "cover_xl", "cover_medium" })
                {
                    var c = Str(al, k);
                    if (c.StartsWith("https://")) { cover = c; break; }
                }
            }
            return new Hit(id, Str(it, "link"), cover, artistName);
        }
        return null;
    }

    static async Task<Dictionary<string, string>?> SongLink(string deezerLink)
    {
        const string host = "api.song.link";
        if (!deezerLink.StartsWith("https://") || !Net.IsUp(host)) return null;
        try
        {
            var json = await Http.GetStringAsync("https://api.song.link/v1-alpha.1/links?url=" + Uri.EscapeDataString(deezerLink));
            using var doc = JsonDocument.Parse(json);
            var res = new Dictionary<string, string>();
            if (doc.RootElement.TryGetProperty("linksByPlatform", out var lp) && lp.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in lp.EnumerateObject())
                {
                    var u = Str(p.Value, "url");
                    if (u.StartsWith("https://") && u.Length < 400) res[p.Name] = u;
                }
            }
            var page = Str(doc.RootElement, "pageUrl");
            if (page.StartsWith("https://")) res["page"] = page;
            return res;
        }
        catch (Exception ex)
        {
            Net.Fail(host, ex);   // например, лимит запросов
            return null;
        }
    }

    // ---------- iTunes (только обложка) ----------

    static async Task<string?> ITunesCoverAsync(string first, string title)
    {
        const string host = "itunes.apple.com";
        if (!Net.IsUp(host)) return null;
        try
        {
            var json = await Http.GetStringAsync("https://itunes.apple.com/search?entity=song&limit=8&term=" +
                                                 Uri.EscapeDataString(first + " " + title));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

            string nt = Norm(title), na = Norm(first);
            if (nt.Length == 0 || na.Length == 0) return null;

            foreach (var it in arr.EnumerateArray())
            {
                if (Norm(Str(it, "trackName")) != nt) continue;
                var an = Norm(Str(it, "artistName"));
                if (an.Length == 0 || !(an == na || an.Contains(na) || na.Contains(an))) continue;
                var art = Str(it, "artworkUrl100");
                if (art.StartsWith("https://")) return art.Replace("100x100bb", "600x600bb");
            }
            return null;
        }
        catch (Exception ex)
        {
            Net.Fail(host, ex);
            return null;
        }
    }

    // ---------- вспомогательное ----------

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    static string? IdOf(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString(),
            _ => null
        };
    }

    static string Q(string s) => s.Replace('"', ' ');

    static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    // «Song (feat. X)» и «Song - Remastered» -> «Song»
    static string Clean(string s)
    {
        s = Regex.Replace(s, @"\s*[\(\[].*?[\)\]]", "");
        s = Regex.Replace(s, @"\s-\s.*$", "");
        return s.Trim();
    }

    static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in Clean(s).ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    static string FirstArtist(string a)
    {
        var parts = Regex.Split(a, @"\s*(?:,|;|&|/|\sfeat\.?\s|\sft\.?\s|\sx\s)\s*", RegexOptions.IgnoreCase);
        return parts.Length > 0 ? parts[0].Trim() : a.Trim();
    }
}
