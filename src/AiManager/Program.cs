using AiManager.Core;

namespace AiManager;

static class Program
{
    /// <summary>
    /// AiManager.exe [--no-window] [--port N] [--claude-dir PATH] [--data-dir PATH] [--dev-www PATH] [--view NAME]
    /// --no-window runs only the local server, which is how the UI is developed and tested in a browser.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        var options = new AppHostOptions();
        bool noWindow = false;
        string? view = null;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--no-window": noWindow = true; break;
                case "--port": options.Port = int.TryParse(Next(), out var p) ? p : null; break;
                case "--claude-dir": options.ClaudeDir = Next(); break;
                case "--data-dir": options.DataDir = Next(); break;
                case "--dev-www": options.DevWww = Next(); break;
                case "--view": view = Next(); break;
            }
        }

        // One instance per data folder: a second copy would fight over the port the hooks point at.
        var scope = (options.DataDir ?? "default").GetHashCode().ToString("x");
        using var single = new Mutex(true, $"AiManager.{scope}", out bool first);
        if (!first)
        {
            if (!noWindow)
                MessageBox.Show("AiManager is already running.", "AiManager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        var host = new AppHost(options);
        try
        {
            host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            host.Log.Write("startup failed: " + ex);
            if (!noWindow) MessageBox.Show(ex.Message, "AiManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        try
        {
            if (noWindow)
            {
                new ManualResetEvent(false).WaitOne();
            }
            else
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new MainForm(host, view));
            }
        }
        finally
        {
            host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        return 0;
    }
}
