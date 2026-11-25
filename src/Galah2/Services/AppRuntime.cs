using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.ViewModels;
using Galah2.Views;

namespace Galah2.Services;

/// <summary>
/// Owns the running app: paths → stores → panel → tray → hotkey, in 1.0's boot order.
///
/// The panel window is created lazily. A hidden launcher that instantiates its surface at startup
/// pays the whole UI cost while sitting in the tray, which is exactly the memory behaviour this
/// rewrite exists to remove.
/// </summary>
public sealed class AppRuntime : IDisposable, IPanelHost
{
    private readonly AppDataPaths _paths;
    private readonly CatalogStore _catalog;
    private readonly SettingsStore _settings;
    private readonly UiStateStore _uiState;
    private readonly TrayService _tray;
    private readonly HotkeyService _hotkey;
    private readonly ThemeService _theme = new();
    private readonly ToastService _toast = new();
    private PanelWindow? _panel;
    private bool _shuttingDown;

    /// <summary>Absolute path of this build, used by autostart and the desktop shortcut.</summary>
    private static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Galah2.exe");
    private SettingsWindow? _settingsWindow;
    private PanelViewModel? _viewModel;

    public AppRuntime(AppDataPaths paths)
    {
        _paths = paths.EnsureCreated();
        DiagnosticsLog.Initialize(_paths);
        IconService.Initialize(_paths);

        _settings = new SettingsStore(_paths);
        _settings.Load();

        // Apply the stored preference before any window exists, so the first frame is already
        // painted in the right palette instead of flashing the wrong one.
        _theme.Apply(_settings.Current.Theme);

        // Re-assert autostart on boot without rewriting settings.json: after the exe is moved or
        // the Run value is edited by hand, the preference and the registry can disagree. This is
        // what 1.x did, and it is also how a user notices the path went stale.
        if (_settings.Current.AutoStart && !AutoStartService.IsEnabled(ExePath))
        {
            if (!AutoStartService.SetEnabled(true, ExePath, out var autoError))
                DiagnosticsLog.Line("autostart", autoError ?? "同步失败");
        }

        _uiState = new UiStateStore(_paths);
        _uiState.Load();

        _catalog = new CatalogStore(_paths);
        _catalog.Load();

        _hotkey = new HotkeyService();
        _hotkey.Triggered += () => Dispatcher.UIThread.Post(Toggle);

        var iconPath = Path.Combine(_paths.Root, "icons", "galah.ico");
        Stream iconStream = File.Exists(iconPath)
            ? File.OpenRead(iconPath)
            : OpenEmbeddedIcon();

        _tray = new TrayService(iconStream, Toggle, ShowSettings, ShowRandPass, RequestExit);

        if (HotkeyParser.TryParse(_settings.Current.Hotkey, out var spec, out _) && spec is not null)
        {
            var registered = _hotkey.Register(spec);
            DiagnosticsLog.Line("hotkey", "registered=" + registered + " " + spec.Text);
        }
        else
            DiagnosticsLog.Line("hotkey", "热键解析失败，回退到托盘");
    }

    /// <summary>Toggle the panel: create on first use, then show/hide.</summary>
    public void Toggle()
    {
        if (_panel is null)
        {
            _viewModel = new PanelViewModel(_catalog, this, _toast);
            _viewModel.ExitRequested += RequestExit;
            _viewModel.ApplyLayout(_settings.Current);
            _viewModel.ApplySettings(_settings.Current);
            _panel = new PanelWindow { DataContext = _viewModel };

            // A close request on the panel means "hide", never "destroy": an Avalonia window that
            // actually closes cannot be shown again, so cancelling here is what keeps the hotkey
            // working after the user clicks the ✕. Hiding in the Closed handler is too late - the
            // window is already marked closed and the next Toggle throws "Cannot re-show a closed
            // window", which is 1.x's unreachable-app bug in a new dress.
            _panel.Closing += (_, e) =>
            {
                if (_shuttingDown) return;
                e.Cancel = true;
                RememberGeometry();   // capture before hiding
                _panel.Hide();
            };

            // Restore the size from last time, and remember it as the user resizes.
            var saved = _uiState.Current.Window;
            if (saved.Width > 0 && saved.Height > 0)
            {
                _panel.Width = saved.Width;
                _panel.Height = saved.Height;
            }
            _panel.SizeChanged += (_, _) => ScheduleRememberGeometry();
            _panel.PositionChanged += (_, _) => ScheduleRememberGeometry();
        }

        if (_panel.IsVisible)
        {
            _panel.Hide();
            return;
        }

        _viewModel?.Refresh();   // a show always re-projects: the library may have changed
        PlacePanel();
        _panel.Show();
        _panel.Activate();
    }

