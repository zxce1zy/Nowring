"""
Lyrics Overlay — тексты песен поверх экрана (Windows 10/11).

Установка:   python -m pip install winsdk requests
Запуск:      двойной клик по start_lyrics.bat (или по LyricsOverlay.exe, если собран build_exe.bat)

Верхняя панель (значки без фона, при наведении становятся фиолетовыми):
    ✥   — зажмите и тащите, чтобы переместить окно
    ☰   — окно настроек (размер, выравнивание, цвет, синхронизация, закрыть)
    ✕   — закрыть приложение
    А− 100% А+ — размер текста (шаг 10%, максимум 100%)
Под текстом: ⏮ предыдущая песня, ⏯ пауза/играть, ⏭ следующая песня.
Над ⏮ и ⏭ при наведении появляется карточка с названием песни.
Выравнивание зависит от положения окна: слева — по левому краю,
по центру — по центру, справа — окно «зеркалится», текст по правому краю.
Ещё: Ctrl + колесо мыши — размер, правая кнопка мыши — меню.
В панели задач у окна свой значок: правая кнопка по нему → «Закрыть окно».
"""

import asyncio
import base64
import ctypes
import datetime
import difflib
import json
import math
import os
import re
import struct
import subprocess
import sys
import threading
import time
import traceback
import urllib.parse
import zlib
import tkinter as tk
from tkinter import font as tkfont
from tkinter import messagebox

import requests

try:                                  # сглаженный текст; без Pillow остаётся прежний вывод
    import io
    from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont
    PIL_OK = True
except Exception:
    PIL_OK = False
from winsdk.windows.media.control import (
    GlobalSystemMediaTransportControlsSessionManager as MediaManager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus,
)

# ---------------------------------------------------------------- настройки
KEY_DARK = "#010101"          # «прозрачный» цвет окна для тёмного фона (почти чёрный)
KEY_LIGHT = "#FEFEFE"         # «прозрачный» цвет окна для светлого фона (почти белый)
TRANSPARENT = KEY_DARK
THEME_MODE = "auto"           # auto — по фону; white — всегда белый текст; dark — всегда тёмный текст
TEXT_DARK_V = 26              # яркость тёмного текста для светлого фона (0 — чёрный)
THEME_SAMPLE_SECONDS = 0.4    # как часто смотреть, какой фон за текстом
FONT_FAMILY = "Segoe UI"
START_FONT_SIZE = 26          # размер шрифта при 100%

BEFORE_NORMAL, AFTER_NORMAL = 3, 1    # обычный режим: 3 спетых, текущая, 1 следующая (всего 5)
BEFORE_COMPACT, AFTER_COMPACT = 1, 1  # компактный режим: 1 спетая, текущая, 1 следующая (всего 3)
COMPACT_AT_PERCENT = 60               # при размере 60% и меньше включается компактный режим
MIN_PERCENT, MAX_PERCENT = 30, 100    # границы размера текста

ANIM_SECONDS = 0.30           # длительность плавного сдвига строк (было 0.45; меньше = быстрее)
LEAD_SECONDS = 0.45           # подсвечивать строку чуть раньше её тайм-кода (тонкая подстройка — в меню ☰)
POLL_SECONDS = 0.1            # как часто спрашивать у плеера позицию в песне
RECONNECT_SECONDS = 1.0       # как часто заново подключаться к системе управления мультимедиа
FRAME_MS = 16                 # кадр анимации (~60 кадров в секунду)
LINE_GAP = 6                  # отступ между строками
TEXT_LEFT_PAD = 6             # отступ текста от левого края (под значками)

HINT_COLOR = "#9A9A9A"        # цвет подсказки «нет тайм-кодов»
PLAIN_HINT = "Нет тайм-кодов: прокрутка колесом мыши"
# Яркость строки по расстоянию от поющейся: 0 — сейчас, 1 — соседняя, 2 — через одну...
DIM_NEXT = [1.0, 0.55, 0.38, 0.28]   # строки, которые ещё будут петься
DIM_PAST = [1.0, 0.42, 0.28, 0.18]   # уже спетые строки (после пения быстро тускнеют)
FROZEN = bool(getattr(sys, "frozen", False))      # True, когда приложение запущено как .exe
if FROZEN:
    APP_DIR = os.path.join(os.environ.get("APPDATA") or os.path.expanduser("~"), "LyricsOverlay")
    try:
        os.makedirs(APP_DIR, exist_ok=True)
    except OSError:
        APP_DIR = os.path.dirname(sys.executable)
else:
    APP_DIR = os.path.dirname(os.path.abspath(__file__))
SETTINGS_FILE = os.path.join(APP_DIR, "lyrics_overlay_settings.json")
APP_NAME = "Lyrics Overlay"
ASK_SHORTCUTS_ONLY_IN_EXE = True   # спрашивать про ярлыки только в .exe (False — и при запуске из Python)
OUTLINE_ENABLED = True        # обводка букв: True — включена, False — выключена
OUTLINE_SCALE = 26            # чем больше число, тем тоньше обводка (26 = 1 пиксель при 100%)

ALIGN_MODE = "auto"           # auto — по положению окна; можно жёстко: left / center / right
COLOR_ICON = "#8C8C8C"        # значки панели и кнопок плеера (серый: виден и на белом, и на чёрном)
COLOR_ICON_HOVER = "#A98BFF"  # значок под мышью (светло-фиолетовый, как значок Lyrics)
COLOR_CLOSE = "#FF6B6B"       # значок закрытия
ICON_SIZE = 22                # размер значков верхней панели
MEDIA_SIZE = 24               # размер кнопок плеера
MENU_BAR = 2                  # толщина полосок значка ☰ (пикселей)
CHROME_HIDE_DELAY = 0.6       # через сколько секунд после ухода курсора пропадают кнопки (если включено скрытие)
TB_H = 4 + ICON_SIZE + 8 + 22 + 4   # высота верхней панели
CTL_H = MEDIA_SIZE + 10             # высота ряда кнопок плеера
UI_PAD = 6                    # отступ значков от края окна
TIP_BG, TIP_BORDER, TIP_FG = "#1F1F1F", "#5A5A5A", "#FFFFFF"   # карточка-подсказка
SW_W, SW_H = 480, 800         # размер окна настроек
SW_BG, SW_PANEL, SW_FG, SW_MUTED = "#16161A", "#2B2B34", "#FFFFFF", "#8E8E9A"   # палитра Nowring
SW_CARD, SW_EDGE, SW_HOVER = "#1F1F26", "#2C2C34", "#363641"                    # карточка, рамка, кнопка под мышью
SW_ON = "#7C54EE"             # выбранная кнопка в окне настроек (фиолетовый, как значок Lyrics)
CHROME_FADE_SECONDS = 0.22    # плавное появление и скрытие кнопок
CHROME_SLIDE_PX = 8           # на сколько пикселей кнопки «выезжают» при появлении
OUTLINE_SOFT = 0.7           # мягкость обводки (размытие, пикселей)
OUTLINE_GAIN = 1.15           # насыщенность обводки
KARAOKE_SEC_PER_CHAR = 0.085   # караоке: сколько секунд в среднем уходит на букву (строка не обязана тянуться до следующей)
KARAOKE_MIN_SEC = 0.9          # самая короткая «спевка» строки
SMOOTH_SS = 3                 # сглаживание текста: во сколько раз рисуем крупнее, а потом уменьшаем
SHADE_STEP = 18               # шаг яркости строк (меньше — плавнее затухание, но больше картинок в памяти)
WIN_FONTS = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")
LATIN_FONTS = ("segoeuib.ttf", "arialbd.ttf", "calibrib.ttf", "tahomabd.ttf")
CJK_FONTS = ("msyhbd.ttc", "YuGothB.ttc", "meiryob.ttc", "malgunbd.ttf", "msgothic.ttc", "simsun.ttc")
SCREEN_MARGIN = 0             # отступ окна от краёв экрана при перетаскивании
MOVE_HIT_ENABLED = False       # увеличенная область нажатия у значка ✥ (нажатие «в пустоту» внутри него)
MOVE_HIT_PAD = 4              # на сколько пикселей область шире самого значка

ERROR_LOG = os.path.join(APP_DIR, "lyrics_overlay_error.log")

# ---------------------------------------------------------------- состояние
state = {
    "track_id": None,
    "synced": [],            # [(секунды, строка), ...]
    "plain": [],             # [строка, ...]
    "position": 0.0,         # позиция в треке в момент pos_time
    "pos_time": 0.0,
    "playing": False,
    "status": "Включите музыку...",
    "duration": None,        # длина текущей песни, секунды (если плеер сообщил)
    "history": [],           # песни, которые приложение видело по порядку
    "hist_idx": -1,          # номер текущей песни в history
}


