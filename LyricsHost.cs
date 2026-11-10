using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Nowring;

/// <summary>
/// Lyrics Overlay (тексты песен поверх экрана): отдельная программа, встроенная в Nowring.
/// Распаковывается в %LocalAppData%\Nowring\lyrics и запускается / закрывается из вкладки «Тексты».
/// </summary>
static class LyricsHost
{
    const string ProcName = "LyricsOverlay";
    static bool _started;   // запускали мы: при выходе из Nowring закроем

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nowring", "lyrics");
    static string ExePath => Path.Combine(Dir, "LyricsOverlay.exe");

    public static bool Running
    {
        get
        {
            try
            {
                var ps = Process.GetProcessesByName(ProcName);
                bool r = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                return r;
            }
            catch { return false; }
        }
    }

    static bool Extract()
    {
        try
        {
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("LyricsOverlay.exe");
            if (src == null) { Log.Write("Lyrics Overlay: в сборке нет LyricsOverlay.exe"); return false; }
            Directory.CreateDirectory(Dir);
            if (!File.Exists(ExePath) || new FileInfo(ExePath).Length != src.Length)
            {
                using var dst = File.Create(ExePath);
                src.CopyTo(dst);
            }

            // чтобы при первом запуске не выскакивал вопрос про ярлыки (Nowring сам управляет запуском)
            var cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LyricsOverlay");
            var cfg = Path.Combine(cfgDir, "lyrics_overlay_settings.json");
            if (!File.Exists(cfg))
            {
                Directory.CreateDirectory(cfgDir);
                File.WriteAllText(cfg, "{\n \"shortcuts_done\": true\n}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Lyrics Overlay (распаковка): " + ex.Message);
            return false;
        }
    }

    public static bool Start()
    {
        if (Running) return true;
        if (!Extract()) return false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ExePath, WorkingDirectory = Dir, UseShellExecute = false })?.Dispose();
            _started = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Lyrics Overlay (запуск): " + ex.Message);
            return false;
        }
    }

    public static void Stop()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcName))
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1200)) p.Kill();
                }
                catch { }
                p.Dispose();
            }
        }
        catch { }
        _started = false;
    }

    /// <summary>При выходе из Nowring закрываем оверлей, если запускали его мы.</summary>
    public static void StopOwned()
    {
        if (_started) Stop();
    }
}
