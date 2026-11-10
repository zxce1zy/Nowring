using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Nowring;

/// <summary>Язык интерфейса: «auto» (как в системе), «ru» или «en». Исходные тексты русские, T() переводит их.</summary>
static class Loc
{
    static string _lang = "ru";
    public static bool En => _lang == "en";
    public static event Action? Changed;
    public static void Notify() => Changed?.Invoke();

    public static void Set(string? setting)
    {
        if (setting is "ru" or "en") { _lang = setting; return; }
        string sys;
        try { sys = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName; } catch { sys = "en"; }
        _lang = sys is "ru" or "uk" or "be" or "kk" ? "ru" : "en";
    }

    public static string T(string ru) => En && Map.TryGetValue(ru, out var en) ? en : ru;

    static readonly Regex Cyr = new("[А-Яа-я]", RegexOptions.Compiled);
    static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, string>> Orig = new();

    /// <summary>Переводит все тексты в дереве окна (и возвращает обратно при переключении).</summary>
    public static void Apply(DependencyObject root)
    {
        Fix(root);
        foreach (var o in LogicalTreeHelper.GetChildren(root))
            if (o is DependencyObject d) Apply(d);
    }

    static void Fix(DependencyObject d)
    {
        if (d is TextBlock tb) Set(d, "T", tb.Text, v => tb.Text = v);
        if (d is ContentControl { Content: string c } cc) Set(d, "C", c, v => cc.Content = v);
        if (d is FrameworkElement { ToolTip: string tt } fe) Set(d, "P", tt, v => fe.ToolTip = v);
        if (d is Window w) Set(d, "W", w.Title, v => w.Title = v);
    }

    static void Set(DependencyObject d, string kind, string cur, Action<string> put)
    {
        var tbl = Orig.GetOrCreateValue(d);
        if (!tbl.TryGetValue(kind, out var ru))
        {
            if (!Cyr.IsMatch(cur) && !Map.ContainsKey(cur)) return;
            tbl[kind] = ru = cur;
        }
        else if (cur != ru && cur != T(ru))
        {
            // текст поменяли из кода: считаем новый текст исходным
            if (!Cyr.IsMatch(cur) && !Map.ContainsKey(cur)) { tbl.Remove(kind); return; }
            tbl[kind] = ru = cur;
        }
        var want = T(ru);
        if (want != cur) put(want);
    }

