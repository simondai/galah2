using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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

        // ---- the dialog renders on the panel surface (light alignment, 2026-11-22) ----
        // Dialogs keep the system title bar but must sit on the same surface as the main
        // panel. Look the token up through the window's resource chain (the app-level
        // ResourceDictionary indexer does not expose merged tokens in 11.3) and compare by
        // identity: DynamicResource hands out the dictionary's own brush instance, so a
        // different object would mean the token wiring is wrong.
        object? surfaceResource = null;
        var surface = (prompt != null && prompt.TryFindResource("DialogSurfaceBrush", out surfaceResource))
            ? surfaceResource as Avalonia.Media.IBrush
            : null;
        Check("prompt sits on the panel surface",
            surface is not null && prompt is not null && ReferenceEquals(prompt.Background, surface),
            prompt is null ? "no window"
                : "bg=" + prompt.Background + " token=" + (surface?.ToString() ?? "MISS"));
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

