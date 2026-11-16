using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gwt;

// One thing the GWT Live indicator draws. The bridge builds these from GWT's data and bakes them into the dll
// as Add(...) calls (Template\GwtLive.cs has the matching It class).
// X: with Bars, in bars from the chart's last bar (FirstBar / FarRight for the edges); without, a fraction of the chart width.
// Deepchart draws boxes and lines only with bar X (tested); a fraction of the width works for text only.
public enum LiveRoot { NQ = 0, ES = 1 }

public enum LiveGroup { Gex = 0, DarkPool = 1, Desk = 2, Structure = 3, Auction = 4 }

public enum LiveKind
{
    Box = 0,       // X1..X2 x Y1..Y2, Fill + Line colours
    Segment = 1,   // horizontal line at Y1 from X1 to X2
    Text = 2,      // label at X1, Y1 (Fill = label background, Line = text colour)
    Level = 3,     // full-width horizontal line at Y1, Text as its right-aligned label
}

public enum LiveDash { Solid = 0, Dash = 1, Dot = 2 }

public sealed class LiveItem
{
    public const double FirstBar = -1e6, FarRight = 1e6;

    public LiveRoot Root;
    public bool Bars;
    public LiveGroup Group;
    public LiveKind Kind;
    public double X1, X2 = 1, Y1, Y2;
    public uint Fill, Line;              // 0xAARRGGBB
    public int Width = 1;
    public LiveDash Dash;
    public string Text = "";
    public int Font = 9;
    public bool Bold, Front;

    public static uint Argb(byte a, byte r, byte g, byte b) => (uint)(a << 24 | r << 16 | g << 8 | b);

    public static uint WithAlpha(uint argb, byte a) => (argb & 0x00FFFFFFu) | (uint)a << 24;

    // The C# statement baked into the dll. Strings are escaped; numbers use the invariant culture.
    public string ToCode() => string.Format(CultureInfo.InvariantCulture,
        "l.Add(new It({0},{1},{2},{3:R},{4:R},{5:R},{6:R},0x{7:X8}u,0x{8:X8}u,{9},{10},{11},{12},{13},{14},{15}));",
        (int)Root, (int)Group, (int)Kind, X1, X2, Y1, Y2, Fill, Line, Width, (int)Dash, Literal(Text), Font,
        Bold ? "true" : "false", Front ? "true" : "false", Bars ? "true" : "false");

    // A full-width shape: from the first bar to well past the last one.
    public LiveItem Wide() { Bars = true; X1 = FirstBar; X2 = FarRight; return this; }

    public static string Literal(string s)
    {
        var b = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c < ' ' || c > '~') b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }
}
