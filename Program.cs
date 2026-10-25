using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Nowring;

static class Program
{
    [STAThread]
    static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception, false);
        try
        {
            using var mutex = new Mutex(true, "Nowring.SingleInstance", out bool first);
            if (!first)
            {
                // программа уже запущена (она в трее): просим её показать окно
                try { EventWaitHandle.OpenExisting("Nowring.ShowWindow").Set(); } catch { }
                return;
            }
            var app = new App();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // ошибка при запуске: показываем текст; ошибка потом: пишем в журнал и работаем дальше
                Fatal(e.Exception, !App.Started);
                e.Handled = true;
                if (!App.Started) app.Shutdown();
            };
            app.Run();
        }
        catch (Exception ex) { Fatal(ex, true); }
    }

    static void Fatal(Exception? ex, bool show)
    {
        Log.Write("Критическая ошибка: " + ex);
        if (!show) return;
        try
        {
            MessageBox.Show(
                "Nowring не смог запуститься:\n\n" + ex?.GetType().Name + ": " + ex?.Message +
                "\n\nПодробности записаны в журнал:\n%LOCALAPPDATA%\\Nowring\\log.txt",
                "Nowring", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}

sealed class App : Application
{
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem _darkItem = null!, _lightItem = null!, _enabledItem = null!;
    Drawing.Icon _iconOn = null!, _iconOff = null!;
    bool _trayOn = true;
    MainWindow? _win;
    PresenceService _svc = null!;
    Settings _s = null!;

    /// <summary>Значок приложения: для окна и панели задач.</summary>
    internal static Drawing.Icon? AppIcon;
    internal static bool Started;

    EventWaitHandle? _showEvent;
    RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _s = Settings.Load();

        // автозапуск мог включить установщик, а путь мог измениться при переустановке
        _s.Autostart = Autostart.IsEnabled();
        if (_s.Autostart) Autostart.Set(true);

        Theme.Apply(_s.Dark);
        _svc = new PresenceService(_s);

        _iconOn = MakeIcon(true);
        _iconOff = MakeIcon(false);
        AppIcon = _iconOn;
        _trayOn = _s.Enabled;

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayOn ? _iconOn : _iconOff,
            Text = _trayOn ? "Nowring" : "Nowring (статус выключен)",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowWindow(); };
        Theme.Changed += UpdateThemeItems;
        _svc.Updated += () => Dispatcher.BeginInvoke(new Action(SyncIcon));

        Task.Run(_svc.StartAsync);

        // второй запуск (ярлык, меню Пуск) открывает окно уже работающей программы
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Nowring.ShowWindow");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow)), null, Timeout.Infinite, false);

        // --hidden: так программу запускает автозапуск Windows, тогда окно не открывается.
        // При обычном запуске (ярлык, Пуск, установщик) окно показывается всегда.
        bool hidden = Array.Exists(e.Args, a => a.Equals("--hidden", StringComparison.OrdinalIgnoreCase));
        if (!hidden || !_s.HasClientId) ShowWindow();
        Started = true;
        Dispatcher.BeginInvoke(new Action(Trim), DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }

    Forms.ContextMenuStrip BuildMenu()
    {
        var m = new Forms.ContextMenuStrip
        {
            Renderer = new MenuRenderer(),
            ShowImageMargin = true,
            ImageScalingSize = new Drawing.Size(16, 16),
            Padding = new Forms.Padding(0, 4, 0, 4),
            Font = new Drawing.Font("Segoe UI", 9.5f)
        };

        // заголовок: значок и название, по клику открывается страница на GitHub
        m.Items.Add(new Forms.ToolStripMenuItem("Nowring", _iconOn.ToBitmap(), (_, _) => OpenGitHub())
        {
            Font = new Drawing.Font("Segoe UI Semibold", 9.5f)
        });
        m.Items.Add(new Forms.ToolStripSeparator());

        m.Items.Add("Открыть", null, (_, _) => ShowWindow());

        _enabledItem = new Forms.ToolStripMenuItem();
        _enabledItem.Click += (_, _) => ToggleEnabled();
        m.Items.Add(_enabledItem);
        m.Items.Add("Журнал ошибок", null, (_, _) => Log.Open());

        var themeRoot = new Forms.ToolStripMenuItem("Тема");
        _darkItem = new Forms.ToolStripMenuItem();
        _lightItem = new Forms.ToolStripMenuItem();
        _darkItem.Click += (_, _) => SetTheme(true);
        _lightItem.Click += (_, _) => SetTheme(false);
        themeRoot.DropDownItems.Add(_darkItem);
        themeRoot.DropDownItems.Add(_lightItem);
        m.Items.Add(themeRoot);

        m.Items.Add(new Forms.ToolStripSeparator());
        m.Items.Add("Выйти", null, (_, _) => Shutdown());

        // пункты повыше, чтобы закруглённая подсветка смотрелась аккуратно
        foreach (var it in m.Items)
            if (it is Forms.ToolStripMenuItem mi) mi.Padding = new Forms.Padding(4, 4, 4, 4);
        foreach (Forms.ToolStripItem it in themeRoot.DropDownItems)
            it.Padding = new Forms.Padding(4, 4, 4, 4);

        m.Opened += (_, _) => { UpdateEnabledItem(); Round(m.Handle); };
        themeRoot.DropDown.Opened += (_, _) => Round(themeRoot.DropDown.Handle);
        UpdateThemeItems();
        UpdateEnabledItem();
        return m;
    }

    static void OpenGitHub()
    {
        try { Process.Start(new ProcessStartInfo(Config.GitHubUrl) { UseShellExecute = true }); }
        catch { }
    }

    void ToggleEnabled()
    {
        _s.Enabled = !_s.Enabled;
        _s.Save();
        UpdateEnabledItem();
        SyncIcon();
        _svc.Schedule();
    }

    void UpdateEnabledItem() =>
        _enabledItem.Text = (_s.Enabled ? "✓  " : "     ") + "Статус активности";

    // выключенный статус: иконка в трее бледная и тёмная
    void SyncIcon()
    {
        if (_tray == null || _s.Enabled == _trayOn) return;
        _trayOn = _s.Enabled;
        _tray.Icon = _trayOn ? _iconOn : _iconOff;
        _tray.Text = _trayOn ? "Nowring" : "Nowring (статус выключен)";
    }

    void SetTheme(bool dark)
    {
        if (_s.Dark == dark) return;
        _s.Dark = dark;
        _s.Save();
        Theme.Apply(dark);
    }

    void UpdateThemeItems()
    {
        _darkItem.Text = (Theme.Dark ? "✓  " : "     ") + "Тёмная";
        _lightItem.Text = (Theme.Dark ? "     " : "✓  ") + "Светлая";
    }

    // Обычное окно: перетаскивается, сворачивается, есть на панели задач. Создаётся при открытии,
    // выгружается при закрытии (память возвращается системе).
    void ShowWindow()
    {
        if (_win != null)
        {
            if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
            _win.Activate();
            return;
        }

        _win = new MainWindow(_svc, _s);
        _win.Closed += (_, _) =>
        {
            _win = null;
            Dispatcher.BeginInvoke(new Action(Trim), DispatcherPriority.ApplicationIdle);
        };
        _win.Show();
        _win.Activate();
    }

    // Выгрузка окна и возврат памяти системе
    static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
    }

    // Скругление системного меню (Windows 11; на Windows 10 просто ничего не делает)
    static void Round(IntPtr hwnd)
    {
        try { int pref = 2; DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int)); } catch { }
    }

    static Drawing.Icon MakeIcon(bool on)
    {
        using var bmp = new Drawing.Bitmap(64, 64);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAlias;
            using var bg = new Drawing.SolidBrush(on
                ? Drawing.Color.FromArgb(88, 101, 242)
                : Drawing.Color.FromArgb(110, 58, 60, 72));
            g.FillEllipse(bg, 2, 2, 60, 60);
            using var fg = new Drawing.SolidBrush(on
                ? Drawing.Color.White
                : Drawing.Color.FromArgb(120, 200, 200, 210));
            using var f = new Drawing.Font("Segoe UI Symbol", 36, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            var sz = g.MeasureString("♪", f);
            g.DrawString("♪", f, fg, (64 - sz.Width) / 2, (64 - sz.Height) / 2);
        }
        var h = bmp.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>Цвета контекстного меню трея под выбранную тему.</summary>
sealed class MenuColors : Forms.ProfessionalColorTable
{
    static Drawing.Color C(int dark, int light)
    {
        int v = Theme.Dark ? dark : light;
        return Drawing.Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }

    public MenuColors() { UseSystemColors = false; }

    public override Drawing.Color ToolStripDropDownBackground => C(0x1E1E25, 0xF7F7FA);
    public override Drawing.Color ImageMarginGradientBegin => C(0x1E1E25, 0xF7F7FA);
    public override Drawing.Color ImageMarginGradientMiddle => C(0x1E1E25, 0xF7F7FA);
    public override Drawing.Color ImageMarginGradientEnd => C(0x1E1E25, 0xF7F7FA);
    public override Drawing.Color MenuBorder => C(0x33333D, 0xDADAE2);
    public override Drawing.Color MenuItemBorder => C(0x2E2E38, 0xE4E4EC);
    public override Drawing.Color MenuItemSelected => C(0x2E2E38, 0xE4E4EC);
    public override Drawing.Color MenuItemSelectedGradientBegin => C(0x2E2E38, 0xE4E4EC);
    public override Drawing.Color MenuItemSelectedGradientEnd => C(0x2E2E38, 0xE4E4EC);
    public override Drawing.Color SeparatorDark => C(0x33333D, 0xDADAE2);
    public override Drawing.Color SeparatorLight => C(0x33333D, 0xDADAE2);
}

sealed class MenuRenderer : Forms.ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

    static Drawing.Color Fore => Theme.Dark
        ? Drawing.Color.FromArgb(0xE6, 0xE6, 0xEA)
        : Drawing.Color.FromArgb(0x1C, 0x1C, 0x22);

    static readonly Drawing.Color Blue = Drawing.Color.FromArgb(0x4F, 0x6B, 0xF0);

    static bool Hot(Forms.ToolStripItem i) => i.Enabled && (i.Selected || i.Pressed);

    // выбранный пункт: скруглённая синяя плашка с небольшими отступами от краёв меню
    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!Hot(e.Item)) return;
        var g = e.Graphics;
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Drawing.Rectangle(5, 1, e.Item.Width - 10, e.Item.Height - 2);
        using var path = Rounded(r, 8);
        using var br = new Drawing.SolidBrush(Blue);
        g.FillPath(br, path);
    }

    static Drawing.Drawing2D.GraphicsPath Rounded(Drawing.Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Hot(e.Item) ? Drawing.Color.White : Fore;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Hot(e.Item) ? Drawing.Color.White : Fore;
        base.OnRenderArrow(e);
    }
}
