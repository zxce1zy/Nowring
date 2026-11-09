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

    // YouTube / Telegram: приложения Discord с такими же названиями.
    public const string YouTubeClientId = "1568775465101959260";
    public const string TelegramClientId = "1568775817691922543";

    // «Свой статус» (Пишу код, Играю…): Discord пишет в заголовке карточки название приложения
    // («Играет в <название>»). Создайте отдельное приложение, например «Сейчас» или «Мой статус»,
    // и вставьте его Application ID. Пока пусто, используется DefaultClientId («Nowring»).
    public const string CustomClientId = "1569072090936508570";

    // Картинка, когда у трека нет обложки (лежит в репозитории: assets/default-cover.png).
    public const string DefaultCoverUrl = "https://raw.githubusercontent.com/zxce1zy/Nowring/main/assets/default-cover.png";

    public const string GitHubUrl = "https://github.com/zxce1zy";

    public static string ForSource(string source) => source switch
    {
        "spotify" => SpotifyClientId,
        "yandex" => YandexClientId,
        "soundcloud" => SoundCloudClientId,
        "youtube" => YouTubeClientId,
        "telegram" => TelegramClientId,
        "custom" => CustomClientId,
        _ => ""
    };
}
