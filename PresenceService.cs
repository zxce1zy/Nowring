using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Nowring;

sealed class PresenceService
{
    readonly Settings _s;
    readonly MediaWatcher _watcher = new();
    readonly DiscordIpc _ipc = new();
    readonly SemaphoreSlim _gate = new(1, 1);

    CancellationTokenSource? _debounce;
    System.Threading.Timer? _retry;
    string? _lastSig;
    long _lastStart;
    string? _connId;          // Application ID, с которым сейчас открыт канал Discord

    // если Discord отклонил статус, упрощаем его для этого трека: 1 = без кнопки, 2 = без ссылок
    string? _waitedKey;       // для этого трека мы уже ждали загрузку обложки перед показом
    string? _degradeKey;
    int _degrade;

    readonly object _cf = new();
    readonly HashSet<string> _inflight = new();
    readonly HashSet<string> _resolved = new();
    readonly Dictionary<string, DateTime> _cooldown = new();

    public TrackInfo? Current { get; private set; }
    public bool DiscordConnected => _ipc.Connected;
    public event Action? Updated;

    public PresenceService(Settings s)
    {
        _s = s;
        _ipc.Disconnected += () => { _lastSig = null; _connId = null; StartRetry(); Updated?.Invoke(); };
        _ipc.ActivityError += OnActivityError;
    }

    public async Task StartAsync()
    {
        _watcher.Changed += Schedule;

        // сразу после входа в Windows (автозапуск) служба медиа может быть ещё не готова: пробуем несколько раз
        for (int attempt = 0; ; attempt++)
        {
            try { await _watcher.StartAsync(); break; }
            catch (Exception ex)
            {
                Log.Write("Запуск слежения за плеером: " + ex.Message);
                if (attempt >= 30) return;
                await Task.Delay(3000);
            }
        }
        Schedule();
    }

    public void ClientIdChanged()
    {
        _ipc.Close();
        _connId = null;
        _lastSig = null;
        Schedule();
    }

    void OnActivityError(string payload)
    {
        Log.Write("Discord отклонил статус: " + payload);
        var key = Current?.Key;
        if (_degradeKey != key) { _degradeKey = key; _degrade = 0; }
        if (_degrade >= 2) return;
        _degrade++;
        _lastSig = null;
        Schedule();
    }