    /// <summary>
    /// Collapses a burst of geometry changes into one debounced commit (dragging and resizing fire
    /// continuously, and a write per pixel would hammer the disk), and offers an immediate commit
    /// for the on-close path. One instance per window that remembers its geometry: the panel and
    /// the RandPass window share this mechanism (the panel's 400 ms is unchanged).
    /// </summary>
    private sealed class GeometryDebouncer
    {
        private readonly DispatcherTimer _timer;
        private readonly Action _onSettled;

        public GeometryDebouncer(Action onSettled, int debounceMs = 400)
        {
            _onSettled = onSettled;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(debounceMs) };
            _timer.Tick += (_, _) => { _timer.Stop(); _onSettled(); };
        }

        /// <summary>Restart the debounce window; the burst becomes one commit.</summary>
        public void Schedule()
        {
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>Drop the pending commit and write now (the window is closing).</summary>
        public void CommitNow()
        {
            _timer.Stop();
            _onSettled();
        }
    }

    private GeometryDebouncer? _panelGeometry;

    private void ScheduleRememberGeometry()
    {
        // Lazy: the timer must be created on the UI thread, and the panel may never open.
        _panelGeometry ??= new GeometryDebouncer(RememberGeometry);
        _panelGeometry.Schedule();
    }

    /// <summary>Write the live window geometry into ui-state.</summary>
    private void RememberGeometry()
    {
        if (_panel is null) return;

        var window = _uiState.Current.Window;
        window.X = _panel.Position.X;
        window.Y = _panel.Position.Y;
        window.Width = _panel.Bounds.Width;
        window.Height = _panel.Bounds.Height;
        _uiState.Save();
    }

    private void PlacePanel()
    {
        if (_panel is null) return;

        var window = _uiState.Current.Window;
        var remembered = (window.X is not null && window.Y is not null)
            ? (window.X.Value, window.Y.Value)
            : ((double, double)?)null;

        var areas = MonitorGeometry.EnumerateWorkAreas();
        if (areas.Count == 0) return;
        var cursor = PanelPlacement.CursorPoint();
        // One fallback policy, in one place: the cursor's monitor, else the primary, else the
        // first area. PlacePanel and any future caller must not each invent their own.
        var area = PanelPlacement.AreaFor(cursor);

        // The work area is in device pixels; the window size is in DIPs. Mixing them places the
        // panel partly off-screen on any display whose scale is not 100 percent - reported as the
        // panel sliding off the screen after being summoned from the tray.
        //
        // Width/Height (the DIP properties), not Bounds: on the very first Show the window has
        // never been laid out, so Bounds is 0x0 and the center/mouse maths degenerates to
        // "top-left at the screen centre" - the first summon of a fresh install landed there.
        var scale = _panel.RenderScaling;
        var (x, y) = PanelPlacement.Resolve(
            _settings.Current.PanelPlacement, remembered, cursor, area,
            _panel.Width * scale, _panel.Height * scale, scale);

        _panel.Position = new Avalonia.PixelPoint((int)x, (int)y);
    }

    private bool Launch(string itemId)
    {
        var found = _catalog.Current.FindItem(itemId);
        if (found is null) return false;
        var result = LauncherService.Launch(LaunchRequest.From(found.Value.Item));
        return result.Status == LaunchStatus.Ok;
    }

    /// <summary>Settings is a single reusable window: opening it twice must not stack copies.
    /// </summary>
    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var vm = new SettingsViewModel(_settings, _paths.Root, ExePath, ApplySettings);
        vm.HotkeyChanged += spec =>
        {
            var registered = _hotkey.Register(spec);
            DiagnosticsLog.Line("hotkey", "registered=" + registered + " " + spec.Text);
            vm.ReportHotkeyResult(registered, spec.Text);
        };