def log_error():
    try:
        if os.path.exists(ERROR_LOG) and os.path.getsize(ERROR_LOG) > 50000:
            return
        with open(ERROR_LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S\n") + traceback.format_exc() + "\n")
    except Exception:
        pass


# ------------------------------------------------- 1. что сейчас играет (см. poll_forever ниже)
async def _media_cmd(cmd):
    mgr = await MediaManager.request_async()
    session = mgr.get_current_session()
    if not session:
        return
    if cmd == "prev":
        await session.try_skip_previous_async()
    elif cmd == "next":
        await session.try_skip_next_async()
    elif cmd == "play":
        await session.try_toggle_play_pause_async()


def send_media_command(cmd):
    """Команда плееру (Spotify, Яндекс Музыка и др.) через системное управление мультимедиа."""
    def run():
        try:
            asyncio.run(_media_cmd(cmd))
        except Exception:
            log_error()
    threading.Thread(target=run, daemon=True).start()


def note_track(track_id):
    """Запоминаем порядок песен: нужно для названия в карточке над ⏮ и ⏭."""
    h, i = state["history"], state["hist_idx"]
    if 0 <= i < len(h) and h[i] == track_id:
        return
    if i - 1 >= 0 and h[i - 1] == track_id:
        state["hist_idx"] = i - 1
    elif 0 <= i + 1 < len(h) and h[i + 1] == track_id:
        state["hist_idx"] = i + 1
    else:
        del h[i + 1:]
        h.append(track_id)
        if len(h) > 200:
            h.pop(0)
        state["hist_idx"] = len(h) - 1


def clean(text: str) -> str:
    text = re.sub(r"[\(\[].*?[\)\]]", "", text)
    text = re.sub(r"\s*-\s*(remaster|official|lyrics|video|live).*$", "", text, flags=re.I)
    return text.strip()


# ------------------------------------------------- 2. поиск текста (LRCLIB)
_STAMP = re.compile(r"\[(\d+):(\d+(?:[.:]\d+)?)\]")
_WORD_TAG = re.compile(r"<\d+:\d+(?:[.:]\d+)?>")
_CREDITS = re.compile(r"^\s*(作词|作曲|编曲|制作人?|混音|母带|录音|词|曲|lyrics?|music|composer|arranger|producer)\s*[:：]", re.I)
UA_LRCLIB = "LyricsOverlay/5.0"
UA_BROWSER = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/120.0 Safari/537.36")
SEARCH_DEADLINE_SECONDS = 25      # дольше этого поиск текста не тянем


def parse_lrc(lrc: str, drop_credits=False):
    """[мм:сс.хх] текст -> [(секунды, строка)]. Понимает несколько меток в одной строке и <слово-метки>."""
    result = []
    for line in (lrc or "").splitlines():
        stamps = _STAMP.findall(line)
        if not stamps:
            continue
        text = _WORD_TAG.sub("", _STAMP.sub("", line)).strip()
        for mm, ss in stamps:
            result.append((int(mm) * 60 + float(ss.replace(":", ".")), text))
    result.sort(key=lambda x: x[0])
    if drop_credits:                      # «作词 : ...» и т.п. в начале текста с Netease
        result = [x for x in result[:8] if not _CREDITS.match(x[1])] + result[8:]
    return result


def finish(data: dict):
    return parse_lrc(data.get("syncedLyrics") or ""), (data.get("plainLyrics") or "").splitlines()


def _norm(text):
    return re.sub(r"[\W_]+", "", (text or "").lower())


def _sim(a, b):
    """Похожесть двух нормализованных строк, 0..1 (вложение одной в другую = 1)."""
    if not a or not b:
        return 0.0
    if len(min(a, b, key=len)) >= 3 and (a in b or b in a):
        return 1.0
    return difflib.SequenceMatcher(None, a, b).ratio()


def rank_candidates(cands, artist, title, duration):
    """Подходящие версии, лучшие первыми. Отсекаем чужие песни с тем же названием:
    исполнитель должен совпасть, либо длина трека — с точностью до 2 секунд."""
    a, t = _norm(artist), _norm(title)
    ranked = []
    for c in cands:
        if not isinstance(c, dict):
            continue
        ct = _norm(c.get("trackName") or c.get("name"))
        ca = _norm(c.get("artistName"))
        if not ct or _sim(t, ct) < 0.8:
            continue
        has_sync, has_plain = bool(c.get("syncedLyrics")), bool(c.get("plainLyrics"))
        if not (has_sync or has_plain):
            continue
        try:
            d = c.get("duration")
            diff = abs(float(d) - duration) if (duration and d) else None
        except (TypeError, ValueError):
            diff = None
        a_sim = _sim(a, ca) if a else 0.0
        if a and a_sim < 0.5 and not (diff is not None and diff <= 2):
            continue
        score = 10 if has_sync else 2
        if ct == t:
            score += 2
        score += round(3 * a_sim)
        if diff is not None:
            score += 3 if diff <= 2 else (1 if diff <= 5 else (-4 if diff > 15 else 0))
        ranked.append((score, c))
    ranked.sort(key=lambda x: -x[0])
    return [c for _, c in ranked]


def pick_best(cands, artist, title, duration):
    ranked = rank_candidates(cands, artist, title, duration)
    return ranked[0] if ranked else None


def _clean_artist(artist):
    artist = re.sub(r"[\(\[].*?[\)\]]", "", artist or "")
    artist = re.sub(r"\s*[-–—]\s*topic\s*$|\s*vevo\s*$|\s+official\s*$", "", artist, flags=re.I)
    return artist.strip()


def _first_artist(artist):
    parts = re.split(r"\s*(?:,|;|&|/|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b|\bи\b|\band\b)\s*",
                     artist or "", maxsplit=1, flags=re.I)
    return parts[0].strip()


def _title_variants(title):
    title = (title or "").strip()
    base = clean(title) or title
    out = [base]
    nofeat = re.sub(r"\s*[-–—]?\s*(feat\.?|ft\.?|featuring)\s.*$", "", base, flags=re.I).strip()
    out.append(nofeat)
    if re.search(r"\s[-–—]\s", base):
        out.append(re.split(r"\s[-–—]\s", base)[0].strip())
    out.append(title)
    uniq = []
    for t in out:
        if t and t not in uniq:
            uniq.append(t)
    return uniq


_SUFFIX_RIGHT = re.compile(r"(radio|single|album|extended|original|acoustic|instrumental|remix|mix|edit|version|mono|stereo|deluxe|bonus|\d{4})\b", re.I)


def query_variants(artist, title):
    """Список вариантов (исполнитель для поиска, название, исполнитель для проверки) — от точных к вольным.
    Покрывает «feat.», несколько исполнителей, «- Topic», названия вида «Артист - Песня» с YouTube."""
    seen, out = set(), []

    def add(a, t, ref):
        t = (t or "").strip()
        if not t:
            return
        key = (_norm(a), _norm(t))
        if key in seen:
            return
        seen.add(key)
        out.append((a, t, ref))

    artist_c = _clean_artist(artist)
    first = _first_artist(artist_c)
    titles = _title_variants(title)
    if titles:
        add(artist_c, titles[0], first)
        if first and first != artist_c:
            add(first, titles[0], first)
        for t in titles[1:]:
            add(artist_c, t, first)
    base = clean(title or "") or (title or "").strip()
    m = re.match(r"^(.+?)\s[-–—]\s(.+)$", base)
    if m and not _SUFFIX_RIGHT.match(m.group(2).strip()):   # «Артист - Песня (Official Video)» с YouTube
        left, right = m.group(1).strip(), m.group(2).strip()
        add(left, right, _first_artist(left))
        add("", right, _first_artist(left))
    if titles:
        add("", titles[0], first)                           # крайний случай: только по названию
    return out[:8]


def _http_json(url, params, headers, deadline):
    if time.time() > deadline:
        return None
    try:
        r = requests.get(url, params=params, headers=headers, timeout=6)
        return r.json() if r.ok else None
    except (requests.RequestException, ValueError):
        return None


def lrclib_lookup(artist, title, ref_artist, duration, deadline):
    headers = {"User-Agent": UA_LRCLIB}

    def get(path, **params):
        return _http_json("https://lrclib.net/api/" + path, params, headers, deadline)

    cands = []
    if artist:
        params = {"artist_name": artist, "track_name": title}
        if duration:
            params["duration"] = int(round(duration))
        exact = get("get", **params)
        if isinstance(exact, dict):
            cands.append(exact)
    best = pick_best(cands, ref_artist, title, duration)
    if best and best.get("syncedLyrics"):
        return best
    queries = ([{"track_name": title, "artist_name": artist}, {"q": f"{artist} {title}"}]
               if artist else [{"q": title}])
    for q in queries:
        found = get("search", **q)
        if isinstance(found, list):
            cands.extend(found)
        best = pick_best(cands, ref_artist, title, duration)
        if best and best.get("syncedLyrics"):
            return best
    return pick_best(cands, ref_artist, title, duration)


def netease_lookup(artist, title, ref_artist, duration, deadline):
    """Запасной источник: Netease Cloud Music (тексты с тайм-кодами, много иностранных песен)."""
    headers = {"User-Agent": UA_BROWSER, "Referer": "https://music.163.com/"}
    data = _http_json("https://music.163.com/api/search/get",
                      {"s": f"{artist} {title}".strip(), "type": 1, "limit": 10, "offset": 0},
                      headers, deadline)
    if not isinstance(data, dict):
        return []
    songs = (data.get("result") or {}).get("songs") or []
    cands = []
    for s in songs:
        if not isinstance(s, dict) or not s.get("id"):
            continue
        dur = s.get("duration") or s.get("dt") or 0
        names = s.get("artists") or s.get("ar") or []
        cands.append({"id": s["id"], "trackName": s.get("name"),
                      "artistName": " ".join(str(x.get("name", "")) for x in names if isinstance(x, dict)),
                      "duration": dur / 1000.0 if isinstance(dur, (int, float)) and dur else None,
                      "syncedLyrics": True})            # настоящий текст запросим ниже
    for c in rank_candidates(cands, ref_artist, title, duration)[:3]:
        lyr = _http_json("https://music.163.com/api/song/lyric",
                         {"id": c["id"], "lv": -1, "kv": -1, "tv": -1}, headers, deadline)
        if not isinstance(lyr, dict):
            continue
        synced = parse_lrc(((lyr.get("lrc") or {}).get("lyric")) or "", drop_credits=True)
        if len(synced) >= 3:
            return synced
    return []


def lyrics_ovh_lookup(artist, title, deadline):
    """Последняя надежда: текст без тайм-кодов."""
    if not artist:
        return []
    url = "https://api.lyrics.ovh/v1/%s/%s" % (urllib.parse.quote(artist, safe=""),
                                               urllib.parse.quote(title, safe=""))
    data = _http_json(url, None, {"User-Agent": UA_LRCLIB}, deadline)
    text = data.get("lyrics") if isinstance(data, dict) else ""
    lines = [l.rstrip() for l in (text or "").replace("\r", "").split("\n")]
    return lines if sum(1 for l in lines if l.strip()) >= 4 else []


def fetch_lyrics(artist: str, title: str, duration=None):
    """Ищем текст по нескольким вариантам названия и в нескольких источниках:
    LRCLIB (с тайм-кодами) -> Netease (с тайм-кодами) -> любой текст без тайм-кодов."""
    deadline = time.time() + SEARCH_DEADLINE_SECONDS
    variants = query_variants(artist, title)
    plain_best = []
    for a, t, ref in variants:
        if time.time() > deadline:
            break
        best = lrclib_lookup(a, t, ref, duration, deadline)
        if best:
            synced, plain = finish(best)
            if synced:
                return synced, plain
            if plain and not plain_best:
                plain_best = plain
    for a, t, ref in variants[:4]:
        if time.time() > deadline:
            break
        synced = netease_lookup(a, t, ref, duration, deadline)
        if synced:
            return synced, plain_best
    if plain_best:
        return [], plain_best
    for a, t, ref in variants[:3]:
        plain = lyrics_ovh_lookup(a, t, deadline)
        if plain:
            return [], plain
    return [], []


def update_clock(pos, playing):
    """Обновляем «часы песни». Мелкие скачки сглаживаем, большие (перемотка) принимаем сразу."""
    now = time.time()
    if state["playing"] and playing and state["pos_time"]:
        estimate = state["position"] + (now - state["pos_time"])
        diff = pos - estimate
        if abs(diff) < 0.25:
            pos = estimate + 0.25 * diff
    state.update(position=pos, pos_time=now, playing=playing)


def fetch_job(track_id, artist, title, duration=None):
    """Поиск текста идёт в отдельном потоке, чтобы не мешать слежению за позицией."""
    try:
        synced, plain = fetch_lyrics(artist, title, duration)
    except Exception:
        log_error()
        synced, plain = [], []
    if state["track_id"] != track_id:          # песня уже сменилась — результат не нужен
        return
    if synced or plain:
        state.update(synced=synced, plain=plain, status="")
    else:
        state["status"] = f"Текст не найден: {artist} — {title}  (☰ → «Искать вручную»)"


# Браузер при наведении мыши на превью (рекомендации YouTube) подменяет данные плеера: название, позицию
# и состояние. Настоящее видео открыто во вкладке, и его название есть в заголовке окна браузера,
# а у превью такого нет. Поэтому превью игнорируем и держим основное видео.
_user32 = ctypes.windll.user32 if sys.platform == "win32" else None
_WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p) if _user32 else None
_BROWSER_CLASSES = ("Chrome_WidgetWin_1", "MozillaWindowClass")          # Chrome/Edge/Opera/Brave и Firefox
_NOT_BROWSER = ("spotify", "yandexmusic", "yandex music", "itunes", "vlc", "foobar", "aimp", "winamp", "tidal", "deezer")


def browser_window_titles():
    """Заголовки окон браузеров (в них название открытой вкладки), в нижнем регистре."""
    out = []
    if not _user32:
        return out

    def cb(hwnd, _):
        try:
            if _user32.IsWindowVisible(hwnd):
                cls = ctypes.create_unicode_buffer(64)
                _user32.GetClassNameW(hwnd, cls, 64)
                if cls.value in _BROWSER_CLASSES:
                    n = _user32.GetWindowTextLengthW(hwnd)
                    if n:
                        buf = ctypes.create_unicode_buffer(n + 1)
                        _user32.GetWindowTextW(hwnd, buf, n + 1)
                        out.append(buf.value.lower())
        except Exception:
            pass
        return True

    try:
        _user32.EnumWindows(_WNDENUMPROC(cb), 0)
    except Exception:
        pass
    return out


def guard_needed(aumid):
    a = (aumid or "").lower()
    return not any(k in a for k in _NOT_BROWSER)


async def poll_forever():
    mgr, mgr_at = None, 0.0
    props_at, title, artist, errors = 0.0, "", "", 0
    real = []          # [источник, название, исполнитель] настоящего видео браузера (открыто во вкладке)
    while True:
        try:
            # Подключение обновляем регулярно: «старое» подключение могло не заметить плеер,
            # запущенный уже после приложения.
            if mgr is None or time.time() - mgr_at > RECONNECT_SECONDS:
                mgr = await MediaManager.request_async()
                mgr_at = time.time()
            session = mgr.get_current_session()
            if not session:
                if real and any(real[1].lower() in w for w in browser_window_titles()):
                    await asyncio.sleep(0.4)     # браузер на время убрал сессию (превью), основное видео открыто: ждём
                    continue
                title, props_at, mgr = "", 0.0, None
                state.update(track_id=None, synced=[], plain=[], playing=False,
                             status="Включите музыку...")
                await asyncio.sleep(0.4)
                continue

            if time.time() - props_at > 0.5:   # название песни спрашиваем реже, чем позицию
                props = await session.try_get_media_properties_async()
                title, artist = props.title or "", props.artist or ""
                props_at = time.time()
            if guard_needed(session.source_app_user_model_id):
                titles = browser_window_titles()
                tl = title.lower()
                if tl and any(tl in w for w in titles):
                    real[:] = [session.source_app_user_model_id, title, artist]       # настоящее видео
                elif real and real[0] == session.source_app_user_model_id and any(real[1].lower() in w for w in titles):
                    await asyncio.sleep(POLL_SECONDS)       # превью под курсором: оставляем основное видео как есть
                    continue
            if not title:
                mgr = None
                state.update(track_id=None, synced=[], plain=[], playing=False,
                             status="Включите музыку...")
                await asyncio.sleep(0.4)
                continue

            timeline = session.get_timeline_properties()
            playing = session.get_playback_info().playback_status == PlaybackStatus.PLAYING
            pos = timeline.position.total_seconds()
            if playing:
                try:
                    now = datetime.datetime.now(datetime.timezone.utc)
                    pos += max(0.0, (now - timeline.last_updated_time).total_seconds())
                except Exception:
                    pass
            update_clock(pos, playing)

            track_id = (artist, title)
            if track_id != state["track_id"]:
                note_track(track_id)
                try:                                   # длительность помогает выбрать нужную версию текста
                    dur = (timeline.end_time - timeline.start_time).total_seconds()
                except Exception:
                    dur = 0
                dur = dur if dur > 10 else None
                state.update(track_id=track_id, synced=[], plain=[], duration=dur,
                             status=f"Ищу текст: {artist} — {title}")
                threading.Thread(target=fetch_job, args=(track_id, artist, title, dur), daemon=True).start()

            if errors:
                errors = 0
                if state["status"].startswith("Ошибка"):
                    state["status"] = "" if (state["synced"] or state["plain"]) else "Включите музыку..."
        except Exception as e:
            errors += 1
            mgr = None
            log_error()
            if errors >= 3:
                state["status"] = f"Ошибка чтения плеера: {e}"
            await asyncio.sleep(1)
        await asyncio.sleep(POLL_SECONDS)


def worker():
    while True:
        try:
            asyncio.run(poll_forever())
        except Exception:
            log_error()
            time.sleep(2)


