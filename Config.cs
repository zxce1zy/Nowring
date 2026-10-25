namespace Nowring;

static class Config
{
    // Application ID по умолчанию (из https://discord.com/developers/applications → General Information).
    // Это не секрет: он одинаковый у всех копий программы. Используется, когда для источника
    // ниже ID не задан, а также для «прочих» сайтов в браузере (например, YouTube).
    public const string DefaultClientId = "1563929328499040256";

    // Отдельные приложения для каждого источника. Название приложения Discord показывает в
    // заголовке «Слушает <название>», поэтому создайте приложения с названиями
    // «Spotify», «Яндекс Музыка», «SoundCloud» и вставьте сюда их Application ID.
    // Пустая строка = использовать DefaultClientId.
    public const string SpotifyClientId = "1563946127697514616";
    public const string YandexClientId = "1563946352436580473";
    public const string SoundCloudClientId = "1563946689797029959";

    public const string GitHubUrl = "https://github.com/zxce1zy";

    public static string ForSource(string source) => source switch
    {
        "spotify" => SpotifyClientId,
        "yandex" => YandexClientId,
        "soundcloud" => SoundCloudClientId,
        _ => ""
    };
}
