using System.Runtime.Versioning;

// Windows-only by design: Win32 tray, global hotkeys, registry, EventWaitHandle IPC. Declaring
// it here (not as the csproj SupportedOSPlatform property) is deliberate - on a plain net8.0
// target that MSBuild property does not emit the assembly attribute, and the 37 CA1416
// warnings stayed. This attribute is what actually silences them.
[assembly: SupportedOSPlatform("windows")]
