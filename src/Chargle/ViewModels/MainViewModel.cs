using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Chargle.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Chargle.ViewModels;

public sealed record AccentOption(string Name, SolidColorBrush Brush);

public sealed class PackViewModel(SoundPack pack) : INotifyPropertyChanged
{
    private bool _isPlaying;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SoundPack Pack { get; } = pack;

    /// <summary>
    /// True while this pack's preview is running. The button becomes a stop button rather than
    /// staying a play button that would silently restart the thing already playing.
    /// </summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            Raise(nameof(IsPlaying));
            Raise(nameof(PlayGlyph));
            Raise(nameof(PlayTooltip));
        }
    }

    public string PlayGlyph => _isPlaying ? "\uE71A" : "\uE768";

    public string PlayTooltip => _isPlaying ? "Stop" : "Hear it: connect, then disconnect";

    public string Id => Pack.Id;
    public string Name => Pack.Name;
    public string Description => Pack.Description;
    public bool IsBuiltIn => Pack.IsBuiltIn;

    /// <summary>The connect sound's samples, for the waveform. Null until the pack is decoded.</summary>
    public float[]? Samples => Pack.Plug?.Samples;

    /// <summary>Called once decoding finishes, so the waveform appears rather than staying blank.</summary>
    public void NotifyDecoded() => Raise(nameof(Samples));

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Everything the window shows and everything it can change. Kept deliberately dumb: it reads
/// and writes <see cref="SettingsStore"/> and formats strings, and does no work of its own.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly App _app;
    private readonly DispatcherQueue _dispatcher;

    private string _statusHeadline = "Checking";
    private string _statusDetail = "";
    private string _reaction = "";
    private string _device = "";
    private PackViewModel? _selectedPack;
    private StartupAvailability _startupState = StartupAvailability.Disabled;

    public MainViewModel(App app)
    {
        _app = app;
        _dispatcher = app.Dispatcher;

        Packs = [.. app.Library.Packs.Select(p => new PackViewModel(p))];
        _selectedPack = Packs.FirstOrDefault(p => p.Id == app.Settings.Current.PackId) ?? Packs.FirstOrDefault();

        app.Power.PowerSourceChanged += OnPowerChanged;
        app.Power.BatteryChanged += OnBatteryChanged;
        app.Watcher.Reacted += OnReacted;

        RefreshStatus(app.Power.State);
        _ = LoadStartupStateAsync();
        _ = ShowWaveformsWhenDecodedAsync();
    }

    /// <summary>
    /// Decoding every pack takes a moment, and it happens off the UI thread so the window opens
    /// immediately. The waveforms simply appear when the samples exist.
    /// </summary>
    private async Task ShowWaveformsWhenDecodedAsync()
    {
        await Task.Run(_app.Library.LoadAll);

        foreach (var pack in Packs) pack.NotifyDecoded();
        Device = _app.Audio.DeviceName is { Length: > 0 } name ? $"Playing to {name}" : "No audio device";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PackViewModel> Packs { get; }

    /// <summary>
    /// Rebuilds the list after the library changes on disk, keeping the current selection if it
    /// is still there. Assigns the backing field directly, because reselecting the same pack
    /// should not count as the user changing their mind and rewrite settings.
    /// </summary>
    public void RefreshPacks()
    {
        string? selectedId = _selectedPack?.Id;

        Packs.Clear();
        foreach (var pack in _app.Library.Packs) Packs.Add(new PackViewModel(pack));

        _selectedPack = Packs.FirstOrDefault(p => p.Id == selectedId) ?? Packs.FirstOrDefault();
        Notify(nameof(SelectedPack));

        foreach (var pack in Packs) pack.NotifyDecoded();
    }

    public PackViewModel? SelectedPack
    {
        get => _selectedPack;
        set
        {
            if (!Set(ref _selectedPack, value) || value is null) return;
            _app.Settings.Update(s => s.PackId = value.Id);
        }
    }

    // ---------------------------------------------------------------- status

    public string StatusHeadline { get => _statusHeadline; private set => Set(ref _statusHeadline, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public string Reaction { get => _reaction; private set => Set(ref _reaction, value); }
    public string Device { get => _device; private set => Set(ref _device, value); }

    public bool IsPluggedIn => _app.Power.State.Source == PowerSource.Ac;

    // -------------------------------------------------------------- settings

    public double Volume
    {
        get => _app.Settings.Current.Volume * 100;
        set
        {
            _app.Settings.Update(s => s.Volume = (float)Math.Clamp(value / 100.0, 0, 1));
            Notify();
        }
    }

    public bool PlayOnPlug
    {
        get => _app.Settings.Current.PlayOnPlug;
        set { _app.Settings.Update(s => s.PlayOnPlug = value); Notify(); }
    }

    public bool PlayOnUnplug
    {
        get => _app.Settings.Current.PlayOnUnplug;
        set { _app.Settings.Update(s => s.PlayOnUnplug = value); Notify(); }
    }

    public bool RespectDoNotDisturb
    {
        get => _app.Settings.Current.RespectDoNotDisturb;
        set { _app.Settings.Update(s => s.RespectDoNotDisturb = value); Notify(); }
    }

    public bool ShowVisualIndicator
    {
        get => _app.Settings.Current.ShowVisualIndicator;
        set { _app.Settings.Update(s => s.ShowVisualIndicator = value); Notify(); }
    }

    /// <summary>Shows the panel on demand, so the setting can be judged rather than imagined.</summary>
    public void PreviewIndicator() => _app.ShowIndicator(_app.Power.State);

    // -------------------------------------------------------- look of the panel

    public int IndicatorStyleIndex
    {
        get => (int)_app.Settings.Current.IndicatorStyle;
        set
        {
            if (value < 0) return;
            _app.Settings.Update(s => s.IndicatorStyle = (IndicatorStyle)value);
            Notify();
        }
    }

    public int IndicatorPlacementIndex
    {
        get => (int)_app.Settings.Current.IndicatorPlacement;
        set
        {
            if (value < 0) return;
            _app.Settings.Update(s => s.IndicatorPlacement = (IndicatorPlacement)value);
            Notify();
        }
    }

    /// <summary>
    /// The palette, each entry carrying its own brush so the picker can show the colour rather
    /// than only name it. Choosing a colour from a list of words is a strange thing to ask.
    /// </summary>
    public IReadOnlyList<AccentOption> AccentOptions { get; } =
    [
        .. IndicatorPalette.All.Select(a => new AccentOption(
            a.Name,
            new SolidColorBrush(a.Colour ?? IndicatorPalette.Resolve(null))))
    ];

    public int IndicatorAccentIndex
    {
        get => IndicatorPalette.IndexOf(_app.Settings.Current.IndicatorAccent);
        set
        {
            if (value < 0 || value >= IndicatorPalette.All.Length) return;

            // The first entry means "follow Windows", which is stored as an empty string rather
            // than as its own colour, so it keeps tracking the accent if the user changes it.
            string name = value == 0 ? "" : IndicatorPalette.All[value].Name;
            _app.Settings.Update(s => s.IndicatorAccent = name);
            Notify();
        }
    }

    public double IndicatorSeconds
    {
        get => _app.Settings.Current.IndicatorSeconds;
        set
        {
            _app.Settings.Update(s => s.IndicatorSeconds = Math.Clamp(value, 0.8, 6.0));
            Notify();
            Notify(nameof(IndicatorSecondsLabel));
        }
    }

    public string IndicatorSecondsLabel => $"{IndicatorSeconds:F1} s";

    // ----------------------------------------------------------------- theme

    public int ThemeIndex
    {
        get => (int)_app.Settings.Current.Theme;
        set
        {
            if (value < 0) return;
            _app.Settings.Update(s => s.Theme = (AppTheme)value);
            _app.ApplyTheme();
            Notify();
        }
    }

    public bool InstantMode
    {
        get => _app.Settings.Current.InstantMode;
        set { _app.Settings.Update(s => s.InstantMode = value); Notify(); Notify(nameof(InstantModeDetail)); }
    }

    public string InstantModeDetail => InstantMode
        ? "The audio device is held open, so the first sound arrives about half a second sooner. Costs a little battery."
        : "The audio device is released when idle. Saves a little battery, and the first sound after a quiet spell takes about half a second longer.";

    public bool StartHidden
    {
        get => _app.Settings.Current.StartHidden;
        set { _app.Settings.Update(s => s.StartHidden = value); Notify(); }
    }

    public bool FullChargeEnabled
    {
        get => _app.Settings.Current.FullChargePercent > 0;
        set
        {
            _app.Settings.Update(s => s.FullChargePercent = value ? 100 : 0);
            Notify();
            Notify(nameof(FullChargePercent));
        }
    }

    public double FullChargePercent
    {
        get => _app.Settings.Current.FullChargePercent is var p and > 0 ? p : 100;
        set { _app.Settings.Update(s => s.FullChargePercent = (int)value); Notify(); }
    }

    public bool LowBatteryEnabled
    {
        get => _app.Settings.Current.LowBatteryPercent > 0;
        set
        {
            _app.Settings.Update(s => s.LowBatteryPercent = value ? 20 : 0);
            Notify();
            Notify(nameof(LowBatteryPercent));
        }
    }

    public double LowBatteryPercent
    {
        get => _app.Settings.Current.LowBatteryPercent is var p and > 0 ? p : 20;
        set { _app.Settings.Update(s => s.LowBatteryPercent = (int)value); Notify(); }
    }

    // ------------------------------------------------------------ run at login

    public bool RunAtLogin
    {
        get => _startupState == StartupAvailability.Enabled;
        set => _ = SetStartupAsync(value);
    }

    public bool RunAtLoginBlocked => _startupState == StartupAvailability.BlockedByUser;

    public string RunAtLoginDetail => _startupState switch
    {
        StartupAvailability.BlockedByUser =>
            "Turned off for this app in Task Manager's Startup tab. Windows only lets you undo that there.",
        StartupAvailability.Unsupported =>
            "Could not be changed on this machine.",
        _ => "Chargle starts with Windows and waits in the tray.",
    };

    private async Task LoadStartupStateAsync()
    {
        _startupState = await StartupService.GetStateAsync();
        NotifyStartup();
    }

    private async Task SetStartupAsync(bool enabled)
    {
        _startupState = await StartupService.SetAsync(enabled);
        _app.Settings.Update(s => s.RunAtLogin = _startupState == StartupAvailability.Enabled);
        NotifyStartup();
    }

    private void NotifyStartup()
    {
        Notify(nameof(RunAtLogin));
        Notify(nameof(RunAtLoginBlocked));
        Notify(nameof(RunAtLoginDetail));
    }

    // ------------------------------------------------------------------ mute

    public bool IsMuted => _app.Settings.Current.IsMutedNow;

    public string MuteLabel
    {
        get
        {
            if (_app.Settings.Current.MutedUntilUtc is not { } until || DateTimeOffset.UtcNow >= until)
                return "Mute for an hour";

            var left = until - DateTimeOffset.UtcNow;
            return left.TotalMinutes < 1
                ? "Muted, less than a minute left"
                : $"Muted for another {(int)left.TotalMinutes} min";
        }
    }

    public void ToggleMute()
    {
        bool muted = IsMuted;
        _app.Settings.Update(s => s.MutedUntilUtc = muted ? null : DateTimeOffset.UtcNow.AddHours(1));
        Notify(nameof(IsMuted));
        Notify(nameof(MuteLabel));
    }

    // ----------------------------------------------------------------- events

    private void OnPowerChanged(PowerChange change) => OnUi(() => RefreshStatus(change.State));

    private void OnBatteryChanged(PowerState state) => OnUi(() => RefreshStatus(state));

    private void OnReacted(CueResult result) => OnUi(() =>
    {
        string what = result.Cue == Cue.Plug ? "Connected" : "Disconnected";

        Reaction = result.Outcome switch
        {
            CueOutcome.Played => $"{what}. Sound started {result.ReactionMs:F2} ms later.",
            CueOutcome.SuppressedByMute => $"{what}. Stayed quiet: Chargle is muted.",
            CueOutcome.SuppressedByFocus => $"{what}. Stayed quiet: you are in Do Not Disturb or full screen.",
            CueOutcome.SuppressedBySetting => $"{what}. Stayed quiet: that cue is switched off.",
            CueOutcome.NoSoundLoaded => $"{what}. No sound is loaded for that cue.",
            _ => what,
        };
    });

    private void RefreshStatus(PowerState state)
    {
        StatusHeadline = PowerStrings.Headline(state.Source);
        StatusDetail = PowerStrings.Detail(state);

        Device = _app.Audio.DeviceName is { Length: > 0 } name
            ? $"Playing to {name}"
            : "No audio device";

        Notify(nameof(IsPluggedIn));
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
