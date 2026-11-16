using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Gwt;

// Conviction Blocks: nearby NDX, QQQ and net OI levels merged into one zone, as GWT's ATAS indicator draws them.
// Score = 1 - prod(1 - weight x strength) over the block's levels (agreement compounds);
// tone = net gamma sign: polarity > 0.35 long (green), < -0.35 short (red), between flip (amber), no signed level neutral (cyan, OI only).

public enum Kind { NdxGamma, QqqGamma, NdxAbs, QqqAbs, NetOi, Hvl, OtherOi }

public enum Tone { Long, Short, Flip, Neutral }

public sealed class Member
{
    public double Price;
    public Kind Kind;
    public int Sign;              // +1 call gamma, -1 put gamma, 0 OI
    public double Strength;
    public bool Qqq, Stale;
    public Tag Tag;
    public double Contribution => Blocks.Weight(Kind) * Strength;
}

public sealed class Block
{
    public double Lo, Hi, Score, Polarity;
    public Tone Tone;
    public int Rank;                                    // 1 = strongest
    public List<Member> Members = new List<Member>();   // strongest contribution first
    public bool Stale => Members.All(m => m.Stale);
}

// Which tags count. Defaults are GWT's: NDX all ranks, QQQ C/P up to 3 and Ab up to 2 with no QQQ net OI, delta off.
public sealed class Filter
{
    public bool Gamma = true, Abs = true, Hvl = true, Oi = true, NetOi = true, Delta = false;
    public int MaxCallRank, MaxPutRank, MaxAbsRank;     // 0 = no cap

    public static Filter Ndx() => new Filter();
    public static Filter Qqq() => new Filter { NetOi = false, MaxCallRank = 3, MaxPutRank = 3, MaxAbsRank = 2 };

    public bool Shows(Tag t) => t.Group switch
    {
        Group.Hvl => Hvl,
        Group.Gamma => Gamma && Within(t.Rank, Protocol.IsPutGamma(t) ? MaxPutRank : MaxCallRank),
        Group.Abs => Abs && Within(t.Rank, MaxAbsRank),
        Group.Delta => Delta,
        Group.CallOi or Group.PutOi or Group.OtherOi => Oi,
        Group.NetOi => NetOi,
        _ => true,
    };

    static bool Within(int rank, int cap) => cap <= 0 || rank <= cap;
}

public static class Blocks
{
    public const double PolarityThreshold = 0.35;

    public static double Weight(Kind k) => k switch
    {
        Kind.NdxGamma => 0.85,
        Kind.QqqGamma => 0.70,
        Kind.NdxAbs => 0.50,
        Kind.QqqAbs => 0.40,
        Kind.NetOi => 0.40,
        Kind.Hvl => 0.85,
        _ => 0.40,
    };

    public static bool TryClassify(Tag t, bool qqq, out Kind kind, out int sign)
    {
        sign = 0;
        switch (t.Group)
        {
            case Group.Gamma:
                kind = qqq ? Kind.QqqGamma : Kind.NdxGamma;
                sign = Protocol.IsPutGamma(t) || t.Value < 0 ? -1 : 1;
                return true;
            case Group.Abs:
                kind = qqq ? Kind.QqqAbs : Kind.NdxAbs;
                sign = Math.Sign(t.Value);
                return true;
            case Group.NetOi: kind = Kind.NetOi; return true;
            case Group.Hvl: kind = Kind.Hvl; return true;
            case Group.CallOi: case Group.PutOi: case Group.OtherOi: kind = Kind.OtherOi; return true;
            default: kind = Kind.OtherOi; return false;
        }
    }

    public static void Collect(Snapshot snap, Filter f, bool stale, List<Member> into)
    {
        if (snap == null) return;
        foreach (var row in snap.Rows)
            foreach (var tag in row.Tags)
                if (f.Shows(tag) && TryClassify(tag, snap.IsQqq, out var kind, out int sign))
                    into.Add(new Member { Price = row.Price, Kind = kind, Sign = sign, Strength = tag.Strength, Qqq = snap.IsQqq, Stale = stale, Tag = tag });
    }

