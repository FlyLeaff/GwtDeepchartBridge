using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Gwt;

// GWT's live level feed: one WebSocket at <server>/api/v1/atas/ws, JSON messages keyed by "op".
// Message shapes follow GWT's ATAS indicator (with GWT's permission); this is our own parser. Ranked strikes ("strike_ndx") are already NQ prices for both profiles: no basis is applied.

public enum Group { Hvl, Gamma, Abs, Delta, CallOi, PutOi, OtherOi, NetOi, Unknown }

public enum Freshness { Live, Degraded, Stale, Unavailable }

public sealed class Tag
{
    public string Code = "";      // C1, P3, Ab2, N5 ... as sent
    public string Display = "";   // QQQ codes get a Q prefix: QC1
    public string Family = "";
    public int Rank;
    public double Value;
    public double Strength;       // 0-1
    public Group Group => Protocol.Classify(Family, Code);
}

public sealed class Row
{
    public double Price;          // NQ points
    public double Strength;       // display strength 0-1
    public Tag[] Tags = Array.Empty<Tag>();
}

public sealed class Snapshot
{
    public string Profile = "";
    public long Revision, DataTsMs, ReceivedMs;
    public string Status = "live";
    public bool RatioStale;       // QQQ: no fresh NQ/QQQ ratio, the server sends no rows
    public Row[] Rows = Array.Empty<Row>();
    public bool IsQqq => Profile == Protocol.Qqq;
    public long AgeMs(long nowMs) => Math.Max(0, nowMs - (DataTsMs > 0 ? DataTsMs : ReceivedMs));
}

public sealed class Frame
{
    public long TsMs;
    public Row[] Rows = Array.Empty<Row>();
}

public static class Protocol
{
    public const string Ndx = "ndx_0dte_mm_v1";
    public const string Qqq = "qqq_0dte_mm_v1";
    public const string WsPath = "/api/v1/atas/ws";
    public const long StaleAfterMs = 180_000;        // about 3 minutes without an update
    public const long UnavailableAfterMs = 600_000;
    const int MaxRows = 32, MaxTags = 8, MaxFrames = 2000;

