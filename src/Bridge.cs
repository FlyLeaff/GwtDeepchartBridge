using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Gwt;

public sealed class Config
{
    public string DevId { get; set; } = "";
    public string DeepchartDir { get; set; } = @"C:\Program Files\Volumetrica Trading\Deepchart\";
    public int MinRefreshSec { get; set; } = 120;    // at most one chart reload per this many seconds
    public int IdleMs { get; set; } = 3000;          // only reload after this long with no mouse/keyboard input
    public int HeartbeatMin { get; set; } = 15;      // reload even when nothing changed, so the chart can tell the bridge is alive

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GwtDeepchartBridge");
    static string ConfigPath => Path.Combine(Dir, "config.json");
    static string TokenPath => Path.Combine(Dir, "token.bin");

    public static Config Load()
    {
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config(); }
        catch { return new Config(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // The GWT token, encrypted with Windows DPAPI for this Windows user: never written in clear, never logged.
    public static string LoadToken()
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static void SaveToken(string token)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(TokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser));
    }
}

// The bridge loop: GWT data -> items -> GwtLive.dll -> reload on open charts. Runs on its own thread; Status is for the tray.
public sealed class Bridge : IDisposable
{
    public const string PluginHeader = "DeepChartsGwtBridge/0.1 (personal)";
    const int DataPollSec = 60;

    readonly Config _cfg;
    readonly string _token;
    readonly CancellationTokenSource _cts = new CancellationTokenSource();
    Feed _feed;
    volatile bool _forceRefresh;
    public volatile string Status = "starting";

    public Bridge(Config cfg, string token) { _cfg = cfg; _token = token; }

    public void RefreshNow() => _forceRefresh = true;

    public void Start() => new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "GwtBridge" }.Start();

    public void Dispose()
    {
        _cts.Cancel();
        _feed?.Dispose();
    }

    void Run(CancellationToken ct)
    {
        string refDir = Path.Combine(AppContext.BaseDirectory, "ref");
        string indicators = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Deepchart", "Indicators");
        _feed = new Feed(Protocol.WebSocketUrl("https://gwtrade.app"), _token, PluginHeader);
        _feed.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        List<LiveItem> dark = new List<LiveItem>(), levels = new List<LiveItem>();
        string darkStatus = "dark pool waiting", levelStatus = "levels waiting";
        DateTime nextPoll = DateTime.MinValue, lastDeploy = DateTime.MinValue;
        string deployedKey = null;
        bool reloadPending = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextPoll)
                {
                    nextPoll = DateTime.UtcNow.AddSeconds(DataPollSec);
                    (dark, darkStatus) = Poll(() => DarkPool.Fetch(http, _token, ct), dark, darkStatus, "dark pool");
                    (levels, levelStatus) = Poll(() => LevelImport.Fetch(http, _token, ct), levels, levelStatus, "levels");
                }
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var items = Gex.Items(_feed, now, out string gexStatus);
                items.AddRange(dark);
                items.AddRange(levels);
                string dataStatus = gexStatus + " · " + darkStatus + " · " + levelStatus;
                string key = string.Join("\n", items.Select(i => i.ToCode())) + dataStatus;

                bool changed = key != deployedKey;
                bool due = _forceRefresh || deployedKey == null
                    || (changed && DateTime.UtcNow - lastDeploy >= TimeSpan.FromSeconds(_cfg.MinRefreshSec))
                    || DateTime.UtcNow - lastDeploy >= TimeSpan.FromMinutes(_cfg.HeartbeatMin);
                if (!due && !reloadPending)
                    Status = "watching · " + dataStatus + " · last refresh " + Ago(lastDeploy);
                else if (IdleMs() < _cfg.IdleMs)
                    Status = "waiting for you to stop clicking · " + dataStatus;
                else
                {
                    bool rescan = _forceRefresh;   // "Refresh charts now" also re-checks charts known to have no GWT Live
                    if (due)
                    {
                        _forceRefresh = false;
                        var dll = Compiler.Build(Compiler.DataSource(items, now, dataStatus), _cfg.DevId, refDir, _cfg.DeepchartDir);
                        Compiler.Deploy(dll, indicators);
                        deployedKey = key;
                        lastDeploy = DateTime.UtcNow;
                        Thread.Sleep(1500);   // let Deepchart's folder watcher load and check the new dll before the reload
                    }
                    var notes = new List<string>();
                    bool idle = IdleMs() >= _cfg.IdleMs;   // re-check: the user may have started clicking during the build
                    int n = idle ? Reloader.ReloadAll(notes, rescan) : 0;
                    reloadPending = !idle;   // only the reload is retried once they stop; the dll is already deployed
                    Status = (idle ? "refreshed " + n + " chart(s)" : "deployed, reload postponed: you were clicking") + " · " + dataStatus
                        + (notes.Count > 0 ? " · " + string.Join("; ", notes) : "");
                    Log.Write(Status);
                }
            }
            catch (Exception ex)
            {
                Status = "error: " + ex.Message;
                Log.Write(Status);
            }
            ct.WaitHandle.WaitOne(2000);
        }
    }

    static (List<LiveItem>, string) Poll(Func<Task<(List<LiveItem> items, string status)>> fetch, List<LiveItem> last, string lastStatus, string name)
    {
        try
        {
            var (items, status) = fetch().GetAwaiter().GetResult();
            return (items ?? last, status);
        }
        catch (Exception ex)
        {
            return (last, name + ": " + ex.GetType().Name);   // keep the last good levels on a network blip
        }
    }

    static string Ago(DateTime t) => t <= DateTime.MinValue.AddDays(2) ? "never" : ((int)(DateTime.UtcNow - t).TotalSeconds) + "s ago";

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // Milliseconds since the last mouse or keyboard input anywhere on this Windows session.
    public static long IdleMs()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? (uint)Environment.TickCount - li.dwTime : 0;
    }
}

public static class Log
{
    static readonly object Gate = new object();

    public static string PathOf => System.IO.Path.Combine(Config.Dir, "bridge.log");

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                var fi = new FileInfo(PathOf);
                if (fi.Exists && fi.Length > 2_000_000)
                    File.Delete(PathOf);
                File.AppendAllText(PathOf, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
