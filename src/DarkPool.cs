using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Gwt;

// GWT dark pool levels (/api/v1/darkpool/levels): QQQ drawn on NQ, SPY drawn on ES, the way GWT's ATAS indicator draws them.
public static class DarkPool
{
    public const string BaseUrl = "https://gwtrade.app";
    const string PluginHeader = "DeepChartsGwtBridge/0.1 (personal)";
    const int Top = 5;                 // ATAS default "Levels shown"
    const uint Pink = 0xFFFF3EB5;      // ATAS default colour 255,62,181
    // Notional bar lane in bars right of the last bar (ATAS: 14% of the width from the left edge; Deepchart draws lines only with bar X).
    const double Lane = 30;
    const double BarX = 2;

    public static async Task<(List<LiveItem> items, string status)> Fetch(HttpClient http, string token, CancellationToken ct)
    {
        var (body, err) = await Get(http, "/api/v1/darkpool/levels", token, ct).ConfigureAwait(false);
        return err != null ? (new List<LiveItem>(), "dark pool: " + err) : Parse(body, Top);
    }

    // GET with GWT's headers. The error string never contains the token.
    internal static async Task<(string body, string err)> Get(HttpClient http, string path, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, "no token");
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.TryAddWithoutValidation("X-GWT-Token", token.Trim());
        req.Headers.TryAddWithoutValidation("X-GWT-Plugin", PluginHeader);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15)); // ATAS request timeout
        try
        {
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (code != 200)
                return (null, "HTTP " + code + code switch
                {
                    401 => " (invalid token)", 403 => " (token expired)", 404 => " (not served)",
                    503 => " (server warming up)", _ => ""
                });
            return (await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "timeout"); }
        catch (HttpRequestException) { return (null, "server unreachable"); }
        catch (IOException) { return (null, "read failed"); }
    }

    public static (List<LiveItem> items, string status) Parse(string json, int top = Top)
    {
        var items = new List<LiveItem>();
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tickers", out var tickers) || tickers.ValueKind != JsonValueKind.Array)
                return (items, "dark pool: unreadable reply");
            string nq = AddTicker(items, Find(tickers, "QQQ"), LiveRoot.NQ, top);
            string es = AddTicker(items, Find(tickers, "SPY"), LiveRoot.ES, top);
            return (items, "dark pool QQQ>NQ " + nq + ", SPY>ES " + es);
        }
        catch (JsonException) { return (new List<LiveItem>(), "dark pool: unreadable reply"); }
    }

    // ATAS: chart price = level.futures + (etfPrice - level.price) * ticker.ratio; 0 = not drawable.
    public static double ToFutures(double etfPrice, double levelEtf, double levelFutures, double ratio)
    {
        if (!(etfPrice > 0) || !(levelFutures > 0)) return 0;
        double v = levelFutures + (etfPrice - levelEtf) * ratio;
        return v > 0 ? v : 0;
    }

    static string AddTicker(List<LiveItem> items, JsonElement? found, LiveRoot root, int top)
    {
        if (found == null) return "missing";
        var t = found.Value;
        double ratio = Num(t, "ratio");
        if (!(t.TryGetProperty("basis_ok", out var ok) && ok.ValueKind == JsonValueKind.True) || !(ratio > 0)) return "basis unavailable";
        var levels = new List<JsonElement>();
        if (t.TryGetProperty("levels", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray()) if (e.ValueKind == JsonValueKind.Object) levels.Add(e);
        levels.Sort((a, b) => Num(a, "rank").CompareTo(Num(b, "rank")));
        if (levels.Count > top) levels.RemoveRange(top, levels.Count - top);
        double max = 0;
        foreach (var e in levels) max = Math.Max(max, Num(e, "notional"));

        // ATAS paints in passes (zones, lines, bars, tags), each from the weakest level to the strongest.
        var zones = new List<LiveItem>(); var lines = new List<LiveItem>(); var bars = new List<LiveItem>(); var tags = new List<LiveItem>();
        int n = 0;
        for (int i = levels.Count - 1; i >= 0; i--)
        {
            var e = levels[i];
            double etf = Num(e, "price"), fut = Num(e, "futures");
            double px = ToFutures(etf, etf, fut, ratio);
            if (px <= 0) continue;
            n++;
            int rank = (int)Num(e, "rank");
            int tier = rank <= 3 ? 0 : rank <= 5 ? 1 : 2;
            byte lineA = tier == 0 ? (byte)255 : tier == 1 ? (byte)220 : (byte)170;
            double lo = ToFutures(Num(e, "low"), etf, fut, ratio), hi = ToFutures(Num(e, "high"), etf, fut, ratio);
            if (tier < 2 && lo > 0 && hi > 0 && lo != hi)
                zones.Add(Item(root, LiveKind.Box, LiveItem.FirstBar, LiveItem.FarRight, Math.Min(lo, hi), Math.Max(lo, hi), LiveItem.WithAlpha(Pink, tier == 0 ? (byte)45 : (byte)22), 0, 1));
            lines.Add(Item(root, LiveKind.Segment, LiveItem.FirstBar, LiveItem.FarRight, px, px, 0, LiveItem.WithAlpha(Pink, lineA), tier == 0 ? 2 : 1, tier == 2 ? LiveDash.Dot : LiveDash.Solid));
            double barEnd = BarX + (max > 0 ? Lane * Math.Clamp(Num(e, "notional") / max, 0, 1) : 0);
            bars.Add(Item(root, LiveKind.Segment, BarX, barEnd, px, px, 0,
                LiveItem.WithAlpha(Pink, tier == 0 ? (byte)225 : tier == 1 ? (byte)175 : (byte)125), tier == 0 ? 6 : tier == 1 ? 4 : 3));
            var tag = Item(root, LiveKind.Text, barEnd + 1, barEnd + 1, px, px, LiveItem.Argb(tier == 2 ? (byte)170 : (byte)215, 22, 22, 26), LiveItem.WithAlpha(Pink, lineA), 1);
            tag.Text = "DP" + rank.ToString(CultureInfo.InvariantCulture) + " " + FormatPrice(px, 0.25)
                + " · " + FormatNotional(Num(e, "notional")) + " · " + FormatShare(Num(e, "share"));
            tags.Add(tag);
        }
        items.AddRange(zones); items.AddRange(lines); items.AddRange(bars); items.AddRange(tags);
        return n.ToString(CultureInfo.InvariantCulture);
    }

    static LiveItem Item(LiveRoot root, LiveKind kind, double x1, double x2, double y1, double y2, uint fill, uint line, int width, LiveDash dash = LiveDash.Solid) =>
        new LiveItem { Root = root, Group = LiveGroup.DarkPool, Kind = kind, Bars = true, X1 = x1, X2 = x2, Y1 = y1, Y2 = y2, Fill = fill, Line = line, Width = width, Dash = dash, Font = 9, Front = true };

    static JsonElement? Find(JsonElement tickers, string ticker)
    {
        foreach (var t in tickers.EnumerateArray())
            if (t.ValueKind == JsonValueKind.Object && string.Equals(Str(t, "ticker"), ticker, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    // ATAS style: rounded to the tick, space as thousands separator ("21 345.25").
    internal static string FormatPrice(double price, double tick)
    {
        if (!(tick > 0) || !double.IsFinite(tick)) tick = 0.01;
        int dec = 0;
        while (dec < 6 && Math.Abs(tick * Math.Pow(10, dec) - Math.Round(tick * Math.Pow(10, dec))) >= 1e-7) dec++;
        return (Math.Round(price / tick) * tick).ToString("F" + dec, CultureInfo.InvariantCulture);   // 28106.50, as the GEX labels
    }

    static string FormatNotional(double v)
    {
        double a = Math.Abs(v);
        var c = CultureInfo.InvariantCulture;
        if (a >= 1e9) return "$" + (v / 1e9).ToString(a >= 1e11 ? "0" : "0.0", c) + "B";
        if (a >= 1e6) return "$" + (v / 1e6).ToString("0", c) + "M";
        if (a >= 1e3) return "$" + (v / 1e3).ToString("0", c) + "K";
        return "$" + v.ToString("0", c);
    }

    static string FormatShare(double share)
    {
        double p = share * 100;
        return p.ToString(p < 9.95 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";
    }

    internal static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;

    public static List<string> SelfCheck()
    {
        var f = new List<string>();
        void Check(bool ok, string what) { if (!ok) f.Add("DarkPool: " + what); }
        // Shaped like DarkPoolProtocol.TryParse expects. QQQ ratio 41.2: futures 21000 at QQQ 510.
        const string json = @"{""version"":7,""source"":""live"",""tickers"":[
 {""ticker"":""QQQ"",""index"":""NDX"",""family"":""nq"",""from"":""2026-11-09"",""to"":""2026-11-13"",""sessions"":5,""price"":511.2,
  ""ratio"":41.2,""ratio_at"":1763000000,""ratio_source"":""live"",""basis"":12.5,""basis_ok"":true,""total"":9.1e9,""as_of"":1763000000,""error"":"""",
  ""levels"":[
   {""rank"":2,""price"":505,""low"":504.5,""high"":505.5,""notional"":1.0e9,""size"":2000000,""trades"":40,""share"":0.11,""index"":20700,""futures"":20794},
   {""rank"":1,""price"":510,""low"":509.5,""high"":510.5,""notional"":2.0e9,""size"":4000000,""trades"":80,""share"":0.22,""index"":20900,""futures"":21000},
   {""rank"":3,""price"":500,""low"":0,""high"":0,""notional"":5.0e8,""size"":1,""trades"":1,""share"":0.055,""index"":1,""futures"":20588},
   {""rank"":4,""price"":499,""low"":498.9,""high"":499.1,""notional"":4.0e8,""size"":1,""trades"":1,""share"":0.04,""index"":1,""futures"":20547},
   {""rank"":5,""price"":498,""low"":497.9,""high"":498.1,""notional"":3.0e8,""size"":1,""trades"":1,""share"":0.03,""index"":1,""futures"":20506},
   {""rank"":6,""price"":497,""low"":496.9,""high"":497.1,""notional"":2.0e8,""size"":1,""trades"":1,""share"":0.02,""index"":1,""futures"":20465}]},
 {""ticker"":""SPY"",""ratio"":10.05,""basis_ok"":false,""levels"":[{""rank"":1,""price"":600,""low"":599,""high"":601,""notional"":1e9,""share"":0.3,""futures"":6030}]}]}";
        var (items, status) = Parse(json);
        Check(status == "dark pool QQQ>NQ 5, SPY>ES basis unavailable", "status '" + status + "'");
        Check(items.TrueForAll(i => i.Root == LiveRoot.NQ && i.Group == LiveGroup.DarkPool), "root/group");
        var tags = items.FindAll(i => i.Kind == LiveKind.Text);
        Check(tags.Count == 5, "5 tags, got " + tags.Count);
        var dp1 = tags.Find(i => i.Text.StartsWith("DP1 "));
        Check(dp1 != null && dp1.Y1 == 21000 && dp1.Text == "DP1 21000.00 · $2.0B · 22%", "DP1 tag '" + dp1?.Text + "'");
        Check(tags.Find(i => i.Text.StartsWith("DP6")) == null, "rank 6 must be cut by top 5");
        var zones = items.FindAll(i => i.Kind == LiveKind.Box);
        // DP1 zone 509.5..510.5 QQQ -> 21000 -/+ 0.5*41.2; rank 3 has no zone; ranks 4,5 get the fainter tier-1 zone.
        var z1 = zones.Find(z => z.Y1 < 21000 && z.Y2 > 21000);
        Check(zones.Count == 4, "4 zones, got " + zones.Count);
        Check(z1 != null && Math.Abs(z1.Y1 - 20979.4) < 1e-9 && Math.Abs(z1.Y2 - 21020.6) < 1e-9 && z1.Fill == 0x2DFF3EB5u, "DP1 zone");
        var bar1 = items.Find(i => i.Kind == LiveKind.Segment && i.Width == 6 && i.Y1 == 21000);
        Check(bar1 != null && Math.Abs(bar1.X2 - (BarX + Lane)) < 1e-12, "DP1 bar full lane");
        var bar2 = items.Find(i => i.Kind == LiveKind.Segment && i.Width == 6 && i.Y1 == 20794);
        Check(bar2 != null && Math.Abs(bar2.X2 - (BarX + Lane / 2)) < 1e-12, "DP2 bar half lane");
        Check(Math.Abs(ToFutures(505.5, 505, 20794, 41.2) - 20814.6) < 1e-9, "ToFutures");
        Check(Parse("{\"x\":1}").status == "dark pool: unreadable reply" && Parse("not json").items.Count == 0, "bad json");
        return f;
    }
}
