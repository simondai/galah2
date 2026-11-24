using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.ViewModels;
using Galah2.Views;

/// <summary>
/// Answers the one question a screenshot cannot: does the prompt actually hand its result back,
/// and does it do so without blocking the UI thread?
///
/// The original bug was ShowDialog().GetAwaiter().GetResult() on the UI thread. It did not throw -
/// it deadlocked, so the window painted and then ignored every keystroke. A test that only checks
/// "the window appeared" passes on the broken code, which is why this drives the callback instead.
/// </summary>
namespace DialogProbe;

internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
    }

    private static IClassicDesktopStyleApplicationLifetime _lifetime = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        // Guard first: this probe configures the PRODUCT App, and the product single-instance-exits
        // (code 0, no output) when a Galah2 is already running. Without this check a user's live
        // test instance would make the whole probe vanish with zero lines and a passing exit code.
        var running = System.Diagnostics.Process.GetProcessesByName("Galah2");
        if (running.Length > 0)
        {
            Console.WriteLine("FAIL  no Galah2 instance may be running while this probe configures the product App  [found: "
                + string.Join(",", running.Select(p => p.Id)) + "]");
            foreach (var p in running) p.Dispose();
            return 1;
        }
        foreach (var p in running) p.Dispose();
        running = null!;

        // Use the product Application so Tokens.axaml is loaded exactly as it is in the real
        // app - the surface assertions only mean something against the real token dictionary.
        // A desktop lifetime is registered (never started) so shown windows are tracked.
        _lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        AppBuilder.Configure<Galah2.App>().UsePlatformDetect().SetupWithLifetime(_lifetime);
        var code = 0;
        try { RunChecks(); code = Report(); }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED " + ex.GetType().Name + ": " + ex.Message);
            code = 1;
        }
        return code;
    }

    public static void RunChecks()
    {
        // ---- a prompt answers through its callback, and does not block ----
        string? answer = null;
        var answered = false;
        var returnedImmediately = true;

        PromptWindow.Ask(null, "测试", "初始值", text =>
        {
            answer = text;
            answered = true;
        });

        // If Ask had blocked, this line would never be reached on the UI thread.
        returnedImmediately = !answered;
        Check("PromptWindow.Ask returns without blocking", returnedImmediately,
            answered ? "callback fired synchronously" : "returned at once");

        // Drive it the way a user would: find the open prompt and press OK.
        var prompt = FindWindow<PromptWindow>();
        Check("prompt window is open", prompt is not null);

        // ---- borderless surface, the panel's grammar (full alignment, 2026-11-22) ----
        // The dialog is now chrome-less like the panel: transparent window, a rounded
        // RootBorder carrying the opaque panel fill. Look the token up through the window's
        // resource chain (the app-level ResourceDictionary indexer does not expose merged
        // tokens in 11.3) and compare by identity: DynamicResource hands out the
        // dictionary's own brush instance, so a different object means wrong token wiring.
        object? surfaceResource = null;
        var surface = (prompt != null && prompt.TryFindResource("DialogSurfaceBrush", out surfaceResource))
            ? surfaceResource as Avalonia.Media.IBrush
            : null;
        var rootBorder = prompt?.FindControl<Border>("RootBorder");
        Check("dialog window is borderless", prompt?.SystemDecorations == SystemDecorations.None,
            prompt is null ? "no window" : prompt.SystemDecorations.ToString());
        Check("dialog surface is the panel fill on the RootBorder",
            surface is not null && rootBorder is not null && ReferenceEquals(rootBorder.Background, surface),
            rootBorder is null ? "no RootBorder"
                : "bg=" + rootBorder.Background + " token=" + (surface?.ToString() ?? "MISS"));
        if (prompt is not null)
        {
            var input = prompt.FindControl<TextBox>("Input");
            Check("input field exists and is enabled", input is not null && input.IsEnabled);
            Check("input is prefilled", input?.Text == "初始值", input?.Text ?? "null");

            // Simulate typing then confirming.
            if (input is not null) input.Text = "改名后的值";
            PressOk(prompt);
        }

        Pump();
        Check("confirming returns the typed value", answer == "改名后的值", answer ?? "null");

        // ---- cancelling reports null, so the caller is never left waiting ----
        var cancelled = false;
        string? cancelAnswer = "sentinel";
        PromptWindow.Ask(null, "测试2", "x", text => { cancelAnswer = text; cancelled = true; });
        Pump();

        var prompt2 = FindWindow<PromptWindow>();
        if (prompt2 is not null) PressCancel(prompt2);
        Pump();
        Check("cancelling reports null", cancelled && cancelAnswer is null, cancelAnswer ?? "null");

        // ---- dismissal via OnClosed also settles, or the caller waits forever ----
        var closedSettled = false;
        ConfirmWindow.Ask(null, "测试3", "确定吗", ok => { closedSettled = true; _ = ok; });
        Pump();

        var confirm = FindWindow<ConfirmWindow>();
        if (confirm is not null) confirm.Close();
        Pump();
        Check("dismissing a confirm still settles", closedSettled);

        // ---- confirm returns true for OK ----
        bool? confirmResult = null;
        ConfirmWindow.Ask(null, "测试4", "确定吗", ok => confirmResult = ok);
        Pump();

        var confirm2 = FindWindow<ConfirmWindow>();
        if (confirm2 is not null) PressConfirmOk(confirm2);
        Pump();
        Check("confirming a confirm returns true", confirmResult == true, confirmResult?.ToString() ?? "null");

        // ---- the chrome close button (the system title bar is gone) settles as "no" ----
        // With no OS title bar, the only dismissal left besides the dialog's own buttons is
        // the ✕ in the custom chrome. It must settle the callback, or the caller waits forever.
        bool? chromeCloseResult = null;
        ConfirmWindow.Ask(null, "测试5", "标题栏关闭", ok => chromeCloseResult = ok);
        Pump();

        var confirm3 = FindWindow<ConfirmWindow>();
        var chromeClose = confirm3?.GetVisualDescendantsOfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "✕");
        Check("chrome carries a close button", chromeClose is not null);
        if (chromeClose is not null)
            chromeClose.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check("chrome close settles as dismiss", chromeCloseResult == false,
            chromeCloseResult?.ToString() ?? "no callback");

        // ---- RandPass generator window (spec 2026-11-24-randpass) ----
        // Structure + generation + the single-commit-on-close rule. A real double-tap copy needs
        // synthetic pointer input (unavailable in-process), so the copy path is asserted at the
        // MarkCopied contract level, same class of limitation as the earlier menu acceptances.
        var rpRoot = Path.Combine(Path.GetTempPath(), "randpassprobe-" + Guid.NewGuid().ToString("N")[..8]);
        var rpStore = new SettingsStore(new AppDataPaths(rpRoot).EnsureCreated());
        var rpVm = new RandPassViewModel(rpStore);
        var rp = new RandPassWindow { DataContext = rpVm };
        rp.Show();
        Pump();

        Check("RandPass window is open", FindWindow<RandPassWindow>() is not null);
        Check("RandPass window is borderless", rp.SystemDecorations == SystemDecorations.None);
        var rpSurfaceOk = rp.TryFindResource("DialogSurfaceBrush", out var rpRes);
        var rpRootBorder = rp.FindControl<Border>("RootBorder");
        var rpSurface = rpRes as Avalonia.Media.IBrush;
        Check("RandPass surface is the dialog fill on RootBorder",
            rpSurfaceOk && rpSurface is not null && rpRootBorder is not null
            && ReferenceEquals(rpRootBorder.Background, rpSurface));

        // Layout v2 (spec §7): the two NumericUpDowns are gone; length/count are −/value/+ rows.
        var rpSpinners = rp.GetVisualDescendantsOfType<NumericUpDown>().ToList();
        Check("RandPass carries no NumericUpDown", rpSpinners.Count == 0, rpSpinners.Count + "");
        // The steppers are the replacement: two rows of [− value ＋], so exactly four chip
        // buttons carrying the minus/plus glyphs, all visible after layout.
        var rpMinus = rp.GetVisualDescendantsOfType<Button>()
            .Where(b => (b.Content as string) == "−").ToList();
        var rpPlus = rp.GetVisualDescendantsOfType<Button>()
            .Where(b => (b.Content as string) == "＋").ToList();
        Check("RandPass has the four stepper buttons (−/＋ ×2), all visible",
            rpMinus.Count == 2 && rpPlus.Count == 2
            && rpMinus.All(b => b.IsVisible) && rpPlus.All(b => b.IsVisible),
            "minus=" + rpMinus.Count + " plus=" + rpPlus.Count);
        // Stepper round-trip through the real XAML wiring: the first ＋ in visual order must be
        // the length row's button (binding identity). A synthetic pointer click is not available
        // in-process, so the button's resolved Command is executed directly — the binding and the
        // command are the two product-owned links; the Button's own click plumbing is framework.
        if (rpPlus.Count == 2)
        {
            // Visual order is [length row, count row]; every button must be bound to its own
            // command, or a row swap would silently drive the wrong parameter.
            Check("stepper bindings map to the right commands",
                ReferenceEquals(rpMinus[0].Command, rpVm.DecLengthCommand)
                && ReferenceEquals(rpMinus[1].Command, rpVm.DecCountCommand)
                && ReferenceEquals(rpPlus[0].Command, rpVm.IncLengthCommand)
                && ReferenceEquals(rpPlus[1].Command, rpVm.IncCountCommand));
            rpPlus[0].Command?.Execute(rpPlus[0].CommandParameter);
            Pump();
            Check("length ＋ steps the property (8→9)", rpVm.Length == 9, rpVm.Length + "");
            // The TextBox is the other half of the same binding: it shows the stepped value, and
            // an edited value reaches the property (un-clamped in transit — CurrentOptions/Commit
            // clamp, the setter must not, so typing "64" is not crushed to "4" mid-keystroke).
            var rpLenBox = rp.GetVisualDescendantsOfType<TextBox>().FirstOrDefault();
            Check("length TextBox shows the stepped value", rpLenBox?.Text == "9", rpLenBox?.Text ?? "no TextBox");
            // −/+ must clamp to the documented range (4–64 / 1–100), not wrap or overshoot.
            rpVm.Length = PasswordOptions.MaxLength;
            rpPlus[0].Command?.Execute(null);
            Check("length ＋ clamps at 64", rpVm.Length == 64, rpVm.Length + "");
            rpVm.Count = PasswordOptions.MinCount;
            rpMinus[1].Command?.Execute(null);
            Check("count − clamps at 1", rpVm.Count == 1, rpVm.Count + "");

            if (rpLenBox is not null)
            {
                rpLenBox.Text = "24";
                Pump();
                Check("length TextBox writes back to the property", rpVm.Length == 24, rpVm.Length + "");
                rpVm.Generate();
                Pump();
                Check("generation honors the edited length", rpVm.Results.All(r => r.Length == 24),
                    rpVm.Results.Count > 0 ? rpVm.Results[0].Length + " chars" : "empty");
                // Restore the shipped defaults before the defaults check below (the length
                // through the box — one more round-trip; the count went to 1 via the clamp test).
                rpLenBox.Text = "8";
                rpVm.Count = 10;
                Pump();
            }
            else
            {
                rpVm.Length = 8;
                rpVm.Count = 10;
            }
        }
        var rpBoxes = rp.GetVisualDescendantsOfType<CheckBox>().ToList();
        // Seven: lower/upper/digits/double-digit/no-confusables/custom-chars/custom-format.
        Check("RandPass has the seven parameter checkboxes", rpBoxes.Count == 7, rpBoxes.Count + "");
        var rpCombos = rp.GetVisualDescendantsOfType<ComboBox>().ToList();
        Check("RandPass format combo offers exactly the two presets",
            rpCombos.Count == 1 && rpCombos[0].Items.Count == 2,
            rpCombos.Count + "/" + (rpCombos.Count > 0 ? rpCombos[0].Items.Count : 0) + "");
        Check("RandPass combo presets are the spec two",
            rpCombos.Count == 1 && rpCombos[0].Items.Cast<object>().SequenceEqual(
                new object[] { "4-*-4", "2-*-4-*-2" }));

        // Layout v2 positions (spec §7): after an arrange pass the generate button leads the
        // left column, above the first checkbox, and the splitter sits in the middle column
        // between the parameter border (col 0) and the result list (col 2).
        rp.UpdateLayout();
        var rpGen = rp.GetVisualDescendantsOfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "生成密码");
        Check("RandPass has a 生成密码 button", rpGen is not null);
        var rpFirstCheck = rpBoxes.FirstOrDefault();
        // TranslatePoint returns null while the visual has no laid-out bounds; the window is
        // shown and UpdateLayout'd above, so both points must exist — a null here is a layout
        // failure, not a "equal position".
        var genPt = rpGen?.TranslatePoint(new Point(0, 0), rp);
        var chkPt = rpFirstCheck?.TranslatePoint(new Point(0, 0), rp);
        Check("generate button sits above the first checkbox",
            genPt is not null && chkPt is not null && genPt.Value.Y < chkPt.Value.Y,
            genPt?.ToString() + " vs " + chkPt?.ToString());

        var rpSplitters = rp.GetVisualDescendantsOfType<GridSplitter>().ToList();
        Check("RandPass splitter sits between the columns",
            rpSplitters.Count == 1
            && rpSplitters[0].GetVisualParent() is Grid { } rpSplitGrid
            && Grid.GetColumn(rpSplitters[0]) == 1
            && rpSplitGrid.Children.Any(c => c is Border && Grid.GetColumn(c) == 0)
            && rpSplitGrid.Children.Any(c => c is ListBox && Grid.GetColumn(c) == 2),
            rpSplitters.Count + " splitter(s)");

        // The frameless window resizes from its 6px edges: pure maths, all eight zones plus a
        // centre that must NOT match (the PanelWindow probe's pattern, spec §7).
        var rpSz = new Size(580, 540);
        bool RpHas(int edges, int flag) => (edges & flag) != 0;
        Check("RandPass west edge zone", RpHas(RandPassWindow.EdgesFor(rpSz, new Point(0, 270)), RandPassWindow.EdgeW));
        Check("RandPass east edge zone", RpHas(RandPassWindow.EdgesFor(rpSz, new Point(579, 270)), RandPassWindow.EdgeE));
        Check("RandPass north edge zone", RpHas(RandPassWindow.EdgesFor(rpSz, new Point(290, 0)), RandPassWindow.EdgeN));
        Check("RandPass south edge zone", RpHas(RandPassWindow.EdgesFor(rpSz, new Point(290, 539)), RandPassWindow.EdgeS));
        Check("RandPass north-west corner", RandPassWindow.EdgesFor(rpSz, new Point(0, 0)) == (RandPassWindow.EdgeN | RandPassWindow.EdgeW));
        Check("RandPass north-east corner", RandPassWindow.EdgesFor(rpSz, new Point(579, 0)) == (RandPassWindow.EdgeN | RandPassWindow.EdgeE));
        Check("RandPass south-west corner", RandPassWindow.EdgesFor(rpSz, new Point(0, 539)) == (RandPassWindow.EdgeS | RandPassWindow.EdgeW));
        Check("RandPass south-east corner", RandPassWindow.EdgesFor(rpSz, new Point(579, 539)) == (RandPassWindow.EdgeS | RandPassWindow.EdgeE));
        Check("RandPass centre is not a resize zone", RandPassWindow.EdgesFor(rpSz, new Point(290, 270)) == 0);

        Check("RandPass loads the shipped defaults",
            rpVm.Length == 8 && rpVm.Count == 10 && rpVm.Lower && rpVm.Upper && rpVm.Digits
            && rpVm.DoubleDigits && rpVm.NoConfusables && rpVm.CustomEnabled && !rpVm.UseFormat);

        rpVm.Generate();
        Pump();
        var rpList = rp.FindControl<ListBox>("ResultList");
        Check("generate fills the result list with Count rows",
            rpVm.Results.Count == 10 && (rpList?.Items.Count ?? 0) == 10,
            rpVm.Results.Count + "/" + (rpList?.Items.Count ?? -1) + "");
        Check("default generation yields 8-char rows (no format)",
            rpVm.Results.All(r => r.Length == 8));

        // The custom-format switch reveals the combo and generation carries dashes.
        rpVm.UseFormat = true;
        rpVm.Generate();
        Pump();
        // Length is 8 (spec §7 default): the 4-*-4 star segment is 0 and drops, leaving [4,4]
        // plus one dash — 9 chars total (the 14-char 16/2 shape is now the Length=14 case).
        Check("format ON switches the output shape to 4-*-4 @8 (9 chars, 1 dash)",
            rpVm.Results.All(r => r.Length == 9 && r.Count(c => c == '-') == 1),
            rpVm.Results.Count > 0 ? rpVm.Results[0] : "empty");

        // Copy contract: a successful clipboard write is reported as transient status.
        rpVm.MarkCopied();
        Check("MarkCopied raises the copied status", rpVm.HasCopied && rpVm.CopyStatus == "已复制");

        // Closing commits the edited section exactly once.
        rpVm.Length = 20;
        rp.Close();
        Pump();
        Check("closing commits the RandPass section to settings",
            rpStore.Current.RandPassLength == 20 && rpStore.Current.RandPassUseFormat == true,
            rpStore.Current.RandPassLength + "/" + rpStore.Current.RandPassUseFormat);
        try { Directory.Delete(rpRoot, true); } catch { }

        // ---- ui-state schema 3: the randPassWindow node round-trips (spec §7) ----
        // The geometry memory's read side is what restores the window; here the exact contract
        // is proven: write → serialize → reload → equal, and a schema-2 file (no node at all,
        // i.e. every ui-state.json that exists today) loads at the window's own default size
        // with no remembered position — not the panel's 720x460.
        var uiRoot = Path.Combine(Path.GetTempPath(), "randpass-ui-" + Guid.NewGuid().ToString("N")[..8]);
        var uiPaths = new AppDataPaths(uiRoot).EnsureCreated();
        var uiStore = new UiStateStore(uiPaths);
        uiStore.Load();
        uiStore.Current.RandPassWindow = new Galah2.Models.WindowState { X = 120, Y = 80, Width = 580, Height = 540 };
        Check("ui-state writes schema 3 with the randPassWindow node",
            uiStore.FileStore.CurrentSchema == 3 && uiStore.Save(),
            "schema=" + uiStore.FileStore.CurrentSchema);
        var uiReread = new UiStateStore(uiPaths);
        uiReread.Load();
        Check("randPassWindow geometry survives a reload",
            uiReread.Current.Schema == 3
            && uiReread.Current.RandPassWindow.X == 120
            && uiReread.Current.RandPassWindow.Y == 80
            && Math.Abs(uiReread.Current.RandPassWindow.Width - 580) < 0.001
            && Math.Abs(uiReread.Current.RandPassWindow.Height - 540) < 0.001,
            "schema=" + uiReread.Current.Schema
            + " pos=" + uiReread.Current.RandPassWindow.X + "," + uiReread.Current.RandPassWindow.Y
            + " size=" + uiReread.Current.RandPassWindow.Width + "x" + uiReread.Current.RandPassWindow.Height);

        File.WriteAllText(uiPaths.UiStateFile,
            "{\"schema\": 2, \"window\": {\"x\": 1, \"y\": 2, \"width\": 720, \"height\": 460}, \"activeTabId\": null}");
        var uiLegacy = new UiStateStore(uiPaths);
        uiLegacy.Load();
        Check("a schema-2 file loads with the RandPass default size, no position",
            uiLegacy.Current.Schema == 3
            && uiLegacy.Current.RandPassWindow.Width == 580
            && uiLegacy.Current.RandPassWindow.Height == 540
            && uiLegacy.Current.RandPassWindow.X is null
            && uiLegacy.Current.Window.Width == 720,
            uiLegacy.Current.RandPassWindow.Width + "x" + uiLegacy.Current.RandPassWindow.Height
            + " x=" + uiLegacy.Current.RandPassWindow.X);
        try { Directory.Delete(uiRoot, true); } catch { }

        // ---- tray menu (spec 2026-11-24-randpass entry 3) ----
        // TrayService builds a real native tray icon, which needs the live product App this
        // probe already configured. The menu rows are NativeMenuItems (outside any visual
        // tree): Header + the Click contract are what a right-click user gets.
        var trayIconFile = Path.Combine(AppContext.BaseDirectory, "app.ico");
        using var tray = new Galah2.Services.TrayService(
            File.OpenRead(trayIconFile),
            onActivate: () => { },
            onSettings: () => { },
            onRandPass: () => { },
            onExit: () => { });
        // Items include a separator row; the actionable rows are the non-separator ones.
        var trayItems = (tray.Icon.Menu?.Items.OfType<NativeMenuItem>().ToList() ?? new List<NativeMenuItem>())
            .Where(m => m is not NativeMenuItemSeparator).ToList();
        var trayTexts = trayItems.Select(m => m.Header).ToList();
        Check("tray menu rows are 设置… / RandPass / 退出",
            trayTexts.SequenceEqual(new[] { "设置…", "RandPass", "退出" }),
            string.Join("|", trayTexts));
        // NativeMenuItem.Click is a plain CLR event (the native menu dispatches it from the OS
        // only), so a callback cannot be fired in-process. HasClickHandlers is the exact
        // "is this row wired" contract - the same greyed-out-row failure mode the panel menus
        // used to show when a row had no live command.
        var trayRp = trayItems.FirstOrDefault(m => m.Header == "RandPass");
        Check("tray RandPass row has a live click handler", trayRp is not null && trayRp.HasClickHandlers);
        Check("tray 设置… row has a live click handler",
            trayItems.FirstOrDefault(m => m.Header == "设置…")?.HasClickHandlers == true);
        Check("tray 退出 row has a live click handler",
            trayItems.FirstOrDefault(m => m.Header == "退出")?.HasClickHandlers == true);
    }



    private static void PressOk(PromptWindow window)
    {
        // The XAML wires Click handlers by name; invoke the same path a button click takes.
        var button = window.GetVisualDescendantsOfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "确定");
        button?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    private static void PressCancel(PromptWindow window)
    {
        var button = window.GetVisualDescendantsOfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "取消");
        button?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    private static void PressConfirmOk(ConfirmWindow window)
    {
        var button = window.GetVisualDescendantsOfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "确定");
        button?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    private static T? FindWindow<T>() where T : Window =>
        _lifetime.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible);

    /// <summary>Let queued UI work run, without blocking the dispatcher.</summary>
    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(60);
        }
    }

    public static int Report()
    {
        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : "=== " + _fail + " FAILED ===");
        return _fail == 0 ? 0 : 1;
    }
}

/// <summary>Minimal visual-tree walk; avoids depending on extension packages.</summary>
internal static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual visual)
        where T : Avalonia.Visual
    {
        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(visual))
        {
            if (child is T match) yield return match;
            foreach (var nested in GetVisualDescendantsOfType<T>(child)) yield return nested;
        }
    }
}