    /// <summary>Склеивает пачку событий SMTC в одно обновление статуса.</summary>
    public void Schedule()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(350, cts.Token); await RefreshAsync(); }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    /// <summary>Добавляет к треку то, что узнали из Deezer / song.link (все артисты, ссылка).</summary>
    static TrackInfo? Merge(TrackInfo? t)
    {
        if (t == null) return null;
        var m = Lookup.Get(t.Key);
        return m == null ? t : t with { Artists = Lookup.MergeArtists(t.Artist, m.Artists), Url = Lookup.UrlFor(m, t) };
    }

    string StateKey(TrackInfo t) => t.Key + (_s.Covers ? "+c" : "");

    async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var t = Merge(await _watcher.SnapshotAsync(_s));
            Current = t;

            if (!_s.HasClientId) return;

            if (!_s.Enabled)
            {
                // статус выключен из трея: закрываем канал (Discord сам уберёт активность)
                StopRetry();
                _lastSig = null;
                _connId = null;
                _ipc.Close();
                return;
            }

            if (t == null)
            {
                StopRetry();
                if (_lastSig != null)
                {
                    _lastSig = null;
                    if (_ipc.Connected) await _ipc.SetActivityAsync(null);
                }
                return;
            }

            string? url = _s.Covers ? CoverCache.GetUrl(t.Key) : null;
            bool need;
            lock (_cf) need = !_resolved.Contains(StateKey(t)) || (_s.Covers && url == null);
            if (need)
            {
                var running = StartResolve(t);
                bool ready = (!_s.Covers || url != null) && Lookup.Get(t.Key) != null;

                // новый трек, которого ещё нет в кэше: чуть ждём обложку, чтобы карточка сразу
                // появилась целиком, а не сначала пустой (не дольше 2 секунд)
                if (running != null && !ready && _waitedKey != t.Key)
                {
                    _waitedKey = t.Key;
                    await Task.WhenAny(running, Task.Delay(2000));
                    t = Merge(t)!;
                    Current = t;
                    url = _s.Covers ? CoverCache.GetUrl(t.Key) : null;
                }
            }

            long start = t.Playing ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)t.Position.TotalSeconds : 0;
            string sig = $"{t.Key}|{t.Source}|{t.Playing}|{url}|{t.ArtistsStatus}|{t.Url}|{_degrade}|{_s.ShowButton}|{(t.Playing ? "" : ((int)t.Position.TotalSeconds).ToString())}";
            if (sig == _lastSig && (!t.Playing || Math.Abs(start - _lastStart) <= 2)) return;

            // у каждого источника своё приложение Discord (от этого зависит «Слушает Spotify»)
            var cid = _s.ClientIdFor(t.Source);
            if (!_ipc.Connected || _connId != cid)
            {
                if (!await _ipc.ConnectAsync(cid)) { _connId = null; StartRetry(); return; }
                _connId = cid;
                _lastSig = null;
            }

            await _ipc.SetActivityAsync(Build(t, url, start));
            _lastSig = sig;
            _lastStart = start;
            StopRetry();
        }
        catch
        {
            _lastSig = null;
            StartRetry();
        }
        finally
        {
            _gate.Release();
            Updated?.Invoke();
        }
    }

    static string Clip(string s, int max = 128)
    {
        s = s.Length > max ? s[..(max - 1)] + "…" : s;
        return s.Length < 2 ? s.PadRight(2) : s;
    }

    object Build(TrackInfo t, string? coverUrl, long start)
    {
        int degrade = _degradeKey == t.Key ? _degrade : 0;
        string artists = t.ArtistsStatus;

        string state;
        if (t.Playing)
            state = artists.Length > 0 ? artists : Fmt.SourceName(t.Source);
        else
        {
            state = "⏸ " + Fmt.Time(t.Position);
            if (t.Duration > TimeSpan.Zero) state += " / " + Fmt.Time(t.Duration);
            if (artists.Length > 0) state += " · " + artists;
        }

        var act = new Dictionary<string, object>
        {
            ["type"] = 2,
            ["details"] = Clip(t.Title),
            ["state"] = Clip(state)
        };

        if (t.Playing)
        {
            var ts = new Dictionary<string, object> { ["start"] = start };
            if (t.Duration > TimeSpan.Zero) ts["end"] = start + (long)t.Duration.TotalSeconds;
            act["timestamps"] = ts;
        }

        string? link = t.Url;
        if (coverUrl != null)
        {
            var assets = new Dictionary<string, object> { ["large_image"] = coverUrl };
            if (t.Album.Length > 0) assets["large_text"] = Clip(t.Album);        // альбом: подсказка и третья строка
            if (link != null && degrade < 2) assets["large_url"] = link;          // клик по обложке
            act["assets"] = assets;
        }
        if (link != null && degrade < 2) act["details_url"] = link;
        if (link != null && degrade < 1 && _s.ShowButton)
            act["buttons"] = new[] { new { label = Fmt.ButtonLabel(t.Source), url = link } };

        return act;
    }

    Task? StartResolve(TrackInfo t)
    {
        var stateKey = StateKey(t);
        lock (_cf)
        {
            if (_inflight.Contains(t.Key)) return null;
            if (_cooldown.TryGetValue(t.Key, out var until) && until > DateTime.UtcNow) return null;
            _inflight.Add(t.Key);
        }
        bool covers = _s.Covers;
        return Task.Run(async () =>
        {
            bool ok = false;
            try { ok = await CoverCache.ResolveAsync(t, covers, Schedule); } catch { }
            lock (_cf)
            {
                _inflight.Remove(t.Key);
                if (ok)
                {
                    if (_resolved.Count > 1000) _resolved.Clear();
                    _resolved.Add(stateKey);
                }
                else _cooldown[t.Key] = DateTime.UtcNow.AddMinutes(5);
            }
            Schedule();
        });
    }

    // Повтор подключения к Discord, только пока он недоступен и есть что показывать.
    void StartRetry() => _retry ??= new System.Threading.Timer(_ => Schedule(), null, 10_000, 10_000);
    void StopRetry() { _retry?.Dispose(); _retry = null; }
}

