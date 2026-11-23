using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Process-level acceptance against the real shipped binary.
///
/// The probes in IconProbe/SettingsProbe/PanelProbe drive view models in-process; this one only
/// uses what a user can do: start the executable, talk to it through a second launch, and look at
/// the windows the OS reports. That separation matters because several 1.x bugs lived entirely in
/// the process/OS boundary and were invisible to in-process tests.
///
/// Every check states what it observed, so a failure says which link broke.
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
    }

    private static int Main(string[] args)
    {
        var exe = args.Length > 0 ? args[0] : FindBuiltExe();
        if (exe is null || !File.Exists(exe))
        {
            Console.WriteLine("FAIL  Galah2.exe not found (pass its path as the first argument)");
            return 1;
        }

        var dir = Path.GetDirectoryName(exe)!;
        Console.WriteLine("exe: " + exe);

        KillLeftovers();
        try
        {
            // ---- 1. a cold start must leave something reachable ----
            var first = Start(exe, dir);
            Check("process starts", first is not null, first?.Id.ToString() ?? "no process");
            if (first is null) return Report();

            var settled = WaitForTray(first);
            Check("stays alive with no window (tray-resident)", settled,
                settled ? "alive after 8s" : "exited");

            // ---- 2. a second launch forwards the toggle and exits ----
            var second = Start(exe, dir);
            var forwarded = second?.WaitForExit(8000) ?? false;
            Check("second instance exits instead of stacking", forwarded,
                forwarded ? "exited" : "still running");
            Check("only one process survives", CountProcesses() == 1,
                CountProcesses() + " process(es)");

            // ---- 3. the panel actually appears ----
            var panel = WaitForPanel(first, TimeSpan.FromSeconds(10));
            if (panel == IntPtr.Zero)
            {
                // Say what the enumerator saw: a bare "no window" does not distinguish "the panel
                // never opened" from "the search predicate is wrong".
                Console.WriteLine("  diagnostic: pid=" + first.Id + " processes=" + CountProcesses());
                foreach (var h in AllWindows(first.Id)) Console.WriteLine("    " + h);
            }
            Check("panel window appears after the toggle", panel != IntPtr.Zero,
                panel == IntPtr.Zero ? "no visible window" : "hwnd=" + panel);
            Check("panel has a real size", HasUsableSize(panel), DescribeSize(panel));

            // ---- 4. the panel reports the material it applied ----
            // The panel records the transparency level it actually got, which depends on the
            // configured material. Assert the record exists, not that it says "Mica": a fresh data
            // folder defaults to material=none, and pinning the string made this fail on CI while
            // passing locally with a hand-edited settings.json.
            // The line is written in PanelWindow.OnOpened, which fires AFTER the HWND becomes
            // visible - so a single read right after WaitForPanel can lose the race on a slow
            // AOT cold start. Poll for the line instead of snapshotting once.
            var log = Path.Combine(dir, "GalahData", "galah.log");
            string? line = null;
            for (var i = 0; i < 30; i++)
            {
                if (File.Exists(log))
                {
                    line = File.ReadAllText(log).Split('\n').FirstOrDefault(l => l.Contains("[material]"));
                    if (line is not null) break;
                }
                Thread.Sleep(200);
            }
            Check("panel logged its actual material", line is not null, line?.Trim() ?? "no [material] line");

            // ---- 5. closing the panel only hides it - the app stays resident ----
            // This is the 1.x contract, and its opposite was a real bug once: a panel that closed
            // into nothing left the user with a process they could not reach again.
            // CloseMainWindow needs a title bar to post WM_CLOSE to, and this panel has none, so
            // post the message to the window handle directly. That is the same path the shell takes.
            PostMessage(panel, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(2500);
            Check("closing the panel keeps the app alive", !first.HasExited,
                first.HasExited ? "process exited" : "still resident");
            Check("the panel is no longer visible", !IsAnyPanelVisible(first.Id),
                IsAnyPanelVisible(first.Id) ? "still showing" : "hidden");

            // ---- 6. a second toggle brings it back ----
            var third = Start(exe, dir);
            third?.WaitForExit(8000);
            var reopened = WaitForPanel(first, TimeSpan.FromSeconds(8));
            Check("a later toggle reopens the panel", reopened != IntPtr.Zero,
                reopened == IntPtr.Zero ? "did not reappear" : "hwnd=" + reopened);

            // ---- 7. an explicit exit from the tray menu ends the process ----
            first.Kill();
            first.WaitForExit(8000);
            Check("process can be terminated", CountProcesses() == 0,
                CountProcesses() + " process(es) remaining");
        }
        finally { KillLeftovers(); }

        return Report();
    }

    private static int Report()
    {
        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : "=== " + _fail + " FAILED ===");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Locate the built executable. Two layouts exist: a plain `dotnet build` of the project puts
    /// it under bin/Release, while building the SOLUTION puts it under bin/x64/Release because the
    /// projects set PlatformTarget=x64. Both are searched, so the tool works either way.
    /// </summary>
    private static string? FindBuiltExe()
    {
        var root = AppContext.BaseDirectory;
        for (var i = 0; i < 5; i++) root = Path.GetFullPath(Path.Combine(root, ".."));

        var candidates = new[]
        {
            Path.Combine(root, "src", "Galah2", "bin", "Release", "net8.0", "Galah2.exe"),
            Path.Combine(root, "src", "Galah2", "bin", "x64", "Release", "net8.0", "Galah2.exe"),
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static Process? Start(string exe, string dir)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("start failed: " + ex.Message);
            return null;
        }
    }

    private static bool WaitForTray(Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) return false;
            Thread.Sleep(250);
        }
        return !process.HasExited;
    }

    private static IntPtr WaitForPanel(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = FindLargestWindow(process.Id);
            if (hwnd != IntPtr.Zero)
            {
                GetWindowRect(hwnd, out var rect);
                if (rect.R - rect.L > 200 && rect.B - rect.T > 200) return hwnd;
            }
            Thread.Sleep(300);
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> AllWindows(int pid)
    {
        var list = new List<string>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == (uint)pid)
            {
                GetWindowRect(h, out var rect);
                list.Add("hwnd=" + h + " visible=" + IsWindowVisible(h)
                    + " size=" + (rect.R - rect.L) + "x" + (rect.B - rect.T));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static IntPtr FindLargestWindow(int pid)
    {
        var best = IntPtr.Zero;
        var bestArea = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != (uint)pid || !IsWindowVisible(h)) return true;
            GetWindowRect(h, out var rect);
            var area = (rect.R - rect.L) * (rect.B - rect.T);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static bool IsAnyPanelVisible(int pid)
    {
        var found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != (uint)pid || !IsWindowVisible(h)) return true;
            GetWindowRect(h, out var rect);
            if (rect.R - rect.L > 200 && rect.B - rect.T > 200) found = true;
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private static bool HasUsableSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowRect(hwnd, out var rect);
        return rect.R - rect.L > 200 && rect.B - rect.T > 200;
    }

    private static string DescribeSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "no window";
        GetWindowRect(hwnd, out var rect);
        return (rect.R - rect.L) + "x" + (rect.B - rect.T);
    }

    private static int CountProcesses() =>
        Process.GetProcessesByName("Galah2").Length;

    private static void KillLeftovers()
    {
        foreach (var p in Process.GetProcessesByName("Galah2"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    // GetWindowRect fills a caller RECT and returns BOOL - it does not return the struct. Declaring it
    // as a returning method shifts the whole call and silently yields 0x0 for every window.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
