namespace Galah2.Services;

/// <summary>
/// Category navigation maths (Sprint 3 / ROADMAP E4). Pure so the wrapping and digit rules are
/// unit-testable without a dispatcher: the UI layer only decides which gesture means which delta.
/// </summary>
public static class TabNavigation
{
    /// <summary>
    /// Index reached by stepping from the current selection, wrapping at both ends.
    /// </summary>
    /// <param name="count">Number of categories.</param>
    /// <param name="current">Index of the selected category, or -1 when none.</param>
    /// <param name="delta">+1 forward, -1 backward.</param>
    /// <returns>New index, or -1 when there is nothing to switch between.</returns>
    public static int StepIndex(int count, int current, int delta)
    {
        if (count < 2) return -1;
        // With nothing selected, step forward from "before the first" and backward from
        // "at the first" so both directions wrap to the expected end.
        var from = current < 0 ? (delta >= 0 ? -1 : 0) : current;
        // A single "+ count" only covers delta = -1; normalize with a positive modulus so any
        // delta (e.g. -5 with 4 tabs) still produces a valid index.
        return ((from + delta) % count + count) % count;
    }

    /// <summary>
    /// Category index addressed by a digit key (1..9 map to the first nine categories; 0 is
    /// deliberately unused so Ctrl+0 cannot silently jump to a tenth target).
    /// </summary>
    /// <param name="digit">Digit 0..9 from the key event.</param>
    /// <returns>Zero-based index, or -1 when the digit addresses nothing.</returns>
    public static int DigitToIndex(int digit) => digit is >= 1 and <= 9 ? digit - 1 : -1;
}