    static readonly Dictionary<string, string> Map = new()
    {
        // вкладки и разделы
        ["Главная"] = "Home", ["Источники"] = "Sources", ["Интерфейс"] = "Interface", ["Активность"] = "Activity",
        ["Тексты"] = "Lyrics", ["ТЕКСТЫ ПЕСЕН НА ЭКРАНЕ"] = "ON-SCREEN LYRICS", ["УПРАВЛЕНИЕ"] = "CONTROLS",
        ["Показывать текст песни поверх экрана"] = "Show song lyrics on screen", ["Запускать вместе с Nowring"] = "Start with Nowring",
        ["Работает: окно с текстом на экране"] = "Running: lyrics window is on screen", ["Выключено"] = "Off",
        ["Запускаю…"] = "Starting…", ["Закрываю…"] = "Closing…",
        ["Не удалось запустить (подробности в журнале ошибок)"] = "Could not start (see the error log)",
        ["Текст идёт в такт песне, которая играет сейчас (Spotify, Яндекс Музыка, SoundCloud и др.).\n✥ — перетащить окно, ☰ — настройки (размер, цвет, выравнивание, синхронизация), ✕ — закрыть.\nCtrl + колесо мыши — размер текста, правая кнопка мыши — меню.\nПриложение Lyrics Overlay сделано другом, внутри Nowring оно работает как отдельное окно."]
            = "Lyrics scroll in time with the song that is playing now (Spotify, Yandex Music, SoundCloud, etc.).\n✥ moves the window, ☰ opens settings (size, color, alignment, sync), ✕ closes it.\nCtrl + mouse wheel changes the text size, right click opens the menu.\nLyrics Overlay was made by a friend; inside Nowring it runs as a separate window.",
        ["Свой статус"] = "Custom status", ["О программе"] = "About",
        ["Свернуть"] = "Minimize", ["Закрыть"] = "Close", ["Справка / Отзыв"] = "Help / Feedback",
        ["МУЗЫКА"] = "MUSIC", ["ВИДЕО И МЕССЕНДЖЕРЫ"] = "VIDEO AND MESSENGERS", ["ВНЕШНИЙ ВИД"] = "APPEARANCE",
        ["АКЦЕНТНЫЙ ЦВЕТ"] = "ACCENT COLOR", ["ПРИЛОЖЕНИЕ"] = "APPLICATION", ["ЯЗЫК"] = "LANGUAGE",
        ["КАРТОЧКА"] = "CARD", ["СВОЙ СТАТУС"] = "CUSTOM STATUS", ["ШАБЛОНЫ"] = "TEMPLATES", ["ОБЛОЖКА"] = "COVER",
        ["Ничего не играет"] = "Nothing is playing",
        // источники
        ["Яндекс Музыка"] = "Yandex Music", ["Telegram (только музыка)"] = "Telegram (music only)",
        ["Браузер (прочие сайты)"] = "Browser (other sites)", ["Браузер"] = "Browser",
        // активность
        ["Статус активности в Discord"] = "Activity status in Discord", ["Показывать обложки"] = "Show covers",
        ["YouTube как музыка (с полосой прогресса)"] = "YouTube as music (with progress bar)",
        ["Кнопка «Открыть в…» в карточке"] = "“Open in…” button on the card",
        ["Discord в браузере (нужен Vencord, см. README)"] = "Discord in browser (needs Vencord, see README)",
        ["Показывать исполнителя"] = "Show artist", ["Показывать альбом / плейлист"] = "Show album / playlist",
        ["Значок приложения на обложке (Spotify, YouTube…)"] = "App badge on the cover (Spotify, YouTube…)",
        ["Вебхук для обложек (необязательно, см. README)"] = "Cover webhook (optional, see README)",
        ["Название и исполнитель в одной строке"] = "Title and artist on one line",
        ["Исполнитель и альбом в одной строке"] = "Artist and album on one line",
        ["Поменять местами название и исполнителя"] = "Swap title and artist",
        ["Приставка к исполнителю"] = "Artist prefix", ["Приставка к альбому"] = "Album prefix", ["Нет"] = "None",
        ["Показывать альбом, когда исполнителя нет"] = "Show album when the artist is missing",
        ["Показывать время воспроизведения"] = "Show playback time",
        ["Не показывать никакой информации о песне"] = "Don't show any song info",
        // интерфейс
        ["Тёмная тема"] = "Dark theme",
        ["Переключатели, вкладки и фон на главной. «Авто»: цвет подстраивается под играющий трек"] = "Switches, tabs and the home background. “Auto”: the colour follows the playing track",
        ["Авто: под цвет трека"] = "Auto: match the track", ["Бирюзовый"] = "Teal", ["Синий"] = "Blue", ["Зелёный"] = "Green",
        ["Красный"] = "Red", ["Оранжевый"] = "Orange", ["Жёлтый"] = "Yellow", ["Розовый"] = "Pink", ["Фиолетовый"] = "Purple",
        ["Автоматический (как в системе)"] = "Automatic (system)", ["Авто"] = "Auto", ["Русский"] = "Русский",
        ["Запускать вместе с Windows"] = "Start with Windows", ["Автоматически искать обновления"] = "Check for updates automatically",
        ["Проверить обновления"] = "Check for updates", ["Выйти из приложения"] = "Quit the app",
        // свой статус
        ["Включить свой статус"] = "Enable custom status", ["Показывать вместо музыки"] = "Show instead of music",
        ["Показывать таймер"] = "Show timer", ["Что делаешь"] = "What you're doing", ["Описание"] = "Description",
        ["Выбрать картинку…"] = "Choose image…", ["Убрать"] = "Remove", ["Вид карточки в Discord"] = "Card style in Discord",
        ["Играет"] = "Playing", ["Слушает"] = "Listening", ["Смотрит"] = "Watching",
        ["Если в Discord видны две активности: Настройки → Подключения → Spotify → выключите «Показывать Spotify как статус». (Нажмите, чтобы скрыть.)"]
            = "If Discord shows two activities: Settings → Connections → Spotify → turn off “Display Spotify as your status”. (Click to hide.)",
        // о программе / обновления
        ["Версия 3.0.0 · 2026"] = "Version 3.0.0 · 2026", ["Обновить"] = "Update", ["Скачать"] = "Download", ["Скачивается…"] = "Downloading…",
        ["Доступна версия "] = "Version available: ", ["Обновить до "] = "Update to ", [" (открыть)"] = " (open)",
        ["Проверяю…"] = "Checking…", ["Не удалось узнать последнюю версию"] = "Couldn't get the latest version",
        ["У вас последняя версия "] = "You have the latest version ", ["Нет связи с GitHub"] = "Can't reach GitHub",
        ["Скачиваю версию "] = "Downloading version ", ["Версия "] = "Version ", [" скачана и готова к установке"] = " downloaded and ready to install",
        ["Файл обновления повреждён (контрольная сумма), попробую позже"] = "The update file is corrupted (checksum), will retry later",
        ["🐞  Сообщить о проблеме"] = "🐞  Report a problem", ["💡  Предложить идею"] = "💡  Suggest an idea",
        ["📄  Открыть журнал ошибок"] = "📄  Open the error log", ["📋  Скопировать данные для отчёта"] = "📋  Copy report details",
        ["⭐  Страница на GitHub"] = "⭐  GitHub page",
        // статус подключения, трей
        ["не задан Application ID (Config.cs)"] = "Application ID is not set (Config.cs)", ["статус выключен"] = "status is off",
        ["Discord подключён"] = "Discord connected", ["Discord не найден"] = "Discord not found",
        ["Открыть"] = "Open", ["Журнал ошибок"] = "Error log", ["Выйти"] = "Quit", ["Статус активности"] = "Activity status",
        ["Nowring (статус выключен)"] = "Nowring (status is off)", ["Картинка для статуса"] = "Status image",
        ["Изображения"] = "Images", ["Слушает музыку"] = "Listening to music",
        // кнопки в Discord
        ["Открыть в Spotify"] = "Open in Spotify", ["Открыть в Яндекс Музыке"] = "Open in Yandex Music",
        ["Открыть в SoundCloud"] = "Open in SoundCloud", ["Найти на YouTube"] = "Find on YouTube",
        ["Открыть в Telegram"] = "Open in Telegram", ["Открыть трек"] = "Open track",
        // шаблоны
        ["Пишу код"] = "Coding", ["Сижу в редакторе"] = "In the editor", ["Играю"] = "Gaming", ["Катаю с друзьями"] = "Playing with friends",
        ["Учусь"] = "Studying", ["Грызу гранит науки"] = "Hitting the books", ["Смотрю кино"] = "Watching a movie",
        ["Вечер с фильмом"] = "Movie night", ["Рисую"] = "Drawing", ["Творю красоту"] = "Making art", ["Работаю"] = "Working",
        ["Не беспокоить"] = "Do not disturb", ["Стримлю"] = "Streaming", ["Заходи в эфир"] = "Come to the stream",
        ["Читаю"] = "Reading", ["Погружаюсь в книгу"] = "Lost in a book", ["Тренируюсь"] = "Working out", ["Качаю железо"] = "Lifting",
        ["Сплю"] = "Sleeping", ["Не будить"] = "Do not wake",
    };
}
