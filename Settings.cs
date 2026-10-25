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
    public bool Browser { get; set; } = true;
    public bool Covers { get; set; } = true;
    public bool Autostart { get; set; }
    public bool Dark { get; set; } = true;

    /// <summary>Показывать статус в Discord (выключатель без выхода из программы).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Кнопка «Открыть в …» в карточке активности.</summary>
    public bool ShowButton { get; set; } = true;

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
        "soundcloud" => Browser,
        _ => false
    };

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nowring");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
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
        "yandex" => "Яндекс Музыка",
        "soundcloud" => "SoundCloud",
        _ => "Браузер"
    };

    public static string ButtonLabel(string src) => src switch
    {
        "spotify" => "Открыть в Spotify",
        "yandex" => "Открыть в Яндекс Музыке",
        "soundcloud" => "Открыть в SoundCloud",
        _ => "Открыть трек"
    };
}
