using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Gwt;

// GWT Level Importer (/api/v1/atas/levels?root=NQ|ES): Desk, Market Structure and Market Auction levels, already in futures points.
public static class LevelImport
{
    const uint LabelBack = 0xE10F1116;  // ATAS LabelBack 225,15,17,22
    const double ZoneLabelX = 0.80;     // shortcut: zone labels sit at a fixed 80% of the width (ATAS right-aligns them), move if they hit the price scale

    public static async Task<(List<LiveItem> items, string status)> Fetch(HttpClient http, string token, CancellationToken ct)
    {
        var items = new List<LiveItem>();
        var parts = new List<string>();
        foreach (var root in new[] { LiveRoot.NQ, LiveRoot.ES })
        {
            var (body, err) = await DarkPool.Get(http, "/api/v1/atas/levels?root=" + root, token, ct).ConfigureAwait(false);
            if (err != null) { parts.Add(root + " " + err); continue; }
            var (it, st) = Parse(body, root);
            items.AddRange(it);
            parts.Add(st);
        }
        return (items, "levels " + string.Join(", ", parts));
    }

    public static (List<LiveItem> items, string status) Parse(string json, LiveRoot root)
    {
        var zones = new List<LiveItem>();
        var lines = new List<LiveItem>();
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
                return (new List<LiveItem>(), root + " unreadable reply");
            string served = DarkPool.Str(r, "root");
            if (served.Length > 0 && !string.Equals(served, root.ToString(), StringComparison.OrdinalIgnoreCase))
                return (new List<LiveItem>(), root + " server sent " + served);
            double tick = DarkPool.Num(r, "tick");
            if (!(tick > 0)) tick = 0.25;

            foreach (var l in levels.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.Object) continue;
                double price = DarkPool.Num(l, "price");
                if (!(price > 0)) continue;
                string group = DarkPool.Str(l, "group");
                if (group == "darkpool") continue;   // drawn by the DarkPool client (its own toggle); would otherwise show twice
                uint c = Color(group, DarkPool.Str(l, "tone"));
                // shortcut: a merged level (members from several groups) goes to its primary group only; split it if per-group toggles must hide parts
                int shown = l.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() > 0 ? m.GetArrayLength() : 1;
                string label = DarkPool.Str(l, "tag") + "  " + DarkPool.FormatPrice(price, tick);
                double lo = DarkPool.Num(l, "lo"), hi = DarkPool.Num(l, "hi");
                var g = Group(group);
                if (hi > lo && lo > 0)
                {
                    // ATAS: fill alpha 34, dashed edges at Fade(120, 85% line opacity), label at the price clamped into the zone.
                    zones.Add(new LiveItem { Root = root, Group = g, Kind = LiveKind.Box, Bars = true, X1 = LiveItem.FirstBar, X2 = LiveItem.FarRight, Y1 = lo, Y2 = hi,
                        Fill = LiveItem.WithAlpha(c, 34), Line = LiveItem.WithAlpha(c, 102), Dash = LiveDash.Dash, Front = true });
                    lines.Add(new LiveItem { Root = root, Group = g, Kind = LiveKind.Text, X1 = ZoneLabelX, Y1 = Math.Clamp(price, lo, hi),
                        Fill = LabelBack, Line = c, Text = label, Front = true });
                }
                else
                {
                    string style = DarkPool.Str(l, "style");
                    lines.Add(new LiveItem { Root = root, Group = g, Kind = LiveKind.Level, Y1 = price, Y2 = price,
                        Fill = LabelBack, Line = LiveItem.WithAlpha(c, 182), // Fade(215, 85)
                        Width = Math.Clamp(1 + (shown >= 3 ? 2 : shown == 2 ? 1 : 0), 1, 6),
                        Dash = style == "dashed" ? LiveDash.Dash : style == "dotted" ? LiveDash.Dot : LiveDash.Solid,
                        Text = label, Front = true });
                }
            }
            var missing = new List<string>();
            if (r.TryGetProperty("unavailable", out var un) && un.ValueKind == JsonValueKind.Array)
                foreach (var u in un.EnumerateArray()) if (u.ValueKind == JsonValueKind.Object) missing.Add(DarkPool.Str(u, "group"));
            zones.AddRange(lines);
            return (zones, root + " " + (zones.Count - CountLabels(zones)) + " levels" + (missing.Count > 0 ? " (missing " + string.Join("/", missing) + ")" : ""));
        }
        catch (JsonException) { return (new List<LiveItem>(), root + " unreadable reply"); }
    }

    static int CountLabels(List<LiveItem> items) => items.FindAll(i => i.Kind == LiveKind.Text).Count;

    // ATAS panel: "Desk, Market Structure and Auction levels". Structure and the two Market Auction groups get their own toggle; the rest is the desk.
    public static LiveGroup Group(string group) => group switch
    {
        "structure" => LiveGroup.Structure,
        "auction" or "zones" => LiveGroup.Auction,
        _ => LiveGroup.Desk, // session, 0dte, oi, darkpool, gammamap, note, vwap
    };

    // LevelImportLayout.LevelColor
    public static uint Color(string group, string tone)
    {
        switch (group)
        {
            case "session": return 0xFFCBD5E1;
            case "auction": return tone == "poc" ? 0xFF5EEAD4 : 0xFF2DBEAA;
            case "0dte": case "note": case "gammamap": case "oi":
                return tone switch
                {
                    "call" => 0xFF34D399, "put" => 0xFFF87171, "zero" => 0xFFFBBF24,
                    "max_gamma" => 0xFF60A5FA, "top_abs" => 0xFFC084FC, "combo" => 0xFF94A3B8,
                    _ => group == "note" ? 0xFFEAB308 : 0xFF94A3B8,
                };
            case "darkpool": return 0xFFFF3EB5;
            case "vwap": return 0xFF22D3EE;
            case "structure": return tone == "high" ? 0xFFFB923C : tone == "low" ? 0xFF38BDF8 : 0xFFA5B4FC;
            case "zones": return tone switch { "hvn" => 0xFF818CF8, "lvn" => 0xFF94A3B8, "poc" => 0xFF5EEAD4, _ => 0xFF2DD4BF };
            default: return 0xFFCBD5E1;
        }
    }

    public static List<string> SelfCheck()
    {
        var f = new List<string>();
        void Check(bool ok, string what) { if (!ok) f.Add("LevelImport: " + what); }
        // Shaped like LevelImportProtocol.TryParse expects.
        const string json = @"{""root"":""NQ"",""contract"":""NQZ6"",""tick"":0.25,""version"":3,""as_of_ms"":1763000000000,""server_ts_ms"":1763000000000,
 ""last"":21010.5,""basis"":{""value_pts"":120.5,""source"":""live""},""source"":""live"",
 ""groups"":[{""key"":""session"",""name"":""Session""},{""key"":""structure"",""name"":""Structure""}],
 ""levels"":[
  {""id"":""pdh"",""group"":""session"",""tag"":""PDH"",""label"":""Prior day high"",""price"":21100.25,""tone"":"""",""style"":""solid"",""weight"":1},
  {""id"":""dte0_c1"",""group"":""0dte"",""tag"":""+Gamma"",""price"":21200,""tone"":""call"",""style"":""dashed"",""weight"":2,
   ""members"":[{""id"":""dte0_c1"",""group"":""0dte"",""tag"":""+Gamma"",""label"":""""},{""id"":""oi_c"",""group"":""oi"",""tag"":""OI +G"",""label"":""""}]},
  {""id"":""swh"",""group"":""structure"",""tag"":""Strong High"",""price"":21300.5,""tone"":""high"",""style"":""dotted""},
  {""id"":""vpoc"",""group"":""auction"",""tag"":""POC"",""price"":20950,""tone"":""poc""},
  {""id"":""z1"",""group"":""zones"",""tag"":""HVN"",""price"":20900,""lo"":20880,""hi"":20920,""tone"":""hvn""},
  {""id"":""bad"",""group"":""session"",""tag"":""X"",""price"":0}],
 ""unavailable"":[{""group"":""gammamap"",""reason"":""not published""}]}";
        var (items, status) = Parse(json, LiveRoot.NQ);
        Check(status == "NQ 5 levels (missing gammamap)", "status '" + status + "'");
        Check(items.TrueForAll(i => i.Root == LiveRoot.NQ), "root");
        LiveItem By(string tagStart) => items.Find(i => i.Text.StartsWith(tagStart));
        var pdh = By("PDH");
        Check(pdh != null && pdh.Kind == LiveKind.Level && pdh.Group == LiveGroup.Desk && pdh.Y1 == 21100.25 && pdh.Text == "PDH  21100.25"
              && pdh.Line == 0xB6CBD5E1u && pdh.Width == 1 && pdh.Dash == LiveDash.Solid, "PDH");
        var g = By("+Gamma");
        Check(g != null && g.Group == LiveGroup.Desk && g.Width == 2 && g.Dash == LiveDash.Dash && g.Line == 0xB634D399u, "+Gamma merged");
        var sh = By("Strong High");
        Check(sh != null && sh.Group == LiveGroup.Structure && sh.Dash == LiveDash.Dot && sh.Line == 0xB6FB923Cu, "Structure");
        var poc = By("POC");
        Check(poc != null && poc.Group == LiveGroup.Auction && poc.Line == 0xB65EEAD4u, "POC");
        var box = items.Find(i => i.Kind == LiveKind.Box);
        Check(box != null && box.Group == LiveGroup.Auction && box.Y1 == 20880 && box.Y2 == 20920 && box.Fill == 0x22818CF8u && box.Dash == LiveDash.Dash, "zone box");
        var hvn = By("HVN");
        Check(hvn != null && hvn.Kind == LiveKind.Text && hvn.Y1 == 20900 && hvn.Fill == LabelBack, "zone label");
        Check(items.IndexOf(box) == 0, "zones drawn first");
        Check(Parse(json, LiveRoot.ES).status == "ES server sent NQ" && Parse("[]", LiveRoot.ES).items.Count == 0, "root mismatch / bad json");
        return f;
    }
}
