using System.Reflection;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;
using Galah2.ViewModels;
using Galah2.Views;

// Windows-only product under test (its whole assembly is [SupportedOSPlatform(windows)]).
[assembly: SupportedOSPlatform("windows")]

namespace InteractionProbe;
/// <summary>
/// Wheel and number-key routing against the REAL PanelWindow - not the view model.
///
/// The audit round 3 P0s (tile list could not scroll at all; the two switch-tab settings were
/// never read) both lived in the View layer, where every existing probe is blind: they drive
/// view models directly. This probe raises real routed wheel events on the real template's
/// ScrollContentPresenter, so it exercises the exact handler chain a user's wheel goes through:
/// presenter class handler (scroll) -> ScrollViewer instance handler (edge -> tab switch).
///
/// The event construction uses reflection because Avalonia 11 offers no public ctor for
/// PointerWheelEventArgs with a real IPointer; this is the same technique the audit round-3
/// instrument used, kept here as regression armour.
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
    }

    [STAThread]
    public static int Main()
    {
        AppBuilder.Configure<Galah2.App>().UsePlatformDetect().SetupWithoutStarting();
        try { return RunChecks(); }
        catch (Exception ex)
        {
            for (var e = (Exception)ex; e != null; e = e.InnerException) Console.WriteLine("EX " + e.GetType().Name + ": " + e.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int RunChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "ixn-" + Guid.NewGuid().ToString("N")[..6]);
        var paths = new AppDataPaths(root).EnsureCreated();
        IconService.Initialize(paths);

        // A catalog with three tabs and enough items in the first one to overflow the viewport.
        // 300 tiles: at ~8 per row that is ~38 rows, far past the ~9-row viewport - the no-scroll
        // edge cases (extent == viewport) made edge tests vacuous in an earlier run.
        var catalog = new CatalogStore(paths);
        catalog.Load();
        for (var t = 0; t < 3; t++)
        {
            var tab = catalog.AddTab("分类" + (t + 1));
            catalog.AddItems(tab.Id, Enumerable.Range(0, 300).Select(i => new CatalogItem
            {
                Id = "i" + t + "-" + i,
                Name = "项目" + i,
                Kind = ItemKinds.App,
                Target = "C:/nonexistent/probe-" + i + ".exe",
            }));
        }

        var settings = new SettingsDocument();
        var vm = new PanelViewModel(catalog, new NullHost(), new ToastService());
        vm.ApplySettings(settings);
        // CatalogDocument.CreateEmpty() puts a "默认" tab at position 0, so the list of tabs is
        // [默认, 分类1, 分类2, 分类3]. Select by NAME, never by a hand-assumed index.
        var tab1 = vm.Tabs.First(t => t.Name == "分类1");
        var tab2 = vm.Tabs.First(t => t.Name == "分类2");
        var tab3 = vm.Tabs.First(t => t.Name == "分类3");
        vm.SelectTab(tab1);
        var window = new PanelWindow { DataContext = vm };
        window.Show();
        Pump();
        vm.ApplyIconSize(settings.IconSize);   // re-project tiles at the configured size
        Pump();

        // The real scroll machinery lives in the presenter inside TileScroll's template.
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.Name == "TileScroll");
        var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();

        Pump();
        Check("tile list overflows (scrolling is possible)",
            scroll.Extent.Height > scroll.Viewport.Height,
            "extent=" + (int)scroll.Extent.Height + " viewport=" + (int)scroll.Viewport.Height);

        var pointer = MakePointer();

        // ---- 1. the P0 fix itself: the wheel must scroll the list again ----
        var before = scroll.Offset.Y;
        RaiseWheel(pointer, presenter, down: true);
        Pump();
        Check("wheel scrolls the tile list", scroll.Offset.Y > before + 0.01,
            "offset " + (int)before + " -> " + (int)scroll.Offset.Y);
        Check("no tab switch while the list can still scroll",
            vm.SelectedTab!.Name == "分类1", vm.SelectedTab.Name);

        // ---- 2. edge semantics: wheel down at the bottom switches to the next category ----
        scroll.Offset = new Vector(0, scroll.Extent.Height);   // jump to the bottom
        Pump();
        var tabBefore = vm.SelectedTab!;
        RaiseWheel(pointer, presenter, down: true);
        Pump();
        Check("wheel down at the bottom switches category",
            ReferenceEquals(vm.SelectedTab, tab2), tabBefore.Name + " -> " + vm.SelectedTab!.Name);

        // The new category starts at the top: wheel UP switches back (wraparound behaviour).
        // Reset the offset first: the ScrollViewer keeps its scroll position across tab
        // switches (same template), so "at the top" must be made true, not assumed.
        scroll.Offset = new Vector(0, 0);
        Pump();
        RaiseWheel(pointer, presenter, down: false);
        Pump();
        Check("wheel up at the top switches back",
            ReferenceEquals(vm.SelectedTab, tab1), "now " + vm.SelectedTab!.Name);

        // ---- 3. WheelSwitchTab=false: the wheel scrolls, never switches ----
        settings.WheelSwitchTab = false;
        vm.ApplySettings(settings);
        scroll.Offset = new Vector(0, scroll.Extent.Height);   // at the bottom edge again
        Pump();
        RaiseWheel(pointer, presenter, down: true);
        Pump();
        Check("WheelSwitchTab=false: no switch even at an edge",
            ReferenceEquals(vm.SelectedTab, tab1), "stayed " + vm.SelectedTab!.Name);
        settings.WheelSwitchTab = true;
        vm.ApplySettings(settings);

        // ---- 4. NumberKeysSwitchTab: both digit rows, and the off switch ----
        // Semantics (1.x parity): the digit addresses the Nth category in the strip, so Ctrl+3
        // means vm.Tabs[2] - which here is 分类2, because the strip starts with 默认.
        vm.SelectTab(tab1);
        Pump();
        RaiseKey(window, Key.D3, KeyModifiers.Control);
        Pump();
        Check("Ctrl+D3 selects the third category", ReferenceEquals(vm.SelectedTab, tab2),
            vm.SelectedTab!.Name);

        RaiseKey(window, Key.NumPad1, KeyModifiers.Control);
        Pump();
        Check("Ctrl+NumPad1 selects the first category", ReferenceEquals(vm.SelectedTab, vm.Tabs[0]),
            vm.SelectedTab!.Name);

        RaiseKey(window, Key.D0, KeyModifiers.Control);
        Pump();
        Check("Ctrl+D0 selects nothing", ReferenceEquals(vm.SelectedTab, vm.Tabs[0]),
            "stayed " + vm.SelectedTab!.Name);

        settings.NumberKeysSwitchTab = false;
        vm.ApplySettings(settings);
        RaiseKey(window, Key.D2, KeyModifiers.Control);
        Pump();
        Check("NumberKeysSwitchTab=false: Ctrl+D2 does nothing",
            ReferenceEquals(vm.SelectedTab, vm.Tabs[0]), "stayed " + vm.SelectedTab!.Name);
        settings.NumberKeysSwitchTab = true;
        vm.ApplySettings(settings);

        // ---- 5. wraparound through TabNavigation.StepIndex: last -> first -> last ----
        vm.SelectTab(tab3);
        Pump();
        RaiseKey(window, Key.Tab, KeyModifiers.Control);
        Pump();
        Check("Ctrl+Tab wraps last -> first", ReferenceEquals(vm.SelectedTab, vm.Tabs[0]),
            vm.SelectedTab!.Name);
        RaiseKey(window, Key.Tab, KeyModifiers.Control | KeyModifiers.Shift);
        Pump();
        Check("Ctrl+Shift+Tab wraps first -> last", ReferenceEquals(vm.SelectedTab, tab3),
            vm.SelectedTab!.Name);

        // ---- 6. the drag-start routing: Button swallows the BUBBLE press, the TUNNEL sees it ----
        //
        // This is the root cause of the 2026-11-23 "drag reorder does nothing" report, locked in
        // as a permanent regression. A Button's class handler marks PointerPressed handled in the
        // BUBBLE phase (Button.cs: e.Handled = true), and the route only calls an instance
        // handler when the event is unhandled (EventRoute.cs). So the old `PointerPressed="..."`
        // on a tile/chip never ran - the drag origin stayed null and the drag never started.
        // The fix captures the press in a WINDOW-LEVEL TUNNEL handler, which runs before the
        // class handler. Raising a real press on a real tile proves both halves at once.
        var pressPointer = MakePointer();
        var tileBtn = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("tile") && b.IsVisible && !b.Classes.Contains("addtile"));
        var tileLabel = tileBtn.GetVisualDescendants().OfType<TextBlock>().First();

        bool bubbleOnButton = false, tunnelOnButton = false, tunnelOnWindow = false;
        tileBtn.AddHandler(InputElement.PointerPressedEvent,
            (object? s, PointerPressedEventArgs a) => bubbleOnButton = true,
            RoutingStrategies.Bubble);
        tileBtn.AddHandler(InputElement.PointerPressedEvent,
            (object? s, PointerPressedEventArgs a) => tunnelOnButton = true,
            RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerPressedEvent,
            (object? s, PointerPressedEventArgs a) => tunnelOnWindow = true,
            RoutingStrategies.Tunnel);

        // Raise the press from the label - the element a real hit would actually return - so the
        // route is label -> tile Button -> ... -> window, exactly like a physical click.
        RaisePress(pressPointer, tileLabel, window);
        Pump();
        Check("bubble press on a Button is swallowed (the bug)", !bubbleOnButton);
        Check("tunnel press on the Button is seen (the fix's mechanism)", tunnelOnButton);
        Check("window tunnel press is seen (where the fix captures it)", tunnelOnWindow);

        // The pure helpers the fix relies on: walking up from the hit element to the draggable
        // button, and the 8 px threshold that separates a click from a drag.
        Check("DragButtonFrom walks up from the label to the tile button",
            ReferenceEquals(PanelWindow.DragButtonFrom(tileLabel), tileBtn));
        Check("DragButtonFrom returns null for a non-draggable element",
            PanelWindow.DragButtonFrom(window) is null);
        Check("a 7 px move is not a drag", !PanelWindow.PassedThreshold(new Point(0, 0), new Point(4, 3)));
        Check("an 8 px move is a drag", PanelWindow.PassedThreshold(new Point(0, 0), new Point(4, 4)));

        window.Close();
        try { Directory.Delete(root, true); } catch { /* best effort */ }
        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : "=== " + _fail + " FAILED ===");
        return _fail;
    }

    /// <summary>Raise a real left-button PointerPressed on a target, routed up to the window.</summary>
    private static void RaisePress(IPointer pointer, Control target, Visual root)
    {
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton,
            PointerUpdateKind.LeftButtonPressed);
        var e = new PointerPressedEventArgs(target, pointer, root, new Point(100, 100),
            0UL, props, KeyModifiers.None, 1);
        ((Interactive)target).RaiseEvent(e);
    }

    private static void Pump()
    {
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    /// <summary>Scripted shell: the panel under test never needs a real dialog here.</summary>
    private sealed class NullHost : IPanelHost
    {
        public void HidePanel() { }
        public void ShowAddPicker(string tabId) { }
        public CatalogItem? ShowEditItem(string tabId, CatalogItem? existing) => null;
        public void Prompt(string title, string initial, Action<string?> onAnswer) => onAnswer(null);
        public void Confirm(string title, string message, Action<bool> onAnswer) => onAnswer(false);
        public void ShowMaintenance() { }
        public void ShowSettings() { }
        public bool Launch(CatalogItem item) => true;
        public Task<string?> PickFile(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFolder(string title) => Task.FromResult<string?>(null);
    }

    /// <summary>Raise a wheel event on the presenter, as the win32 input layer would.</summary>
    private static void RaiseWheel(IPointer pointer, Visual source, bool down)
    {
        var delta = new Vector(0, down ? -3 : 3);
        var args = MakeWheelArgs(pointer, source, delta);
        ((Interactive)source).RaiseEvent(args);
    }

    private static void RaiseKey(Window window, Key key, KeyModifiers modifiers)
    {
        // RaiseEvent on KeyDownEvent with a constructed KeyEventArgs: same routed-event path
        // the real keyboard takes (the window's KeyDown handlers run). The properties are
        // init-only in Avalonia 11, hence the object initializer.
        var e = new KeyEventArgs
        {
            Key = key,
            KeyModifiers = modifiers,
            RoutedEvent = InputElement.KeyDownEvent,
        };
        e.Source = window;
        window.RaiseEvent(e);
    }

    /// <summary>Reflection-built PointerWheelEventArgs with a real IPointer.</summary>
    private static PointerWheelEventArgs MakeWheelArgs(IPointer pointer, Visual source, Vector delta)
    {
        var ctor = typeof(PointerWheelEventArgs)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var t = ps[i].ParameterType;
            if (t == typeof(IPointer)) args[i] = pointer;
            else if (t == typeof(Visual) || t == typeof(IInputElement) || t == typeof(Interactive))
                args[i] = source;
            else if (t == typeof(Point)) args[i] = new Point(100, 100);
            else if (t == typeof(ulong)) args[i] = 42UL;
            else if (t == typeof(Vector)) args[i] = delta;
            else if (t == typeof(PointerPointProperties)) args[i] = PointerPointProperties.None;
            else if (t == typeof(KeyModifiers)) args[i] = KeyModifiers.None;
            else if (t.IsEnum) args[i] = Enum.GetValues(t).GetValue(0);
            else if (!t.IsValueType) args[i] = null;
            else args[i] = Activator.CreateInstance(t);
        }
        var e = (PointerWheelEventArgs)ctor.Invoke(args);
        e.Source = source;
        return e;
    }

    /// <summary>
    /// Construct a real Avalonia Pointer via reflection. Its only ctor is internal, and it needs
    /// the InputManager instance; the audit round-3 instrument proved this shape works.
    /// </summary>
    private static IPointer MakePointer()
    {
        var ctor = typeof(Avalonia.Input.Pointer).GetConstructors(BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic).First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        var asm = typeof(Avalonia.Input.Pointer).Assembly;
        for (var i = 0; i < ps.Length; i++)
        {
            var t = ps[i].ParameterType;
            if (t == typeof(IntPtr)) args[i] = new IntPtr(1);
            else if (t == typeof(ulong)) args[i] = 1UL;
            else if (t == typeof(int)) args[i] = 1;
            else if (t == typeof(bool)) args[i] = true;
            else if (t.IsEnum) args[i] = Enum.GetValues(t).GetValue(0);
            else if (t.Name == "InputManager")
                args[i] = asm.GetType("Avalonia.Input.InputManager")!
                    .GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetValue(null);
            else if (!t.IsValueType) args[i] = null;
            else args[i] = Activator.CreateInstance(t);
        }
        return (IPointer)ctor.Invoke(args);
    }
}
