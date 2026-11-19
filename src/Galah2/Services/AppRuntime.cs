using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
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
public sealed class AppRuntime : IDisposable
{
    private readonly AppDataPaths _paths;
    private readonly CatalogStore _catalog;
    private readonly SettingsStore _settings;
    private readonly UiStateStore _uiState;
    private readonly TrayService _tray;
    private readonly HotkeyService _hotkey;
    private PanelWindow? _panel;
    private PanelViewModel? _viewModel;

    public AppRuntime(AppDataPaths paths)
    {
        _paths = paths.EnsureCreated();
        DiagnosticsLog.Initialize(_paths);

        _settings = new SettingsStore(_paths);
        _settings.Load();

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

        _tray = new TrayService(iconStream, Toggle, ShowSettingsPlaceholder, RequestExit);

        if (HotkeyParser.TryParse(_settings.Current.Hotkey, out var spec, out _) && spec is not null)
            _hotkey.Register(spec);
        else
            DiagnosticsLog.Line("hotkey", "热键解析失败，回退到托盘");
    }

    /// <summary>Toggle the panel: create on first use, then show/hide.</summary>
    public void Toggle()
    {
        if (_panel is null)
        {
            _viewModel = new PanelViewModel(_catalog, Launch);
            _panel = new PanelWindow { DataContext = _viewModel };
            _panel.Closed += (_, _) => _panel.Hide();
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

    private void PlacePanel()
    {
        if (_panel is null) return;

        var window = _uiState.Current.Window;
        var remembered = (window.X is not null && window.Y is not null)
            ? (window.X.Value, window.Y.Value)
            : ((double, double)?)null;

        var areas = MonitorGeometry.EnumerateWorkAreas();
        if (areas.Count == 0) return;
        var area = MonitorGeometry.WorkAreaAtCursor() ?? areas[0];

        var cursor = PanelPlacement.CursorPoint();
        var (x, y) = PanelPlacement.Resolve(
            _settings.Current.PanelPlacement, remembered, cursor, area, _panel.Width, _panel.Height);

        _panel.Position = new Avalonia.PixelPoint((int)x, (int)y);
    }

    private bool Launch(string itemId)
    {
        var found = _catalog.Current.FindItem(itemId);
        if (found is null) return false;
        var result = LauncherService.Launch(LaunchRequest.From(found.Value.Item));
        return result.Status == LaunchStatus.Ok;
    }

    private void ShowSettingsPlaceholder() =>
        DiagnosticsLog.Line("settings", "设置窗口尚未迁移到 2.0（Sprint 3）");

    private void RequestExit()
    {
        Dispose();
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes
                .IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private static Stream OpenEmbeddedIcon()
    {
        var uri = new Uri("avares://Galah2/Assets/galah.ico");
        return Avalonia.Platform.AssetLoader.Open(uri);
    }

    public void Dispose()
    {
        _tray.Dispose();
        _hotkey.Dispose();
        _panel?.Close();
    }
}
