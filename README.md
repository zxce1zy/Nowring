<p align="center">
  <img src="assets/logo.png" width="112" alt="Nowring">
</p>

<h1 align="center">Nowring</h1>

<p align="center">
  Музыка из Spotify, Яндекс Музыки и SoundCloud в статусе Discord.<br>
  Лёгкая программа для Windows: живёт в трее и почти не занимает память.
</p>

<p align="center">
  <a href="https://github.com/zxce1zy/Nowring/releases/latest"><img alt="release" src="https://img.shields.io/github/v/release/zxce1zy/Nowring?color=5865F2"></a>
  <a href="LICENSE"><img alt="license" src="https://img.shields.io/badge/license-MIT-5865F2"></a>
  <img alt="platform" src="https://img.shields.io/badge/Windows-10%20%2F%2011-5865F2">
</p>

<!-- Добавьте сюда скриншоты: карточка в Discord и окно программы -->

## Возможности

- Статус «Слушает Spotify» / «Слушает Яндекс Музыка» / «Слушает SoundCloud» с названием, всеми исполнителями (фиты), обложкой и полоской прогресса.
- Работает **по событиям** Windows (Windows media session), без опроса: пауза, перемотка и смена трека обновляются сразу.
- На паузе статус не пропадает, а показывает «⏸ 1:23 / 3:45».
- Кнопка «Открыть в Spotify / Яндекс Музыке / SoundCloud» и ссылка на трек в карточке.
- Обложки находятся автоматически, в том числе без VPN, и запоминаются: при повторном прослушивании всё готово сразу.
- Иконка в трее, тёмная и светлая темы, переключатель статуса без выхода из программы, автозапуск.
- Окно создаётся только при открытии и выгружается при закрытии, поэтому в фоне программа почти не занимает оперативную память.

## Установка

1. Откройте [**Releases**](https://github.com/zxce1zy/Nowring/releases/latest).
2. Скачайте **`Nowring-Setup-x.y.z.exe`**, запустите и следуйте мастеру. На странице выбора папки можно указать любой путь. По умолчанию программа ставится только для вас и не требует прав администратора.
3. Или скачайте **`Nowring-portable.zip`**: это портативная версия, которую не нужно устанавливать. Распакуйте архив в любую папку (все файлы вместе) и запустите `Nowring.exe`.

Требования: Windows 10 (1903 и новее) или Windows 11, 64-bit, **приложение Discord** на этом же компьютере (в браузерной версии статус не работает). Устанавливать .NET отдельно не нужно.

В Discord должна быть включена опция: *Настройки → Конфиденциальность активности → Показывать текущую активность как статус*.

> **Windows защитил ваш компьютер.** Файлы пока не подписаны сертификатом, поэтому SmartScreen может показать предупреждение: нажмите «Подробнее» → «Выполнить в любом случае». Контрольные суммы лежат в `SHA256SUMS.txt` рядом с файлами релиза, а исходный код открыт.

## Если в Discord две активности

Родную карточку Spotify показывает сам Discord. Чтобы осталась только карточка Nowring, выключите в Discord: *Настройки → Подключения → Spotify → «Показывать Spotify как статус»*. Из программы эту настройку изменить нельзя.

## Источники

| Источник | Как определяется |
|---|---|
| Spotify (приложение) | Windows media session |
| Яндекс Музыка (приложение) | Windows media session |
| Яндекс Музыка и SoundCloud в браузере | Windows media session, сайт определяется по названиям вкладок браузера (локально, ничего не отправляется) |
| Другое аудио в браузере (YouTube и т. д.) | включается переключателем «Браузер», показывается под общим названием |

Каждый источник включается и выключается в окне программы.

## Какие данные уходят в интернет

Для каждого нового трека программа ищет его в открытых сервисах без ключей. Отправляются только название трека и исполнитель.

| Сервис | Зачем |
|---|---|
| Яндекс Музыка (`api.music.yandex.net`) | обложка, все исполнители, ссылка на трек |
| Deezer (`api.deezer.com`) и song.link (`api.song.link`) | обложка, исполнители, ссылка на трек в Spotify и других сервисах |
| iTunes (`itunes.apple.com`) | запасной источник обложки |
| telegra.ph, catbox.moe, 0x0.st, uguu.se | только если трека нет в каталогах (например, скачанный файл): обложка из плеера один раз загружается на открытый хостинг, ссылку получает Discord |

Обложки можно отключить переключателем «Показывать обложки». Всё, что программа узнала, хранится локально:

- настройки: `%APPDATA%\Nowring\settings.json`
- кэш обложек и данных о треках: `%LOCALAPPDATA%\Nowring\covers`
- журнал ошибок сети: `%LOCALAPPDATA%\Nowring\log.txt` (открывается из трея)

## Ограничения

- Анимированные обложки показать нельзя: Windows отдаёт программе только статичную картинку.
- Карточки в профиле и в голосовом канале рисует сам Discord. Программа управляет только текстом, обложкой, прогрессом и кнопкой, поэтому внешний вид может отличаться от родного Spotify.
- Spotify в Windows часто отдаёт только первого исполнителя. Остальных (фиты) программа находит по каталогам. Для скачанных файлов, которых нет в каталогах, останется то, что отдал плеер.
- Сайт в браузере определяется по названиям вкладок. Если открыто сразу несколько музыкальных сайтов, программа может ошибиться.

## Сборка из исходников

Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bat
build.bat
```

Готовый `Nowring.exe` появится в папке `out`. Чтобы собрать установщик, дополнительно установите [Inno Setup](https://jrsoftware.org/isdl.php) версии 6.6 или новее (в ней появилась тёмная тема установщика) и запустите `installer\make-installer.bat`. Результат будет в папке `dist`.

### Свои копии программы (форки)

Название в статусе («Слушает …») берётся из названия приложения Discord, поэтому у каждого источника своё приложение. Application ID не секрет, он общий для всех копий программы.

1. На [discord.com/developers/applications](https://discord.com/developers/applications) создайте приложения «Spotify», «Яндекс Музыка» и «SoundCloud» (и, если нужно, общее для остальных сайтов).
2. Впишите их Application ID в `Config.cs`.
3. Соберите программу.

### Релизы

Релиз собирает GitHub Actions: достаточно создать тег.

```bash
git tag v1.0.0
git push --tags
```

В Releases появятся `Nowring.exe`, `Nowring-Setup-1.0.0.exe` и `SHA256SUMS.txt`.

## English

**Nowring** is a lightweight Windows tray app that shows what you are playing in **Spotify, Yandex Music and SoundCloud** as a Discord "Listening to" status: title, all artists, cover art, progress bar and an "Open in…" button. It uses Windows media session events (no polling), keeps the status on pause, finds cover art automatically and caches it, and unloads its window when closed to keep memory low.

Download the installer or the portable `Nowring-portable.zip` from [Releases](https://github.com/zxce1zy/Nowring/releases/latest). Requires Windows 10 1903+ / 11 x64 and the Discord desktop app. To avoid two activities at once, turn off *Discord Settings → Connections → Spotify → Display Spotify as your status*. Track title and artist are sent to Deezer, song.link, iTunes and Yandex Music to find cover art and links (see the table above). Build with the .NET 8 SDK (`build.bat`).

## Лицензия

[MIT](LICENSE)
