using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Gwt;

// GWT Deepchart Bridge: a tray app that keeps the GWT Live indicator on Deepchart current (Bridge.cs).
static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "GwtDeepchartBridge";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selfcheck")
            return SelfCheck();
        using var single = new Mutex(false, @"Local\GwtDeepchartBridge");
        try { if (!single.WaitOne(0)) return 0; } catch (AbandonedMutexException) { }

        ApplicationConfiguration.Initialize();
        var cfg = Config.Load();
        string token = Config.LoadToken();
        if (token.Length == 0 || cfg.DevId.Length == 0)
        {
            if (!SettingsForm.Ask(cfg, out token))
                return 0;
        }

        Bridge bridge = null;
        void StartBridge() { bridge?.Dispose(); bridge = new Bridge(cfg, token); bridge.Start(); }
        StartBridge();

        var status = new ToolStripMenuItem("starting") { Enabled = false };
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = RunsAtStartup(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh charts now", null, (_, _) => bridge.RefreshNow());
        menu.Items.Add("Settings…", null, (_, _) => { if (SettingsForm.Ask(cfg, out var t)) { token = t; StartBridge(); } });
        menu.Items.Add(startup);
        menu.Items.Add("Open log", null, (_, _) => { if (File.Exists(Log.PathOf)) Process.Start(new ProcessStartInfo(Log.PathOf) { UseShellExecute = true }); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Application.Exit());

        using var tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "GWT Deepchart Bridge", ContextMenuStrip = menu, Visible = true };
        using var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            string s = bridge.Status;
            status.Text = s.Length > 120 ? s.Substring(0, 117) + "…" : s;
            tray.Text = "GWT Bridge · " + (s.Length > 50 ? s.Substring(0, 50) : s);   // tray tooltips max 63 chars
        };
        timer.Start();
        Log.Write("bridge started");
        Application.Run();
        tray.Visible = false;
        bridge.Dispose();
        return 0;
    }

    // Offline: client parsers, then a real compile of GWT Live from the shipped ref pack (nothing deployed, GWT not contacted).
    static int SelfCheck()
    {
        var fails = new System.Collections.Generic.List<string>();
        fails.AddRange(DarkPool.SelfCheck());
        fails.AddRange(LevelImport.SelfCheck());
        try
        {
            var item = new LiveItem { Kind = LiveKind.Text, Text = "q\"uote\\ é", Y1 = 1.5 };
            var dll = Compiler.Build(Compiler.DataSource(new[] { item }, 1, "check"), Guid.Empty.ToString(),
                Path.Combine(AppContext.BaseDirectory, "ref"), Config.Load().DeepchartDir);
            if (dll.Length < 4000) fails.Add("compiled dll suspiciously small");
        }
        catch (Exception ex) { fails.Add("compile: " + ex.Message); }
        Console.WriteLine(fails.Count == 0 ? "SelfCheck OK" : "FAIL\n" + string.Join("\n", fails));
        return fails.Count == 0 ? 0 : 1;
    }

    static bool RunsAtStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, "\"" + Environment.ProcessPath + "\"");
        else k.DeleteValue(RunName, false);
    }
}

sealed class SettingsForm : Form
{
    readonly TextBox _token = new TextBox { UseSystemPasswordChar = true, Width = 360 };
    readonly TextBox _dev = new TextBox { Width = 360 };
    readonly TextBox _dir = new TextBox { Width = 360 };

    SettingsForm(Config cfg, bool hasToken)
    {
        Text = "GWT Deepchart Bridge · settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        _dev.Text = cfg.DevId;
        _dir.Text = cfg.DeepchartDir;
        _token.PlaceholderText = hasToken ? "(saved, leave empty to keep it)" : "paste your GWT token";
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c) { layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 0, 2) }); layout.Controls.Add(c); }
        Row("GWT token (same one as in GWT's ATAS indicator):", _token);
        Row("Your Deepchart developer id:", _dev);
        Row("Deepchart install folder:", _dir);
        layout.Controls.Add(new Label { Text = "The token is stored encrypted for your Windows user only.", AutoSize = true, Margin = new Padding(0, 10, 0, 6) });
        layout.Controls.Add(ok);
        Controls.Add(layout);
        AcceptButton = ok;
    }

    public static bool Ask(Config cfg, out string token)
    {
        string saved = Config.LoadToken();
        using var f = new SettingsForm(cfg, saved.Length > 0);
        token = saved;
        while (f.ShowDialog() == DialogResult.OK)
        {
            string t = f._token.Text.Trim().Length > 0 ? f._token.Text.Trim() : saved;
            string dev = f._dev.Text.Trim(), dir = f._dir.Text.Trim();
            string err = t.Length == 0 ? "Paste your GWT token."
                : !Guid.TryParse(dev, out _) ? "The developer id looks like xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx."
                : !File.Exists(Path.Combine(dir, "VolSysAPI.dll")) ? "VolSysAPI.dll is not in that folder: pick Deepchart's install folder."
                : null;
            if (err != null) { MessageBox.Show(err, "GWT Deepchart Bridge"); continue; }
            cfg.DevId = dev;
            cfg.DeepchartDir = dir.EndsWith("\\") ? dir : dir + "\\";
            cfg.Save();
            if (t != saved) Config.SaveToken(t);
            token = t;
            return true;
        }
        return false;
    }
}
