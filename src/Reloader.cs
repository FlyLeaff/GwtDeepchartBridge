using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Gwt;

// Reloads GWT Live on every open Deepchart chart through Windows UI Automation, no mouse: open the chart's Indicators tab
// (top-bar button just left of the settings gear), press GWT Live's eye twice (hide, show), close the tab.
// Deepchart hot-loads a changed dll for new instances only; the eye toggle is what makes an open chart pick it up.
// A row is [delete | name | eye | settings]: only ever the eye, and only when the row looks exactly like that.
public static class Reloader
{
    static readonly Regex Row = new Regex(@"^GWT Live \(\d+\)");          // "GWT Live (2) | Third"; not "GWT Live Test (..."
    static readonly Regex Chart = new Regex(@"^(M?NQ|M?ES)\b", RegexOptions.IgnoreCase);

    // Chart windows (by handle) already looked into: true = has GWT Live. Only GWT Live charts get their tab opened again;
    // a window without it is checked once, then left alone until a rescan (tray "Refresh charts now").
    // Keyed "pid:hwnd" and saved to disk, so a bridge restart does not re-check charts while Deepchart keeps running.
    static readonly string KnownPath = System.IO.Path.Combine(Config.Dir, "known_charts.json");
    static readonly Dictionary<string, bool> Known = LoadKnown();

    static Dictionary<string, bool> LoadKnown()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(System.IO.File.ReadAllText(KnownPath)) ?? new Dictionary<string, bool>(); }
        catch { return new Dictionary<string, bool>(); }
    }

    static void SaveKnown()
    {
        try { System.IO.File.WriteAllText(KnownPath, System.Text.Json.JsonSerializer.Serialize(Known)); }
        catch { }   // only costs a re-check after the next restart
    }

    // Returns how many GWT Live instances were reloaded, and notes for the log.
    public static int ReloadAll(List<string> notes, bool rescan)
    {
        int done = 0;
        var seen = new HashSet<string>();
        foreach (var p in Process.GetProcessesByName("Deepchart"))
        {
            var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, p.Id);
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, cond))
            {
                string title = Safe(() => w.Current.Name) ?? "";
                string key = p.Id + ":" + (Safe(() => w.Current.NativeWindowHandle) ?? 0);
                if (!Chart.IsMatch(title))
                    continue;
                seen.Add(key);
                if (!rescan && Known.TryGetValue(key, out bool has) && !has)
                    continue;
                try
                {
                    int n = ReloadWindow(w, notes);
                    Known[key] = n > 0;
                    done += n;
                }
                catch (Exception ex) { notes.Add(title.Trim() + ": " + ex.Message); }
            }
        }
        foreach (string gone in Known.Keys.Where(k => !seen.Contains(k)).ToList())
            Known.Remove(gone);   // closed windows or a Deepchart restart: those get checked afresh
        SaveKnown();
        return done;
    }

    static int ReloadWindow(AutomationElement w, List<string> notes)
    {
        var tabButton = TabButton(w) ?? throw new InvalidOperationException("Indicators-tab button not found");
        bool opened = false;
        var pop = Popup(w);
        if (pop == null)
        {
            Invoke(tabButton);
            opened = true;
            var sw = Stopwatch.StartNew();
            while (pop == null && sw.ElapsedMilliseconds < 2000)
                pop = Popup(w);
            if (pop == null)
                throw new InvalidOperationException("Indicators tab did not open");
        }
        int n = 0;
        try
        {
            foreach (var eye in Eyes(pop))
            {
                Invoke(eye);   // hide
                Invoke(eye);   // show: back to back works (tested), the tab stays up ~0.4 s
                n++;
            }
        }
        finally
        {
            if (opened && !Close(w, pop))
                notes.Add((Safe(() => w.Current.Name) ?? "chart").Trim() + ": Indicators tab did not close");
        }
        return n;
    }

    // The tab button only opens the tab (tested: a second press leaves it open); a mouse click closes it because the popup
    // loses the mouse. WM_CANCELMODE to the popup's own window does the same without touching mouse or keyboard (tested,
    // and the tab reopens normally afterwards).
    static bool Close(AutomationElement w, AutomationElement pop)
    {
        var hwnd = new IntPtr(Safe(() => pop.Current.NativeWindowHandle) ?? 0);
        if (hwnd == IntPtr.Zero)
            return false;
        SendMessage(hwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500)
        {
            if (!IsWindowVisible(hwnd))
                return true;
            Thread.Sleep(50);
        }
        return false;
    }

    // The open Indicators tab of this chart: a visible top-level popup window of Deepchart's process over the chart, holding
    // the text "Indicators". Found through Windows, not the chart's automation subtree: a long-running client's view of that
    // subtree did not pick up the newly opened popup (tested), while AutomationElement.FromHandle is always current.
    static AutomationElement Popup(AutomationElement w)
    {
        int pid = w.Current.ProcessId;
        var chart = new IntPtr(w.Current.NativeWindowHandle);
        var area = w.Current.BoundingRectangle;
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (h != chart && IsWindowVisible(h) && GetWindowThreadProcessId(h, out uint p) != 0 && p == pid
                && GetWindowRect(h, out RECT r) && area.Contains(r.Left + 1, r.Top + 1))
                found.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (var h in found)
        {
            var e = AutomationElement.FromHandle(h);
            if (e.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Indicators")) != null)
                return e;
        }
        return null;
    }

    const uint WM_CANCELMODE = 0x001F;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    // The unnamed top-bar button immediately left of the settings gear (btnSettings).
    static AutomationElement TabButton(AutomationElement w)
    {
        double top = w.Current.BoundingRectangle.Y;
        var all = w.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .Where(e => Safe(() => e.Current.BoundingRectangle.Y - top < 30 && e.Current.BoundingRectangle.Width == 16) == true).ToList();
        var gear = all.FirstOrDefault(e => e.Current.AutomationId == "btnSettings");
        if (gear == null)
            return null;
        double gx = gear.Current.BoundingRectangle.X;
        return all.Where(e => e.Current.ControlType == ControlType.Button && e.Current.AutomationId == "" && e.Current.BoundingRectangle.X < gx)
            .OrderBy(e => e.Current.BoundingRectangle.X).LastOrDefault();
    }


    // The eye of each GWT Live row; throws (pressing nothing) if any such row is not [delete | name | eye | settings].
    static List<AutomationElement> Eyes(AutomationElement pop)
    {
        var all = pop.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToList();
        var eyes = new List<AutomationElement>();
        foreach (var txt in all.Where(e => e.Current.ControlType == ControlType.Text && Row.IsMatch(e.Current.Name ?? "")))
        {
            var r = txt.Current.BoundingRectangle;
            var row = all.Where(e => e.Current.ControlType == ControlType.Button && Math.Abs(e.Current.BoundingRectangle.Y - r.Y) < 10)
                .OrderBy(e => e.Current.BoundingRectangle.X).ToList();
            var left = row.Where(e => e.Current.BoundingRectangle.X < r.X).ToList();
            var right = row.Where(e => e.Current.BoundingRectangle.X > r.X).ToList();
            if (row.Count != 3 || left.Count != 1 || right.Count != 2)
                throw new InvalidOperationException("unexpected row layout for '" + txt.Current.Name + "', nothing pressed");
            eyes.Add(right[0]);
        }
        return eyes;
    }

    static void Invoke(AutomationElement e) => ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    static T? Safe<T>(Func<T> f) where T : struct { try { return f(); } catch { return null; } }

    static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
}
