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
        if (a.Contains("chrome") || a.Contains("msedge") || a.Contains("firefox") || a.Contains("308046b0af4a39cb") || a.Contains("opera") ||
            a.Contains("brave") || a.Contains("vivaldi") || a.Contains("yandex") || a.Contains("browser"))
            return "browser";
        return null;
    }

    public async Task<TrackInfo?> SnapshotAsync(Settings st)
    {
        var mgr = _mgr;
        if (mgr == null) return null;

        Session? best = null;
        string bestSrc = "";
        int bestScore = 0;
        var cur = mgr.GetCurrentSession()?.SourceAppUserModelId;

        foreach (var s in mgr.GetSessions())
        {
            var src = Classify(s.SourceAppUserModelId);
            if (src == null) continue;
            // браузерная сессия может оказаться Яндекс Музыкой или SoundCloud: окончательно решаем ниже
            if (src == "browser" ? !(st.Browser || st.Yandex) : !st.IsEnabled(src)) continue;
            var status = s.GetPlaybackInfo().PlaybackStatus;
            int score = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? 3
                      : status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ? 1 : 0;
            if (score == 0) continue;
            if (s.SourceAppUserModelId == cur) score++;
            if (score > bestScore) { best = s; bestSrc = src; bestScore = score; }
        }
        if (best == null) return null;

        var props = await best.TryGetMediaPropertiesAsync();
        var title = props.Title?.Trim() ?? "";
        var artist = (props.Artist ?? "").Replace(" - Topic", "").Trim();
        var album = props.AlbumTitle?.Trim() ?? "";
        if (title.Length == 0) return null;
        if (bestSrc == "spotify" && (title == "Advertisement" || title == "Spotify")) return null;

        if (bestSrc == "browser")
        {
            bestSrc = BrowserSites.Detect(best.SourceAppUserModelId, title, artist);
            if (bestSrc == "yandex" ? !st.Yandex : !st.Browser) return null;
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

        return new TrackInfo(CoverCache.Key(artist, title, album), title, artist, album,
            bestSrc, playing, pos, dur, now, load);
    }
}