def load_settings():
    try:
        with open(SETTINGS_FILE, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def save_settings(data):
    try:
        with open(SETTINGS_FILE, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=1)
    except Exception:
        log_error()


# ------------------------------------------------- скруглённые виджеты (в tkinter своих нет)
def _round_rect(cv, x1, y1, x2, y2, r, **kw):
    pts = [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r, x2, y2 - r, x2, y2,
           x2 - r, y2, x1 + r, y2, x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
    return cv.create_polygon(pts, smooth=True, **kw)


class RBtn(tk.Canvas):
    """Кнопка со скруглёнными углами; настраивается так же, как tk.Button (bg, fg, activebackground...)."""

    def __init__(self, parent, text="", command=None, bg=SW_PANEL, fg=SW_FG, activebackground=SW_HOVER,
                 activeforeground=None, font=None, pady=7, radius=10, cursor="hand2"):
        self._font = tkfont.Font(font=font or (FONT_FAMILY, 10, "bold"))
        h = self._font.metrics("linespace") + pady * 2
        super().__init__(parent, bg=parent.cget("bg"), highlightthickness=0, bd=0, width=10, height=h, cursor=cursor)
        self.command, self._text, self._r = command, text, radius
        self._bg, self._fg, self._abg, self._afg = bg, fg, activebackground, activeforeground or fg
        self._hover = False
        self.bind("<Configure>", lambda e: self._draw())
        self.bind("<Enter>", lambda e: self._set_hover(True))
        self.bind("<Leave>", lambda e: self._set_hover(False))
        self.bind("<ButtonRelease-1>", self._click)

    def _set_hover(self, v):
        self._hover = v
        self._draw()

    def _click(self, e):
        if self._hover and self.command:
            self.command()

    def configure(self, cnf=None, **kw):
        for key, attr in (("bg", "_bg"), ("fg", "_fg"), ("activebackground", "_abg"),
                          ("activeforeground", "_afg"), ("text", "_text")):
            if key in kw:
                setattr(self, attr, kw.pop(key))
        if cnf or kw:
            super().configure(cnf, **kw)
        self._draw()

    config = configure

    def _draw(self):
        w, h = self.winfo_width(), self.winfo_height()
        if w < 4 or h < 4:
            return
        self.delete("all")
        fill = self._abg if self._hover else self._bg
        _round_rect(self, 1, 1, w - 1, h - 1, self._r, fill=fill, outline=fill)
        self.create_text(w / 2, h / 2, text=self._text, fill=self._afg if self._hover else self._fg, font=self._font)


class RoundCard(tk.Frame):
    """Карточка со скруглёнными углами: виджеты кладём в card.inner. Высоту задаёт содержимое,
    а скруглённый фон рисуется на холсте под ним."""

    def __init__(self, parent, outer=SW_BG, fill=SW_CARD, outline=SW_EDGE, radius=14, pad=14):
        super().__init__(parent, bg=outer)
        self._fill, self._outline, self._radius = fill, outline, radius
        self._bgcv = tk.Canvas(self, bg=outer, highlightthickness=0, bd=0)
        self._bgcv.place(x=0, y=0, relwidth=1, relheight=1)
        self.inner = tk.Frame(self, bg=fill)
        self.inner.pack(fill="x", padx=pad, pady=pad)
        self._bgcv.bind("<Configure>", self._redraw)

    def _redraw(self, e=None):
        cv = self._bgcv
        w, h = cv.winfo_width(), cv.winfo_height()
        if w < 4 or h < 4:
            return
        cv.delete("all")
        _round_rect(cv, 1, 1, w - 1, h - 1, self._radius, fill=self._fill, outline=self._outline)


# ------------------------------------------------- сглаженные значки кнопок
def _in_poly(x, y, pts):
    inside, j = False, len(pts) - 1
    for i in range(len(pts)):
        xi, yi = pts[i]
        xj, yj = pts[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def _seg_dist(x, y, x0, y0, x1, y1):
    dx, dy = x1 - x0, y1 - y0
    ln = dx * dx + dy * dy
    t = 0.0 if ln == 0 else max(0.0, min(1.0, ((x - x0) * dx + (y - y0) * dy) / ln))
    return math.hypot(x - (x0 + t * dx), y - (y0 + t * dy))


def _icon_shapes(name, s, playing):
    """Фигуры значка в пикселях (0..s): ("r", x0, y0, x1, y1), ("c", x0, y0, x1, y1, полуширина) — линия
    с круглыми концами, ("p", [точки]) — многоугольник."""
    k = s / 30.0
    def pt(v):
        return v * k
    cx = cy = s / 2
    R = []
    if name == "move":
        R += [("c", 5, cy, s - 5, cy, 1.0), ("c", cx, 5, cx, s - 5, 1.0)]
        for pts in (((cx, 0.5), (cx - 3.5, 5.5), (cx + 3.5, 5.5)),
                    ((cx, s - 0.5), (cx - 3.5, s - 5.5), (cx + 3.5, s - 5.5)),
                    ((0.5, cy), (5.5, cy - 3.5), (5.5, cy + 3.5)),
                    ((s - 0.5, cy), (s - 5.5, cy - 3.5), (s - 5.5, cy + 3.5))):
            R.append(("p", list(pts)))
    elif name == "menu":
        h = MENU_BAR / 2.0
        for yy in (4, 10, 16):
            R.append(("c", 2 + h, yy + h, s - 2 - h, yy + h, h))
    elif name == "close":
        R += [("c", 4, 4, s - 4, s - 4, 2.0), ("c", s - 4, 4, 4, s - 4, 2.0)]
    elif name == "prev":
        R += [("r", pt(4), pt(5), pt(8), s - pt(5)),
              ("p", [(s - pt(5), pt(5)), (s - pt(5), s - pt(5)), (pt(10), cy)])]
    elif name == "next":
        R += [("p", [(pt(5), pt(5)), (pt(5), s - pt(5)), (s - pt(10), cy)]),
              ("r", s - pt(8), pt(5), s - pt(4), s - pt(5))]
    elif playing:                                           # пауза
        R += [("r", pt(7), pt(5), pt(12), s - pt(5)), ("r", s - pt(12), pt(5), s - pt(7), s - pt(5))]
    else:                                                   # играть
        R.append(("p", [(pt(8), pt(4)), (pt(8), s - pt(4)), (s - pt(4), cy)]))
    return R


def _shape_hit(sh, x, y):
    t = sh[0]
    if t == "r":
        return sh[1] - 0.5 <= x <= sh[3] + 0.5 and sh[2] - 0.5 <= y <= sh[4] + 0.5
    if t == "c":
        return _seg_dist(x, y, sh[1], sh[2], sh[3], sh[4]) <= sh[5]
    pts = sh[1]
    if _in_poly(x, y, pts):
        return True
    for i in range(len(pts)):
        a, b = pts[i], pts[(i + 1) % len(pts)]
        if _seg_dist(x, y, a[0], a[1], b[0], b[1]) <= 0.55:
            return True
    return False


_ICON_MASKS = {}


def _icon_mask(name, s, playing, ss=4):
    """Маска прозрачности значка (сглаживание: каждый пиксель считаем по ss×ss точкам)."""
    key = (name, s, playing)
    m = _ICON_MASKS.get(key)
    if m is None:
        n = int(math.ceil(s)) + 2
        shapes = _icon_shapes(name, s, playing)
        px = []
        for py in range(n):
            for pxx in range(n):
                c = 0
                for sy in range(ss):
                    y = py - 1 + (sy + 0.5) / ss
                    for sx in range(ss):
                        x = pxx - 1 + (sx + 0.5) / ss
                        for sh in shapes:
                            if _shape_hit(sh, x, y):
                                c += 1
                                break
                px.append(round(255 * c / (ss * ss)))
        m = _ICON_MASKS[key] = (n, px)
    return m


def _mask_png(n, mask, rgb):
    raw = bytearray()
    r, g, b = rgb
    for y in range(n):
        raw.append(0)
        for a in mask[y * n:(y + 1) * n]:
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


# ------------------------------------------------- значок (рисуем сами): круг как у Nowring, но фиолетовый
def _bar(x, y, x0, x1, cy, half):
    """Попадание точки в «капсулу» (строка текста) от x0 до x1 с центром по высоте cy."""
    cx = min(max(x, x0 + half), x1 - half)
    return math.hypot(x - cx, y - cy) <= half


def _emoji_pixel(x, y):
    """Цвет точки (x, y) в сетке 64x64 или None, если точка вне круга."""
    if math.hypot(x - 32, y - 32) > 30.5:
        return None
    base = (146, 108, 248) if y < 40 else (124, 84, 238)         # верх светлее, низ темнее: как у Nowring
    if _bar(x, y, 15, 49, 23.5, 3.2):
        return (255, 255, 255)                                    # строки текста песни
    if _bar(x, y, 15, 39, 32.5, 3.2):
        return (255, 255, 255)
    if _bar(x, y, 15, 44, 41.5, 3.2):
        return tuple(round(c * 0.45 + 255 * 0.55) for c in base)  # приглушённая строка
    return base


def make_icon_png(size=64, ss=3):
    raw = bytearray()
    scale = 64.0 / size
    for py in range(size):
        raw.append(0)
        for px in range(size):
            r = g = b = a = 0
            for sy in range(ss):
                for sx in range(ss):
                    col = _emoji_pixel((px + (sx + .5) / ss) * scale, (py + (sy + .5) / ss) * scale)
                    if col:
                        r += col[0]; g += col[1]; b += col[2]; a += 1
            if a:
                raw += bytes((r // a, g // a, b // a, round(255 * a / (ss * ss))))
            else:
                raw += bytes((0, 0, 0, 0))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


# ------------------------------------------------- свой значок в панели задач
def set_window_app_id(hwnd, app_id):
    """Даёт окну отдельную «личность» в панели задач (своя кнопка и свой значок).
    Нужно, когда Python установлен из Microsoft Store: иначе Windows подставляет значок Python."""
    from ctypes import wintypes

    class GUID(ctypes.Structure):
        _fields_ = [("Data1", ctypes.c_ulong), ("Data2", ctypes.c_ushort),
                    ("Data3", ctypes.c_ushort), ("Data4", ctypes.c_ubyte * 8)]

    class PROPERTYKEY(ctypes.Structure):
        _fields_ = [("fmtid", GUID), ("pid", wintypes.DWORD)]

    class PROPVARIANT(ctypes.Structure):          # VT_LPWSTR = 31, строка лежит по смещению 8
        _fields_ = [("vt", ctypes.c_ushort), ("pad", ctypes.c_ushort * 3),
                    ("ptr", ctypes.c_void_p), ("pad2", ctypes.c_void_p)]

    def guid(text):
        g = GUID()
        ctypes.windll.ole32.CLSIDFromString(ctypes.c_wchar_p(text), ctypes.byref(g))
        return g

    key = PROPERTYKEY(guid("{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}"), 5)   # System.AppUserModel.ID
    iid = guid("{886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}")                   # IPropertyStore

    fn = ctypes.windll.shell32.SHGetPropertyStoreForWindow
    fn.argtypes = [wintypes.HWND, ctypes.c_void_p, ctypes.c_void_p]
    store = ctypes.c_void_p()
    if fn(hwnd, ctypes.byref(iid), ctypes.byref(store)) != 0 or not store.value:
        return False

    ole32 = ctypes.windll.ole32
    ole32.CoTaskMemAlloc.restype = ctypes.c_void_p
    ole32.CoTaskMemAlloc.argtypes = [ctypes.c_size_t]
    buf = ctypes.create_unicode_buffer(app_id)
    mem = ole32.CoTaskMemAlloc(ctypes.sizeof(buf))
    ctypes.memmove(mem, buf, ctypes.sizeof(buf))
    pv = PROPVARIANT()
    pv.vt = 31
    pv.ptr = mem

    obj = store.value
    vtable = ctypes.cast(obj, ctypes.POINTER(ctypes.c_void_p))[0]
    methods = ctypes.cast(vtable, ctypes.POINTER(ctypes.c_void_p))
    set_value = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)(methods[6])
    commit = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p)(methods[7])
    release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(methods[2])

    try:
        hr = set_value(obj, ctypes.byref(key), ctypes.byref(pv))
        commit(obj)
    finally:
        ole32.PropVariantClear(ctypes.byref(pv))      # освобождает строку
        release(obj)
    return hr == 0


# ------------------------------------------------- ярлыки (рабочий стол / меню Пуск)
def _special_folder(csidl):
    buf = ctypes.create_unicode_buffer(260)
    if ctypes.windll.shell32.SHGetFolderPathW(0, csidl, 0, 0, buf) == 0 and buf.value:
        return buf.value
    return None


def shortcut_paths():
    """Где лежат (или будут лежать) ярлыки. Папки спрашиваем у Windows: рабочий стол может быть в OneDrive."""
    desktop = _special_folder(0x10)     # CSIDL_DESKTOPDIRECTORY
    programs = _special_folder(0x02)    # CSIDL_PROGRAMS — «Пуск → Все приложения»
    return {
        "desktop": os.path.join(desktop, APP_NAME + ".lnk") if desktop else None,
        "start": os.path.join(programs, APP_NAME + ".lnk") if programs else None,
    }


def make_shortcut(lnk_path):
    """Создаёт ярлык на это приложение. Возвращает True, если файл ярлыка появился."""
    if FROZEN:
        target, args, icon = sys.executable, "", sys.executable + ",0"
    else:                                # запуск из Python: ярлык на pythonw.exe + этот скрипт
        target = os.path.join(os.path.dirname(sys.executable), "pythonw.exe")
        if not os.path.exists(target):
            target = sys.executable
        args, icon = '"%s"' % os.path.abspath(__file__), ""
    workdir = os.path.dirname(target if FROZEN else os.path.abspath(__file__))
    script = ("$s=(New-Object -ComObject WScript.Shell).CreateShortcut($env:LO_LNK);"
              "$s.TargetPath=$env:LO_TARGET;$s.Arguments=$env:LO_ARGS;"
              "$s.WorkingDirectory=$env:LO_DIR;$s.Description='Lyrics Overlay';"
              "if($env:LO_ICON){$s.IconLocation=$env:LO_ICON};$s.Save()")
    env = dict(os.environ, LO_LNK=lnk_path, LO_TARGET=target, LO_ARGS=args, LO_DIR=workdir, LO_ICON=icon)
    try:
        os.makedirs(os.path.dirname(lnk_path), exist_ok=True)
        subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-Command", script], env=env, stdin=subprocess.DEVNULL,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       creationflags=0x08000000, timeout=30)   # 0x08000000 — без мигающего окна консоли
    except Exception:
        log_error()
    return os.path.exists(lnk_path)


# ------------------------------------------------- 3. окно
def shade(d, light=False):
    """Цвет строки по расстоянию d от поющейся (d<0 — спетые, d>0 — будущие).
    light=False: белый текст тускнеет к тёмному; light=True: тёмный текст тускнеет к светлому."""
    table = DIM_NEXT if d >= 0 else DIM_PAST
    a = abs(d)
    k = int(a)
    if k >= len(table) - 1:
        b = table[-1]
    else:
        b = table[k] + (table[k + 1] - table[k]) * (a - k)
    v = 255 - (255 - TEXT_DARK_V) * b if light else 255 * b
    v = max(0, min(255, round(v)))
    return "#%02x%02x%02x" % (v, v, v)


def ease(t):                       # плавное замедление в конце движения
    return 1 - (1 - t) ** 3


# ------------------------------------------------- сглаженный текст (Pillow)
_RESAMPLE = None
if PIL_OK:
    _RESAMPLE = getattr(getattr(Image, "Resampling", Image), "LANCZOS")


class SmoothText:
    """Строка текста рисуется Pillow в крупном размере и уменьшается: края букв получаются гладкими
    (у обычного текста tkinter их нет). Результат — картинка с прозрачностью для холста."""

    def __init__(self):
        self.fonts, self.layouts, self.mask_cache, self.images = {}, {}, {}, {}

    @staticmethod
    def kind(text):
        """Какой шрифт нужен строке или None, если лучше оставить обычный вывод tkinter (арабский, тайский, эмодзи...)."""
        cjk = sym = letters = False
        for ch in text:
            o = ord(ch)
            if 0x0590 <= o <= 0x0EFF or 0x1F000 <= o <= 0x1FFFF or 0xFE00 <= o <= 0xFE0F:
                return None
            if 0x2E80 <= o <= 0x9FFF or 0xAC00 <= o <= 0xD7AF or 0xF900 <= o <= 0xFAFF or 0xFF00 <= o <= 0xFFEF:
                cjk = True
            elif 0x2190 <= o <= 0x2BFF:
                sym = True
            elif ch.isalnum():
                letters = True
        if cjk:
            return "cjk"
        return "sym" if sym and not letters else "latin"

    def font(self, kind, px):
        key = (kind, px)
        f = self.fonts.get(key)
        if f is None:
            names = {"latin": LATIN_FONTS, "cjk": CJK_FONTS + LATIN_FONTS,
                     "sym": ("seguisym.ttf",) + LATIN_FONTS}[kind]
            for n in names:
                try:
                    f = ImageFont.truetype(os.path.join(WIN_FONTS, n), px)
                    break
                except Exception:
                    f = None
            if f is None:
                raise RuntimeError("нет шрифта для сглаженного текста")
            self.fonts[key] = f
        return f

    @staticmethod
    def wrap(text, font, aw):
        out = []
        for para in text.split("\n"):
            cur = ""
            for word in para.split(" "):
                cand = word if not cur else cur + " " + word
                if font.getlength(cand) <= aw:
                    cur = cand
                    continue
                if cur:
                    out.append(cur)
                    cur = ""
                while len(word) > 1 and font.getlength(word) > aw:     # слово длиннее строки (или иероглифы без пробелов)
                    n = len(word)
                    while n > 1 and font.getlength(word[:n]) > aw:
                        n -= 1
                    out.append(word[:n])
                    word = word[n:]
                cur = word
            out.append(cur)
        return out or [""]

    def layout(self, text, px, aw):
        key = (text, px, aw)
        r = self.layouts.get(key)
        if r is None:
            kind = self.kind(text)
            if kind is None:
                raise ValueError("неподходящий текст")
            font = self.font(kind, px)
            asc, desc = font.getmetrics()
            ls = asc + desc + max(1, round(px * 0.06))
            r = (kind, self.wrap(text, font, aw), ls)
            if len(self.layouts) > 3000:
                self.layouts.clear()
            self.layouts[key] = r
        return r

    def height(self, text, px, aw):
        _, lines, ls = self.layout(text, px, aw)
        return ls * len(lines)

    def pad(self, px):
        return max(3, round(px * 0.12))

    def masks(self, text, px, aw, align, outline):
        key = (text, px, aw, align, outline)
        m = self.mask_cache.get(key)
        if m is None:
            kind, lines, ls = self.layout(text, px, aw)
            pad, S = self.pad(px), SMOOTH_SS
            W, H = aw + 2 * pad, ls * len(lines) + 2 * pad
            big = self.font(kind, px * S)
            tm, om = Image.new("L", (W * S, H * S), 0), Image.new("L", (W * S, H * S), 0)
            dt, do = ImageDraw.Draw(tm), ImageDraw.Draw(om)
            sw = round(outline * S)
            for k, line in enumerate(lines):
                if not line.strip():
                    continue
                lw = big.getlength(line)
                if align == "left":
                    x = pad * S
                elif align == "right":
                    x = (pad + aw) * S - lw
                else:
                    x = pad * S + (aw * S - lw) / 2
                y = (pad + k * ls) * S
                dt.text((x, y), line, font=big, fill=255)
                do.text((x, y), line, font=big, fill=255, stroke_width=sw, stroke_fill=255)
            tm = tm.resize((W, H), _RESAMPLE)
            om = om.resize((W, H), _RESAMPLE).filter(ImageFilter.GaussianBlur(OUTLINE_SOFT))
            om = om.point(lambda a: min(255, int(a * OUTLINE_GAIN)))
            if len(self.mask_cache) > 150:
                self.mask_cache.clear()
            m = self.mask_cache[key] = (tm, om, W, H)
        return m

    # ---------- караоке: слева направо подсвечиваем уже спетую часть строки
    @staticmethod
    def char_weight(ch):
        """Сколько «времени пения» занимает символ: гласные — слоги, согласные быстрее, пробел — короткая пауза."""
        if ch in "аеёиоуыэюяАЕЁИОУЫЭЮЯaeiouyAEIOUY":
            return 1.0
        if ch.isspace():
            return 0.5
        if ch.isalpha():
            return 1.0 if ord(ch) >= 0x2E80 else 0.45     # иероглифы и кана — один слог на знак
        if ch.isdigit():
            return 0.6
        return 0.3

    def kprep(self, text, px, aw, align):
        key = ("k", text, px, aw, align)
        r = self.layouts.get(key)
        if r is None:
            kind, lines, ls = self.layout(text, px, aw)
            pad, S = self.pad(px), SMOOTH_SS
            big = self.font(kind, px * S)
            W, H = aw + 2 * pad, ls * len(lines) + 2 * pad
            rows = []
            for line in lines:
                lw = big.getlength(line) / S
                x0 = pad if align == "left" else (pad + aw - lw if align == "right" else pad + (aw - lw) / 2)
                ws = [self.char_weight(c) for c in line]
                pre = [big.getlength(line[:j]) / S for j in range(len(line) + 1)]
                rows.append((x0, ws, sum(ws), pre))
            r = (ls, pad, W, H, rows, sum(w for _, _, w, _ in rows))
            self.layouts[key] = r
        return r

    def karaoke_region(self, text, px, aw, align, p):
        """Маска «уже спето» (L): строки до текущей целиком, у текущей — до нужной буквы, край мягкий."""
        ls, pad, W, H, rows, total = self.kprep(text, px, aw, align)
        region = Image.new("L", (W, H), 0)
        if total <= 0 or p <= 0:
            return region
        d = ImageDraw.Draw(region)
        target = min(1.0, p) * total
        acc = 0.0
        last = len(rows) - 1
        for k, (x0, ws, tot, pre) in enumerate(rows):
            top = 0 if k == 0 else pad + k * ls
            bot = H if k == last else pad + (k + 1) * ls
            if acc + tot <= target + 1e-9:
                d.rectangle((0, top, W, bot), fill=255)
                acc += tot
                continue
            need, cum, cut = target - acc, 0.0, x0
            for j, w in enumerate(ws):
                if cum + w >= need:
                    frac = (need - cum) / w if w else 0.0
                    cut = x0 + pre[j] + (pre[j + 1] - pre[j]) * frac
                    break
                cum += w
            soft = max(4, round(px * 0.35))
            solid = max(0, round(cut - soft))
            d.rectangle((0, top, solid, bot), fill=255)
            for i in range(soft):                              # мягкая кромка «волны» караоке
                x = solid + 1 + i
                if x >= W:
                    break
                d.line((x, top, x, bot), fill=round(255 * (1 - (i + 1) / (soft + 1))))
            break
        return region

    def photo(self, text, px, aw, align, outline, v, light):
        key = (text, px, aw, align, outline, v, light)
        img = self.images.get(key)
        if img is None:
            tm, om, W, H = self.masks(text, px, aw, align, outline)
            oc = 255 if light else 0
            under = Image.new("RGBA", (W, H), (oc, oc, oc, 0))
            under.putalpha(om)
            top = Image.new("RGBA", (W, H), (v, v, v, 0))
            top.putalpha(tm)
            under.alpha_composite(top)
            buf = io.BytesIO()
            under.save(buf, "PNG", compress_level=1)
            img = tk.PhotoImage(data=base64.b64encode(buf.getvalue()))
            if len(self.images) > 400:
                self.images.clear()          # показанные сейчас картинки держит сам холст (ссылки в строках)
            self.images[key] = img
        return img


class LineItem:
    """Одна строка текста: либо рисуется в слое со сглаживанием (smooth), либо обычный текст tkinter на холсте."""
    __slots__ = ("smooth", "outline", "main", "pad", "rect")

    def __init__(self, smooth, outline, main, pad=0):
        self.smooth, self.outline, self.main, self.pad = smooth, outline, main, pad
        self.rect = None        # где строка лежит в слое текста: (x1, y1, x2, y2)


class _BMIH(ctypes.Structure):
    _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                ("biPlanes", ctypes.c_uint16), ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", ctypes.c_uint32), ("biClrImportant", ctypes.c_uint32)]


class _BLEND(ctypes.Structure):
    _fields_ = [("op", ctypes.c_ubyte), ("flags", ctypes.c_ubyte), ("alpha", ctypes.c_ubyte), ("fmt", ctypes.c_ubyte)]


class TextLayer:
    """Отдельное прозрачное окно для текста. Рисуется через UpdateLayeredWindow с настоящей полупрозрачностью
    (по пикселям), поэтому края букв и тень гладкие на любом фоне. Раньше «прозрачность» была по цвету-ключу:
    любой не совпавший пиксель становился сплошным, отсюда ореолы и «лесенка»."""

    def __init__(self, root):
        from ctypes import wintypes as wt
        self.wt = wt
        u, g = ctypes.WinDLL("user32"), ctypes.WinDLL("gdi32")      # свои экземпляры: типы аргументов не влияют на остальной код
        u.GetParent.argtypes, u.GetParent.restype = [wt.HWND], wt.HWND
        u.GetWindowLongW.argtypes, u.GetWindowLongW.restype = [wt.HWND, ctypes.c_int], ctypes.c_long
        u.SetWindowLongW.argtypes, u.SetWindowLongW.restype = [wt.HWND, ctypes.c_int, ctypes.c_long], ctypes.c_long
        u.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
        u.SetWindowPos.restype = wt.BOOL
        u.ShowWindow.argtypes, u.ShowWindow.restype = [wt.HWND, ctypes.c_int], wt.BOOL
        u.IsWindowVisible.argtypes, u.IsWindowVisible.restype = [wt.HWND], wt.BOOL
        u.GetDC.argtypes, u.GetDC.restype = [wt.HWND], wt.HDC
        u.ReleaseDC.argtypes, u.ReleaseDC.restype = [wt.HWND, wt.HDC], ctypes.c_int
        u.UpdateLayeredWindow.argtypes = [wt.HWND, wt.HDC, ctypes.POINTER(wt.POINT), ctypes.POINTER(wt.SIZE), wt.HDC,
                                          ctypes.POINTER(wt.POINT), wt.COLORREF, ctypes.POINTER(_BLEND), wt.DWORD]
        u.UpdateLayeredWindow.restype = wt.BOOL
        g.CreateCompatibleDC.argtypes, g.CreateCompatibleDC.restype = [wt.HDC], wt.HDC
        g.CreateDIBSection.argtypes = [wt.HDC, ctypes.POINTER(_BMIH), wt.UINT, ctypes.POINTER(ctypes.c_void_p),
                                       wt.HANDLE, wt.DWORD]
        g.CreateDIBSection.restype = wt.HBITMAP
        g.SelectObject.argtypes, g.SelectObject.restype = [wt.HDC, wt.HGDIOBJ], wt.HGDIOBJ
        g.DeleteObject.argtypes, g.DeleteObject.restype = [wt.HGDIOBJ], wt.BOOL
        g.DeleteDC.argtypes, g.DeleteDC.restype = [wt.HDC], wt.BOOL
        self.u, self.g = u, g

        self.top = tk.Toplevel(root)
        self.top.overrideredirect(True)
        self.top.attributes("-topmost", True)
        self.top.geometry("1x1+0+0")
        self.top.update_idletasks()
        self.hwnd = u.GetParent(self.top.winfo_id()) or self.top.winfo_id()
        # WS_EX_LAYERED | WS_EX_TOOLWINDOW (нет кнопки в панели задач) | WS_EX_NOACTIVATE (не забирает фокус)
        style = u.GetWindowLongW(self.hwnd, -20) | 0x00080000 | 0x00000080 | 0x08000000
        u.SetWindowLongW(self.hwnd, -20, style)
        self.placed = None                              # где слой стоит сейчас: (x, y)
        self.shown = False

    def move(self, x, y):
        self.placed = (int(x), int(y))
        self.u.SetWindowPos(self.hwnd, None, int(x), int(y), 0, 0, 0x0001 | 0x0004 | 0x0010)

    def update(self, img, x, y):
        """Показать картинку RGBA в точке (x, y) экрана."""
        wt, u, g = self.wt, self.u, self.g
        w, h = img.size
        data = img.convert("RGBa").tobytes("raw", "BGRa")      # Windows ждёт цвета, умноженные на прозрачность
        hdc = u.GetDC(None)
        mem = g.CreateCompatibleDC(hdc)
        bmi = _BMIH(ctypes.sizeof(_BMIH), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
        bits = ctypes.c_void_p()
        hbm = g.CreateDIBSection(hdc, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
        try:
            if not hbm or not bits.value:
                raise OSError("CreateDIBSection не удался")
            ctypes.memmove(bits, data, len(data))
            old = g.SelectObject(mem, hbm)
            pos, size, src = wt.POINT(int(x), int(y)), wt.SIZE(w, h), wt.POINT(0, 0)
            blend = _BLEND(0, 0, 255, 1)                         # AC_SRC_OVER, значение прозрачности из самих пикселей
            ok = u.UpdateLayeredWindow(self.hwnd, hdc, ctypes.byref(pos), ctypes.byref(size), mem,
                                       ctypes.byref(src), 0, ctypes.byref(blend), 2)
            g.SelectObject(mem, old)
            if not ok:
                raise OSError("UpdateLayeredWindow не удался")
            self.placed = (int(x), int(y))
            if not self.shown:                                   # показать без захвата фокуса, поверх всех окон
                u.ShowWindow(self.hwnd, 4)
                u.SetWindowPos(self.hwnd, wt.HWND(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040)
                self.shown = True
            if not u.IsWindowVisible(self.hwnd):
                raise OSError("окно слоя текста скрыто")
        finally:
            if hbm:
                g.DeleteObject(hbm)
            g.DeleteDC(mem)
            u.ReleaseDC(None, hdc)


class _RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long), ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class _MONINFO(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_ulong), ("rcMonitor", _RECT), ("rcWork", _RECT), ("dwFlags", ctypes.c_ulong)]


def work_area(px, py, fallback):
    """Рабочая область (без панели задач) монитора, на котором находится точка."""
    try:
        from ctypes import wintypes
        u = ctypes.windll.user32
        u.MonitorFromPoint.argtypes = [wintypes.POINT, wintypes.DWORD]
        u.MonitorFromPoint.restype = wintypes.HANDLE
        u.GetMonitorInfoW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_MONINFO)]
        mon = u.MonitorFromPoint(wintypes.POINT(int(px), int(py)), 2)      # 2 — ближайший монитор
        mi = _MONINFO()
        mi.cbSize = ctypes.sizeof(_MONINFO)
        if mon and u.GetMonitorInfoW(mon, ctypes.byref(mi)):
            r = mi.rcWork
            return r.left, r.top, r.right, r.bottom
    except Exception:
        pass
    return fallback



class Overlay:
    def __init__(self):
        self.root = r = tk.Tk()
        self.light = False                 # False: белый текст (тёмный фон), True: тёмный текст (светлый фон)
        self.key = KEY_DARK
        self.lum = None
        self.theme_at = 0.0
        r.title("Lyrics Overlay")
        r.overrideredirect(True)                       # без рамки
        r.attributes("-topmost", True)                 # поверх всех окон
        try:
            r.attributes("-transparentcolor", self.key)   # прозрачный фон
        except tk.TclError:
            pass
        r.configure(bg=self.key)
        r.geometry("+200+200")
        r.protocol("WM_DELETE_WINDOW", r.destroy)

        try:                                           # значок 🤩
            self.icon = tk.PhotoImage(data=base64.b64encode(make_icon_png()))
            r.iconphoto(True, self.icon)
        except Exception:
            log_error()

        self.percent = 100
        self.pos = (200, 200)      # где сейчас окно (мы сами его двигаем)
        self.prefs = load_settings()
        try:                                                           # размер текста и место окна с прошлого раза
            self.percent = max(MIN_PERCENT, min(MAX_PERCENT, int(self.prefs.get("percent", 100))))
            px0, py0 = self.prefs.get("pos_x"), self.prefs.get("pos_y")
            if px0 is not None and py0 is not None:
                r.geometry(f"+{int(px0)}+{int(py0)}")                  # за край экрана выведет keep_on_screen
                self.pos = (int(px0), int(py0))
        except (TypeError, ValueError):
            pass
        self.sync_offset = float(self.prefs.get("sync_offset", 0.0))   # подстройка, секунды
        self.theme_pref = self.prefs.get("theme", THEME_MODE)          # auto / white / dark
        if self.theme_pref not in ("auto", "white", "dark"):
            self.theme_pref = "auto"
        self.align_pref = ALIGN_MODE        # auto / left / center / right
        self.mode = "left"                  # реальное выравнивание сейчас
        self.dx = self.dy = 0
        self.dragging = False
        self.scroll = 0
        self.plain_focus = 0.0
        self.focus = 0.0
        self.target = 0
        self.anim_from = 0.0
        self.anim_t0 = 0.0
        self.src_key = None

        self.items = {}            # номер строки -> (id обводки, id текста)
        self.colors = {}
        self.hcache = {}
        self.layout_key = None
        self.tb_key = None
        self.ctl_key = None
        self.last_sig = None
        self.canvas_h = 0
        self.err_count = 0
        self.drag_pos = None       # куда тащим окно (последняя позиция мыши)
        self.drag_job = None       # отложенное перемещение (склеивает частые события мыши)
        self.hwnd = None           # окно Windows — для быстрого перемещения
        self.smooth = SmoothText() if PIL_OK else None     # сглаженный текст (нет Pillow — обычный)
        self.smooth_off = False    # сглаживание сломалось: дальше обычный текст
        self.layer = None          # окно со сглаженным текстом (TextLayer)
        self.px_k = 96 / 72                                # пикселей в одном пункте шрифта
        try:
            self.px_k = float(r.winfo_fpixels("1i")) / 72
        except Exception:
            pass
        self.chrome_a = 1.0        # прозрачность кнопок: 0 — скрыты, 1 — видны (меняется плавно)
        self.chrome_t = time.time()
        self.chrome_qa = None      # какая прозрачность нарисована сейчас
        self.tb_off = self.ctl_off = 0     # на сколько пикселей сдвинуты кнопки (анимация появления)
        self.need_clamp = True     # проверить, что окно целиком на экране
        self.glide = None          # плавный возврат окна на экран: (x0, y0, x1, y1, время начала)
        self.drag_ref = None       # где мышь при перетаскивании (чтобы выбрать нужный монитор)
        self.sw_moved_at = 0.0     # когда двигали окно настроек (чтобы не тормозить его)
        self.sw_key = None

        self.btn_items = {}        # имя кнопки -> (холст, [id], основной цвет)
        self.btn_imgs = {}         # имя кнопки -> (размер, играет ли) для значков-картинок
        self.icon_cache = {}
        self.karaoke = bool(self.prefs.get("karaoke", True))           # подсветка спетой части строки (как в караоке)
        self.kara = None
        self.hide_chrome = bool(self.prefs.get("hide_chrome", True))   # кнопки видны, только пока курсор над окном
        self.chrome_vis = True
        self.chrome_out_at = 0.0
        self.hover = None
        self.pct_item = None
        self.tip = None
        self.tip_lbl = None

        self.font = (FONT_FAMILY, START_FONT_SIZE, "bold")
        self.linespace = 36
        self.offsets = []
        self.text_x, self.text_anchor, self.text_justify = TEXT_LEFT_PAD, "nw", "left"

        # три прозрачных холста друг под другом: панель, текст, кнопки плеера
        self.tb = self.make_canvas(TB_H)
        self.canvas = self.make_canvas(10)
        self.ctl = self.make_canvas(CTL_H)
        for cv in (self.tb, self.canvas, self.ctl):
            cv.pack(anchor="w")
        self.make_layer()

        # --- окно настроек (вместо маленького меню). Открывается по ☰ и правой кнопке ---
        self.settings_win = None
        self.sw_btns = {}          # (группа, значение) -> кнопка, чтобы подсвечивать выбранную
        self.sw_pct = None
        self.sw_sync = None

        r.after(300, self.enable_taskbar)
        r.after(1200, self.maybe_ask_shortcuts)
        self.tick()

    def make_layer(self):
        """Окно для сглаженного текста; ловит мышь так же, как холст (перетаскивание, колесо, меню)."""
        if self.smooth is None:
            return
        try:
            self.layer = TextLayer(self.root)
            top = self.layer.top
            top.bind("<ButtonPress-1>", self.on_press)
            top.bind("<B1-Motion>", self.on_drag)
            top.bind("<ButtonRelease-1>", self.on_release)
            top.bind("<Motion>", self.on_motion)
            top.bind("<Leave>", self.on_leave)
            top.bind("<Button-3>", self.show_menu)
            top.bind("<MouseWheel>", self.on_wheel)
        except Exception:
            log_error()
            self.layer = None
            self.smooth = None

    def make_canvas(self, height):
        cv = tk.Canvas(self.root, bg=self.key, highlightthickness=0,
                       width=self.width(), height=height)
        cv.bind("<ButtonPress-1>", self.on_press)
        cv.bind("<B1-Motion>", self.on_drag)
        cv.bind("<ButtonRelease-1>", self.on_release)
        cv.bind("<Motion>", self.on_motion)
        cv.bind("<Leave>", self.on_leave)
        cv.bind("<Button-3>", self.show_menu)
        cv.bind("<MouseWheel>", self.on_wheel)
        return cv

    # ---------- панель задач
    def enable_taskbar(self):
        """Окно без рамки по умолчанию не показывается в панели задач — включаем."""
        try:
            user32 = ctypes.windll.user32
            hwnd = user32.GetParent(self.root.winfo_id()) or self.root.winfo_id()
            GWL_EXSTYLE, WS_EX_APPWINDOW, WS_EX_TOOLWINDOW = -20, 0x00040000, 0x00000080
            style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
            style = (style & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW
            user32.SetWindowLongW(hwnd, GWL_EXSTYLE, style)
            try:
                set_window_app_id(hwnd, "LyricsOverlay.Window")
            except Exception:
                log_error()
            self.root.withdraw()
            self.root.after(60, self._show_again)
        except Exception:
            log_error()

    def _show_again(self):
        self.root.deiconify()
        self.root.attributes("-topmost", True)
        if self.layer is not None:
            self.layer.top.attributes("-topmost", True)

    # ---------- меню: выравнивание и подстройка синхронизации
    def set_karaoke(self, value):
        self.karaoke = bool(value)
        self.last_sig = None
        self.save_prefs()

    def set_chrome(self, value):
        self.hide_chrome = bool(value)
        self.save_prefs()

    def set_align(self, value):
        self.align_pref = value

    def change_sync(self, delta):
        """Сдвиг текста относительно песни: плюс — строки появляются раньше."""
        self.sync_offset = 0.0 if delta is None else max(-3.0, min(3.0, round(self.sync_offset + delta, 2)))
        self.save_prefs()

    def save_prefs(self):
        self.prefs.update(sync_offset=self.sync_offset, theme=self.theme_pref, hide_chrome=self.hide_chrome, karaoke=self.karaoke,
                          percent=self.percent)
        try:
            self.prefs.update(pos_x=self.pos[0], pos_y=self.pos[1])
        except (tk.TclError, TypeError, IndexError):
            pass
        save_settings(self.prefs)

    # ---------- тема: цвет текста под фон
    def set_theme(self, value):
        self.theme_pref = value
        self.theme_at = 0.0
        self.save_prefs()

    def text_color(self):
        return shade(0, self.light)

    def outline_color(self):
        return "#FFFFFF" if self.light else "#000000"

    def apply_theme(self, light):
        """Меняем цвет «прозрачности»: края букв сглаживаются именно под цвет фона."""
        self.light = light
        self.key = KEY_LIGHT if light else KEY_DARK
        self.root.configure(bg=self.key)
        try:
            self.root.attributes("-transparentcolor", self.key)
        except tk.TclError:
            pass
        for cv in (self.tb, self.canvas, self.ctl):
            cv.configure(bg=self.key)
        self.layout_key = None            # пересоздать текст в новых цветах
        self.tb_key = None                # и панель (цвет области нажатия)
        self.ctl_key = None

    def sample_background(self):
        """Средняя яркость экрана вокруг текста (0..255) или None. Точки берём вне букв."""
        try:
            c = self.canvas
            boxes = [it.rect if it.smooth else c.bbox(it.main) for it in self.items.values()]
            boxes = [b for b in boxes if b]
            if not boxes:
                return None
            x1, x2 = min(b[0] for b in boxes), max(b[2] for b in boxes)
            y1, y2 = min(b[1] for b in boxes), max(b[3] for b in boxes)
            pts = []
            for k in range(5):
                fx = x1 + (x2 - x1) * (k + 0.5) / 5
                pts += [(fx, y1 - 6), (fx, y2 + 6)]
            for k in range(2):
                fy = y1 + (y2 - y1) * (k + 0.5) / 2
                pts += [(x1 - 6, fy), (x2 + 6, fy)]
            ox, oy = self.pos[0], self.pos[1] + TB_H                 # левый верхний угол области текста на экране
            sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
            user32, gdi32 = ctypes.windll.user32, ctypes.windll.gdi32
            hdc = user32.GetDC(0)
            lums = []
            try:
                for x, y in pts:
                    sx, sy = int(ox + x), int(oy + y)
                    if 0 <= sx < sw and 0 <= sy < sh:
                        col = gdi32.GetPixel(hdc, sx, sy)
                        if col not in (-1, 0xFFFFFFFF):
                            r, g, b = col & 255, (col >> 8) & 255, (col >> 16) & 255
                            lums.append(0.2126 * r + 0.7152 * g + 0.0722 * b)
            finally:
                user32.ReleaseDC(0, hdc)
            if len(lums) < 6:
                return None
            lums.sort()
            return lums[len(lums) // 2]          # медиана: случайные пиксели не мешают
        except Exception:
            return None

    def update_theme(self):
        if self.theme_pref != "auto":
            want = (self.theme_pref == "dark")
        else:
            now = time.time()
            if now - self.theme_at < THEME_SAMPLE_SECONDS:
                return
            self.theme_at = now
            lum = self.sample_background()
            if lum is None:
                return
            self.lum = lum if self.lum is None else 0.6 * self.lum + 0.4 * lum   # сглаживание
            if self.light:
                want = self.lum >= 110            # вернуться к белому тексту, только когда фон заметно потемнел
            else:
                want = self.lum > 150             # перейти на тёмный текст, когда фон явно светлый
        if want != self.light:
            self.apply_theme(want)

    def retry_lyrics(self, artist=None, title=None):
        """Искать текст заново. Можно передать свои исполнителя и название (ручной поиск)."""
        tid = state["track_id"]
        if not tid:
            return
        a = tid[0] if artist is None else artist
        t = tid[1] if title is None else title
        state.update(synced=[], plain=[], status=f"Ищу текст: {a} — {t}")
        threading.Thread(target=fetch_job, args=(tid, a, t, state.get("duration")),
                         daemon=True).start()

    def manual_search(self):
        """Окошко «Искать вручную»: можно поправить исполнителя и название и искать снова."""
        tid = state["track_id"]
        parent = self.settings_win or self.root
        if not tid:
            messagebox.showinfo(APP_NAME, "Сначала включите песню в плеере.", parent=parent)
            return
        win = tk.Toplevel(parent)
        win.title("Искать текст вручную")
        win.configure(bg=SW_BG)
        win.resizable(False, False)
        win.attributes("-topmost", True)
        w, h = 420, 240
        sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
        px = max(0, min(parent.winfo_rootx() + 30, sw - w))
        py = max(0, min(parent.winfo_rooty() + 60, sh - h))
        win.geometry(f"{w}x{h}+{px}+{py}")

        def field(label, value):
            tk.Label(win, text=label, bg=SW_BG, fg=SW_MUTED, anchor="w",
                     font=(FONT_FAMILY, 10, "bold")).pack(fill="x", padx=22, pady=(14, 3))
            e = tk.Entry(win, bg=SW_PANEL, fg=SW_FG, insertbackground=SW_FG, relief="flat",
                         font=(FONT_FAMILY, 12), highlightthickness=1,
                         highlightbackground="#444444", highlightcolor=SW_ON)
            e.insert(0, value)
            e.pack(fill="x", padx=22, ipady=6)
            return e

        e_artist = field("ИСПОЛНИТЕЛЬ", tid[0])
        e_title = field("НАЗВАНИЕ ПЕСНИ", tid[1])

        def go(_event=None):
            a, t = e_artist.get().strip(), e_title.get().strip()
            if not t:
                return
            win.destroy()
            self.retry_lyrics(a, t)

        tk.Button(win, text="Искать", command=go, bg=SW_ON, fg="#FFFFFF", activebackground=SW_ON,
                  activeforeground="#FFFFFF", relief="flat", bd=0, cursor="hand2",
                  font=(FONT_FAMILY, 11, "bold"), pady=9).pack(side="bottom", fill="x", padx=22, pady=(0, 18))
        win.bind("<Return>", go)
        win.bind("<Escape>", lambda _e: win.destroy())
        win.after(60, e_title.focus_force)
        e_title.select_range(0, "end")

    # ---------- вопрос про ярлыки при запуске
    def maybe_ask_shortcuts(self):
        try:
            if ASK_SHORTCUTS_ONLY_IN_EXE and not FROZEN:
                return
            if self.prefs.get("shortcuts_done"):
                return
            paths = shortcut_paths()
            need = {k: v for k, v in paths.items() if v and not os.path.exists(v)}
            if not need:                              # оба ярлыка уже есть — больше не спрашиваем
                self.prefs["shortcuts_done"] = True
                save_settings(self.prefs)
                return
            self.show_shortcut_dialog(need)
        except Exception:
            log_error()

    def show_shortcut_dialog(self, need):
        r = self.root
        win = tk.Toplevel(r)
        win.title(APP_NAME)
        win.configure(bg=SW_BG)
        win.resizable(False, False)
        win.attributes("-topmost", True)
        w, h = 440, 300
        win.geometry(f"{w}x{h}+{(r.winfo_screenwidth() - w) // 2}+{(r.winfo_screenheight() - h) // 3}")
        try:
            win.iconphoto(False, self.icon)
        except Exception:
            pass

        tk.Label(win, text="Добавить ярлыки?", bg=SW_BG, fg=SW_FG,
                 font=(FONT_FAMILY, 14, "bold")).pack(anchor="w", padx=22, pady=(18, 2))
        tk.Label(win, text="Выберите, где создать ярлык для быстрого запуска:", bg=SW_BG, fg=SW_MUTED,
                 font=(FONT_FAMILY, 10)).pack(anchor="w", padx=22, pady=(0, 10))

        vars_ = {}

        def check(parent, text, var, size=11):
            return tk.Checkbutton(parent, text=text, variable=var, bg=SW_BG, fg=SW_FG,
                                  selectcolor=SW_PANEL, activebackground=SW_BG, activeforeground=SW_FG,
                                  highlightthickness=0, bd=0, anchor="w", cursor="hand2",
                                  font=(FONT_FAMILY, size))

        if "start" in need:
            vars_["start"] = tk.BooleanVar(value=True)
            check(win, "Добавить в меню «Пуск»", vars_["start"]).pack(fill="x", padx=22, pady=3)
        if "desktop" in need:
            vars_["desktop"] = tk.BooleanVar(value=True)
            check(win, "Добавить на рабочий стол", vars_["desktop"]).pack(fill="x", padx=22, pady=3)
        never = tk.BooleanVar(value=False)
        check(win, "Больше не спрашивать", never, 10).pack(fill="x", padx=22, pady=(14, 0))

        def close():
            try:
                win.destroy()
            except tk.TclError:
                pass

        def later():
            if never.get():
                self.prefs["shortcuts_done"] = True
                save_settings(self.prefs)
            close()

        def create():
            made, failed = [], []
            for key, var in vars_.items():
                if var.get():
                    (made if make_shortcut(need[key]) else failed).append(
                        "меню «Пуск»" if key == "start" else "рабочий стол")
            self.prefs["shortcuts_done"] = True
            save_settings(self.prefs)
            if failed:
                messagebox.showwarning(APP_NAME, "Не удалось создать ярлык: " + ", ".join(failed),
                                       parent=win)
            elif made:
                messagebox.showinfo(APP_NAME, "Готово! Ярлыки добавлены: " + ", ".join(made) + ".",
                                    parent=win)
            close()

        row = tk.Frame(win, bg=SW_BG)
        row.pack(side="bottom", fill="x", padx=19, pady=(0, 18))
        row.columnconfigure(0, weight=1, uniform="d")
        row.columnconfigure(1, weight=1, uniform="d")
        tk.Button(row, text="Создать ярлыки", command=create, bg=SW_ON, fg="#FFFFFF",
                  activebackground=SW_ON, activeforeground="#FFFFFF", relief="flat", bd=0,
                  cursor="hand2", font=(FONT_FAMILY, 11, "bold"), pady=9
                  ).grid(row=0, column=0, sticky="ew", padx=3)
        tk.Button(row, text="Не сейчас", command=later, bg=SW_PANEL, fg=SW_FG,
                  activebackground=SW_HOVER, activeforeground=SW_FG, relief="flat", bd=0,
                  cursor="hand2", font=(FONT_FAMILY, 11, "bold"), pady=9
                  ).grid(row=0, column=1, sticky="ew", padx=3)
        win.protocol("WM_DELETE_WINDOW", later)

    # ---------- окно настроек 480x520 (вместо маленького меню)
    def open_settings(self):
        if self.settings_win is not None:
            try:
                self.settings_win.deiconify()
                self.settings_win.lift()
                self.settings_win.focus_force()
                return
            except tk.TclError:
                self.settings_win = None
        r = self.root
        win = tk.Toplevel(r)
        win.title("Lyrics Overlay — настройки")
        win.configure(bg=SW_BG)
        win.resizable(False, False)
        win.attributes("-topmost", True)
        sw, sh = r.winfo_screenwidth(), r.winfo_screenheight()
        x = max(0, min(self.pos[0] + 20, sw - SW_W))
        y = max(0, min(self.pos[1] + 40, sh - SW_H))
        win.geometry(f"{SW_W}x{SW_H}+{x}+{y}")
        win.protocol("WM_DELETE_WINDOW", self.close_settings)
        try:
            win.iconphoto(False, self.icon)
        except Exception:
            pass
        try:                                        # заголовок окна в цвет меню (Windows 11)
            win.update_idletasks()
            hwnd = ctypes.windll.user32.GetParent(win.winfo_id())
            for attr, rgb in ((35, SW_BG), (36, "#FFFFFF"), (34, SW_EDGE)):
                bgr = int(rgb[5:7] + rgb[3:5] + rgb[1:3], 16)
                ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, attr, ctypes.byref(ctypes.c_int(bgr)), 4)
        except Exception:
            pass
        self.settings_win = win
        self.sw_btns = {}
        self.sw_key = None
        win.bind("<Configure>", self.on_sw_configure)

        card_inner = None

        def section(title):
            nonlocal card_inner
            card = RoundCard(win)
            card.pack(fill="x", padx=16, pady=(10, 0))
            card_inner = card.inner
            tk.Label(card_inner, text=title, bg=SW_CARD, fg=SW_MUTED, anchor="w",
                     font=(FONT_FAMILY, 10, "bold")).pack(fill="x", pady=(0, 8))
            f = tk.Frame(card_inner, bg=SW_CARD)
            f.pack(fill="x")
            return f

        def hint(text):
            tk.Label(card_inner, text=text, bg=SW_CARD, fg=SW_MUTED, anchor="w",
                     font=(FONT_FAMILY, 9)).pack(fill="x", pady=(8, 0))

        def button(parent, text, command, col, group=None, value=None):
            b = RBtn(parent, text=text, command=command)
            b.grid(row=0, column=col, sticky="ew", padx=3)
            parent.columnconfigure(col, weight=1, uniform=str(id(parent)))
            if group:
                self.sw_btns[(group, value)] = b
            return b

        def act(fn, *args):
            def run():
                fn(*args)
                self.refresh_settings()
            return run

        # размер текста
        f = section("РАЗМЕР ТЕКСТА")
        button(f, "Мельче  А−", act(self.change_size, -10), 0)
        self.sw_pct = tk.Label(f, text="", bg=SW_CARD, fg=SW_FG, font=(FONT_FAMILY, 12, "bold"))
        self.sw_pct.grid(row=0, column=1, sticky="ew")
        f.columnconfigure(1, weight=1, uniform=str(id(f)))
        button(f, "Крупнее  А+", act(self.change_size, +10), 2)

        # выравнивание
        f = section("ВЫРАВНИВАНИЕ")
        for c, (label, v) in enumerate((("Авто", "auto"), ("Слева", "left"),
                                        ("По центру", "center"), ("Справа", "right"))):
            button(f, label, act(self.set_align, v), c, "align", v)
        hint("Авто — зависит от положения окна на экране")

        # цвет текста
        f = section("ЦВЕТ ТЕКСТА")
        for c, (label, v) in enumerate((("Авто (по фону)", "auto"), ("Белый", "white"),
                                        ("Тёмный", "dark"))):
            button(f, label, act(self.set_theme, v), c, "theme", v)
        hint("Белый — для тёмного фона, тёмный — для светлого")

        # кнопки на оверлее
        f = section("ОТОБРАЖЕНИЕ")
        button(f, "Скрывать кнопки без курсора", act(self.set_chrome, True), 0, "chrome", True)
        button(f, "Показывать всегда", act(self.set_chrome, False), 1, "chrome", False)
        hint("Кнопки появляются, когда курсор наведён на текст")
        f2 = tk.Frame(card_inner, bg=SW_CARD)
        f2.pack(fill="x", pady=(10, 0))
        button(f2, "Караоке: по буквам", act(self.set_karaoke, True), 0, "karaoke", True)
        button(f2, "Вся строка сразу", act(self.set_karaoke, False), 1, "karaoke", False)
        hint("Караоке: спетая часть строки белая, остальное серое")

        # синхронизация
        f = section("СИНХРОНИЗАЦИЯ ТЕКСТА")
        button(f, "Позже −0.1 с", act(self.change_sync, -0.1), 0)
        self.sw_sync = tk.Label(f, text="", bg=SW_CARD, fg=SW_FG, font=(FONT_FAMILY, 12, "bold"))
        self.sw_sync.grid(row=0, column=1, sticky="ew")
        f.columnconfigure(1, weight=1, uniform=str(id(f)))
        button(f, "Раньше +0.1 с", act(self.change_sync, +0.1), 2)
        button(f, "Сбросить", act(self.change_sync, None), 3)

        # действия
        f = section("ДЕЙСТВИЯ")
        button(f, "Найти текст заново", self.retry_lyrics, 0)
        button(f, "Искать вручную…", self.manual_search, 1)

        # нижний ряд: «Главное меню» и «Закрыть приложение»
        bottom = tk.Frame(win, bg=SW_BG)
        bottom.pack(side="bottom", fill="x", padx=16, pady=(14, 16))
        bottom.columnconfigure(0, weight=1, uniform="bottom")
        bottom.columnconfigure(1, weight=1, uniform="bottom")
        back = RBtn(bottom, text="← Главное меню", command=self.back_to_main_menu,
                    font=(FONT_FAMILY, 11, "bold"), pady=9)
        back.grid(row=0, column=0, sticky="ew", padx=(0, 5))
        close = RBtn(bottom, text="Закрыть приложение", command=self.root.destroy,
                     bg="#3A2227", fg=COLOR_CLOSE, activebackground="#4D2B32", activeforeground=COLOR_CLOSE,
                     font=(FONT_FAMILY, 11, "bold"), pady=9)
        close.grid(row=0, column=1, sticky="ew", padx=(5, 0))

        win.update_idletasks()                      # высота окна — по содержимому, чтобы ничего не прижималось
        hh = max(win.winfo_reqheight(), 200)
        win.geometry(f"{SW_W}x{hh}+{x}+{max(0, min(y, sh - hh - 48))}")
        self.refresh_settings()
        self.settings_loop()

    def back_to_main_menu(self):
        """Кнопка «← Главное меню»: просит Nowring показать своё окно и закрывает оверлей."""
        ok = False
        try:
            k32 = ctypes.windll.kernel32
            k32.OpenEventW.restype = ctypes.c_void_p
            k32.OpenEventW.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_wchar_p]
            k32.SetEvent.argtypes = [ctypes.c_void_p]
            k32.CloseHandle.argtypes = [ctypes.c_void_p]
            h = k32.OpenEventW(0x0002, False, "Nowring.ShowWindow")      # EVENT_MODIFY_STATE
            if h:
                ok = bool(k32.SetEvent(h))
                k32.CloseHandle(h)
        except Exception:
            ok = False
        if ok:
            self.root.destroy()                     # Nowring открыл главное окно — оверлей закрываем

    def on_sw_configure(self, e):
        if e.widget is self.settings_win:           # окно настроек двигают — запоминаем момент
            self.sw_moved_at = time.time()

    def close_settings(self):
        win, self.settings_win = self.settings_win, None
        if win is not None:
            try:
                win.destroy()
            except tk.TclError:
                pass

    def refresh_settings(self):
        """Обновляет подписи и подсветку выбранных кнопок."""
        if self.settings_win is None:
            return
        key = (self.percent, round(self.sync_offset, 2), self.align_pref, self.theme_pref, self.hide_chrome, self.karaoke)
        if key == self.sw_key:                      # ничего не изменилось — ничего не перерисовываем
            return
        self.sw_key = key
        try:
            self.sw_pct.configure(text=f"{self.percent}%")
            self.sw_sync.configure(text=f"{self.sync_offset:+.1f} с")
            for (group, value), b in self.sw_btns.items():
                cur = {"align": self.align_pref, "theme": self.theme_pref, "chrome": self.hide_chrome, "karaoke": self.karaoke}[group]
                if cur == value:
                    b.configure(bg=SW_ON, fg="#FFFFFF", activebackground=SW_ON, activeforeground="#FFFFFF")
                else:
                    b.configure(bg=SW_PANEL, fg=SW_FG, activebackground=SW_HOVER, activeforeground=SW_FG)
        except tk.TclError:
            self.settings_win = None

    def settings_loop(self):
        """Пока окно открыто, подтягиваем изменения (например, размер колесом Ctrl+колесо)."""
        if self.settings_win is None:
            return
        self.refresh_settings()
        if self.settings_win is not None:
            self.settings_win.after(400, self.settings_loop)

    def pick_mode(self):
        if self.dragging:                 # пока тащим окно — ничего не перестраиваем
            return self.mode
        if self.align_pref != "auto":
            return self.align_pref
        sw = self.root.winfo_screenwidth()
        # положение считаем на том мониторе, где окно (на втором мониторе x больше ширины первого)
        left, _, right, _ = work_area(self.pos[0] + self.width() / 2, self.pos[1] + 10, (0, 0, sw, 0))
        free = (right - left) - self.width()
        if free <= 40:
            return "center"
        p = (self.pos[0] - left) / free     # 0 = у левого края, 1 = у правого
        h = 0.04                                    # небольшой «гистерезис», чтобы не мигало на границе
        t1 = 1 / 3 + (h if self.mode == "left" else -h)
        t2 = 2 / 3 - (h if self.mode == "right" else -h)
        return "left" if p < t1 else ("right" if p > t2 else "center")

    def row_xs(self, widths, gap, w):
        """X-координаты ряда элементов с учётом выравнивания."""
        total = sum(widths) + gap * (len(widths) - 1)
        if self.mode == "left":
            x = UI_PAD
        elif self.mode == "right":
            x = w - UI_PAD - total
        else:
            x = (w - total) / 2
        xs = []
        for wd in widths:
            xs.append(x)
            x += wd + gap
        return xs

    # ---------- мышь: перетаскивание, кнопки, подсказки
    def button_at(self, cv, x, y):
        if not isinstance(cv, tk.Canvas):          # слой текста (не холст): кнопок там нет
            return None
        for item in reversed(cv.find_overlapping(x - 2, y - 2, x + 2, y + 2)):
            for tag in cv.gettags(item):
                if tag.startswith("btn:"):
                    return tag[4:]
        return None

    def on_press(self, e):
        name = self.button_at(e.widget, e.x, e.y)
        self.dragging = False
        if name in (None, "move"):                 # текст, пустое место и значок ✥ — тащат окно
            self.dx = e.x_root - self.pos[0]
            self.dy = e.y_root - self.pos[1]
            self.dragging = True
            return
        self.hide_tip()
        if name == "close":
            self.root.destroy()
        elif name == "menu":
            self.show_menu(e)
        elif name == "smaller":
            self.change_size(-10)
        elif name == "larger":
            self.change_size(+10)
        elif name in ("prev", "play", "next"):
            send_media_command(name)

    def get_hwnd(self):
        if self.hwnd is None:
            try:
                user32 = ctypes.windll.user32
                self.hwnd = user32.GetParent(self.root.winfo_id()) or self.root.winfo_id()
            except Exception:
                self.hwnd = 0
        return self.hwnd

    def window_size(self):
        return self.width(), TB_H + max(self.canvas_h, 10) + CTL_H

    def clamp_pos(self, x, y, ref=None):
        """Ближайшее положение окна, при котором оно целиком на экране (на том мониторе, где мышь или окно)."""
        ww, wh = self.window_size()
        rx, ry = ref if ref else (x + ww / 2, y + wh / 2)
        sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
        l, t, r, b = work_area(rx, ry, (0, 0, sw, sh))
        m = SCREEN_MARGIN
        return int(max(l + m, min(x, r - m - ww))), int(max(t + m, min(y, b - m - wh)))

    def move_to(self, x, y):
        """Двигаем окно (с учётом краёв экрана)."""
        x, y = self.clamp_pos(x, y, self.drag_ref)
        self.move_raw(x, y)

    def move_raw(self, x, y):
        self.pos = (int(x), int(y))
        if self.layer is not None:
            try:
                self.layer.move(x, y + TB_H)
            except Exception:
                pass
        self._move_root(x, y)

    def _move_root(self, x, y):
        """Двигаем окно напрямую через Windows (SetWindowPos): намного плавнее, чем root.geometry()."""
        try:
            hwnd = self.get_hwnd()
            # 0x0001 без смены размера | 0x0004 без смены порядка окон | 0x0010 не активировать
            if hwnd and ctypes.windll.user32.SetWindowPos(hwnd, 0, int(x), int(y), 0, 0, 0x0001 | 0x0004 | 0x0010):
                return
        except Exception:
            pass
        self.hwnd = None
        self.root.geometry(f"+{int(x)}+{int(y)}")

    def apply_drag(self):
        self.drag_job = None
        if self.drag_pos is not None:
            self.move_to(*self.drag_pos)

    def on_drag(self, e):
        if not self.dragging:
            return
        self.glide = None
        self.drag_ref = (e.x_root, e.y_root)
        self.drag_pos = (e.x_root - self.dx, e.y_root - self.dy)
        if self.drag_job is None:                  # много событий мыши -> одно перемещение за раз
            self.drag_job = self.root.after_idle(self.apply_drag)

    def on_release(self, e):
        if self.drag_job is not None:
            self.root.after_cancel(self.drag_job)
            self.drag_job = None
        if self.dragging and self.drag_pos is not None:
            self.move_to(*self.drag_pos)
        self.dragging = False
        self.drag_pos = None
        self.drag_ref = None
        self.need_clamp = True
        self.save_prefs()                                   # запомнить, где окно

    def keep_on_screen(self):
        """Окно выросло (размер, строки) или оказалось у края: плавно возвращаем его целиком на экран."""
        self.need_clamp = False
        x, y = self.pos
        nx, ny = self.clamp_pos(x, y)
        if (nx, ny) != (x, y):
            self.glide = (x, y, nx, ny, time.time())

    def step_glide(self):
        if self.glide is None:
            return
        x0, y0, x1, y1, t0 = self.glide
        t = (time.time() - t0) / 0.2
        if t >= 1:
            self.glide = None
            self.move_raw(x1, y1)
            self.root.after(50, self.save_prefs)
        else:
            k = ease(t)
            self.move_raw(x0 + (x1 - x0) * k, y0 + (y1 - y0) * k)

    def on_motion(self, e):
        if self.dragging:
            return
        name = self.button_at(e.widget, e.x, e.y)
        self.set_hover(name)
        if name in ("prev", "next"):
            self.show_tip(name, e.x_root, e.y_root)
        else:
            self.hide_tip()

    def on_leave(self, e):
        self.set_hover(None)
        self.hide_tip()

    def show_menu(self, e=None):
        """☰ и правая кнопка мыши открывают окно настроек (маленькое меню убрано)."""
        self.hide_tip()
        self.open_settings()

    def icon_image(self, name, s, color, playing):
        key = (name, s, color, playing)
        img = self.icon_cache.get(key)
        if img is None:
            n, mask = _icon_mask(name, s, playing)
            rgb = tuple(int(color[i:i + 2], 16) for i in (1, 3, 5))
            img = self.icon_cache[key] = tk.PhotoImage(data=base64.b64encode(_mask_png(n, mask, rgb)))
        return img

    def faded(self, col):
        """Цвет значка с учётом прозрачности кнопок: смешиваем с «прозрачным» цветом окна."""
        a = 1.0 if self.chrome_qa is None else self.chrome_qa
        if a >= 1.0:
            return col
        k = self.key
        return "#%02x%02x%02x" % tuple(round(int(col[i:i + 2], 16) * a + int(k[i:i + 2], 16) * (1 - a)) for i in (1, 3, 5))

    def paint(self, name, color):
        it = self.btn_items.get(name)
        if not it:
            return
        cv, ids, _ = it
        color = self.faded(color)
        meta = self.btn_imgs.get(name)
        for i in ids:
            if meta:
                cv.itemconfigure(i, image=self.icon_image(name, meta[0], color, meta[1]))
            else:
                cv.itemconfigure(i, fill=color)

    def paint_all(self):
        for name, (_, _, base) in list(self.btn_items.items()):
            self.paint(name, COLOR_ICON_HOVER if name == self.hover else base)
        if self.pct_item is not None:
            self.tb.itemconfigure(self.pct_item, fill=self.faded(COLOR_ICON))

    def set_hover(self, name):
        if name == self.hover:
            return
        old = self.btn_items.get(self.hover)
        if old:
            self.paint(self.hover, old[2])
        self.hover = name
        if name in self.btn_items:
            self.paint(name, COLOR_ICON_HOVER)

    def update_chrome(self, rebuilt=False):
        """Кнопки (панель сверху и плеер снизу) плавно появляются, когда курсор над окном, и плавно скрываются
        после его ухода (если включено в ☰)."""
        want = True
        if self.hide_chrome and not self.dragging and self.settings_win is None:
            r = self.root
            px, py = r.winfo_pointerxy()
            rx, ry = r.winfo_rootx(), r.winfo_rooty()
            inside = rx <= px < rx + r.winfo_width() and ry <= py < ry + r.winfo_height()
            if inside:
                self.chrome_out_at = 0.0
            elif self.chrome_vis:
                if self.chrome_out_at == 0.0:
                    self.chrome_out_at = time.time()
                want = time.time() - self.chrome_out_at < CHROME_HIDE_DELAY
            else:
                want = False
        if want != self.chrome_vis:
            self.chrome_vis = want
            if not want:
                self.set_hover(None)
                self.hide_tip()

        now = time.time()
        dt = min(0.1, max(0.0, now - self.chrome_t))      # длинную паузу между кадрами не пропускаем рывком
        self.chrome_t = now
        target = 1.0 if want else 0.0
        if self.chrome_a < target:
            self.chrome_a = min(target, self.chrome_a + dt / CHROME_FADE_SECONDS)
        elif self.chrome_a > target:
            self.chrome_a = max(target, self.chrome_a - dt / CHROME_FADE_SECONDS)
        qa = round(self.chrome_a * 10) / 10.0
        if rebuilt:
            self.tb_off = self.ctl_off = 0
            self.chrome_qa = None
        if qa != self.chrome_qa:
            self.chrome_qa = qa
            off = round((1 - ease(qa)) * CHROME_SLIDE_PX)       # кнопки чуть выезжают от краёв при появлении
            for cv, attr, sign in ((self.tb, "tb_off", -1), (self.ctl, "ctl_off", 1)):
                new, cur = sign * off, getattr(self, attr)
                if new != cur:
                    cv.move("all", 0, new - cur)
                    setattr(self, attr, new)
                cv.itemconfigure("all", state="normal" if qa > 0 else "hidden")
            self.paint_all()

    def check_hover(self):
        """Страховка: если мышь ушла с кнопки, а событие «ушла» не пришло — убираем подсветку."""
        if self.hover is None:
            return
        cv = self.btn_items.get(self.hover, (None,))[0]
        if cv is None:
            self.set_hover(None)
            self.hide_tip()
            return
        px, py = self.root.winfo_pointerxy()
        if self.button_at(cv, px - cv.winfo_rootx(), py - cv.winfo_rooty()) != self.hover:
            self.set_hover(None)
            self.hide_tip()

    # ---------- карточка-подсказка над курсором
    def tip_text(self, which):
        h, i = state["history"], state["hist_idx"]
        j = i - 1 if which == "prev" else i + 1
        if i >= 0 and 0 <= j < len(h):
            artist, title = h[j]
            name = f"{artist} — {title}" if artist else title
            return ("Предыдущая: " if which == "prev" else "Следующая: ") + name
        return "Предыдущая песня" if which == "prev" else "Следующая песня"

    def show_tip(self, which, x, y):
        if self.tip is None:
            t = tk.Toplevel(self.root)
            t.overrideredirect(True)
            t.attributes("-topmost", True)
            t.configure(bg=TIP_BORDER)
            self.tip_lbl = tk.Label(t, bg=TIP_BG, fg=TIP_FG, padx=12, pady=6, justify="left",
                                    wraplength=460, font=(FONT_FAMILY, 11, "bold"))
            self.tip_lbl.pack(padx=1, pady=1)
            self.tip = t
        text = self.tip_text(which)
        if self.tip_lbl.cget("text") != text:
            self.tip_lbl.configure(text=text)
        self.tip.update_idletasks()
        tw, th = self.tip.winfo_reqwidth(), self.tip.winfo_reqheight()
        px = max(0, min(x - tw // 2, self.root.winfo_screenwidth() - tw))
        py = y - th - 18                                    # над курсором
        if py < 0:
            py = y + 24
        self.tip.geometry(f"+{px}+{py}")
        self.tip.deiconify()

    def hide_tip(self):
        if self.tip is not None:
            self.tip.withdraw()

    # ---------- размер
    def fsize(self):
        return max(7, round(START_FONT_SIZE * self.percent / 100))

    def width(self):
        return min(int(self.fsize() * 34), self.root.winfo_screenwidth() - 60)

    def change_size(self, delta):
        self.percent = max(MIN_PERCENT, min(MAX_PERCENT, self.percent + delta))
        for cv in (self.tb, self.canvas, self.ctl):
            cv.configure(width=self.width())
        self.need_clamp = True
        self.save_prefs()                                   # запомнить размер текста

    def on_wheel(self, e):
        if e.state & 0x4:                                   # Ctrl + колесо = размер
            self.change_size(+10 if e.delta > 0 else -10)
        else:                                               # колесо = прокрутка обычного текста
            self.scroll = max(0, self.scroll + (-1 if e.delta > 0 else 1))

    # ---------- какие строки показывать
    def set_target(self, idx, src):
        now = time.time()
        if src != self.src_key:                             # новая песня — без анимации
            self.src_key = src
            self.focus = self.anim_from = float(idx)
            self.target, self.anim_t0 = idx, now
            return
        if idx != self.target:
            if abs(idx - self.focus) > 4:                   # перемотка — прыжок без анимации
                self.focus = self.anim_from = float(idx)
            else:
                self.anim_from = self.focus
            self.target, self.anim_t0 = idx, now
        t = (now - self.anim_t0) / ANIM_SECONDS
        if t >= 1:
            self.focus = float(self.target)
        else:
            self.focus = self.anim_from + (self.target - self.anim_from) * ease(t)

    def kara_progress(self, synced, idx, pos):
        """Какая часть строки idx уже спета (0..1): по времени до следующей строки, но не дольше, чем строку реально поют."""
        t0, text = synced[idx]
        n = len(text.strip())
        if n == 0:
            return None
        t1 = synced[idx + 1][0] if idx + 1 < len(synced) else t0 + 4.0
        dur = min(max(0.2, t1 - t0), max(KARAOKE_MIN_SEC, n * KARAOKE_SEC_PER_CHAR))
        return max(0.0, min(1.0, (pos - t0) / dur))

    def get_view(self):
        self.kara = None
        compact = self.percent <= COMPACT_AT_PERCENT
        before, after = (BEFORE_COMPACT, AFTER_COMPACT) if compact else (BEFORE_NORMAL, AFTER_NORMAL)

        if state["status"]:
            self.src_key = None
            return [state["status"]], 0.0, 0, 0, False, ("status", state["status"])

        synced = state["synced"]
        if synced:
            pos = state["position"] + LEAD_SECONDS + self.sync_offset
            if state["playing"]:
                pos += time.time() - state["pos_time"]
            idx = 0
            for i, (t, _) in enumerate(synced):
                if t <= pos:
                    idx = i
                else:
                    break
            src = ("sync", state["track_id"])
            self.set_target(idx, src)
            if self.karaoke:
                p = self.kara_progress(synced, idx, pos)
                if p is not None:
                    self.kara = (idx, p)
            return [l or "♪" for _, l in synced], self.focus, before, after, True, src

        plain = state["plain"]
        src = ("plain", state["track_id"])
        if src != self.src_key:
            self.src_key = src
            self.scroll, self.plain_focus = 0, 0.0
        self.scroll = min(self.scroll, max(0, len(plain) - 1))
        self.plain_focus += (self.scroll - self.plain_focus) * 0.25
        if abs(self.scroll - self.plain_focus) < 0.01:
            self.plain_focus = float(self.scroll)
        full = [PLAIN_HINT] + [l or " " for l in plain]
        self.scroll = min(self.scroll, max(0, len(full) - 1))
        return full, self.plain_focus, 0, before + after, False, src

    # ---------- значки панели и кнопок плеера
    def register(self, cv, name, ids, base):
        self.btn_items[name] = (cv, ids, base)

    def drop_buttons(self, cv):
        self.btn_items = {k: v for k, v in self.btn_items.items() if v[0] is not cv}
        if self.hover not in self.btn_items:
            self.hover = None

    def hit_color(self):
        """Цвет подложки на один тон отличается от «прозрачного»: Windows считает такую область окном
        (ловит нажатия), а на тёмном/светлом фоне она почти не заметна."""
        return "#FDFDFD" if self.light else "#020202"

    def draw_icon(self, cv, name, x, y, s):
        """Значки — сглаженные картинки (рисуются с подпиксельной точностью, без «лесенки»)."""
        col = COLOR_CLOSE if name == "close" else COLOR_ICON
        tag = ("btn:" + name,)
        if name == "move" and MOVE_HIT_ENABLED:             # невидимая «подложка»: нажатие в пустоту внутри значка тоже работает
            pad = MOVE_HIT_PAD
            cv.create_rectangle(x - pad, y - pad + 1, x + s + pad, y + s + pad,
                                fill=self.hit_color(), outline="", tags=tag)
        playing = bool(state["playing"])
        img = self.icon_image(name, s, col, playing)
        item = cv.create_image(x - 1, y - 1, image=img, anchor="nw", tags=tag)
        self.btn_imgs[name] = (s, playing)
        self.register(cv, name, [item], col)

    def build_toolbar(self):
        cv, w = self.tb, self.width()
        cv.delete("all")
        self.drop_buttons(cv)
        self.pct_item = None
        S = ICON_SIZE
        names = ["move", "menu", "close"]
        if self.mode == "right":                             # зеркальный порядок
            names.reverse()
        for name, x in zip(names, self.row_xs([S] * 3, 8, w)):
            self.draw_icon(cv, name, x, 4, S)

        y = 4 + S + 8
        widths = [34, 56, 34]
        font = (FONT_FAMILY, 12, "bold")
        xs = self.row_xs(widths, 2, w)
        for (name, label), x, wd in zip((("smaller", "А−"), (None, f"{self.percent}%"), ("larger", "А+")),
                                        xs, widths):
            if name:
                item = cv.create_text(x + wd / 2, y + 10, text=label, font=font,
                                      fill=COLOR_ICON, tags=("btn:" + name,))
                self.register(cv, name, [item], COLOR_ICON)
            else:
                self.pct_item = cv.create_text(x + wd / 2, y + 10, text=label,
                                               font=(FONT_FAMILY, 11, "bold"), fill=COLOR_ICON)

    def build_controls(self):
        cv, w = self.ctl, self.width()
        cv.delete("all")
        self.drop_buttons(cv)
        S = MEDIA_SIZE
        for name, x in zip(("prev", "play", "next"), self.row_xs([S] * 3, 14, w)):
            self.draw_icon(cv, name, x, 4, S)

    # ---------- рисование текста
    def px(self):
        """Размер шрифта в пикселях."""
        return max(8, round(self.fsize() * self.px_k))

    def smooth_ok(self, text):
        return (self.smooth is not None and self.layer is not None and not self.smooth_off
                and SmoothText.kind(text) is not None)

    def smooth_failed(self):
        """Сглаженный текст не получился: записываем причину и возвращаемся к обычному выводу."""
        log_error()
        self.smooth_off = True
        self.hcache.clear()
        self.layout_key = None
        if self.layer is not None:
            try:
                self.layer.update(Image.new("RGBA", (1, 1), (0, 0, 0, 0)), self.pos[0], self.pos[1] + TB_H)
            except Exception:
                pass

    def measure(self, text):
        key = (text, self.fsize(), self.width(), self.mode, self.smooth_off)
        h = self.hcache.get(key)
        if h is None:
            if self.smooth_ok(text):
                try:
                    h = self.smooth.height(text, self.px(), self.width() - 24)
                except Exception:
                    self.smooth_failed()
                    h = None
            if h is None:
                item = self.canvas.create_text(-9999, -9999, text=text, font=self.font,
                                               width=self.width() - 24, justify=self.text_justify,
                                               anchor=self.text_anchor)
                bb = self.canvas.bbox(item)
                self.canvas.delete(item)
                h = (bb[3] - bb[1]) if bb else self.linespace
            if len(self.hcache) > 3000:
                self.hcache.clear()
            self.hcache[key] = h
        return h

    def create_items(self, i, text):
        c, w = self.canvas, self.width()
        if self.smooth_ok(text):
            try:
                self.smooth.layout(text, self.px(), w - 24)
                self.items[i] = LineItem(True, [], None, self.smooth.pad(self.px()))
                return
            except Exception:
                self.smooth_failed()
        opts = dict(text=text, font=self.font, width=w - 24,
                    justify=self.text_justify, anchor=self.text_anchor)
        outline = [c.create_text(0, 0, fill=self.outline_color(), **opts) for _ in self.offsets]
        main = c.create_text(0, 0, fill=self.text_color(), **opts)
        self.items[i] = LineItem(False, outline, main)

    @staticmethod
    def blit(base, layer, x, y):
        """Накладываем картинку на слой, обрезая то, что выходит за края."""
        bw, bh = base.size
        lw, lh = layer.size
        sx, sy, dx, dy = max(0, -x), max(0, -y), max(0, x), max(0, y)
        cw, ch = min(lw - sx, bw - dx), min(lh - sy, bh - dy)
        if cw <= 0 or ch <= 0:
            return
        if (sx, sy, cw, ch) != (0, 0, lw, lh):
            layer = layer.crop((sx, sy, sx + cw, sy + ch))
        base.alpha_composite(layer, (dx, dy))

    def paint_layer(self, draws, w, h, px, aw, mode, outline_px):
        """Собираем все строки в одну картинку с настоящей прозрачностью и показываем окном-слоем."""
        try:
            base = Image.new("RGBA", (w, h), (0, 0, 0, 0))
            oc = 255 if self.light else 0
            for item in draws:
                text, x, y, v = item[:4]
                kara = item[4] if len(item) > 4 else None
                tm, om, W, H = self.smooth.masks(text, px, aw, mode, outline_px)
                under = Image.new("RGBA", (W, H), (oc, oc, oc, 0))      # мягкая тень под буквами
                under.putalpha(om.point(lambda a: a * 55 // 100) if self.light else om)   # у тёмного текста ореол слабее
                top = Image.new("RGBA", (W, H), (v, v, v, 0))
                top.putalpha(tm)
                under.alpha_composite(top)
                if kara is not None:                                    # караоке: спетая часть — ярким цветом поверх серого
                    p, v_sung = kara
                    reg = self.smooth.karaoke_region(text, px, aw, mode, p)
                    hi = Image.new("RGBA", (W, H), (v_sung, v_sung, v_sung, 0))
                    hi.putalpha(ImageChops.multiply(tm, reg))
                    under.alpha_composite(hi)
                self.blit(base, under, x, y)
            self.layer.update(base, self.pos[0], self.pos[1] + TB_H)
        except Exception:
            self.smooth_failed()

    @staticmethod
    def qshade(v):
        """Яркость строки округляем до шага: так сглаженных картинок нужно меньше."""
        if v >= 255 - SHADE_STEP // 2:
            return 255
        return max(0, min(255, round(v / SHADE_STEP) * SHADE_STEP))

    def frame(self):
        lines, focus, before, after, emph, src = self.get_view()
        n = len(lines)
        c = self.canvas
        fs, w = self.fsize(), self.width()
        self.mode = mode = self.pick_mode()

        # панель и кнопки плеера (перерисовываются, только когда что-то поменялось)
        rebuilt = False
        tb_key = (w, mode, self.percent)
        if tb_key != self.tb_key:
            self.tb_key = tb_key
            self.build_toolbar()
            rebuilt = True
        ctl_key = (w, mode, bool(state["playing"]))
        if ctl_key != self.ctl_key:
            self.ctl_key = ctl_key
            self.build_controls()
            rebuilt = True
        self.update_chrome(rebuilt)
        self.step_glide()
        if self.need_clamp and not self.dragging:
            self.keep_on_screen()
        if not (self.dragging or time.time() - self.sw_moved_at < 0.25):   # пока двигаем окно — не отвлекаемся
            self.check_hover()
            self.update_theme()

        layout_key = (src, fs, w, mode)
        if layout_key != self.layout_key:                   # размер, песня или выравнивание изменились
            self.layout_key = layout_key
            c.delete("all")
            self.items.clear()
            self.colors.clear()
            if mode == "left":
                self.text_x, self.text_anchor, self.text_justify = TEXT_LEFT_PAD, "nw", "left"
            elif mode == "right":
                self.text_x, self.text_anchor, self.text_justify = w - TEXT_LEFT_PAD, "ne", "right"
            else:
                self.text_x, self.text_anchor, self.text_justify = w / 2, "n", "center"
            self.font = (FONT_FAMILY, fs, "bold")
            self.linespace = tkfont.Font(family=FONT_FAMILY, size=fs, weight="bold").metrics("linespace")
            o = max(1, round(fs / OUTLINE_SCALE))
            self.offsets = ([(-o, 0), (o, 0), (0, -o), (0, o), (-o, -o), (o, o), (-o, o), (o, -o)]
                            if OUTLINE_ENABLED else [])
            self.last_sig = None
        if n == 0:
            return

        focus = max(0.0, min(focus, n - 1))
        fl, ce = math.floor(focus), math.ceil(focus)
        lo = max(0, fl - before - 1)
        hi = min(n - 1, ce + after + 1)

        kara = self.kara
        sig = (layout_key, round(focus, 3), lo, hi, before, after,
               (kara[0], round(kara[1] * 240)) if kara else None)
        if sig == self.last_sig:
            return
        self.last_sig = sig

        unit = self.linespace + LINE_GAP
        ys, y = {}, 0
        for i in range(lo, hi + 1):
            ys[i] = y
            y += self.measure(lines[i]) + LINE_GAP

        def y_at(x):                       # где начинается строка с дробным номером x
            a = int(math.floor(x))
            b = min(a + 1, hi)
            return ys[a] + (ys[b] - ys[a]) * (x - a)

        # верх окна: пока спетых строк мало, текст прижат к панели и пустоты сверху нет
        y0 = y_at(max(0.0, focus - before))
        cx = self.text_x

        # высота холста рассчитана на полный набор строк, чтобы кнопки плеера не прыгали
        fr = min(max(ce, lo), hi)
        tr = min(max(fr - before, lo), hi)
        end = min(fr + after, hi)
        bottom = (ys[end] + self.measure(lines[end]) - ys[tr]
                  + (fr + after - end) * unit + (before - (fr - tr)) * unit)
        need = int(bottom + 6)
        if need != self.canvas_h:
            self.canvas_h = need
            c.configure(height=need)
            self.need_clamp = True

        px, aw = self.px(), w - 24
        outline_px = round(max(0.5, px / 55) * 4) / 4 if OUTLINE_ENABLED else 0
        box_left = cx if mode == "left" else (cx - aw if mode == "right" else cx - aw / 2)
        draws = []
        for i in range(lo, hi + 1):
            if i not in self.items:
                self.create_items(i, lines[i])
            it = self.items[i]
            sy = ys[i] - y0

            if emph:
                color = shade(i - focus, self.light)
            else:
                color = HINT_COLOR if lines[i] == PLAIN_HINT else self.text_color()

            if it.smooth:
                x1, y1 = round(box_left - it.pad), round(sy - it.pad)
                it.rect = (x1, y1, x1 + aw + 2 * it.pad, y1 + self.measure(lines[i]) + 2 * it.pad)
                if kara and i == kara[0]:
                    v_uns = int(shade(1.0, self.light)[1:3], 16)         # ещё не спетое — серым, как следующие строки
                    draws.append((lines[i], x1, y1, v_uns, (kara[1], int(color[1:3], 16))))
                else:
                    draws.append((lines[i], x1, y1, int(color[1:3], 16)))
                continue

            for cid, (ox, oy) in zip(it.outline, self.offsets):
                c.coords(cid, cx + ox, sy + oy)
            c.coords(it.main, cx, sy)
            if self.colors.get(i) != color:
                self.colors[i] = color
                c.itemconfigure(it.main, fill=color)

        if self.layer is not None and not self.smooth_off:
            self.paint_layer(draws, w, max(need, 1), px, aw, mode, outline_px)

        for i in [k for k in self.items if k < lo or k > hi]:   # убрать ушедшие строки
            it = self.items.pop(i)
            self.colors.pop(i, None)
            for cid in it.outline + ([it.main] if it.main is not None else []):
                c.delete(cid)

    def tick(self):
        t0 = time.perf_counter()
        try:
            self.frame()
        except Exception:
            self.err_count += 1
            if self.err_count <= 5:
                log_error()
        spent = int((time.perf_counter() - t0) * 1000)      # время кадра вычитаем из паузы
        delay = max(1, FRAME_MS - spent)
        if time.time() - self.sw_moved_at < 0.25:           # двигают окно настроек — не мешаем ему
            delay = max(delay, 60)
        self.root.after(delay, self.tick)


if __name__ == "__main__":
    try:                              # отдельный значок в панели задач (а не значок Python)
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("LyricsOverlay.App")
    except Exception:
        pass
    threading.Thread(target=worker, daemon=True).start()
    Overlay().root.mainloop()
