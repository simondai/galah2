# Galah 2.0

A launcher for Windows, rewritten on **Avalonia 11 + NativeAOT**.

1.x lives on the `main` branch (WPF). This branch (`v2`) is the rewrite.

> **Read [FINDINGS-2.0.md](FINDINGS-2.0.md) before assuming this rewrite saves memory.**
> Measured on real hardware, the memory picture is not what the rewrite was started for:
> idle usage is *higher* than 1.x, and the panel saves only 5–13%. What 2.0 does deliver is a
> **65% smaller artifact** (68.6 MB → 23.8 MB), no .NET runtime dependency, and native startup.

---

## Status

| Area | State |
|---|---|
| Tray icon, left-click activate, native right-click menu | working |
| Global hotkey (message-only sink, survives a never-shown panel) | working |
| Single instance + forward toggle | working |
| Panel: category chips + item grid, dark surface | working |
| Reads 1.x `GalahData/catalog.json` unchanged | working |
| Item launch (exe / lnk / url / protocol) | working |
| Shell link (.lnk) create + read, **without `ComImport`** | working, 11/11 probes |
| NativeAOT publish | working (23.8 MB) |
| Icon extraction | **not ported** (1.x `IconService`) |
| Settings / add-picker / maintenance windows | **not ported** |
| Drag and drop, material effects | **not ported** |

---

## Build

```bash
dotnet build Galah2.sln -c Release
```

Run:

```bash
dotnet run --project src/Galah2 -c Release
```

The exe looks for `GalahData/` next to itself (same contract as 1.x), so copy an existing
library in to test with real data.

### NativeAOT

Requires **VS 2022 or Build Tools with the "Desktop development with C++" workload** and a
Windows SDK — the ILCompiler links with MSVC's `link.exe`.

```bash
dotnet publish src/Galah2 -c Release -r win-x64 -p:PublishAot=true -o artifacts/aot
```

**AOT must be smoke-tested, not just compiled.** `JsonSerializer` reflection throws
`JsonSerializerIsReflectionDisabled` the moment the first document loads, which is *before*
any window exists — the failure looks like "double-click does nothing". Publish succeeding
proves nothing. Start it once and confirm a tray icon appears.

---

## Measure

```powershell
powershell -ExecutionPolicy Bypass -File tools/perf/measure.ps1
```

Reports **Private Working Set** (the number Task Manager shows). Working Set includes shared
DLL pages and looks much larger — never quote it as the app's own memory.

---

## Verification tools

```bash
dotnet run --project tools/ComProbe -c Release
```

Exercises the hand-written COM vtable path against a real `.lnk`: writes one, reads every
field back, and checks that a corrupt file reports an error instead of crashing.

---

## Layout

```
src/Galah2/
  Services/Interop/ShellLinkCom.cs   hand-written COM vtables (AOT has no built-in wrappers)
  Services/Interop/Win32.cs          P/Invoke surface
  Services/                          tray, hotkey, single instance, placement, launcher
  Persistence/                       JSON stores + source-generated JsonContext
  Views/ ViewModels/                 Avalonia UI
  Styles/Baseline.axaml              the entire visual theme of 2.0
tools/ComProbe/                      COM round-trip probes
tools/perf/measure.ps1               memory measurement
```

## License

MIT — see [LICENSE](LICENSE).
