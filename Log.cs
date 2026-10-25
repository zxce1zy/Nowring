using System;
using System.IO;

namespace Nowring;

/// <summary>Маленький журнал ошибок сети: %LOCALAPPDATA%\Nowring\log.txt (до 100 КБ).</summary>
static class Log
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nowring");
    static readonly object L = new();

    /// <summary>Открывает журнал в блокноте (или папку, если журнала ещё нет).</summary>
    public static void Open()
    {
        try
        {
            var f = Path.Combine(Dir, "log.txt");
            if (File.Exists(f)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + f + "\"") { UseShellExecute = true });
            else { Directory.CreateDirectory(Dir); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Dir) { UseShellExecute = true }); }
        }
        catch { }
    }

    public static void Write(string msg)
    {
        try
        {
            lock (L)
            {
                Directory.CreateDirectory(Dir);
                var f = Path.Combine(Dir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 100_000) File.Delete(f);
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n");
            }
        }
        catch { }
    }
}
