using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Nowring;

sealed class Settings
{
    public bool Spotify { get; set; } = true;
    public bool Yandex { get; set; } = true;
    public bool SoundCloud { get; set; } = true;
    public bool YouTube { get; set; } = true;
    public bool Telegram { get; set; } = true;
    /// <summary>Прочие сайты в браузере.</summary>
    public bool Browser { get; set; } = true;
    public bool Covers { get; set; } = true;
    public bool Autostart { get; set; }
    /// <summary>Запускать тексты песен (Lyrics Overlay) вместе с Nowring.</summary>
    public bool LyricsAuto { get; set; }
    public bool Dark { get; set; } = true;

    /// <summary>Проверять обновления на GitHub и скачивать их в фоне.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Акцентный цвет приложения (#RRGGBB).</summary>
    public string Accent { get; set; } = "#14C9A3";

    /// <summary>Показывать статус в Discord (выключатель без выхода из программы).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Кнопка «Открыть в …» в карточке активности.</summary>
    public bool ShowButton { get; set; }

    /// <summary>Маленький значок приложения (Spotify, YouTube…) в углу обложки в Discord.</summary>
    public bool ShowBadge { get; set; }

    /// <summary>Поддержка Discord в браузере: локальный мост для плагина Vencord (arRPC).</summary>
    public bool WebBridge { get; set; }

    /// <summary>Язык интерфейса: auto (как в системе), ru, en.</summary>
    public string Lang { get; set; } = "auto";

    // вид карточки (настройки как в других программах для Discord)
    public bool TitleArtistOneLine { get; set; }
    public bool ArtistAlbumOneLine { get; set; }
    public bool SwapOrder { get; set; }
    public string ArtistPrefix { get; set; } = "";   // "", "от", "by"
    public string AlbumPrefix { get; set; } = "";    // "", "в", "on"
    public bool AlbumNoArtist { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool HideInfo { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ViewSig => $"{TitleArtistOneLine}{ArtistAlbumOneLine}{SwapOrder}{ArtistPrefix}{AlbumPrefix}{AlbumNoArtist}{ShowProgress}{HideInfo}{Lang}";

    /// <summary>Вебхук своего приватного канала Discord: самый надёжный хостинг обложек.</summary>
    public string Webhook { get; set; } = "";

    /// <summary>Показывать исполнителя в карточке Discord.</summary>
    public bool ShowArtist { get; set; } = true;

    /// <summary>Показывать альбом / плейлист (третья строка) в карточке Discord.</summary>
    public bool ShowAlbum { get; set; } = true;

    /// <summary>Вид карточки «Свой статус»: 0 «Играет», 2 «Слушает», 3 «Смотрит».</summary>
    public int CustomType { get; set; } = 3;

    /// <summary>YouTube в Discord как музыка («Слушает», с полосой прогресса), а не «Смотрит».</summary>
    public bool VideoAsMusic { get; set; } = true;

    /// <summary>Показывать YouTube Shorts в статусе (по умолчанию выключено).</summary>
    public bool ShowShorts { get; set; }

    /// <summary>Версия настроек (для одноразовых миграций).</summary>
    public int Ver { get; set; }

    // ---- свой статус
    public bool CustomOn { get; set; }
    public bool CustomOver { get; set; } = true;     // показывать вместо музыки
    public bool CustomTimer { get; set; } = true;    // таймер «прошло времени»
    public string CustomTitle { get; set; } = "";
    public string CustomText { get; set; } = "";
    public string CustomImage { get; set; } = "";    // "tpl:код" или "file:путь"
    public long CustomStart { get; set; }

    /// <summary>Необязательно: свой Application ID (только через settings.json). Обычно пусто.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Где окно стояло в прошлый раз.</summary>
    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }

    /// <summary>Скрыть подсказку про двойную активность Spotify.</summary>
    public bool HideHint { get; set; }

    static bool Valid(string id) => id.Length >= 15 && id.All(char.IsDigit);

    /// <summary>Application ID для источника: свой, если задан в Config.cs, иначе общий.</summary>
    public string ClientIdFor(string source)
    {
        if (!string.IsNullOrWhiteSpace(ClientId)) return ClientId.Trim();
        var id = Config.ForSource(source).Trim();
        return Valid(id) ? id : Config.DefaultClientId;
    }

    [JsonIgnore]
    public bool HasClientId => Valid(string.IsNullOrWhiteSpace(ClientId) ? Config.DefaultClientId : ClientId.Trim());

    public bool IsEnabled(string source) => source switch
    {
        "spotify" => Spotify,
        "yandex" => Yandex,
        "browser" => Browser,
        "soundcloud" => SoundCloud,
        "youtube" => YouTube,
        "telegram" => Telegram,
        _ => false
    };

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nowring");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            if (s.Ver < 2) { s.ShowButton = false; s.Ver = 2; s.Save(); }   // кнопка «Открыть» теперь выключена по умолчанию
            return s;
        }
        catch { return new Settings { Ver = 2 }; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Nowring";

    const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue(ValueName) == null) return false;

            // если пользователь выключил Nowring в Диспетчере задач, Windows его не запустит
            using var ap = Registry.CurrentUser.OpenSubKey(ApprovedPath);
            if (ap?.GetValue(ValueName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1) return false;
            return true;
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
            if (key == null) return;
            if (on && Environment.ProcessPath is { } exe)
            {
                key.SetValue(ValueName, "\"" + exe + "\" --hidden");
                // снимаем отметку «отключено» из Диспетчера задач, иначе запись есть, а запуска нет
                using var ap = Registry.CurrentUser.OpenSubKey(ApprovedPath, true);
                ap?.DeleteValue(ValueName, false);
            }
            else key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}

static class Fmt
{
    public static string Time(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string SourceName(string src) => src switch
    {
        "spotify" => "Spotify",
        "yandex" => Loc.T("Яндекс Музыка"),
        "soundcloud" => "SoundCloud",
        "youtube" => "YouTube",
        "telegram" => "Telegram",
        "custom" => Loc.T("Свой статус"),
        _ => Loc.T("Браузер")
    };

    public static string ButtonLabel(string src) => src switch
    {
        "spotify" => Loc.T("Открыть в Spotify"),
        "yandex" => Loc.T("Открыть в Яндекс Музыке"),
        "soundcloud" => Loc.T("Открыть в SoundCloud"),
        "youtube" => Loc.T("Найти на YouTube"),
        "telegram" => Loc.T("Открыть в Telegram"),
        _ => Loc.T("Открыть трек")
    };
}
