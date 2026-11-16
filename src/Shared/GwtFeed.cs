using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Gwt;

// What the indicator reads: the live socket (Feed, Source = Direct) or the bridge's snapshot file (FileSource, Source = File).
public interface ISource : IDisposable
{
    int Version { get; }
    bool Stopped { get; }
    string Status { get; }
    Snapshot Snapshot(string profile);
    string ProfileError(string profile);
    List<Frame> Trail(string profile);
    void Poll(long nowMs);   // chart thread, about once a second
}

// The WebSocket client. Runs on a background task; the chart thread only reads Version, Status, Snapshot() and Trail().
// The token goes in the X-GWT-Token header and nowhere else: never into Status, a log or a file.
public sealed class Feed : ISource
{
    public static readonly string[] Profiles = { Protocol.Ndx, Protocol.Qqq };
    const double MinBackoffSec = 2, MaxBackoffSec = 60, PingSec = 25;

    readonly string _url, _token, _plugin;
    readonly object _lock = new object();
    readonly Dictionary<string, Snapshot> _snaps = new Dictionary<string, Snapshot>();
    readonly Dictionary<string, SortedDictionary<long, Frame>> _trails = new Dictionary<string, SortedDictionary<long, Frame>>();
    readonly Dictionary<string, string> _profileErrors = new Dictionary<string, string>();
    readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
    CancellationTokenSource _cts;
    string _status = "starting";
    string _reject = "", _lastError = "";
    bool _gotLevels;
    bool _stopped;           // a rejection that retrying would not fix (or would fight the user's other connection)
    bool _historyCapable, _qqqCapable = true;
    double _minBackoff = MinBackoffSec;
    int _version;

    public Feed(string wsUrl, string token, string plugin)
    {
        _url = wsUrl;
        _token = token;
        _plugin = plugin;
    }

    public int Version => Volatile.Read(ref _version);
    public bool Stopped { get { lock (_lock) return _stopped; } }

    public string Status
    {
        get { lock (_lock) return _status; }
    }

    public Snapshot Snapshot(string profile)
    {
        lock (_lock) return _snaps.TryGetValue(profile, out var s) ? s : null;
    }

    public string ProfileError(string profile)
    {
        lock (_lock) return _profileErrors.TryGetValue(profile, out var e) ? e : "";
    }

    public List<Frame> Trail(string profile)
    {
        lock (_lock) return _trails.TryGetValue(profile, out var t) ? new List<Frame>(t.Values) : new List<Frame>();
    }

