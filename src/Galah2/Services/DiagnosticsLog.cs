using System.Text;
using Galah2.Persistence;

namespace Galah2.Services;

/// <summary>
/// Append-only diagnostics file inside the data folder. It is derived data (never authoritative) and
/// is only written when there is actually something to report, so a clean no-op restart writes
/// nothing anywhere except the ui-state delta (INV-1/INV-5).
/// </summary>
public static class DiagnosticsLog
{
    private static AppDataPaths? _paths;
    private static readonly object Gate = new();

    /// <summary>Point the logger at the data root.</summary>
    /// <param name="paths">Data paths of the running instance.</param>
    public static void Initialize(AppDataPaths paths) => _paths = paths;

    /// <summary>
    /// Append one line per note when the list is non-empty.
    /// </summary>
    /// <param name="notes">Notes collected during the phase.</param>
    /// <param name="phase">Short phase label, e.g. "boot".</param>
    public static void Write(IEnumerable<string> notes, string phase)
    {
        var lines = notes.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (lines.Count == 0 || _paths is null) return;
        Write(phase, lines);
    }

    /// <summary>Append a single message.</summary>
    /// <param name="phase">Phase label.</param>
    /// <param name="message">Message text.</param>
    public static void Line(string phase, string message) => Write(phase, new[] { message });

    private static void Write(string phase, IReadOnlyList<string> lines)
    {
        // A log call can happen before the logger is pointed at a data folder (the secondary
        // instance exits before it initialises the paths). Diagnostics must never break the
        // product — not even with a null reference.
        if (_paths is null) return;
        lock (Gate)
        {
            try
            {
                var file = Path.Combine(_paths.Root, "galah.log");
                var builder = new StringBuilder();
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                foreach (var line in lines) builder.Append(stamp).Append(" [").Append(phase).Append("] ").AppendLine(line);
                File.AppendAllText(file, builder.ToString());
            }
            catch (IOException) { /* diagnostics must never break the product */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
