using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;

namespace Galah2.ViewModels;

/// <summary>
/// RandPass dialog view model. Loads the RandPass section from the settings document and
/// commits it back in ONE Update() when the window closes (the single-commit rule - no write
/// per checkbox click). Generation is a pure synchronous call (PasswordGenerator), cheap even
/// at the max 100 × 64, so it runs straight on the UI thread.
/// </summary>
public sealed class RandPassViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    private int _length;
    private int _count;
    private bool _lower;
    private bool _upper;
    private bool _digits;
    private bool _doubleDigits;
    private bool _noConfusables;
    private string _customChars;
    private bool _customEnabled;
    private bool _useFormat;
    private string _format;
    private string _copyStatus = "";
    private System.Timers.Timer? _copyTimer;

    public RandPassViewModel(SettingsStore store)
    {
        _store = store;

        // Snapshot the persisted section into the editable properties.
        var d = store.Current;
        _length = d.RandPassLength;
        _count = d.RandPassCount;
        _lower = d.RandPassLower;
        _upper = d.RandPassUpper;
        _digits = d.RandPassDigits;
        _doubleDigits = d.RandPassDoubleDigits;
        _noConfusables = d.RandPassNoConfusables;
        _customChars = d.RandPassCustomChars;
        _customEnabled = d.RandPassCustomEnabled;
        _useFormat = d.RandPassUseFormat;
        _format = d.RandPassFormat;

        GenerateCommand = new RelayCommand(Generate);

        // Stepper buttons: ±1 with clamping. The TextBox path deliberately does NOT clamp (see
        // the Length/Count properties) — a stepper is a discrete action and clamping there is
        // what makes −/＋ impossible to overdrive.
        void StepLength(int delta) =>
            Length = Math.Clamp(Length + delta, PasswordOptions.MinLength, PasswordOptions.MaxLength);
        void StepCount(int delta) =>
            Count = Math.Clamp(Count + delta, PasswordOptions.MinCount, PasswordOptions.MaxCount);
        IncLengthCommand = new RelayCommand(() => StepLength(1));
        DecLengthCommand = new RelayCommand(() => StepLength(-1));
        IncCountCommand = new RelayCommand(() => StepCount(1));
        DecCountCommand = new RelayCommand(() => StepCount(-1));
    }

    /// <summary>One 生成密码 click: (re)fill the result list with Count fresh passwords.</summary>
    public void Generate()
    {
        var opts = CurrentOptions;
        Results.Clear();
        for (var i = 0; i < Math.Clamp(Count, PasswordOptions.MinCount, PasswordOptions.MaxCount); i++)
            Results.Add(PasswordGenerator.Generate(opts));
    }

    /// <summary>The options the properties currently describe.</summary>
    public PasswordOptions CurrentOptions => new()
    {
        Length = Math.Clamp(Length, PasswordOptions.MinLength, PasswordOptions.MaxLength),
        Count = Math.Clamp(Count, PasswordOptions.MinCount, PasswordOptions.MaxCount),
        Lower = Lower,
        Upper = Upper,
        Digits = Digits,
        DoubleDigits = DoubleDigits,
        NoConfusables = NoConfusables,
        CustomChars = CustomChars,
        CustomEnabled = CustomEnabled,
        UseFormat = UseFormat,
        Format = Format,
    };

    /// <summary>The two format presets for the combo box.</summary>
    public IReadOnlyList<string> FormatOptions => PasswordFormats.All;

    // ── parameters ──
    // Length/Count are NOT clamped in the setters: the length TextBox types digit by digit, and
    // clamping would turn the intermediate "6" of a "64" into 4. Out-of-range values are clamped
    // where they matter — CurrentOptions (generation) and Commit (persistence) — both of which
    // Math.Clamp against PasswordOptions' min/max.
    public int Length { get => _length; set => Set(ref _length, value); }
    public int Count { get => _count; set => Set(ref _count, value); }
    public bool Lower { get => _lower; set => Set(ref _lower, value); }
    public bool Upper { get => _upper; set => Set(ref _upper, value); }
    public bool Digits { get => _digits; set => Set(ref _digits, value); }
    public bool DoubleDigits { get => _doubleDigits; set => Set(ref _doubleDigits, value); }
    public bool NoConfusables { get => _noConfusables; set => Set(ref _noConfusables, value); }
    public string CustomChars { get => _customChars; set => Set(ref _customChars, value); }
    public bool CustomEnabled { get => _customEnabled; set => Set(ref _customEnabled, value); }
    public bool UseFormat { get => _useFormat; set => Set(ref _useFormat, value); }
    public string Format { get => _format; set => Set(ref _format, value); }

    /// <summary>Result rows, one password each. Double-clicking a row copies it (view).</summary>
    public ObservableCollection<string> Results { get; } = new();

    public ICommand GenerateCommand { get; }

    // ── length/count stepper buttons (−/+ one step, clamped) ──
    public ICommand IncLengthCommand { get; }
    public ICommand DecLengthCommand { get; }
    public ICommand IncCountCommand { get; }
    public ICommand DecCountCommand { get; }

    /// <summary>Drives the visibility of the CopyStatus label.</summary>
    public bool HasCopied => CopyStatus.Length > 0;

    /// <summary>"已复制" transient feedback after a clipboard write; cleared after ~1.5s.</summary>
    public string CopyStatus
    {
        get => _copyStatus;
        private set
        {
            if (Set(ref _copyStatus, value))
            {
                Raise(nameof(HasCopied));
                _copyTimer?.Dispose();
                _copyTimer = value.Length > 0
                    ? new System.Timers.Timer(1500) { AutoReset = false }
                    : null;
                if (_copyTimer is not null)
                {
                    _copyTimer.Elapsed += (_, _) =>
                        Dispatcher.UIThread.Post(() => CopyStatus = "");
                    _copyTimer.Start();
                }
            }
        }
    }

    /// <summary>Called by the view after a successful clipboard write.</summary>
    public void MarkCopied() => CopyStatus = "已复制";

    /// <summary>
    /// Persist the edited section in a single commit. The view calls this exactly once, on close.
    /// </summary>
    public bool Commit()
    {
        var (len, cnt, lo, up, di, dd, nc, cc, ce, uf, fmt) =
            (Length, Count, Lower, Upper, Digits, DoubleDigits, NoConfusables, CustomChars,
             CustomEnabled, UseFormat, Format);
        return _store.Update(d =>
        {
            d.RandPassLength = Math.Clamp(len, PasswordOptions.MinLength, PasswordOptions.MaxLength);
            d.RandPassCount = Math.Clamp(cnt, PasswordOptions.MinCount, PasswordOptions.MaxCount);
            d.RandPassLower = lo;
            d.RandPassUpper = up;
            d.RandPassDigits = di;
            d.RandPassDoubleDigits = dd;
            d.RandPassNoConfusables = nc;
            d.RandPassCustomChars = cc;
            d.RandPassCustomEnabled = ce;
            d.RandPassUseFormat = uf;
            d.RandPassFormat = Array.IndexOf(PasswordFormats.All, fmt) >= 0 ? fmt : PasswordFormats.FourStarFour;
        });
    }
}