        _settingsWindow = new SettingsWindow { DataContext = vm };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>Apply a settings change to the live UI (the store has already persisted it).</summary>
    private void ApplySettings(SettingsDocument settings)
    {
        _theme.Apply(settings.Theme);
        _viewModel?.ApplyLayout(settings);
        _viewModel?.ApplySettings(settings);

        // Layout and material are window state, not view-model state: re-apply them here so a
        // change takes effect on the panel that is already open.
        _panel?.ApplyMaterial();
    }


    private void RequestExit()
    {
        Dispose();
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes
                .IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    // ---- IPanelHost ---------------------------------------------------------
    // The panel never touches a window or a dialog directly: it asks the shell. That keeps the
    // view model testable and gives every dialog one owner.

    void IPanelHost.HidePanel() => _panel?.Hide();

    bool IPanelHost.Launch(CatalogItem item)
    {
        var result = LauncherService.Launch(LaunchRequest.From(item));
        if (result.Status != LaunchStatus.Ok)
            DiagnosticsLog.Line("launch", item.Name + ": " + result.Message);
        return result.Status == LaunchStatus.Ok;
    }

    void IPanelHost.ShowAddPicker(string tabId)
    {
        var vm = new AddPickerViewModel(_catalog, tabId);
        var window = new AddPickerWindow { DataContext = vm };
        vm.CloseRequested += () =>
        {
            window.Close();
            _viewModel?.Refresh();   // the picker commits in one action, so one re-project
        };
        ShowOwned(window);
    }

    /// <summary>
    /// Add/edit dialog. The store write happens inside the dialog's own commit path, so the panel
    /// only needs to know WHEN to re-project. Returning early here is deliberate: ShowDialog would
    /// block the UI thread that the dialog itself needs to run on.
    /// </summary>
    CatalogItem? IPanelHost.ShowEditItem(string tabId, CatalogItem? existing)
    {
        var isNew = existing is null || existing.Id is "draft";
        var vm = new EditItemViewModel(_catalog, tabId, isNew ? null : existing, this, _paths)
        {
            Toast = _toast,
        };
        var window = new EditItemWindow { DataContext = vm };
        vm.CloseRequested += () =>
        {
            window.Close();
            if (vm.Completed) _viewModel?.Refresh();
        };
        ShowOwned(window);
        return null;
    }

    void IPanelHost.Prompt(string title, string initial, Action<string?> onAnswer) =>
        PromptWindow.Ask(_panel, title, initial, onAnswer);

    void IPanelHost.Confirm(string title, string message, Action<bool> onAnswer) =>
        ConfirmWindow.Ask(_panel, title, message, onAnswer);

    void IPanelHost.ShowMaintenance()
    {
        var vm = new MaintenanceViewModel(_catalog, _settings, _paths)
        {
            // Import needs a picker, which is a window capability: hand it down instead of
            // letting the view model reach for a window it does not own.
            PickImportFile = async () =>
            {
                var picker = _panel?.StorageProvider ?? _settingsWindow?.StorageProvider;
                if (picker is null) return null;
                var files = await picker.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "选择 Galah 备份文件",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Galah 备份")
                        {
                            Patterns = new[] { "*.json" }
                        }
                    }
                });
                return files.Count > 0 ? files[0].Path.LocalPath : null;
            }
        };
        var window = new MaintenanceWindow { DataContext = vm };
        vm.CloseRequested += window.Close;
        vm.CatalogChanged += () => _viewModel?.Refresh();
        ShowOwned(window);
    }

    void IPanelHost.ShowSettings() => ShowSettings();

    /// <summary>RandPass generator, single instance: re-entry activates the open window.</summary>
    private RandPassWindow? _randPassWindow;
    private GeometryDebouncer? _randPassGeometry;

    // The last geometry the window actually had, captured from its LIVE events. This exists
    // because the platform resets Window.Position to (0,0) DURING the close sequence: the
    // probe reads (100,100) in Closing but (0,0) in Closed for the same window (spec §9.1).
    // Reading the window after it starts closing hands the rememberer (0,0), which the next
    // open then faithfully "restores" — the top-left pinning bug. The panel avoids the same
    // trap by capturing in Closing (it hides instead of closing); RandPass closes for real,
    // so it captures into fields while alive and commits from Closing.
    private PixelPoint _randPassLivePosition;
    private Size _randPassLiveSize;
    private bool _randPassClosing;

    void IPanelHost.ShowRandPass() => ShowRandPass();

    private void ShowRandPass()
    {
        if (_randPassWindow is not null)
        {
            _randPassWindow.Activate();
            return;
        }

        var vm = new RandPassViewModel(_settings);
        _randPassWindow = new RandPassWindow { DataContext = vm };
        _randPassGeometry = new GeometryDebouncer(RememberRandPassGeometry);

        // The XAML carries no startup location: this is the single place that decides where the
        // window opens — the remembered geometry, else centred (PlacePanel's fallback policy).
        // It also seeds the live-geometry fields with the intended values.
        PlaceRandPassWindow();

        _randPassWindow.SizeChanged += RandPass_GeometryLive;
        _randPassWindow.PositionChanged += RandPass_GeometryLive;
        _randPassWindow.Closing += (_, _) =>
        {
            // Commit from Closing, never from Closed: by the time Closed fires, Position has
            // already reset to (0,0). Closing also settles a resize that finished mid-debounce.
            // The flag stops the close sequence's own reset events from scheduling a LATE
            // commit that would write the (0,0) the reset reports.
            _randPassClosing = true;
            _randPassGeometry?.CommitNow();
        };
        _randPassWindow.Closed += (_, _) =>
        {
            _randPassClosing = false;
            _randPassGeometry = null;
            _randPassWindow = null;
        };
        _randPassWindow.Show();
    }

    /// <summary>Follow the window's live geometry into fields (the values a close-time read cannot give).</summary>
    private void RandPass_GeometryLive(object? sender, EventArgs e)
    {
        if (_randPassClosing) return;   // the close sequence resets Position to (0,0); never follow it
        var window = _randPassWindow;
        if (window is null || ReferenceEquals(sender, window) is not true) return;
        _randPassLivePosition = window.Position;
        if (window.Bounds.Width > 0 && window.Bounds.Height > 0)
            _randPassLiveSize = new Size(window.Bounds.Width, window.Bounds.Height);
        _randPassGeometry?.Schedule();
    }

    /// <summary>
    /// Open RandPass where it was last closed (spec 2026-11-24-randpass §7/§9), or — when no
    /// usable save exists — centred on the work area that holds the pointer (the same fallback
    /// chain PlacePanel uses). Either way the intended rectangle is handed to the window, which
    /// re-asserts it on Opened: a Position set before the platform window exists can be dropped
    /// by some platform states, and the window is the only party that can guarantee it stuck.
    ///
    /// X/Y are stored in the units of Window.Position (device pixels), Width/Height in the units
    /// of Window.Width (DIPs) — the same split as the panel's ui-state entry.
    /// </summary>
    private void PlaceRandPassWindow()
    {
        var window = _randPassWindow;
        if (window is null) return;

        var w = window.Width;   // XAML defaults until a valid save says otherwise
        var h = window.Height;
        PixelPoint? pos = null;

        if (TryGetSavedRandPassGeometry(out var sx, out var sy, out var sw, out var sh))
        {
            w = sw;
            h = sh;
            pos = new PixelPoint((int)sx!.Value, (int)sy!.Value);
        }

        window.Width = w;
        window.Height = h;

        pos ??= CenterOnPointerArea(w, h);
        window.Position = pos.Value;
        window.IntendedGeometry = (pos, new Size(w, h));

        // Seed the live-geometry fields with the intended values; the window's live events
        // refresh them while it is open (RandPass_GeometryLive).
        _randPassLivePosition = pos.Value;
        _randPassLiveSize = new Size(w, h);
    }

    /// <summary>
    /// Centre a DIP size on the work area holding the pointer (else primary, else first). The
    /// work area is device pixels, the size DIPs — multiply by the current scale, the same
    /// DIP/device-pixel split PlacePanel documents.
    /// </summary>
    private static PixelPoint CenterOnPointerArea(double widthDip, double heightDip)
    {
        var scale = MonitorGeometry.SystemScale();
        var area = PanelPlacement.AreaFor(PanelPlacement.CursorPoint());
        var (mx, my) = PanelPlacement.Resolve(
            PanelPositions.Center, null, null, area, widthDip * scale, heightDip * scale, scale);
        return new PixelPoint((int)mx, (int)my);
    }

    /// <summary>
    /// Sanitize the saved RandPass geometry on read: a non-finite coordinate, a too-small size,
    /// or a centre that no longer lands on any work area all read as "no valid save" and the
    /// caller falls back to centred placement. The too-small check uses the window's own
    /// MinWidth/MinHeight (480x400): the window cannot shrink below that, so a sane write is
    /// always at or above it.
    /// </summary>
    private bool TryGetSavedRandPassGeometry(out double? x, out double? y, out double w, out double h)
    {
        x = y = null;
        w = h = 0;

        var s = _uiState.Current.RandPassWindow;
        s.SanitizePosition();
        if (s.X is not double sx || s.Y is not double sy) return false;
        if (!double.IsFinite(sx) || !double.IsFinite(sy)) return false;
        if (!double.IsFinite(s.Width) || !double.IsFinite(s.Height)) return false;

        if (s.Width < 480 || s.Height < 400) return false;

        var scale = MonitorGeometry.SystemScale();
        var cx = sx + s.Width * scale / 2;
        var cy = sy + s.Height * scale / 2;
        var onScreen = MonitorGeometry.EnumerateWorkAreas().Any(a =>
            cx >= a.Left && cx <= a.Right && cy >= a.Top && cy <= a.Bottom);
        if (!onScreen) return false;

        x = sx;
        y = sy;
        w = s.Width;
        h = s.Height;
        return true;
    }

    /// <summary>
    /// Write the RandPass window's geometry into ui-state (schema 3) — from the LIVE-GEOMETRY
    /// FIELDS, never from the window itself. By the time this runs (Closing / a debounce that
    /// settles during close), Window.Position has already reset to (0,0) and Bounds may be
    /// collapsing; the fields hold the last true values the window's live events reported.
    /// </summary>
    private void RememberRandPassGeometry()
    {
        if (_randPassLiveSize.Width <= 0 || _randPassLiveSize.Height <= 0) return;

        var saved = _uiState.Current.RandPassWindow;
        saved.X = _randPassLivePosition.X;
        saved.Y = _randPassLivePosition.Y;
        saved.Width = _randPassLiveSize.Width;
        saved.Height = _randPassLiveSize.Height;
        _uiState.Save();
    }

    /// <summary>File picker, owned by a visible window.</summary>
    async Task<string?> IPanelHost.PickFile(string title)
    {
        var picker = ActiveStorageProvider();
        if (picker is null) return null;

        var files = await picker.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    /// <summary>Folder picker, owned by a visible window.</summary>
    async Task<string?> IPanelHost.PickFolder(string title)
    {
        var picker = ActiveStorageProvider();
        if (picker is null) return null;

        var folders = await picker.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>
    /// Show a dialog owned by a live window. The panel is the natural owner, but it may never have
    /// been created - the user can reach the tray menu before ever opening it - and Show(owner)
    /// throws on null.
    /// </summary>
    private void ShowOwned(Window window)
    {
        Window? owner = _panel is not null ? _panel : _settingsWindow;
        if (owner is not null && owner.IsVisible) window.Show(owner);
        else window.Show();
    }

    /// <summary>
    /// Any live window can host a picker; the panel first because it is what the user is looking at.
    /// </summary>
    private Avalonia.Platform.Storage.IStorageProvider? ActiveStorageProvider() =>
        _panel?.StorageProvider ?? _settingsWindow?.StorageProvider;
    private static Stream OpenEmbeddedIcon()
    {
        var uri = new Uri("avares://Galah2/Assets/galah.ico");
        return Avalonia.Platform.AssetLoader.Open(uri);
    }

    private int _disposed;

    public void Dispose()
    {
        // RequestExit() disposes and then calls desktop.Shutdown(); desktop.Exit disposes the
        // runtime a second time. Enter the teardown only once.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shuttingDown = true;      // let the panel close for real on the way out
        _tray.Dispose();
        _hotkey.Dispose();
        _theme.Dispose();
        _toast.Dispose();
        _panel?.Close();
    }
}
