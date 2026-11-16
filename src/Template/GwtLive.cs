using System;
using System.Collections.Generic;
using System.ComponentModel;
using VolSysAPI;
using VolSysAPI.Indicators;
using VolumetricaAPI.Chart;
using VolumetricaControls;
using VolumetricaCore;
using static VolSysAPI.ExternalStructure;

namespace GwtLive;

// GWT Live for Deepchart. Compiled by the GWT Deepchart Bridge on the member's PC with the current GWT levels baked in
// (LiveData, generated). The bridge reloads it on open charts by toggling its eye in the Indicators tab.
// Sandbox: no files, network, threads, decimal constants, records or constant array initialisers (Deepchart rejects them).
public sealed class It
{
    public readonly int Root, Grp, Kind, Width, Dash, Font;
    public readonly double X1, X2, Y1, Y2;
    public readonly uint Fill, Line;
    public readonly string Text;
    public readonly bool Bold, Front, Bars;   // Bars: X in bars from the last bar (+-1e6 = first bar / far right); else a fraction of the width

    public It(int root, int grp, int kind, double x1, double x2, double y1, double y2, uint fill, uint line,
        int width, int dash, string text, int font, bool bold, bool front, bool bars)
    {
        Root = root; Grp = grp; Kind = kind; X1 = x1; X2 = x2; Y1 = y1; Y2 = y2; Fill = fill; Line = line;
        Width = width; Dash = dash; Text = text; Font = font; Bold = bold; Front = front; Bars = bars;
    }
}

public class GwtLive : Indicator
{
    public static IndicatorDescriptionBase Register() => new IndicatorDescriptionBase
    {
        Name = "GWT Live",
        Description = "GWT's live levels (GEX, dark pool, desk, market structure, market auction), kept current by the GWT Deepchart Bridge.",
        Tags = new List<string> { "gwt", "gamma", "gex", "dark pool", "levels" }
    };

    [Category("Groups"), DisplayName("GEX levels")]
    [VolCustom(CategoryIndex = 0, PropertyIndex = 0)]
    public bool ShowGex { get; set; } = true;

    [Category("Groups"), DisplayName("Dark pool levels")]
    [VolCustom(CategoryIndex = 0, PropertyIndex = 1)]
    public bool ShowDarkPool { get; set; } = true;

    [Category("Groups"), DisplayName("Desk levels")]
    [VolCustom(CategoryIndex = 0, PropertyIndex = 2)]
    public bool ShowDesk { get; set; } = true;

    [Category("Groups"), DisplayName("Market structure")]
    [VolCustom(CategoryIndex = 0, PropertyIndex = 3)]
    public bool ShowStructure { get; set; } = true;

    [Category("Groups"), DisplayName("Market auction")]
    [VolCustom(CategoryIndex = 0, PropertyIndex = 4)]
    public bool ShowAuction { get; set; } = true;

    [Category("Groups"), DisplayName("Status chip on chart")]
    [VolCustom(CategoryIndex = 0, PropertyIndex = 5)]
    public bool ShowChip { get; set; } = false;   // the status line shows the same text

    const double LevelLabelX = 0.88;     // right of the GEX pills (0.78) and zone labels (0.80)
    const long StaleAfterMs = 1800000;   // the bridge reloads at least every 15 minutes (heartbeat); older means it is not running

    const int Ahead = 2000;              // full-width shapes run this many bars past the last bar

    IAnnGroup _group;
    IAnnotation _chip;
    long _nextChipMs;
    int _root, _drawnCount, _last;

    public override void OnSet(bool setDefault, bool themeOverride)
    {
        OnEndCall = CallHandler.HistRT;
        Description = "GWT Live";
    }

    public override void OnLoad()
    {
        _group = VAn.CreateAnnGroup();
        _group.Area = ChartAreaRef.Main;
        IndVars.Ann_List.AddGroup(_group);
        _chip = null;
        _nextChipMs = 0;

        _drawnCount = -1;
        _root = RootOf(VAn.SymbolName);
        if (_root < 0)
            StatusMessage = "GWT Live · NQ, MNQ, ES or MES charts only";
    }

    public override void OnEnd(bool isRt)
    {
        var bars = VAn.BarVars;
        if (_root >= 0 && bars != null && bars.Count > 0 && bars.Count != _drawnCount)
            Redraw(bars.Count);   // on every new bar: the right-margin marks are placed from the last bar
        long now = Environment.TickCount64;
        if (now < _nextChipMs)
            return;
        _nextChipMs = now + 5000;
        UpdateChip();
    }

