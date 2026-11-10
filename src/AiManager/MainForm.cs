using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using AiManager.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AiManager;

/// <summary>The program window: a WebView2 showing the UI served by the local host, plus a tray icon that
/// speaks up when a session starts waiting for a decision.</summary>
sealed class MainForm : Form
{
    readonly AppHost _host;
    readonly string? _startView;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 1500 };
    HashSet<string> _knownPending = new();
    bool _primed;

    public MainForm(AppHost host, string? startView = null)
    {
        _host = host;
        _startView = startView;
        Text = "AiManager";
        Icon = MakeIcon();
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1480, 980);
        if (host.Config.WindowBounds is { Length: 4 } b && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(b[0], b[1], b[2], b[3]))))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(b[0], b[1], Math.Max(b[2], 980), Math.Max(b[3], 640));
        }
        BackColor = Color.FromArgb(0x14, 0x16, 0x1A);
        Controls.Add(_web);

        _tray.Icon = Icon;
        _tray.Text = "AiManager";
        _tray.Visible = true;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) BringUp("decisions"); };
        _tray.BalloonTipClicked += (_, _) => BringUp("decisions");

        _poll.Tick += (_, _) => CheckPending();
        Load += async (_, _) => await InitWebView();
        FormClosing += (_, _) =>
        {
            if (WindowState == FormWindowState.Normal)
                _host.Config.WindowBounds = new[] { Left, Top, Width, Height };
            _host.SaveConfig();
            _tray.Visible = false;
        };
    }

    async Task InitWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_host.Paths.DataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            _host.Log.Write("WebView2 failed: " + ex);
            MessageBox.Show(this,
                "AiManager needs the Microsoft Edge WebView2 Runtime, which could not be started.\n\n" + ex.Message,
                "AiManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        // Links in session text open in the user's browser, never inside the app window.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(_host.Url, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.WebMessageReceived += (_, e) => OnWebMessage(e.WebMessageAsJson);
        core.Navigate(_host.Url + (_startView is { Length: > 0 } ? "#" + Uri.EscapeDataString(_startView) : ""));
        _poll.Start();
    }

    /// <summary>The page reports its theme so the window frame can match it.</summary>
    void OnWebMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("theme", out var theme))
            {
                bool dark = theme.GetString() == "dark";
                int flag = dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref flag, sizeof(int));
                BackColor = dark ? Color.FromArgb(0x14, 0x16, 0x1A) : Color.FromArgb(0xF4, 0xF5, 0xF7);
            }
        }
        catch (JsonException) { }
    }

    /// <summary>Raises a tray notice for each decision that newly started waiting.</summary>
    void CheckPending()
    {
        var state = _host.Store.BuildState();
        var pending = state.Decisions.Where(d => d.Status == "pending").ToList();
        var ids = pending.Select(d => d.Id).ToHashSet();
        Text = pending.Count > 0 ? $"({pending.Count}) AiManager" : "AiManager";
        _tray.Text = pending.Count > 0 ? $"AiManager · {pending.Count}" : "AiManager";

        if (_primed && state.Loaded)
        {
            var fresh = pending.Where(d => !_knownPending.Contains(d.Id)).ToList();
            if (fresh.Count > 0 && (ActiveForm != this || WindowState == FormWindowState.Minimized))
            {
                var d = fresh[0];
                _tray.BalloonTipTitle = string.IsNullOrEmpty(d.SessionTitle) ? "AiManager" : d.SessionTitle;
                _tray.BalloonTipText = d.Question.Length > 200 ? d.Question[..200] + "…" : d.Question;
                _tray.ShowBalloonTip(8000);
                FlashWindow(Handle, true);
            }
        }
        if (state.Loaded) _primed = true;
        _knownPending = ids;
    }

    void BringUp(string view)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        if (_web.CoreWebView2 != null)
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { view }));
    }

    /// <summary>The window and tray use the icon built into the EXE; the drawn copy is only a fallback
    /// for when that cannot be read.</summary>
    static Icon MakeIcon()
    {
        try
        {
            if (Icon.ExtractAssociatedIcon(Application.ExecutablePath) is { } embedded) return embedded;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception) { }

        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(0x1B, 0x1F, 0x27));
            using var path = new GraphicsPath();
            const int r = 14;
            path.AddArc(2, 2, r * 2, r * 2, 180, 90);
            path.AddArc(62 - r * 2, 2, r * 2, r * 2, 270, 90);
            path.AddArc(62 - r * 2, 62 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(2, 62 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var pen = new Pen(Color.White, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new Point(16, 44), new Point(16, 32), new Point(48, 32), new Point(48, 44) });
            g.DrawLine(pen, 32, 22, 32, 32);
            using var dot = new SolidBrush(Color.White);
            g.FillEllipse(dot, 25, 9, 14, 14);
            g.FillEllipse(dot, 9, 41, 14, 14);
            g.FillEllipse(dot, 41, 41, 14, 14);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _tray.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