    public void Poll(long nowMs) { }   // the socket task pushes; nothing to pull

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(() => RunAsync(ct));
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
    }

    async Task RunAsync(CancellationToken ct)
    {
        var rng = new Random();
        double backoff = MinBackoffSec;
        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            try
            {
                ws.Options.SetRequestHeader("X-GWT-Token", _token);
                ws.Options.SetRequestHeader("X-GWT-Plugin", _plugin);
                ws.Options.CollectHttpResponseDetails = true;
                SetStatus("connecting");
                await ws.ConnectAsync(new Uri(_url), ct).ConfigureAwait(false);
                lock (_lock)
                {
                    ClearLocked();
                    _reject = _lastError = "";
                    _gotLevels = false;
                    _status = "connected, waiting for levels";
                    _version++;
                }
                await SendAsync(ws, Protocol.Subscribe(Profiles), ct).ConfigureAwait(false);
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ping = PingAsync(ws, pingCts.Token);
                await ReceiveAsync(ws, ct).ConfigureAwait(false);
                pingCts.Cancel();
                try { await ping.ConfigureAwait(false); } catch { }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnConnectFailure(ws, ex);
            }
            finally
            {
                try { ws.Dispose(); } catch { }
            }
            if (ct.IsCancellationRequested) break;
            double delay;
            lock (_lock)
            {
                ClearLocked();
                _version++;
                if (_stopped) break;
                if (_gotLevels) backoff = _minBackoff;   // a connection that delivered levels resets the backoff; a rejected one keeps growing it
                delay = Math.Min(backoff + rng.NextDouble() * Math.Min(backoff, _minBackoff), MaxBackoffSec);
                string why = _reject.Length > 0 ? _reject : _lastError;
                _status = "reconnecting in " + Math.Round(delay) + "s" + (why.Length > 0 ? " (" + why + ")" : "");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, MaxBackoffSec);
        }
    }

    async Task ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
                    return;
                }
                if (r.MessageType == WebSocketMessageType.Text)
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);
            if (sb.Length == 0) continue;
            foreach (var reply in Handle(sb.ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                await SendAsync(ws, reply, ct).ConfigureAwait(false);
            if (Stopped)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
                return;
            }
        }
    }

    async Task PingAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(PingSec), ct).ConfigureAwait(false);
            if (!await SendAsync(ws, Protocol.Ping, ct).ConfigureAwait(false)) return;
        }
    }

    async Task<bool> SendAsync(ClientWebSocket ws, string payload, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open) return false;
            await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
        finally { _sendGate.Release(); }
    }

    // Handles one server message and returns the messages to send back. No socket here, so tests drive it directly.
    public List<string> Handle(string json, long nowMs)
    {
        var replies = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return replies; }
        using (doc)
        {
            var o = doc.RootElement;
            string op = Protocol.Str(o, "op");
            lock (_lock)
            {
                switch (op)
                {
                    case "hello":
                        double hint = Protocol.Num(o, "backoff_hint_sec", 0);
                        if (hint > 0) _minBackoff = Math.Min(hint, MaxBackoffSec);
                        var caps = new HashSet<string>();
                        var capsEl = Protocol.Get(o, "capabilities");
                        if (capsEl.ValueKind == JsonValueKind.Array)
                            foreach (var c in capsEl.EnumerateArray())
                                if (c.ValueKind == JsonValueKind.String) caps.Add(c.GetString());
                        _historyCapable = caps.Contains("ranked_history_v1");
                        _qqqCapable = caps.Contains("ranked_profile_qqq_v1") && caps.Contains("ranked_multi_v1");
                        if (!caps.Contains("ranked_profile_v2")) _status = "connected (server has no ranked levels)";
                        AddHistoryRequests(replies);
                        break;

                    case "ranked_profile_snapshot":
                        var snap = Protocol.ParseSnapshot(o, nowMs);
                        if (snap == null || Array.IndexOf(Profiles, snap.Profile) < 0) break;
                        _snaps[snap.Profile] = snap;
                        _profileErrors.Remove(snap.Profile);
                        if (snap.Status == "live" || snap.Status == "degraded")
                            TrailOf(snap.Profile)[snap.DataTsMs > 0 ? snap.DataTsMs : nowMs] = new Frame { TsMs = snap.DataTsMs > 0 ? snap.DataTsMs : nowMs, Rows = snap.Rows };
                        _gotLevels = true;
                        if (_reject.Length == 0) _status = "connected";
                        break;

                    case "ranked_profile_history":
                        if (!Protocol.ParseHistory(o, out string profile, out _, out var frames) || Array.IndexOf(Profiles, profile) < 0) break;
                        var trail = TrailOf(profile);
                        foreach (var f in frames) trail.TryAdd(f.TsMs, f);   // a live frame at the same minute wins
                        break;

                    case "clear":   // the session closed: drop everything, subscribe again for the next one
                        ClearLocked();
                        replies.Add(Protocol.Subscribe(Profiles));
                        AddHistoryRequests(replies);
                        break;

                    case "error":
                        OnError(o);
                        break;

                    default:
                        return replies;   // pong and anything new: nothing changed
                }
                _version++;
            }
        }
        return replies;
    }

    void AddHistoryRequests(List<string> replies)
    {
        if (!_historyCapable) return;
        foreach (var p in Profiles)
            if (p != Protocol.Qqq || _qqqCapable)
                replies.Add(Protocol.HistoryRequest(p));
    }

    void OnError(JsonElement o)
    {
        string reason = Protocol.Cap(Protocol.Str(o, "reason"), 120);
        if (Protocol.Str(o, "scope") == "ranked_profile")
        {
            string p = Protocol.Str(o, "profile");
            foreach (var key in p.Length > 0 ? new[] { p } : Profiles)
                _profileErrors[key] = reason;
            return;
        }
        switch (reason)
        {
            case "conn_per_token_exceeded": Reject("connection limit for this token", stop: false); break;
            case "session_displaced": Reject("replaced by a newer connection; reload the indicator to take over", stop: true); break;
            case "entitlement_expired": Reject("token expired", stop: true); break;
            case "gexstream_unavailable": case "gexladder_unavailable": break;   // features we do not subscribe to
            default:
                // An unknown plugin header would most likely show up here: stop instead of retrying, the user asks GWT.
                if (reason.IndexOf("plugin", StringComparison.OrdinalIgnoreCase) >= 0) Reject("server refused this plugin (" + reason + ")", stop: true);
                else if (reason.Length > 0) _status = "server error: " + reason;
                break;
        }
    }

    void Reject(string why, bool stop)
    {
        _reject = why;
        _stopped |= stop;
        _status = "rejected: " + why;
    }

    void OnConnectFailure(ClientWebSocket ws, Exception ex)
    {
        HttpStatusCode code = 0;
        try { code = ws.HttpStatusCode; } catch { }
        lock (_lock)
        {
            switch ((int)code)
            {
                case 401: Reject("invalid token", stop: true); break;
                case 403: Reject("token expired or this plugin is not allowed (HTTP 403)", stop: true); break;
                case 404: Reject("no GWT endpoint at this Server URL (HTTP 404)", stop: true); break;
                case 400: case 426: Reject("server refused the connection (HTTP " + (int)code + "), plugin header?", stop: true); break;
                default:
                    _lastError = Protocol.Cap(ex.Message, 120);
                    break;
            }
            _version++;
        }
    }

    SortedDictionary<long, Frame> TrailOf(string profile)
    {
        if (!_trails.TryGetValue(profile, out var t)) _trails[profile] = t = new SortedDictionary<long, Frame>();
        return t;
    }

    void ClearLocked()
    {
        _snaps.Clear();
        _trails.Clear();
        _profileErrors.Clear();
    }

    void SetStatus(string s)
    {
        lock (_lock) { _status = s; _version++; }
    }

}
