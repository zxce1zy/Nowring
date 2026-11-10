"""Сборка LyricsOverlay.exe одним запуском.

Как пользоваться: положите этот файл в одну папку с lyrics_overlay_v3.py и запустите
двойным кликом (или:  python build_exe.py ). Готовый файл LyricsOverlay.exe появится рядом.
"""
import sys

for _s in (sys.stdout, sys.stderr):      # консоль CI (cp1252) не умеет кириллицу: пишем в UTF-8
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

import importlib
import os
import shutil
import struct
import subprocess
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.join(HERE, "lyrics_overlay_v3.py")
ICON = os.path.join(HERE, "icon.ico")
OUT_TMP = os.path.join(HERE, "_out")
BUILD_TMP = os.path.join(HERE, "_build")
AUTO = "--auto" in sys.argv          # запуск из сборки Nowring: без пауз, exe кладём в папку lyrics (на уровень выше)
EXE = os.path.normpath(os.path.join(HERE, "..", "LyricsOverlay.exe")) if AUTO else os.path.join(HERE, "LyricsOverlay.exe")


def find_compatible_python():
    """winsdk не имеет готовых сборок для новых Python (3.13+): ищем установленный 3.12 / 3.11 / 3.10."""
    for ver in ("3.12", "3.11", "3.10"):
        try:
            r = subprocess.run(["py", f"-{ver}", "-c", "import sys;print(sys.executable)"],
                               capture_output=True, text=True, stdin=subprocess.DEVNULL)
        except OSError:
            return None
        if r.returncode == 0 and r.stdout.strip():
            return r.stdout.strip()
    return None


def run(cmd, title):
    print(f"\n=== {title} ===")
    if subprocess.run(cmd, cwd=HERE).returncode != 0:
        raise RuntimeError(f"Шаг не удался: {title}. Прокрутите выше и посмотрите причину.")


def make_icon():
    """Рисуем значок 🤩 (тот же, что в приложении) и кладём его в icon.ico."""
    importlib.invalidate_caches()
    sys.path.insert(0, HERE)
    from lyrics_overlay_v3 import make_icon_png
    sizes = [16, 32, 48, 64, 256]
    images = [make_icon_png(s, ss=2 if s >= 128 else 3) for s in sizes]
    offset = 6 + 16 * len(images)
    entries, data = b"", b""
    for size, png in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32,
                               len(png), offset + len(data))
        data += png
    with open(ICON, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)) + entries + data)


def cleanup():
    for folder in (OUT_TMP, BUILD_TMP, os.path.join(HERE, "__pycache__")):
        shutil.rmtree(folder, ignore_errors=True)
    if os.path.exists(ICON):
        os.remove(ICON)


def main():
    if not os.path.exists(APP):
        raise RuntimeError("Рядом с build_exe.py нет файла lyrics_overlay_v3.py. "
                           "Положите оба файла в одну папку (если скачали архив — сначала распакуйте).")
    if sys.platform != "win32":
        raise RuntimeError("Собирать .exe нужно на Windows.")

    if sys.version_info >= (3, 13) and not os.environ.get("LO_RELAUNCHED"):
        good = find_compatible_python()
        if good:
            print(f"Python {sys.version_info.major}.{sys.version_info.minor} слишком новый для winsdk.")
            print("Перезапускаю сборку с:", good)
            env = dict(os.environ, LO_RELAUNCHED="1")
            code = subprocess.run([good, os.path.abspath(__file__)] + sys.argv[1:], env=env).returncode
            if code != 0:
                raise RuntimeError("Сборка на Python 3.12 не удалась, см. текст выше.")
            return
        print("ВНИМАНИЕ: у вас Python %d.%d, а winsdk надёжно работает на Python 3.12." % sys.version_info[:2])
        print("Установите его командой:  winget install Python.Python.3.12")
        print("и запустите сборку так:    py -3.12 build_exe.py")
        raise RuntimeError("Нужен Python 3.12 (см. подсказку выше).")

    run([sys.executable, "-m", "pip", "install", "--upgrade", "winsdk", "requests", "pillow", "pyinstaller"],
        "1/3 Установка библиотек")

    print("\n=== 2/3 Рисую значок ===")
    make_icon()

    run([sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "--onefile", "--noconsole",
         "--name", "LyricsOverlay", "--icon", ICON, "--collect-all", "winsdk",
         "--distpath", OUT_TMP, "--workpath", BUILD_TMP, "--specpath", BUILD_TMP, APP],
        "3/3 Сборка .exe (1-2 минуты)")

    built = os.path.join(OUT_TMP, "LyricsOverlay.exe")
    if not os.path.exists(built):
        raise RuntimeError("PyInstaller завершился, но LyricsOverlay.exe не появился.")
    if os.path.exists(EXE):
        os.remove(EXE)
    shutil.move(built, EXE)
    cleanup()
    print("\n=== ГОТОВО ===")
    print("Файл:", EXE)
    print("Запускайте двойным кликом. При первом запуске приложение спросит про ярлыки.")


if __name__ == "__main__":
    ok = True
    try:
        main()
    except Exception:
        ok = False
        traceback.print_exc()
        cleanup()
        print("\n=== СБОРКА НЕ УДАЛАСЬ. Скопируйте текст выше и пришлите мне. ===")
    if not AUTO and not os.environ.get("LO_RELAUNCHED"):
        input("\nНажмите Enter, чтобы закрыть окно...")
    sys.exit(0 if ok else 1)
