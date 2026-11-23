using Galah2.Views;

namespace DragArmProbe;

/// <summary>
/// Regression probe for the 2026-11-23 "panel teleports when clicking 设置 in the ☰ menu" bug.
///
/// The panel's chrome drag and edge resize follow the pointer in OnPointerMoved while the left
/// button is down. The origin state used to persist after the gesture (a release on the flyout
/// popup never reaches the panel), so the next button-down movement re-fired the drag from the
/// stale origin and the panel flew off-screen.
///
/// DragArmState makes the state valid only for the press that armed it. These are the exact
/// transitions the bug depended on:
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
    }

    public static int Main()
    {
        // 1. a real chrome press arms the drag, and a held movement may move the window.
        var a = new DragArmState();
        Check("fresh state: nothing armed", !a.DragArmed && !a.ResizeArmed);
        a.ArmDrag();
        Check("chrome press arms the drag", a.DragArmed);

        // 2. THE BUG: a release that lands on the flyout popup - the panel never sees it, but the
        //    flyout taking activation does arrive as Deactivated. After that, a button-down
        //    movement over the panel must NOT drag.
        a.Deactivated();
        Check("flyout activation ends the gesture (no ghost drag)", !a.DragArmed);

        // 3. a release the panel does observe also ends it.
        var b = new DragArmState();
        b.ArmDrag();
        b.Release();
        Check("observed release disarms", !b.DragArmed);

        // 4. a movement without the button down (the press ended somewhere) also ends it.
        var c = new DragArmState();
        c.ArmDrag();
        c.ButtonUpMoved();
        Check("button-up movement disarms", !c.DragArmed);

        // 5. nothing armed: a button-down movement never drags, even with a stale origin around.
        var d = new DragArmState();
        d.ArmDrag();
        d.Release();            // one real drag happened earlier, origin fields still hold values
        Check("stale origin alone cannot drag", !d.DragArmed);

        // 6. resize arming follows the same rules.
        var r = new DragArmState();
        r.ArmResize();
        Check("edge press arms the resize", r.ResizeArmed);
        r.Deactivated();
        Check("flyout activation ends the resize gesture too", !r.ResizeArmed);

        // 7. arming one gesture does not arm the other.
        var m = new DragArmState();
        m.ArmDrag();
        Check("arm drag leaves resize disarmed", m.DragArmed && !m.ResizeArmed);
        m.Release();
        m.ArmResize();
        Check("arm resize leaves drag disarmed", m.ResizeArmed && !m.DragArmed);

        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : $"=== {_fail} FAILED ===");
        return _fail == 0 ? 0 : 1;
    }
}