/// <summary>
/// Обложки: сжатый JPEG на диске (для окна) + ссылка для Discord.
/// Сначала берём прямую ссылку из Deezer (ничего не загружаем), иначе загружаем на файлообменник.
/// </summary>
static class CoverCache
{
    internal sealed record Entry(string Url, long Exp);   // Exp = 0: ссылка постоянная

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nowring", "covers");
    static readonly string MapFile = Path.Combine(Dir, "urls2.json");
    static readonly object L = new();
    static readonly Dictionary<string, Entry> Map = LoadMap();
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Nowring/1.0");
        return h;
    }

    static Dictionary<string, Entry> LoadMap()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(MapFile)) ?? new(); }
        catch { return new(); }
    }

    public static string Key(string artist, string title, string album)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes(artist + "\n" + title + "\n" + album));
        return Convert.ToHexString(h)[..20];
    }

    public static string? GetUrl(string key)
    {
        lock (L)
        {
            if (!Map.TryGetValue(key, out var e)) return null;
            if (e.Exp > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > e.Exp) { Map.Remove(key); return null; }
            return e.Url;
        }
    }

    public static string? GetPath(string key)
    {
        var p = Path.Combine(Dir, key + ".jpg");
        return File.Exists(p) ? p : null;
    }

    static void SetUrl(string key, string url, long exp = 0)
    {
        lock (L)
        {
            if (Map.Count > 1000) Map.Clear();
            Map[key] = new Entry(url, exp);
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(MapFile, JsonSerializer.Serialize(Map));
            }
            catch { }
        }
    }

    /// <summary>
    /// Узнаёт данные о треке (артисты, ссылка, обложка), готовит локальную обложку для окна и
    /// ссылку на обложку для Discord. Возвращает false, если обложку для Discord получить не удалось.
    /// </summary>
    public static async Task<bool> ResolveAsync(TrackInfo t, bool covers, Action partial)
    {
        Directory.CreateDirectory(Dir);

        // 1. Deezer + song.link (кэшируется на время работы программы)
        var meta = Lookup.Get(t.Key);
        if (meta == null)
        {
            meta = await Lookup.ResolveAsync(t);
            Lookup.Put(t.Key, meta);
        }

        // 2. прямая ссылка на обложку: Discord показывает её сразу
        if (covers && GetUrl(t.Key) == null && meta.CoverUrl != null) SetUrl(t.Key, meta.CoverUrl);
        partial();

        // 3. локальная копия для окна (из Windows media session)
        var path = Path.Combine(Dir, t.Key + ".jpg");
        if (!File.Exists(path))
        {
            var raw = await t.LoadThumb();
            if (raw != null)
            {
                try { await File.WriteAllBytesAsync(path, Shrink(raw)); Prune(); }
                catch (Exception ex) { Log.Write("Обложка: " + ex.Message); }
            }
        }

        if (!covers || GetUrl(t.Key) != null) return true;

        // 4. нет в Deezer (например, скачанный файл): загружаем настоящую обложку на файлообменник
        if (!File.Exists(path)) { Log.Write("Нет обложки от плеера для: " + t.Title); return false; }
        var up = await UploadAsync(path);
        if (up == null) return false;
        SetUrl(t.Key, up.Value.Url, up.Value.Exp);
        return true;
    }

    static byte[] Shrink(byte[] src)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 300;
        bi.StreamSource = new MemoryStream(src);
        bi.EndInit();
        bi.Freeze();
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bi));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Пробует несколько бесплатных хостингов по очереди. Ошибки пишет в log.txt.</summary>
    static async Task<(string Url, long Exp)?> UploadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var name = Path.GetFileName(path);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var u = await PostTelegraph(bytes, name);
        if (u != null) return (u, 0);

        u = await Post("catbox.moe", "https://catbox.moe/user/api.php", "fileToUpload", bytes, name, ("reqtype", "fileupload"));
        if (u != null) return (u, 0);

        u = await Post("0x0.st", "https://0x0.st", "file", bytes, name);
        if (u != null) return (u, now + 25 * 86400);

        u = await Post("uguu.se", "https://uguu.se/upload?output=text", "files[]", bytes, name);
        if (u != null) return (u, now + 2 * 3600);   // хранят около 3 часов

        return null;
    }

    // telegra.ph: бесплатный хостинг картинок без ключа, Telegram в России обычно доступен
    static async Task<string?> PostTelegraph(byte[] bytes, string name)
    {
        const string host = "telegra.ph";
        if (!Net.IsUp(host)) return null;
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new("image/jpeg");
            form.Add(file, "file", name);
            using var r = await Http.PostAsync("https://telegra.ph/upload", form);
            var body = (await r.Content.ReadAsStringAsync()).Trim();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                && doc.RootElement[0].TryGetProperty("src", out var v))
            {
                var src = v.GetString();
                if (!string.IsNullOrEmpty(src) && src.StartsWith("/")) return "https://telegra.ph" + src;
            }
            Log.Write($"Загрузка обложки, {host}: HTTP {(int)r.StatusCode} {(body.Length > 120 ? body[..120] : body)}");
        }
        catch (Exception ex)
        {
            Net.Fail(host, ex);
        }
        return null;
    }

    static async Task<string?> Post(string host, string url, string field, byte[] bytes, string name,
        params (string, string)[] extra)
    {
        if (!Net.IsUp(host)) return null;
        try
        {
            using var form = new MultipartFormDataContent();
            foreach (var (k, v) in extra) form.Add(new StringContent(v), k);
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new("image/jpeg");
            form.Add(file, field, name);
            using var r = await Http.PostAsync(url, form);
            var s = (await r.Content.ReadAsStringAsync()).Trim();
            if (r.IsSuccessStatusCode && s.StartsWith("http") && !s.Contains('\n'))
                return s.Replace("http://", "https://");
            Log.Write($"Загрузка обложки, {host}: HTTP {(int)r.StatusCode} {(s.Length > 120 ? s[..120] : s)}");
        }
        catch (Exception ex)
        {
            Net.Fail(host, ex);
        }
        return null;
    }

    static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.jpg").OrderBy(f => f.LastWriteTimeUtc).ToList();
            if (files.Count <= 500) return;
            foreach (var f in files.Take(files.Count - 400)) f.Delete();
        }
        catch { }
    }
}
