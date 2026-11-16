using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Gwt;

// GWT's ranked NDX + QQQ 0DTE levels as Conviction Blocks for NQ charts, the way GwtLevels drew them
// (..\GwtLevels\GwtLevels.cs DrawBlocks), turned into LiveItems. Defaults are GwtLevels' defaults.
public static class Gex
{
    const double ClusterDistance = 7, MaxBlockHeight = 24, BlockPadding = 2;
    const int LabelledBlocks = 7, Opacity = 100, Font = 9;
    const double LabelX = 0.78;            // pill: fraction of the chart width
    const double TickLeft = 2, TickRight = 6;   // strike ticks: bars right of the last bar (shapes need bar X on Deepchart)

    static readonly uint LongC = LiveItem.Argb(255, 86, 200, 126), ShortC = LiveItem.Argb(255, 224, 93, 93),
        FlipC = LiveItem.Argb(255, 240, 180, 76), NetOiC = LiveItem.Argb(255, 0, 220, 255), AbsC = LiveItem.Argb(255, 171, 130, 224),
        OiC = LiveItem.Argb(255, 245, 194, 107), StaleC = LiveItem.Argb(255, 150, 150, 155), HvlC = LiveItem.Argb(255, 64, 205, 224),
        PillBack = LiveItem.Argb(150, 16, 16, 20);

    public static List<LiveItem> Items(ISource feed, long nowMs, out string status)
    {
        var members = new List<Member>();
        var parts = new List<string>();
        foreach (var p in Feed.Profiles)
        {
            var s = feed.Snapshot(p);
            string name = p == Protocol.Qqq ? "QQQ" : "NDX";
            if (s == null) { parts.Add(name + " waiting"); continue; }
            var f = Protocol.FreshnessOf(s.Status, s.AgeMs(nowMs));
            if (s.Rows.Length == 0) { parts.Add(name + (s.IsQqq && s.RatioStale ? " ratio stale" : " no levels")); continue; }
            if (f == Freshness.Unavailable) { parts.Add(name + " unavailable"); continue; }
            Blocks.Collect(s, s.IsQqq ? Filter.Qqq() : Filter.Ndx(), f == Freshness.Stale, members);
            parts.Add(name + (f == Freshness.Stale ? " stale" : ""));
        }
        status = feed.Status == "connected" ? "GEX " + string.Join(" + ", parts) : "GEX " + feed.Status;

        var items = new List<LiveItem>();
        foreach (var b in Blocks.Build(members, ClusterDistance, MaxBlockHeight).OrderByDescending(b => b.Rank))   // strongest drawn last, on top
        {
            bool labelled = b.Rank <= LabelledBlocks;
            uint color = b.Stale ? StaleC : ToneColor(b.Tone);
            byte fill = Blocks.FillAlpha(b.Score, Opacity, labelled);
            string label = Blocks.Label(b);
            items.Add(new LiveItem
            {
                Root = LiveRoot.NQ, Group = LiveGroup.Gex, Kind = LiveKind.Box, Bars = true, X1 = LiveItem.FirstBar, X2 = LiveItem.FarRight,
                Y1 = b.Hi + BlockPadding, Y2 = b.Lo - BlockPadding,
                Fill = LiveItem.WithAlpha(color, fill), Line = LiveItem.WithAlpha(color, Blocks.EdgeAlpha(fill, labelled)),
                Text = label + "   " + Px(b.Lo) + (b.Hi > b.Lo ? " - " + Px(b.Hi) : ""),
            });
            foreach (var m in b.Members)
                items.Add(new LiveItem
                {
                    Root = LiveRoot.NQ, Group = LiveGroup.Gex, Kind = LiveKind.Segment, Bars = true, X1 = TickLeft, X2 = TickRight,
                    Y1 = m.Price, Line = m.Stale ? StaleC : TagColor(m.Tag), Width = 2, Front = true,
                    Text = m.Tag.Display + "  " + Px(m.Price),
                });
            if (labelled)
                items.Add(new LiveItem
                {
                    Root = LiveRoot.NQ, Group = LiveGroup.Gex, Kind = LiveKind.Text, X1 = LabelX, Y1 = (b.Hi + b.Lo) / 2,
                    Fill = PillBack, Line = color, Text = label, Font = Font, Bold = b.Rank == 1, Front = true,
                });
        }
        return items;
    }

    static uint ToneColor(Tone t) => t switch { Tone.Long => LongC, Tone.Short => ShortC, Tone.Flip => FlipC, _ => NetOiC };

    static uint TagColor(Tag t) => t.Group switch
    {
        Group.Gamma => Protocol.IsPutGamma(t) || t.Value < 0 ? ShortC : LongC,
        Group.Abs => AbsC,
        Group.NetOi => NetOiC,
        Group.Hvl => HvlC,
        _ => OiC,
    };

    static string Px(double p) => p.ToString("0.##", CultureInfo.InvariantCulture);
}
