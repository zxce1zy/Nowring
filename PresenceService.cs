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
        CoverCache.Hook = s;
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
    TrackInfo? CustomTrack(bool musicOn)
    {
        if (!_s.CustomOn || (!_s.CustomOver && musicOn)) return null;
        var title = _s.CustomTitle.Trim();
        var text = _s.CustomText.Trim();
        if (title.Length == 0 && text.Length == 0) return null;
        if (title.Length == 0) { title = text; text = ""; }
        var img = _s.CustomImage ?? "";
        var token = img;
        if (img.StartsWith("file:")) { try { token += File.GetLastWriteTimeUtc(img[5..]).Ticks; } catch { } }
        var now = DateTimeOffset.Now;
        var pos = _s.CustomTimer && _s.CustomStart > 0 ? now - DateTimeOffset.FromUnixTimeSeconds(_s.CustomStart) : TimeSpan.Zero;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        return new TrackInfo(CoverCache.Key("custom", token, ""), title, text, "", "custom", true, pos, TimeSpan.Zero, now,
            () => Templates.LoadAsync(img));
    }

    static TrackInfo? Merge(TrackInfo? t)
    {
        if (t == null || t.Source == "custom") return t;
        var m = Lookup.Get(t.Key);
        return m == null ? t : t with { Artists = Lookup.MergeArtists(t.Artist, m.Artists), Url = Lookup.UrlFor(m, t) };
    }

    // Сайт в браузере (Яндекс Музыка / SoundCloud) определяется не сразу: если не нашли,
    // пробуем ещё несколько раз с паузой, пока трек играет.
    bool _bridgeShown;
    int _browserTries;
    string? _browserKey;

    string StateKey(TrackInfo t) => t.Key + (_s.Covers ? "+c" : "");

    async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var music = await _watcher.SnapshotAsync(_s);
            var t = Merge(CustomTrack(music != null) ?? music);
            Current = t;

            if (t != null && t.Key != _browserKey) { _browserKey = t.Key; _browserTries = 0; }
            if (t != null && t.Source == "browser" && _browserTries < 6)
            {
                _browserTries++;
                _ = Task.Delay(3500).ContinueWith(_ => Schedule());
            }

            if (!_s.HasClientId) return;
            if (_s.WebBridge) WebBridge.Start(); else WebBridge.Stop();

            if (!_s.Enabled)
            {
                // статус выключен из трея: закрываем канал (Discord сам уберёт активность)
                StopRetry();
                _lastSig = null;
                _connId = null;
                _ipc.Close();
                WebBridge.Clear();
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
                if (_bridgeShown) { _bridgeShown = false; WebBridge.Clear(); }
                return;
            }

            string? url = _s.Covers ? CoverCache.GetUrl(t.Key) : null;
            bool need;
            lock (_cf) need = !_resolved.Contains(StateKey(t)) || (_s.Covers && url == null && t.Source != "custom");
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
            string sig = $"{t.Key}|{t.Title}|{t.Source}|{t.Playing}|{url}|{t.ArtistsStatus}|{t.Url}|{_degrade}|{_s.ShowButton}|{_s.ShowBadge}|{_s.ShowArtist}|{_s.WebBridge}|{_s.ShowAlbum}|{_s.CustomType}|{_s.VideoAsMusic}|{_s.ViewSig}|{(t.Playing ? "" : ((int)t.Position.TotalSeconds).ToString())}";
            if (sig == _lastSig && (!t.Playing || Math.Abs(start - _lastStart) <= 2)) return;

            // у каждого источника своё приложение Discord (от этого зависит «Слушает Spotify»)
            var cid = _s.ClientIdFor(t.Source);
            bool ipcOk = true;
            if (!_ipc.Connected || _connId != cid)
            {
                if (await _ipc.ConnectAsync(cid)) { _connId = cid; _lastSig = null; }
                else { _connId = null; ipcOk = false; }
            }
            if (!ipcOk && !_s.WebBridge) { StartRetry(); return; }

            var info = await AppIcons.InfoAsync(cid);
            string? badge = null;
            if (_s.ShowBadge && t.Source != "custom") badge = info.IconUrl ?? "badge";
            var act = Build(t, url, start, badge);
            if (ipcOk) await _ipc.SetActivityAsync(act);
            if (_s.WebBridge)
            {
                WebBridge.Publish(cid, info.Name ?? Fmt.SourceName(t.Source), act);
                _bridgeShown = true;
            }

            // Discord на компьютере недоступен, но работает мост для браузера: пробуем подключиться к нему позже
            _lastSig = ipcOk ? sig : null;
            _lastStart = start;
            if (ipcOk) StopRetry(); else StartRetry();
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

    static string J(params string[] p) => string.Join(" — ", p.Where(x => x.Length > 0));

    object Build(TrackInfo t, string? coverUrl, long start, string? badge)
    {
        int degrade = _degradeKey == t.Key ? _degrade : 0;
        bool custom = t.Source == "custom";
        string artists = _s.ShowArtist || custom ? t.ArtistsStatus : "";
        string source = Fmt.SourceName(t.Source);
        bool hide = _s.HideInfo && !custom;

        string details = t.Title, state;
        string al = "";
        bool albumInState = false;
        if (custom)
            state = artists;
        else if (hide)
        {
            details = Loc.T("Слушает музыку");
            state = source;
        }
        else
        {
            string a = artists.Length > 0 && _s.ArtistPrefix.Length > 0 ? _s.ArtistPrefix + " " + artists : artists;
            if (t.Album.Length > 0 && _s.ShowAlbum) al = (_s.AlbumPrefix.Length > 0 ? _s.AlbumPrefix + " " : "") + t.Album;
            if (_s.TitleArtistOneLine)
            {
                details = _s.SwapOrder ? J(a, t.Title) : J(t.Title, a);
                state = al;
                albumInState = al.Length > 0;
            }
            else if (_s.ArtistAlbumOneLine)
            {
                state = J(a, al);
                albumInState = al.Length > 0;
                if (_s.SwapOrder && state.Length > 0) { details = state; state = t.Title; }
            }
            else
            {
                state = a;
                if (state.Length == 0 && al.Length > 0 && _s.AlbumNoArtist) { state = al; albumInState = true; }
                else if (state.Length == 0 && _s.ShowArtist) state = source;
                if (_s.SwapOrder && a.Length > 0) (details, state) = (state, details);
            }
            if (state.Length == 0 && _s.ShowArtist) state = source;
        }

        if (!t.Playing)
        {
            albumInState = false;
            state = "⏸ " + Fmt.Time(t.Position);
            if (t.Duration > TimeSpan.Zero) state += " / " + Fmt.Time(t.Duration);
            if (artists.Length > 0 && !hide) state += " · " + artists;
        }

        var act = new Dictionary<string, object>
        {
            ["type"] = custom ? (_s.CustomType is 0 or 2 or 3 ? _s.CustomType : 3) : !_s.VideoAsMusic && t.Source == "youtube" ? 3 : 2,
            ["details"] = Clip(details),
            ["state"] = Clip(state)
        };

        if (t.Playing && (custom ? _s.CustomTimer : _s.ShowProgress))
        {
            var ts = new Dictionary<string, object> { ["start"] = start };
            if (t.Duration > TimeSpan.Zero) ts["end"] = start + (long)t.Duration.TotalSeconds;
            act["timestamps"] = ts;
        }

        string? link = hide ? null : t.Url;
        if (link != null && link.Length > 250) link = null;   // Discord отклоняет ссылки длиннее 256 знаков
        // нет обложки: стандартная картинка (только для музыки/видео при включённых обложках)
        string? large = hide ? null : coverUrl ?? (_s.Covers && t.Source != "custom" ? Config.DefaultCoverUrl : null);
        if (large != null)
        {
            var assets = new Dictionary<string, object> { ["large_image"] = large };
            // значок приложения в углу обложки: иконка приложения Discord (или картинка "badge" из Art Assets)
            if (badge != null)
            {
                assets["small_image"] = badge;
                assets["small_text"] = Fmt.SourceName(t.Source);
            }
            if (!albumInState && (custom ? t.Album.Length > 0 : al.Length > 0)) assets["large_text"] = Clip(custom ? t.Album : al);        // альбом: подсказка и третья строка
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
                    // обложки нет нигде: не гоняем поиск по кругу, повторим через пару минут
                    if (covers && t.Source != "custom" && CoverCache.GetUrl(t.Key) == null)
                        _cooldown[t.Key] = DateTime.UtcNow.AddMinutes(2);
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
    public static Settings? Hook;
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

    // Для одного трека (ключ артист+название+альбом) обложка на устройстве может быть разной:
    // скачанный файл с Spotify-названием, но со своей картинкой. Поэтому обложки хранятся по
    // «id обложки» = ключ трека + отпечаток картинки с устройства; Latest помнит актуальный id.
    static readonly Dictionary<string, string> Latest = new();
    static readonly Dictionary<string, bool> Custom = new();

    static string? UrlOf(string id)
    {
        lock (L)
        {
            if (!Map.TryGetValue(id, out var e)) return null;
            if (e.Exp > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > e.Exp) { Map.Remove(id); return null; }
            return e.Url;
        }
    }

    public static string? GetUrl(string key)
    {
        string? id;
        lock (L) Latest.TryGetValue(key, out id);
        return id == null ? null : UrlOf(id);
    }

    public static string? GetPath(string key)
    {
        string? id;
        lock (L) Latest.TryGetValue(key, out id);
        if (id == null) return null;
        var p = Path.Combine(Dir, id + ".jpg");
        return File.Exists(p) ? p : null;
    }

    static void SetUrl(string key, string url, long exp = 0)
    {
        Log.Write("Обложка для Discord: " + (url.Length > 200 ? url[..200] + "…" : url) + " (" + url.Length + ")");
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
        string id = t.Key;
        bool hasDev = false;
        ulong devHash = 0;

        // поиск в каталогах идёт параллельно с чтением обложки с устройства
        var metaCached = Lookup.Get(t.Key);
        Task<TrackMeta>? metaTask = metaCached == null && t.Source is not ("youtube" or "custom") ? Task.Run(() => Lookup.ResolveAsync(t)) : null;

        // 1. Обложка прямо с устройства (из плеера/файла): у неё приоритет над каталогами
        byte[]? raw = null;
        try { raw = await t.LoadThumb(); } catch { }
        if (raw != null)
        {
            try
            {
                var jpg = Shrink(raw);
                devHash = AHash(jpg);
                hasDev = true;
                id = t.Key + "_" + devHash.ToString("x16")[..10];
                var p = Path.Combine(Dir, id + ".jpg");
                if (!File.Exists(p)) await File.WriteAllBytesAsync(p, jpg);
                Prune();
            }
            catch (Exception ex)
            {
                Log.Write("Обложка с устройства: " + ex.Message);
                id = t.Key;
                hasDev = false;
            }
        }
        lock (L)
        {
            if (Latest.Count > 1000) Latest.Clear();
            Latest[t.Key] = id;
        }
        var path = Path.Combine(Dir, id + ".jpg");

        // 2. Данные о треке (все исполнители, ссылки) ищем всегда, даже если обложка уже есть:
        // плеер отдаёт только первого исполнителя, а фиты видны только в каталогах
        var meta = metaCached;
        if (meta == null)
        {
            // видео (YouTube) и свой статус не ищем в музыкальных каталогах
            meta = metaTask == null ? TrackMeta.Empty : await metaTask;
            Lookup.Put(t.Key, meta);
        }
        partial();   // исполнители уже можно показывать

        // 3. Обложка для Discord: с устройства, иначе из каталога.
        // Если картинка на устройстве заметно отличается от каталожной (например, свой скачанный трек
        // с тем же названием), каталожную не подставляем: лучше стандартная картинка, чем чужая обложка.
        if (covers && UrlOf(id) == null)
        {
            // обложка из каталога (Deezer и т.п.) Discord показывает надёжно: берём её, если она совпадает с картинкой на устройстве
            bool custom = meta.CoverUrl != null && hasDev && await IsCustomArt(id, devHash, meta.CoverUrl);
            if (meta.CoverUrl != null && !custom) SetUrl(id, meta.CoverUrl);
            else
            {
                if (File.Exists(path))
                {
                    var up = await UploadAsync(path);
                    if (up != null) SetUrl(id, up.Value.Url, up.Value.Exp);
                }
                if (UrlOf(id) == null && meta.CoverUrl != null) SetUrl(id, meta.CoverUrl);   // лучше каталожная, чем «?»
            }
        }

        partial();
        return true;
    }

    /// <summary>Отличается ли картинка с устройства от обложки из каталога (по грубому отпечатку 8x8).</summary>
    static async Task<bool> IsCustomArt(string id, ulong dev, string catalogUrl)
    {
        lock (L) if (Custom.TryGetValue(id, out var known)) return known;
        bool differs;
        try
        {
            var bytes = await Http.GetByteArrayAsync(catalogUrl);
            differs = System.Numerics.BitOperations.PopCount(dev ^ AHash(bytes)) > 10;
        }
        catch { return false; }
        lock (L)
        {
            if (Custom.Count > 500) Custom.Clear();
            Custom[id] = differs;
        }
        return differs;
    }

    /// <summary>Отпечаток картинки: 64 бита по средней яркости клеток 8x8 (устойчив к пережатию).</summary>
    static ulong AHash(byte[] img)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 8;
        bi.DecodePixelHeight = 8;
        bi.StreamSource = new MemoryStream(img);
        bi.EndInit();
        var g = new FormatConvertedBitmap(bi, System.Windows.Media.PixelFormats.Gray8, null, 0);
        var px = new byte[64];
        g.CopyPixels(px, 8, 0);
        double avg = px.Average(b => (double)b);
        ulong bits = 0;
        for (int i = 0; i < 64; i++) if (px[i] > avg) bits |= 1UL << i;
        return bits;
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

        var u = await PostWebhook(bytes, name);
        if (u != null) return (u, now + 20 * 3600);   // ссылки вебхука подписаны и живут около суток

        // бесплатные хостинги пробуем одновременно: берём того, кто ответил первым
        var jobs = new List<Task<(string? Url, long Exp)>>
        {
            Task.Run(async () => (await PostTelegraph(bytes, name), 0L)),
            Task.Run(async () => (await Post("catbox.moe", "https://catbox.moe/user/api.php", "fileToUpload", bytes, name, ("reqtype", "fileupload")), 0L)),
            Task.Run(async () => (await Post("litterbox.catbox.moe", "https://litterbox.catbox.moe/resources/internals/api.php", "fileToUpload", bytes, name,
                ("reqtype", "fileupload"), ("time", "72h")), now + 70 * 3600)),
            Task.Run(async () => (await Post("0x0.st", "https://0x0.st", "file", bytes, name), now + 25 * 86400)),
        };
        var limit = Task.Delay(8000);
        while (jobs.Count > 0)
        {
            var done = await Task.WhenAny(jobs.Cast<Task>().Append(limit));
            if (done == limit) break;
            var j = (Task<(string? Url, long Exp)>)done;
            jobs.Remove(j);
            if (j.IsCompletedSuccessfully && j.Result.Url != null) return (j.Result.Url, j.Result.Exp);
        }
        return null;
    }

    // вебхук своего канала Discord: картинка остаётся на cdn.discordapp.com
    static async Task<string?> PostWebhook(byte[] bytes, string name)
    {
        var hook = Hook?.Webhook;
        if (string.IsNullOrWhiteSpace(hook) || !hook.StartsWith("https://") || !hook.Contains("/api/webhooks/")) return null;
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new("image/jpeg");
            form.Add(file, "files[0]", "c" + (uint)name.GetHashCode() + ".jpg");
            form.Add(new StringContent("{\"content\":\"\"}"), "payload_json");
            using var r = await Http.PostAsync(hook + (hook.Contains('?') ? "&" : "?") + "wait=true", form);
            var body = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("attachments", out var a) && a.GetArrayLength() > 0
                && a[0].TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
            {
                Log.Write("Обложка загружена через вебхук");
                return url;
            }
            Log.Write($"Загрузка обложки, вебхук: HTTP {(int)r.StatusCode} {(body.Length > 120 ? body[..120] : body)}");
        }
        catch (Exception ex) { Log.Write("Загрузка обложки, вебхук: " + ex.Message); }
        return null;
    }

    // tmpfiles.org: бесплатный хостинг без ключа, файлы живут около часа
    static async Task<string?> PostTmpFiles(byte[] bytes, string name)
    {
        const string host = "tmpfiles.org";
        if (!Net.IsUp(host)) return null;
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new("image/jpeg");
            form.Add(file, "file", name);
            using var r = await Http.PostAsync("https://tmpfiles.org/api/v1/upload", form);
            var body = (await r.Content.ReadAsStringAsync()).Trim();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("url", out var u))
            {
                var url = u.GetString();
                if (!string.IsNullOrEmpty(url) && url.Contains("tmpfiles.org/"))
                    return url.Replace("http://", "https://").Replace("tmpfiles.org/", "tmpfiles.org/dl/");
            }
            Log.Write($"Загрузка обложки, {host}: HTTP {(int)r.StatusCode} {(body.Length > 120 ? body[..120] : body)}");
        }
        catch (Exception ex)
        {
            Net.Fail(host, ex);
        }
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
            Net.Fail(host, $"HTTP {(int)r.StatusCode} {(body.Length > 100 ? body[..100] : body)}");
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
            Net.Fail(host, $"HTTP {(int)r.StatusCode} {(s.Length > 100 ? s[..100] : s)}");
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
