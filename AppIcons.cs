using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nowring;

/// <summary>
/// Название и иконка приложения Discord (Spotify, YouTube…): иконку вы загружаете на странице приложения
/// в Developer Portal, а программа показывает её значком в углу обложки. Узнаём публичным запросом.
/// </summary>
static class AppIcons
{
    sealed record Info(string? Name, string? IconUrl, DateTime Until);

    static readonly Dictionary<string, Info> Cache = new();
    static readonly HttpClient Http = Create();

    static HttpClient Create()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Nowring/2.0");
        return h;
    }

    public static async Task<(string? Name, string? IconUrl)> InfoAsync(string clientId)
    {
        const string host = "discord.com";
        lock (Cache)
            if (Cache.TryGetValue(clientId, out var known) && known.Until > DateTime.UtcNow)
                return (known.Name, known.IconUrl);
        if (!Net.IsUp(host)) return (null, null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var r = await Http.GetAsync("https://discord.com/api/v10/applications/" + clientId + "/rpc", cts.Token);
            if (!r.IsSuccessStatusCode)
            {
                Log.Write("Значок приложения " + clientId + ": HTTP " + (int)r.StatusCode);
                return (null, null);
            }
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            string? name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            string? icon = root.TryGetProperty("icon", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string? url = string.IsNullOrEmpty(icon) ? null : "https://cdn.discordapp.com/app-icons/" + clientId + "/" + icon + ".png?size=128";
            Log.Write("Значок приложения " + clientId + " (" + name + "): " + (url ?? "иконка не загружена в Developer Portal"));

            // нашли иконку: помним час; нет иконки: проверим снова через 2 минуты (вдруг только что загрузили)
            var until = DateTime.UtcNow + (url != null ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(2));
            lock (Cache) Cache[clientId] = new Info(name, url, until);
            return (name, url);
        }
        catch (Exception ex)
        {
            Net.Fail(host, ex);
            return (null, null);
        }
    }
}
