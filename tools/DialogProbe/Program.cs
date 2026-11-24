using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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

        var rpSpinners = rp.GetVisualDescendantsOfType<NumericUpDown>().ToList();
        Check("RandPass has length+count spinners", rpSpinners.Count == 2, rpSpinners.Count + "");
        // Spinner round-trip: the length control is a decimal and the property an int - the
        // two-way binding must carry an edited value into the property and on into generation.
        if (rpSpinners.Count == 2)
        {
            rpSpinners[0].Value = 24; // length (the first spinner in the left column)
            Pump();
            Check("length spinner writes back to the property", rpVm.Length == 24, rpVm.Length + "");
            rpVm.Generate();
            Pump();
            Check("generation honors the edited length", rpVm.Results.All(r => r.Length == 24),
                rpVm.Results.Count > 0 ? rpVm.Results[0].Length + " chars" : "empty");
            rpSpinners[0].Value = 14; // restore the screenshot default
            Pump();
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

        Check("RandPass loads the screenshot defaults",
            rpVm.Length == 14 && rpVm.Count == 14 && rpVm.Lower && rpVm.Upper && rpVm.Digits
            && rpVm.DoubleDigits && rpVm.NoConfusables && rpVm.CustomEnabled && !rpVm.UseFormat);

        rpVm.Generate();
        Pump();
        var rpList = rp.FindControl<ListBox>("ResultList");
        Check("generate fills the result list with Count rows",
            rpVm.Results.Count == 14 && (rpList?.Items.Count ?? 0) == 14,
            rpVm.Results.Count + "/" + (rpList?.Items.Count ?? -1) + "");
        Check("default generation yields 14-char rows (no format)",
            rpVm.Results.All(r => r.Length == 14));

        // The custom-format switch reveals the combo and generation carries dashes.
        rpVm.UseFormat = true;
        rpVm.Generate();
        Pump();
        Check("format ON switches the output shape to 4-*-4 (16 chars, 2 dashes)",
            rpVm.Results.All(r => r.Length == 16 && r.Count(c => c == '-') == 2),
            rpVm.Results[0]);

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

