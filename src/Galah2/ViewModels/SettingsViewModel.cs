using System.Windows.Input;
using Avalonia.Threading;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;

namespace Galah2.ViewModels;

/// <summary>
/// Settings view state.
///
/// Every edit is applied to the document and committed immediately — 1.x's contract was
/// "改动即时生效并保存", and a dialog that only saves on OK silently loses a change when the
/// user closes it.
///
/// Choices are exposed as indices into a fixed option list rather than raw strings: the combo
/// shows the same order the index maps from, so the two cannot drift apart.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly string _dataRoot;
    private readonly string _executablePath;
    private readonly Action<SettingsDocument> _apply;

    private string _hotkeyText = "";
    private string _hotkeyHint = "";
    private string _shortcutMessage = "";
    private string _autoStartMessage = "";
    private int _themeIndex;
    private int _iconSizeIndex;
    private int _tabPositionIndex;
    private int _clickModeIndex;
    private int _materialIndex;
    private int _placementIndex;
    private bool _hideOnLaunch;
    private bool _hideOnBlur;
    private bool _autoStart;
    private bool _wheelSwitchTab;
    private bool _numberKeysSwitchTab;

    public SettingsViewModel(SettingsStore store, string dataRoot, string executablePath,
        Action<SettingsDocument> apply)
    {
        _store = store;
        _dataRoot = dataRoot;
        _executablePath = executablePath;
        _apply = apply;

        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
        CreateShortcutCommand = new RelayCommand(CreateShortcut);
        Load();
    }

    public event Action? CloseRequested;

    public ICommand CloseCommand { get; }
    public ICommand CreateShortcutCommand { get; }

    // ---- labels shown next to each combo, in the same order the index maps from ----
    public string[] ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];
    public string[] IconSizeOptions { get; } = ["小", "中", "大"];
    public string[] TabPositionOptions { get; } = ["顶部", "左侧"];
    public string[] ClickModeOptions { get; } = ["单击启动", "双击启动"];
    /// <summary>
    /// Only what the app can actually render: an opaque fill, or the same fill at 91% alpha
    /// (the 1.x translucent level). The two options differ only in surface opacity,
    /// so the labels say exactly that - no Mica promises.
    /// </summary>
    public string[] MaterialOptions { get; } = ["不透明（100%）", "半透明"];
    public string[] PlacementOptions { get; } = ["记住上次位置", "跟随鼠标", "屏幕居中"];

    public string HotkeyText
    {
        get => _hotkeyText;
        set
        {
            if (!Set(ref _hotkeyText, value)) return;

            // Parse before committing: a half-typed hotkey must not overwrite a working one.
            if (HotkeyParser.TryParse(value, out var spec, out var error) && spec is not null)
            {
                HotkeyHint = "已生效：" + spec.Text;
                _store.Current.Hotkey = spec.Text;
                Commit();
                HotkeyChanged?.Invoke(spec);
            }
            else
            {
                HotkeyHint = error ?? "格式无效";
            }
        }
    }

    public string HotkeyHint
    {
        get => _hotkeyHint;
        private set => Set(ref _hotkeyHint, value);
    }

    /// <summary>Raised with a valid hotkey so the runtime can re-register it.</summary>
    public event Action<HotkeySpec>? HotkeyChanged;

    /// <summary>
    /// Show whether the runtime actually registered the hotkey. RegisterHotKey fails silently
    /// to everyone else; without this a combo held by another program looks "already effective"
    /// in the settings page while doing nothing.
    /// </summary>
    public void ReportHotkeyResult(bool ok, string text)
        => HotkeyHint = ok ? "已生效：" + text : "注册失败（可能被其他程序占用）：" + text;

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!Set(ref _themeIndex, value)) return;
            _store.Current.Theme = value switch
            {
                1 => ThemeModes.Light,
                2 => ThemeModes.Dark,
                _ => ThemeModes.System
            };
            Commit();
        }
    }

    public int IconSizeIndex
    {
        get => _iconSizeIndex;
        set
        {
            if (!Set(ref _iconSizeIndex, value)) return;
            _store.Current.IconSize = value switch
            {
                0 => IconSizes.Small,
                2 => IconSizes.Large,
                _ => IconSizes.Medium
            };
            Commit();
        }
    }

    public int TabPositionIndex
    {
        get => _tabPositionIndex;
        set
        {
            if (!Set(ref _tabPositionIndex, value)) return;
            _store.Current.TabPosition = value == 1 ? TabPositions.Left : TabPositions.Top;
            Commit();
        }
    }

    public int ClickModeIndex
    {
        get => _clickModeIndex;
        set
        {
            if (!Set(ref _clickModeIndex, value)) return;
            _store.Current.ClickMode = value == 1 ? ClickModes.Double : ClickModes.Single;
            Commit();
        }
    }

    public int MaterialIndex
    {
        get => _materialIndex;
        set
        {
            if (!Set(ref _materialIndex, value)) return;
            _store.Current.PanelMaterial = value == 1
                ? PanelMaterials.Translucent
                : PanelMaterials.None;
            Commit();
        }
    }

    public int PlacementIndex
    {
        get => _placementIndex;
        set
        {
            if (!Set(ref _placementIndex, value)) return;
            _store.Current.PanelPlacement = value switch
            {
                1 => PanelPositions.Mouse,
                2 => PanelPositions.Center,
                _ => PanelPositions.Remember
            };
            Commit();
        }
    }

    public bool HideOnLaunch
    {
        get => _hideOnLaunch;
        set { if (Set(ref _hideOnLaunch, value)) { _store.Current.HideOnLaunch = value; Commit(); } }
    }

    public bool HideOnBlur
    {
        get => _hideOnBlur;
        set { if (Set(ref _hideOnBlur, value)) { _store.Current.HideOnBlur = value; Commit(); } }
    }

    /// <summary>
    /// Autostart writes the HKCU Run value, not just the preference. A checkbox that only
    /// remembered a flag and never touched the registry would look right and do nothing.
    /// </summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value)) return;

            if (AutoStartService.SetEnabled(value, _executablePath, out var error))
            {
                _store.Current.AutoStart = value;
                AutoStartMessage = value ? "已加入开机启动" : "已移出开机启动";
                Commit();
            }
            else
            {
                // The registry refused: put the checkbox back so the UI matches reality.
                Set(ref _autoStart, !value);
                AutoStartMessage = error ?? "启动项写入失败";
            }
        }
    }

    public string AutoStartMessage
    {
        get => _autoStartMessage;
        private set => Set(ref _autoStartMessage, value);
    }

    public bool WheelSwitchTab
    {
        get => _wheelSwitchTab;
        set { if (Set(ref _wheelSwitchTab, value)) { _store.Current.WheelSwitchTab = value; Commit(); } }
    }

    public bool NumberKeysSwitchTab
    {
        get => _numberKeysSwitchTab;
        set { if (Set(ref _numberKeysSwitchTab, value)) { _store.Current.NumberKeysSwitchTab = value; Commit(); } }
    }

    /// <summary>Where the library and log files live, shown so the user can find their data.</summary>
    public string DataFolder => _dataRoot;

    public string ShortcutMessage
    {
        get => _shortcutMessage;
        private set => Set(ref _shortcutMessage, value);
    }

    private void CreateShortcut()
    {
        // Runs off the UI thread: writing a .lnk goes through COM and can take a moment.
        var exe = _executablePath;
        var icon = Path.Combine(_dataRoot, "icons", "galah.ico");
        Task.Run(() => ShortcutMaker.Create(exe, File.Exists(icon) ? icon : null))
            .ContinueWith(t =>
            {
                var result = t.Result;
                Dispatcher.UIThread.Post(() => ShortcutMessage = result.Message);
            }, TaskScheduler.Default);
    }

    private void Load()
    {
        var s = _store.Current;
        _hotkeyText = s.Hotkey;
 
        HotkeyHint = "当前：" + s.Hotkey;

        _themeIndex = s.Theme switch
        {
            ThemeModes.Light => 1,
            ThemeModes.Dark => 2,
            _ => 0
        };
        _iconSizeIndex = s.IconSize switch
        {
            IconSizes.Small => 0,
            IconSizes.Large => 2,
            _ => 1
        };
        _tabPositionIndex = s.TabPosition == TabPositions.Left ? 1 : 0;
        _clickModeIndex = s.ClickMode == ClickModes.Double ? 1 : 0;
        _materialIndex = s.PanelMaterial == PanelMaterials.Translucent ? 1 : 0;
        _placementIndex = s.PanelPlacement switch
        {
            PanelPositions.Mouse => 1,
            PanelPositions.Center => 2,
            _ => 0
        };
        _hideOnLaunch = s.HideOnLaunch;
        _hideOnBlur = s.HideOnBlur;
        _wheelSwitchTab = s.WheelSwitchTab;
        _numberKeysSwitchTab = s.NumberKeysSwitchTab;

        // Report what the registry actually says, not what the preference claims: the two can
        // differ after the exe moves or someone edits the Run key by hand.
        _autoStart = AutoStartService.IsEnabled(_executablePath);
        AutoStartMessage = _autoStart ? "开机启动已开启" : "";

        foreach (var name in new[]
                 {
                     nameof(HotkeyText), nameof(HotkeyHint), nameof(ThemeIndex),
                     nameof(IconSizeIndex), nameof(TabPositionIndex), nameof(ClickModeIndex),
                     nameof(MaterialIndex), nameof(PlacementIndex),
                     nameof(HideOnLaunch), nameof(HideOnBlur), nameof(AutoStart),
                     nameof(AutoStartMessage), nameof(WheelSwitchTab),
                     nameof(NumberKeysSwitchTab), nameof(DataFolder), nameof(ShortcutMessage)
                 })
            Raise(name);
    }

    private void Commit()
    {
        _store.Save();
        _apply(_store.Current);
    }
}
