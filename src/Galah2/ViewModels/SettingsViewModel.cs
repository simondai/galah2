using System.Windows.Input;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;

namespace Galah2.ViewModels;

/// <summary>
/// Settings view state. Every edit is applied to the document and committed immediately — 1.x's
/// contract was "改动即时生效并保存", and a settings dialog that only saves on OK would silently
/// lose a change when the window is dismissed.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly string _dataRoot;
    private readonly Action<SettingsDocument> _apply;
    private string _hotkeyText = "";
    private string _hotkeyHint = "";
    private int _iconSizeIndex;
    private int _themeIndex;
    private bool _hideOnLaunch;
    private bool _autoStart;
    private bool _wheelSwitchTab;
    private bool _numberKeysSwitchTab;

    public SettingsViewModel(SettingsStore store, string dataRoot, Action<SettingsDocument> apply)
    {
        _store = store;
        _dataRoot = dataRoot;
        _apply = apply;
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
        Load();
    }

    public event Action? CloseRequested;

    public ICommand CloseCommand { get; }

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

    /// <summary>0 = small, 1 = medium, 2 = large (mirrors 1.x's two-step icon sizing).</summary>
    /// <summary>0 = follow system, 1 = light, 2 = dark (the order the combo shows).</summary>
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

    public bool HideOnLaunch
    {
        get => _hideOnLaunch;
        set { if (Set(ref _hideOnLaunch, value)) { _store.Current.HideOnLaunch = value; Commit(); } }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set { if (Set(ref _autoStart, value)) { _store.Current.AutoStart = value; Commit(); } }
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
        _hideOnLaunch = s.HideOnLaunch;
        _autoStart = s.AutoStart;
        _wheelSwitchTab = s.WheelSwitchTab;
        _numberKeysSwitchTab = s.NumberKeysSwitchTab;

        foreach (var name in new[]
                 {
                     nameof(HotkeyText), nameof(HotkeyHint), nameof(ThemeIndex),
                     nameof(IconSizeIndex),
                     nameof(HideOnLaunch), nameof(AutoStart),
                     nameof(WheelSwitchTab), nameof(NumberKeysSwitchTab), nameof(DataFolder)
                 })
            Raise(name);
    }

    private void Commit()
    {
        _store.Save();
        _apply(_store.Current);
    }
}