    // ---- client messages ----
    public static string Subscribe(IReadOnlyList<string> profiles) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["op"] = "ranked_profile_sub", ["enabled"] = true, ["profile"] = Ndx, ["profiles"] = profiles,
    });

    public static string HistoryRequest(string profile) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["op"] = "ranked_history_req", ["profile"] = profile });

    public const string Ping = "{\"op\":\"ping\"}";

    // https://host[/base] -> wss://host[/base]/api/v1/atas/ws. Only TLS: the token travels in a header.
    public static string WebSocketUrl(string serverUrl)
    {
        string s = (serverUrl ?? "").Trim().TrimEnd('/');
        if (s.EndsWith(WsPath, StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - WsPath.Length);
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "wss")
            || u.Host.Length == 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0)
            return "";
        return "wss://" + u.Authority + u.AbsolutePath.TrimEnd('/') + WsPath;
    }

    // ---- server messages ----
    public static Snapshot ParseSnapshot(JsonElement o, long nowMs)
    {
        if (Num(o, "v", 1) > 2) return null;
        string profile = Str(o, "profile");
        if (profile.Length == 0) return null;
        var levels = Get(o, "levels");
        if (levels.ValueKind != JsonValueKind.Undefined && levels.ValueKind != JsonValueKind.Null && levels.ValueKind != JsonValueKind.Array)
            return null;
        var snap = new Snapshot
        {
            Profile = profile,
            Revision = (long)Num(o, "revision", 0),
            DataTsMs = (long)Num(o, "data_ts_ms", 0),
            ReceivedMs = nowMs,
            Status = NormalizeStatus(Str(o, "status")),
        };
        var scale = Get(o, "scale");
        if (scale.ValueKind == JsonValueKind.Object)
        {
            double ratio = Num(scale, "ratio", double.NaN);
            snap.RatioStale = !(ratio > 0) || Bool(scale, "stale", true);
        }
        if (levels.ValueKind != JsonValueKind.Array) return snap;

        string prefix = snap.IsQqq ? "Q" : "";
        var rows = new List<Row>();
        foreach (var r in levels.EnumerateArray())
        {
            if (rows.Count >= MaxRows) break;
            if (r.ValueKind != JsonValueKind.Object) continue;
            double price = Num(r, "strike_ndx", double.NaN);
            if (!double.IsFinite(price) || price <= 0) continue;
            double rowStrength = Clamp01(Num(r, "display_strength_0_1", 0));
            var tags = new List<Tag>();
            var tagsEl = Get(r, "tags");
            if (tagsEl.ValueKind == JsonValueKind.Array)
                foreach (var t in tagsEl.EnumerateArray())
                {
                    if (tags.Count >= MaxTags) break;
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    string code = Cap(Str(t, "code"), 16);
                    if (code.Length == 0) continue;
                    double s = Num(t, "strength_0_1", double.NaN);
                    double v = Num(t, "value", 0);
                    tags.Add(new Tag
                    {
                        Code = code,
                        Display = prefix + code,
                        Family = Cap(Str(t, "family"), 24),
                        Rank = (int)Num(t, "rank", 0),
                        Value = double.IsFinite(v) ? v : 0,
                        Strength = double.IsFinite(s) ? Clamp01(s) : rowStrength,   // no per-tag strength: the row's
                    });
                }
            rows.Add(new Row { Price = price, Strength = rowStrength, Tags = tags.ToArray() });
        }
        snap.Rows = rows.ToArray();
        return snap;
    }

    // ranked_profile_history: frames [{t: ms, l: [[strike, strength, "C1 Ab2", value, strike_src?], ...]}], in chunks until final.
    public static bool ParseHistory(JsonElement o, out string profile, out bool final, out List<Frame> frames)
    {
        frames = new List<Frame>();
        profile = Str(o, "profile");
        final = Bool(o, "final", false);
        var arr = Get(o, "frames");
        if (Num(o, "v", 1) > 1 || profile.Length == 0 || arr.ValueKind != JsonValueKind.Array) return false;
        string prefix = profile == Qqq ? "Q" : "";
        foreach (var f in arr.EnumerateArray())
        {
            if (frames.Count >= MaxFrames) break;
            if (f.ValueKind != JsonValueKind.Object) continue;
            long t = (long)Num(f, "t", 0);
            if (t <= 0) continue;
            var rows = new List<Row>();
            var l = Get(f, "l");
            if (l.ValueKind == JsonValueKind.Array)
                foreach (var r in l.EnumerateArray())
                {
                    if (rows.Count >= MaxRows) break;
                    if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() < 4) continue;
                    double price = At(r, 0), strength = Clamp01(At(r, 1)), value = At(r, 3);
                    if (!double.IsFinite(price) || price <= 0) continue;
                    string codes = r[2].ValueKind == JsonValueKind.String ? r[2].GetString() ?? "" : "";
                    var tags = new List<Tag>();
                    foreach (var c in codes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (tags.Count < MaxTags)
                        {
                            string code = Cap(c, 16);
                            tags.Add(new Tag { Code = code, Display = prefix + code, Family = FamilyOfCode(code), Value = double.IsFinite(value) ? value : 0, Strength = strength });
                        }
                    if (tags.Count > 0) rows.Add(new Row { Price = price, Strength = strength, Tags = tags.ToArray() });
                }
            frames.Add(new Frame { TsMs = t, Rows = rows.ToArray() });
        }
        frames.Sort((a, b) => a.TsMs.CompareTo(b.TsMs));
        return true;
    }

    // ---- shared rules ----
    public static Group Classify(string family, string code)
    {
        family ??= "";
        if (string.Equals(code, "HVL", StringComparison.OrdinalIgnoreCase) || string.Equals(family, "hvl", StringComparison.OrdinalIgnoreCase))
            return Group.Hvl;
        switch (family)
        {
            case "gamma": case "gamma_pos": case "gamma_neg": return Group.Gamma;
            case "gamma_abs": return Group.Abs;
            case "delta": case "delta_pos": case "delta_neg": return Group.Delta;
            case "call_oi": return Group.CallOi;
            case "put_oi": return Group.PutOi;
        }
        if (family.StartsWith("net_oi", StringComparison.Ordinal)) return Group.NetOi;
        if (family.IndexOf("oi", StringComparison.OrdinalIgnoreCase) >= 0) return Group.OtherOi;
        if (family.StartsWith("gamma", StringComparison.Ordinal)) return Group.Gamma;
        if (family.StartsWith("delta", StringComparison.Ordinal)) return Group.Delta;
        return Group.Unknown;
    }

    // History rows carry codes only.
    public static string FamilyOfCode(string code)
    {
        if (code.StartsWith("Ab", StringComparison.Ordinal)) return "gamma_abs";
        if (code.Length > 1 && char.IsDigit(code[1]))
            switch (code[0])
            {
                case 'C': return "gamma_pos";
                case 'P': return "gamma_neg";
                case 'N': return "net_oi_90d";
            }
        return string.Equals(code, "HVL", StringComparison.OrdinalIgnoreCase) ? "hvl" : "";
    }

    public static bool IsPutGamma(Tag t) =>
        t.Family == "gamma_neg" || (t.Code.Length > 1 && t.Code[0] == 'P' && char.IsDigit(t.Code[1]));

    public static Freshness FreshnessOf(string status, long ageMs)
    {
        var f = status switch
        {
            "live" => Freshness.Live,
            "stale" => Freshness.Stale,
            "unavailable" => Freshness.Unavailable,
            _ => Freshness.Degraded,
        };
        if (ageMs >= UnavailableAfterMs) return Freshness.Unavailable;
        if (ageMs >= StaleAfterMs && f < Freshness.Stale) return Freshness.Stale;
        return f;
    }

    public static string AgeText(long ageMs)
    {
        long s = ageMs / 1000;
        return s < 120 ? s.ToString(CultureInfo.InvariantCulture) + "s"
            : s < 7200 ? (s / 60).ToString(CultureInfo.InvariantCulture) + "m"
            : (s / 3600).ToString(CultureInfo.InvariantCulture) + "h";
    }

    static string NormalizeStatus(string s) => s switch
    {
        "" => "live",
        "live" or "degraded" or "stale" or "unavailable" => s,
        _ => "degraded",
    };

    // ---- JSON helpers (missing or wrong-typed fields fall back) ----
    public static JsonElement Get(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : default;

    public static string Str(JsonElement o, string name)
    {
        var v = Get(o, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    public static double Num(JsonElement o, string name, double fallback)
    {
        var v = Get(o, name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : fallback;
    }

    static bool Bool(JsonElement o, string name, bool fallback)
    {
        var v = Get(o, name);
        return v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback);
    }

    static double At(JsonElement arr, int i) =>
        arr[i].ValueKind == JsonValueKind.Number && arr[i].TryGetDouble(out double d) ? d : double.NaN;

    static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    public static string Cap(string s, int max) => s.Length > max ? s.Substring(0, max) : s;
}
