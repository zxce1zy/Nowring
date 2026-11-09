using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Nowring;

sealed record TrackInfo(
    string Key, string Title, string Artist, string Album, string Source,
    bool Playing, TimeSpan Position, TimeSpan Duration, DateTimeOffset At,
    Func<Task<byte[]?>> LoadThumb,
    string[]? Artists = null, string? Url = null)
{
    /// <summary>Исполнители для окна: «A, B».</summary>
    public string ArtistsDisplay => Artists is { Length: > 0 } a ? string.Join(", ", a) : Artist;

    /// <summary>Исполнители для статуса Discord: «A, B».</summary>
    public string ArtistsStatus => ArtistsDisplay;
}

/// <summary>Слушает Windows media session (SMTC) по событиям, без опроса.</summary>
sealed class MediaWatcher
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    readonly List<Action> _unhooks = new();
    readonly object _lock = new();
    TrackInfo? _lastYt;
    string _lastYtApp = "";   // последнее «настоящее» видео YouTube (не превью при наведении мыши)

    public event Action? Changed;

    public async Task StartAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.SessionsChanged += (_, _) => Resync();
        _mgr.CurrentSessionChanged += (_, _) => Changed?.Invoke();
        Resync();
    }

    void Resync()
    {
        lock (_lock)
        {
            foreach (var u in _unhooks) { try { u(); } catch { } }
            _unhooks.Clear();
            foreach (var s in _mgr!.GetSessions()) Hook(s);
        }
        Changed?.Invoke();
    }

    void Hook(Session s)
    {
        TypedEventHandler<Session, MediaPropertiesChangedEventArgs> a = (_, _) => Changed?.Invoke();
        TypedEventHandler<Session, PlaybackInfoChangedEventArgs> b = (_, _) => Changed?.Invoke();
        TypedEventHandler<Session, TimelinePropertiesChangedEventArgs> c = (_, _) => Changed?.Invoke();
        s.MediaPropertiesChanged += a;
        s.PlaybackInfoChanged += b;
        s.TimelinePropertiesChanged += c;
        _unhooks.Add(() =>
        {
            s.MediaPropertiesChanged -= a;
            s.PlaybackInfoChanged -= b;
            s.TimelinePropertiesChanged -= c;
        });
    }

    static string? Classify(string id)
    {
        var a = id.ToLowerInvariant();
        if (a.Contains("spotify")) return "spotify";
        if (a.Contains("yandex") && a.Contains("music")) return "yandex";
        if (a.Contains("telegram")) return "telegram";
        if (a.Contains("chrome") || a.Contains("msedge") || a.Contains("firefox") || a.Contains("308046b0af4a39cb") || a.Contains("opera") ||
            a.Contains("brave") || a.Contains("vivaldi") || a.Contains("yandex") || a.Contains("browser"))
            return "browser";
        return null;
    }

    bool YtAlive()
    {
        var y = _lastYt!;
        if (y.Playing && y.Duration > TimeSpan.Zero
            && y.Position + (DateTimeOffset.Now - y.At) > y.Duration + TimeSpan.FromSeconds(3)) return false;
        return BrowserSites.HasTab(_lastYtApp, y.Title);
    }

    public async Task<TrackInfo?> SnapshotAsync(Settings st)
    {
        var mgr = _mgr;
        if (mgr == null) return null;

        var cur = mgr.GetCurrentSession()?.SourceAppUserModelId;
        var cands = new List<(Session S, string Src, int Score)>();
        foreach (var s in mgr.GetSessions())
        {
            var src = Classify(s.SourceAppUserModelId);
            if (src == null) continue;
            // браузерная сессия может оказаться любым сайтом: окончательно решаем ниже
            if (src == "browser" ? !(st.Browser || st.Yandex || st.SoundCloud || st.YouTube || st.Telegram)
                                  : !st.IsEnabled(src)) continue;
            var status = s.GetPlaybackInfo().PlaybackStatus;
            int score = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? 3
                      : status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ? 1 : 0;
            if (score == 0) continue;
            if (s.SourceAppUserModelId == cur) score++;
            cands.Add((s, src, score));
        }
        cands.Sort((x, y) => y.Score.CompareTo(x.Score));

        Session? best = null;
        string bestSrc = "", title = "", artist = "", album = "";
        foreach (var (s, src0, _) in cands)
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var ti = props.Title?.Trim() ?? "";
            var ar = (props.Artist ?? "").Replace(" - Topic", "").Trim();
            if (ti.Length == 0) continue;
            var src = src0;
            if (src == "spotify" && (ti == "Advertisement" || ti == "Spotify")) continue;

            if (src == "browser")
            {
                var (site, matched) = BrowserSites.Detect(s.SourceAppUserModelId, ti, ar);
                src = site;
                // YouTube: превью, на которое случайно навели мышь, не открыто во вкладке — его пропускаем,
                // приоритет у большого видео
                if (src == "youtube" && !matched) continue;
            }
            if (!st.IsEnabled(src)) continue;
            // Telegram: только музыка (у голосовых и кружков нет исполнителя)
            if (src == "telegram" && ar.Length == 0) continue;

            best = s; bestSrc = src; title = ti; artist = ar; album = props.AlbumTitle?.Trim() ?? "";
            break;
        }

        // Chrome при наведении мыши на превью подменяет сессию видео и потом может убрать её совсем.
        // Тогда остаётся либо ничего, либо чужая сессия на паузе (например, последний трек Spotify).
        // Пока вкладка с видео открыта и оно не закончилось, держим настоящее видео YouTube.
        // Играющий сейчас другой источник (не на паузе) имеет приоритет.
        bool otherPlaying = best != null && bestSrc != "youtube"
            && best.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (_lastYt != null && st.YouTube && (best == null || (bestSrc != "youtube" && !otherPlaying)) && YtAlive())
            return _lastYt;

        if (best == null)
        {
            _lastYt = null;
            return null;
        }

        var info = best.GetPlaybackInfo();
        var tl = best.GetTimelineProperties();
        bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var dur = tl.EndTime - tl.StartTime;
        if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;
        var pos = tl.Position - tl.StartTime;
        var now = DateTimeOffset.Now;

        if (playing)
        {
            var elapsed = now - tl.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(12))
                pos += elapsed * (info.PlaybackRate ?? 1.0);
        }
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (dur > TimeSpan.Zero && pos > dur) pos = dur;

        var session = best;
        Func<Task<byte[]?>> load = async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    var p = await session.TryGetMediaPropertiesAsync();
                    var r = p.Thumbnail;
                    if (r != null)
                    {
                        using var rs = await r.OpenReadAsync();
                        using var stream = rs.AsStreamForRead();
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        if (ms.Length > 0) return ms.ToArray();
                    }
                }
                catch { }
                await Task.Delay(800);
            }
            return null;
        };

        var result = new TrackInfo(CoverCache.Key(artist, title, album), title, artist, album,
            bestSrc, playing, pos, dur, now, load);
        if (bestSrc == "youtube") { _lastYt = result; _lastYtApp = best.SourceAppUserModelId; }
        else _lastYt = null;
        return result;
    }
}