    void Redraw(int count)
    {
        _drawnCount = count;
        _last = count;   // bar i is centred on X = i + 1, so the last bar sits at X = count
        _group.Clear();
        _chip = null;
        var items = new List<It>();
        LiveData.Fill(items);
        foreach (var it in items)
            if (it.Root == _root && Shows(it.Grp))
                Draw(it);
        if (ShowChip)
        {
            _chip = New(AnnotationType.Text, true);
            _chip.CoordinateYType = CoordinateTypeEnum.Relative;
            _chip.X = 0.005;
            _chip.Y = 0.015;   // relative Y runs top-down on Deepchart
            _chip.BackColor = ColorRef.FromArgb(150, 16, 16, 20);
            _chip.ShowLblBackground = true;
            _chip.FontSize = 9;
            _chip.PlotForeground = true;
        }
        UpdateChip();
    }

    double Bx(double x) => x <= -100000 ? 0 : x >= 100000 ? _last + Ahead : _last + x;

    void UpdateChip()
    {
        long age = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds - LiveData.BuiltMs;   // not DateTimeOffset: unproven in the sandbox
        bool stale = age > StaleAfterMs;
        string text = "GWT Live · " + (stale ? "bridge not updating (" + Age(age) + " old)" : LiveData.Status + " · " + Age(age) + " ago");
        StatusMessage = text;
        if (_chip == null)
            return;
        _chip.Text = text;
        _chip.ForeColor = stale ? ColorRef.FromRgb(150, 150, 155) : ColorRef.FromRgb(86, 200, 126);
    }

    static string Age(long ms) => ms < 0 ? "0s" : ms < 120000 ? (ms / 1000) + "s" : ms < 7200000 ? (ms / 60000) + "m" : (ms / 3600000) + "h";

    static int RootOf(string symbol)
    {
        string s = (symbol ?? "").ToUpperInvariant();
        if (s.StartsWith("NQ") || s.StartsWith("MNQ")) return 0;
        if (s.StartsWith("ES") || s.StartsWith("MES")) return 1;
        return -1;
    }

    bool Shows(int grp) => grp switch
    {
        0 => ShowGex,
        1 => ShowDarkPool,
        2 => ShowDesk,
        3 => ShowStructure,
        4 => ShowAuction,
        _ => true,
    };

    void Draw(It it)
    {
        IAnnotation a;
        switch (it.Kind)
        {
            case 0:
                a = New(AnnotationType.Rectangle, false);
                a.X = Bx(it.X1); a.X2 = Bx(it.X2); a.Y = it.Y1; a.Y2 = it.Y2;
                a.BackColor = Color(it.Fill);
                a.LineColor = Color(it.Line);
                break;
            case 1:
                a = New(AnnotationType.Line, false);
                a.X = Bx(it.X1); a.X2 = Bx(it.X2); a.Y = a.Y2 = it.Y1;
                a.LineColor = Color(it.Line);
                break;
            case 2:
                a = New(AnnotationType.Text, !it.Bars);
                a.X = it.Bars ? Bx(it.X1) : it.X1; a.Y = it.Y1;
                a.Text = it.Text;
                a.ForeColor = Color(it.Line);
                a.BackColor = Color(it.Fill);
                a.ShowLblBackground = (it.Fill >> 24) != 0;
                a.FontSize = it.Font;
                a.FontBold = it.Bold;
                break;
            default:
                // a full-width line + its own label near the right edge (a ray's label sits at its start, the left edge)
                a = New(AnnotationType.Line, false);
                a.X = 0; a.X2 = _last + Ahead; a.Y = a.Y2 = it.Y1;
                a.LineColor = Color(it.Line);
                if (it.Text.Length > 0)
                {
                    var t = New(AnnotationType.Text, true);
                    t.X = LevelLabelX; t.Y = it.Y1;
                    t.Text = it.Text;
                    t.ForeColor = Color(it.Line | 0xFF000000u);
                    t.BackColor = Color(it.Fill);
                    t.ShowLblBackground = (it.Fill >> 24) != 0;
                    t.FontSize = it.Font;
                    t.PlotForeground = true;
                }
                break;
        }
        a.LineWidth = it.Width;
        a.LineDashStyle = it.Dash == 1 ? DashStyle.Dash : it.Dash == 2 ? DashStyle.Dot : DashStyle.Solid;
        a.PlotForeground = it.Front;
        if (it.Text.Length > 0)
            a.Tooltip = it.Text;
    }

    IAnnotation New(AnnotationType type, bool relativeX)
    {
        var a = VAn.CreateAnnotation(type);
        a.Area = ChartAreaRef.Main;
        a.CoordinateXType = relativeX ? CoordinateTypeEnum.Relative : CoordinateTypeEnum.Absolute;
        a.CoordinateYType = CoordinateTypeEnum.Absolute;
        a.Visible = true;
        VAn.AddAnnotation(_group, a);
        return a;
    }

    static ColorRef Color(uint argb) => ColorRef.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
