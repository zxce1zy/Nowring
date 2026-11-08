using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nowring;

/// <summary>
/// Автообновление: смотрит последний релиз на GitHub, скачивает установщик в фоне, проверяет SHA256
/// и по кнопке «Обновить» тихо ставит новую версию поверх (установщик сам закрывает и снова запускает программу).
/// У портативной версии (без установщика) только уведомление со ссылкой на релиз.
/// </summary>
static class Updater
{
    const string Api = "https://api.github.com/repos/zxce1zy/Nowring/releases/latest";
    const string AssetPrefix = "https://github.com/zxce1zy/Nowring/";
    public const string ReleasesUrl = "https://github.com/zxce1zy/Nowring/releases/latest";

    public static event Action? Changed;
    public static string? LatestVersion { get; private set; }
    public static string? ReadyPath { get; private set; }
    public static string Status { get; private set; } = "";
    public static bool Busy { get; private set; }

    static string? _readyVersion;

    static readonly HttpClient Http = Create();

    static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Nowring-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    static Version Norm(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Установлена установщиком (рядом с exe лежит unins000.exe): тогда можно обновлять тихо.</summary>
    public static bool Installed
    {
        get
        {
            try
            {
                var d = Path.GetDirectoryName(Environment.ProcessPath);
                return d != null && File.Exists(Path.Combine(d, "unins000.exe"));
            }
            catch { return false; }
        }
    }

    static bool TryParse(string tag, out Version v)
    {
        tag = tag.Trim().TrimStart('v', 'V');
        int cut = tag.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) tag = tag[..cut];
        if (Version.TryParse(tag, out var p)) { v = Norm(p); return true; }
        v = new Version(0, 0, 0);
        return false;
    }

    static void SetStatus(string s)
    {
        Status = s;
        Changed?.Invoke();
    }

    public static async Task LoopAsync(Settings s)
    {
        await Task.Delay(TimeSpan.FromSeconds(25));
        while (true)
        {
            if (s.AutoUpdate) await CheckAsync(false);
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    public static async Task CheckAsync(bool manual)
    {
        const string host = "api.github.com";
        if (Busy) return;
        var cur = Current;
        if (!manual && (cur.Major == 0 && cur.Minor == 0 && cur.Build == 0 || !Net.IsUp(host))) return;   // dev-сборка

        Busy = true;
        if (manual) SetStatus("Проверяю…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await Http.GetAsync(Api, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                if (manual) SetStatus("Не удалось проверить (HTTP " + (int)resp.StatusCode + ")");
                else Net.Fail(host, "HTTP " + (int)resp.StatusCode);
                return;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (!TryParse(root.GetProperty("tag_name").GetString() ?? "", out var latest))
            {
                if (manual) SetStatus("Не удалось разобрать версию релиза");
                return;
            }

            if (latest <= cur)
            {
                LatestVersion = null;
                ReadyPath = null;
                SetStatus("У вас последняя версия " + cur.ToString(3));
                return;
            }

            LatestVersion = latest.ToString(3);
            SetStatus("Доступна версия " + LatestVersion);
            if (!Installed) return;                                   // портативная: только уведомление
            if (ReadyPath != null && File.Exists(ReadyPath) && _readyVersion == LatestVersion) return;

            await DownloadAsync(root);
        }
        catch (Exception ex)
        {
            Log.Write("Обновление: " + ex.GetType().Name + " " + ex.Message);
            if (manual) SetStatus("Нет связи с GitHub");
            else Net.Fail(host, ex);
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    static async Task DownloadAsync(JsonElement release)
    {
        string? setupName = null, setupUrl = null, sumsUrl = null;
        foreach (var a in release.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith(AssetPrefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith("Nowring-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { setupName = name; setupUrl = url; }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
        }
        if (setupName == null || setupUrl == null)
        {
            SetStatus("Доступна версия " + LatestVersion + " (установщик в релизе не найден)");
            return;
        }

        SetStatus("Скачиваю версию " + LatestVersion + "…");
        var dir = Path.Combine(Path.GetTempPath(), "Nowring-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, setupName);
        var tmp = file + ".part";

        using (var r = await Http.GetAsync(setupUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            r.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await r.Content.CopyToAsync(fs);
        }

        if (sumsUrl != null)
        {
            var sums = await Http.GetStringAsync(sumsUrl);
            var line = sums.Split('\n').FirstOrDefault(l => l.TrimEnd().EndsWith(setupName, StringComparison.OrdinalIgnoreCase));
            if (line != null)
            {
                var want = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                string got;
                await using (var fs = File.OpenRead(tmp))
                    got = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!got.Equals(want, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tmp); } catch { }
                    Log.Write("Обновление: контрольная сумма не совпала");
                    SetStatus("Файл обновления повреждён (контрольная сумма), попробую позже");
                    return;
                }
            }
        }

        if (File.Exists(file)) File.Delete(file);
        File.Move(tmp, file);
        ReadyPath = file;
        _readyVersion = LatestVersion;
        SetStatus("Версия " + LatestVersion + " скачана и готова к установке");
    }

    /// <summary>Запускает установщик (он закроет программу, обновит файлы и снова откроет её).</summary>
    public static bool Install()
    {
        if (ReadyPath == null || !File.Exists(ReadyPath)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(ReadyPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Обновление: не удалось запустить установщик: " + ex.Message);
            return false;
        }
    }
}
