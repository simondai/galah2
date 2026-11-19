using System.Runtime.InteropServices;
using Galah2.Services;
using Galah2.Services.Interop;

internal static class Program
{
    private static int _fail;
    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
    }

    [STAThread]
    private static int Main()
    {
        CoInitializeEx(IntPtr.Zero, 0x2);
        var dir = Path.Combine(Path.GetTempPath(), "galah2-comprobe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, "probe.lnk");
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Console.WriteLine($"dir = {dir}");

        try
        {
            var created = LinkResolver.TryCreate(lnk, target, "/a b", dir, target, 3);
            Check("TryCreate", created);
            Check("file on disk", File.Exists(lnk),
                File.Exists(lnk) ? new FileInfo(lnk).Length + " bytes" : "missing");

            var info = LinkResolver.Resolve(lnk);
            Check("Resolve().Ok", info.Ok, info.Error ?? "");
            Check("target", info.Target.Equals(Galah2.Persistence.PathNorm.ToStored(target), StringComparison.OrdinalIgnoreCase), info.Target);
            Check("arguments", info.Arguments == "/a b", info.Arguments ?? "<null>");
            Check("workdir", (info.WorkingDirectory ?? "").Equals(Galah2.Persistence.PathNorm.ToStored(dir), StringComparison.OrdinalIgnoreCase),
                info.WorkingDirectory ?? "<null>");
            Check("icon index", info.IconIndex == 3, info.IconIndex.ToString());
            Check("icon source", (info.IconSource ?? "").Length > 0, info.IconSource ?? "<null>");

            var missing = LinkResolver.Resolve(Path.Combine(dir, "nope.lnk"));
            Check("missing reports error", !missing.Ok && missing.Error is not null, missing.Error ?? "");

            var bad = Path.Combine(dir, "bad.lnk");
            File.WriteAllBytes(bad, new byte[300]);
            var badInfo = LinkResolver.Resolve(bad);
            Check("corrupt does not throw", true, badInfo.Error ?? "no error reported");
        }
        catch (Exception ex)
        {
            Check("no exception escaped", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            CoUninitialize();
        }

        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : $"=== {_fail} FAILED ===");
        return _fail == 0 ? 0 : 1;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr a, int b);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
