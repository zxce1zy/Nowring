Исходники Lyrics Overlay (приложение друга, встроено в Nowring).

Отдельно собирать ничего не нужно: при сборке Nowring (build.bat, installer\make-installer.bat,
dotnet publish) Lyrics Overlay пересобирается сам, если изменился lyrics_overlay_v3.py.
Нужен один раз установленный Python 3.12:  winget install Python.Python.3.12
Нет Python или сборка не удалась: Nowring использует готовый lyrics\LyricsOverlay.exe.

Вручную:  py -3.12 build_exe.py   (exe появится рядом; с ключом --auto попадает в папку lyrics)