    // Chain levels sorted by price: a level joins the block below when it is within eps of the block's top
    // and the block stays no taller than maxHeight.
    public static List<Block> Build(List<Member> members, double eps, double maxHeight)
    {
        var sorted = members.OrderBy(m => m.Price).ThenBy(m => m.Qqq).ToList();
        var blocks = new List<Block>();
        foreach (var m in sorted)
        {
            var last = blocks.Count > 0 ? blocks[^1] : null;
            if (last != null && m.Price - last.Hi <= eps && m.Price - last.Lo <= maxHeight)
            {
                last.Hi = m.Price;
                last.Members.Add(m);
            }
            else
                blocks.Add(new Block { Lo = m.Price, Hi = m.Price, Members = { m } });
        }
        foreach (var b in blocks)
        {
            double miss = 1, signed = 0, unsigned = 0;
            foreach (var m in b.Members)
            {
                double c = Math.Clamp(m.Contribution, 0, 1);
                miss *= 1 - c;
                signed += m.Sign * c;
                unsigned += Math.Abs(m.Sign) * c;
            }
            b.Score = 1 - miss;
            b.Polarity = unsigned > 0 ? signed / unsigned : 0;
            b.Tone = unsigned <= 0 ? Tone.Neutral
                : b.Polarity > PolarityThreshold ? Tone.Long
                : b.Polarity < -PolarityThreshold ? Tone.Short : Tone.Flip;
            b.Members = b.Members.OrderByDescending(m => m.Contribution).ThenBy(m => m.Price).ToList();
        }
        int rank = 1;
        foreach (var b in blocks.OrderByDescending(b => b.Score).ThenBy(b => b.Lo))
            b.Rank = rank++;
        return blocks;
    }

    // "B1  QP1  P1  N3  +2  .99": rank, the three leading codes, how many more, score.
    public static string Label(Block b)
    {
        var sb = new StringBuilder("B").Append(b.Rank.ToString(CultureInfo.InvariantCulture));
        int shown = Math.Min(3, b.Members.Count);
        for (int i = 0; i < shown; i++) sb.Append("  ").Append(b.Members[i].Tag.Display);
        if (b.Members.Count > shown) sb.Append("  +").Append((b.Members.Count - shown).ToString(CultureInfo.InvariantCulture));
        return sb.Append("  ").Append(ScoreText(b.Score)).ToString();
    }

    public static string ScoreText(double score)
    {
        string s = Math.Clamp(double.IsFinite(score) ? score : 0, 0, 1).ToString("0.00", CultureInfo.InvariantCulture);
        return s.StartsWith("0", StringComparison.Ordinal) ? s.Substring(1) : s;
    }

    // Fill alpha 0-255: weak blocks faint, labelled (top N) blocks stronger.
    public static byte FillAlpha(double score, int opacityPct, bool labelled) =>
        (byte)Math.Round(Math.Clamp((0.06 + 0.32 * Math.Clamp(score, 0, 1)) * Math.Clamp(opacityPct, 0, 100) / 100.0 * (labelled ? 1.0 : 0.55), 0, 1) * 255);

    public static byte EdgeAlpha(byte fill, bool labelled) => (byte)Math.Min(255, fill * 2 + (labelled ? 40 : 10));
}

// Level Trails: one run per stretch of frames where a code (C1, QP2, Ab1, N3 ...) sat at one price.
public sealed class Run
{
    public Tag Tag;
    public double Price, Strength;
    public long StartMs, EndMs;
    public bool Live;             // still open at the last frame: drawn up to the last bar
}

public static class Trails
{
    public static List<Run> Runs(List<Frame> frames, Filter filter)
    {
        var runs = new List<Run>();
        var open = new Dictionary<string, Run>();
        foreach (var f in frames)
        {
            var seen = new HashSet<string>();
            foreach (var row in f.Rows)
                foreach (var tag in row.Tags)
                {
                    if (!filter.Shows(tag) || tag.Group is not (Group.Gamma or Group.Abs or Group.NetOi) || !seen.Add(tag.Display))
                        continue;
                    if (open.TryGetValue(tag.Display, out var r) && Math.Abs(r.Price - row.Price) < 1e-6)
                    {
                        r.EndMs = f.TsMs;
                        r.Strength = Math.Max(r.Strength, tag.Strength);
                        continue;
                    }
                    if (r != null)
                    {
                        r.EndMs = f.TsMs;   // it stood there until this frame moved it
                        runs.Add(r);
                    }
                    open[tag.Display] = new Run { Tag = tag, Price = row.Price, Strength = tag.Strength, StartMs = f.TsMs, EndMs = f.TsMs };
                }
            foreach (var key in open.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                open[key].EndMs = f.TsMs;
                runs.Add(open[key]);
                open.Remove(key);
            }
        }
        foreach (var r in open.Values)
        {
            r.Live = true;
            runs.Add(r);
        }
        return runs;
    }
}
