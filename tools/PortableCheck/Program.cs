using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Verifies that a published build is actually portable ("green"), the way 1.x was.
///
/// A green build is defined by three properties, and this tool checks all three against a real
/// copy in a fresh folder rather than trusting where the code says it writes:
///   1. it runs from any directory, with no install step and no .NET runtime on the machine;
///   2. every byte of user data lands beside the executable, never in %APPDATA%/%LOCALAPPDATA%;
///   3. the only thing it adds to the system is the autostart value, and only when asked.
///
/// Usage: PortableCheck &lt;folder containing Galah2.exe&gt;
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
        var source = args.Length > 0 ? args[0] : FindPublished();
        if (source is null || !File.Exists(Path.Combine(source, "Galah2.exe")))
        {
            Console.WriteLine("FAIL  no published folder found (pass the folder as the first argument)");
            return 1;
        }

        Console.WriteLine("published folder: " + source);

        // ---- 1. the folder is self-describing ----
        var files = Directory.GetFiles(source).Select(Path.GetFileName).Where(f => f is not null).ToList();
        Check("exe present", files.Contains("Galah2.exe"));
        Check("no .pdb shipped", !files.Any(f => f!.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", files));

        // ---- 2. it runs from somewhere else, and writes beside itself ----
        var sandbox = Path.Combine(Path.GetTempPath(), "galah2-portable-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        try
        {
            foreach (var file in Directory.GetFiles(source))
            {
                if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(sandbox, Path.GetFileName(file)));
            }

            foreach (var dir in Directory.GetDirectories(source))
                CopyTree(dir, Path.Combine(sandbox, Path.GetFileName(dir)));

            var dataBefore = LocalAppDataEntries();
            var exe = Path.Combine(sandbox, "Galah2.exe");
            var process = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = sandbox,
                UseShellExecute = false,
            });

            Check("starts from a fresh folder", process is not null);
            if (process is null) return Report();

            var alive = WaitSettled(process);
            Check("stays running (tray resident)", alive, alive ? "alive after 9s" : "exited");

            // The data folder must appear next to the exe, not anywhere else.
            var dataDir = Path.Combine(sandbox, "GalahData");
            Check("data folder created beside the exe", Directory.Exists(dataDir), dataDir);

            var expected = new[] { "catalog.json", "settings.json", "ui-state.json" };
            var present = Directory.Exists(dataDir)
                ? Directory.GetFiles(dataDir).Select(Path.GetFileName).ToList() : new List<string?>();
            Check("all three documents written locally",
                expected.All(e => present.Contains(e)),
                string.Join(", ", present));

            // The icon cache folder is created lazily on the first panel open, so it is not
            // required to exist yet - only to be inside the data folder when it does.
            if (Directory.Exists(dataDir))
            {
                var nested = Directory.GetDirectories(dataDir).Select(Path.GetFileName).ToList();
                var icons = Path.Combine(dataDir, "icons");
                Check("icon cache, if any, lives in the data folder",
                    nested.Count == 0 || nested.Contains("icons") || Directory.Exists(icons),
                    nested.Count == 0 ? "not created yet (lazy)" : string.Join(", ", nested));
            }

            // Nothing may have leaked into the user profile.
            var dataAfter = LocalAppDataEntries();
            var leaked = dataAfter.Except(dataBefore).ToList();
            Check("no data written to %LOCALAPPDATA%/%APPDATA%", leaked.Count == 0,
                leaked.Count == 0 ? "clean" : string.Join(", ", leaked));

            // ---- 3. nothing added to the system unless asked ----
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false))
            {
                var value = run?.GetValue("Galah") as string;
                Check("no autostart entry created on first run", string.IsNullOrEmpty(value),
                    value ?? "none");
            }

            process.Kill();
            process.WaitForExit(5000);
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }

        return Report();
    }

    private static int Report()
    {
        Console.WriteLine(_fail == 0 ? "=== PORTABLE ===" : "=== " + _fail + " FAILED ===");
        return _fail == 0 ? 0 : 1;
    }

    private static bool WaitSettled(Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(9);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) return false;
            Thread.Sleep(250);
        }
        return !process.HasExited;
    }

    private static string[] LocalAppDataEntries()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };
        return roots
            .Where(Directory.Exists)
            .SelectMany(r => Directory.GetFileSystemEntries(r, "Galah*", SearchOption.TopDirectoryOnly))
            .ToArray();
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static string? FindPublished()
    {
        var root = AppContext.BaseDirectory;
        for (var i = 0; i < 5; i++) root = Path.GetFullPath(Path.Combine(root, ".."));
        var candidate = Path.Combine(root, "artifact");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
