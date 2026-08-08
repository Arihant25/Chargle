using System.Diagnostics;

namespace Chargle.Services;

public enum Cue
{
    Plug,
    Unplug,
}

/// <summary>Why a cue was or was not played. The UI shows this so the app is never mysteriously silent.</summary>
public enum CueOutcome
{
    Played,
    SuppressedByMute,
    SuppressedByFocus,
    SuppressedBySetting,
    NoSoundLoaded,
}

public sealed record CueResult(Cue Cue, CueOutcome Outcome, double ReactionMs, DateTimeOffset At);

/// <summary>A battery level worth saying something about.</summary>
public enum BatteryMilestone
{
    Full,
    Low,
}

/// <summary>A milestone that has just been crossed, and whether it made a sound.</summary>
public sealed record MilestoneResult(BatteryMilestone Milestone, int Percent, bool Chimed);

/// <summary>
/// The policy layer: decides whether a power event should make a sound, and if so which one.
///
/// It is kept apart from both the power plumbing and the audio plumbing on purpose. The rules
/// about muting, focus sessions and battery thresholds are the part most likely to change, and
/// they should not be tangled up with interop or with a render thread.
/// </summary>
public sealed class ChargeWatcher : IDisposable
{
    private readonly PowerMonitor _power;
    private readonly AudioEngine _audio;
    private readonly SoundLibrary _library;
    private readonly SettingsStore _settings;

    // Latches so the threshold chimes fire once per crossing rather than on every battery tick.
    private bool _announcedFull;
    private bool _announcedLow;

    private CancellationTokenSource? _preview;

    public ChargeWatcher(
        PowerMonitor power, AudioEngine audio, SoundLibrary library, SettingsStore settings)
    {
        _power = power;
        _audio = audio;
        _library = library;
        _settings = settings;

        _power.PowerSourceChanged += OnPowerSourceChanged;
        _power.BatteryChanged += OnBatteryChanged;
    }

    /// <summary>Raised after every power transition, played or not. Fires on a system thread.</summary>
    public event Action<CueResult>? Reacted;

    /// <summary>Raised when a battery milestone is crossed. Fires on a system thread.</summary>
    public event Action<MilestoneResult>? MilestoneReached;

    public CueResult? LastResult { get; private set; }

    /// <summary>Plays a single cue on demand. Ignores every suppression rule.</summary>
    public void Preview(SoundPack pack, Cue cue)
    {
        pack.Load();
        var sound = cue == Cue.Plug ? pack.Plug : pack.Unplug;
        if (sound is not null) _audio.Play(sound, _settings.Current.Volume);
    }

    /// <summary>
    /// Plays whatever a milestone is currently set to play, so you can hear the choice instead of
    /// waiting a few hours for the battery to get there. Ignores every suppression rule, as
    /// previews do.
    /// </summary>
    public void PreviewMilestone(BatteryMilestone milestone)
    {
        StopPreview();
        if (ResolveMilestone(milestone) is { } sound) _audio.Play(sound, _settings.Current.Volume);
    }

    /// <summary>
    /// Plays both halves of a pack in order, which is the only way to judge one: the pair is
    /// designed to be a rise and a fall, and hearing either alone tells you almost nothing.
    /// </summary>
    public async Task PreviewPairAsync(SoundPack pack)
    {
        StopPreview();

        pack.Load();
        if (pack.Plug is null || pack.Unplug is null) return;

        var cts = new CancellationTokenSource();
        _preview = cts;

        try
        {
            float volume = _settings.Current.Volume;
            _audio.Play(pack.Plug, volume);

            // Let the first sound play, capped at a second because the long packs spend most of
            // their length on a reverb tail, then leave a clear second of silence. Running the
            // two halves together makes them sound like one four note phrase; the pause is what
            // lets you hear them as a pair moving in opposite directions.
            var firstSound = TimeSpan.FromMilliseconds(
                Math.Min(pack.Plug.Duration.TotalMilliseconds, 1000));

            await Task.Delay(firstSound + TimeSpan.FromSeconds(1), cts.Token);
            _audio.Play(pack.Unplug, volume);

            // Wait out the second sound as well, so whoever called this knows when the preview
            // is genuinely over and can put its button back.
            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Min(pack.Unplug.Duration.TotalMilliseconds, 1200)),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose. Nothing to report.
        }
        finally
        {
            if (ReferenceEquals(_preview, cts)) _preview = null;
            cts.Dispose();
        }
    }

    /// <summary>Cuts a preview short. Safe to call when nothing is playing.</summary>
    public void StopPreview()
    {
        var running = _preview;
        _preview = null;

        if (running is not null)
        {
            try { running.Cancel(); }
            catch (ObjectDisposedException) { /* finished on its own a moment ago */ }
        }

        _audio.StopAll();
    }

    private void OnPowerSourceChanged(PowerChange change)
    {
        var cue = change.State.Source == PowerSource.Ac ? Cue.Plug : Cue.Unplug;
        var outcome = Play(cue, change.Timestamp);

        double reaction = outcome == CueOutcome.Played
            ? _audio.LastDispatchMicroseconds / 1000.0
            : (Stopwatch.GetTimestamp() - change.Timestamp) * 1000.0 / Stopwatch.Frequency;

        var result = new CueResult(cue, outcome, reaction, DateTimeOffset.Now);
        LastResult = result;
        Reacted?.Invoke(result);

        // Coming off mains means the battery is discharging again, so the "it's full" chime
        // should be allowed to fire next time it charges up.
        if (cue == Cue.Unplug) _announcedFull = false;
        else _announcedLow = false;
    }

    private void OnBatteryChanged(PowerState state)
    {
        var settings = _settings.Current;

        if (settings.FullChargePercent > 0 &&
            state.Source == PowerSource.Ac &&
            state.BatteryPercent >= settings.FullChargePercent)
        {
            if (!_announcedFull)
            {
                _announcedFull = true;
                Announce(BatteryMilestone.Full, state);
            }
        }
        else if (state.BatteryPercent < settings.FullChargePercent - 3)
        {
            // Three points of hysteresis: batteries wobble around their reported level, and a
            // bare threshold would chime repeatedly while hovering on it.
            _announcedFull = false;
        }

        if (settings.LowBatteryPercent > 0 &&
            state.Source != PowerSource.Ac &&
            state.BatteryPercent > 0 &&
            state.BatteryPercent <= settings.LowBatteryPercent)
        {
            if (!_announcedLow)
            {
                _announcedLow = true;
                Announce(BatteryMilestone.Low, state);
            }
        }
        else if (state.BatteryPercent > settings.LowBatteryPercent + 3)
        {
            _announcedLow = false;
        }
    }

    /// <summary>
    /// Says the milestone however the user asked to be told.
    ///
    /// Only the sound happens here. Showing the panel needs the UI thread and a window, and this
    /// runs on the power notification thread, so that half goes out as an event for App to pick up.
    /// </summary>
    private void Announce(BatteryMilestone milestone, PowerState state)
    {
        var alert = AlertFor(milestone);

        bool chimed = alert is MilestoneAlert.Chime or MilestoneAlert.Both
                      && PlayMilestone(milestone);

        MilestoneReached?.Invoke(new MilestoneResult(milestone, state.BatteryPercent, chimed));
    }

    private MilestoneAlert AlertFor(BatteryMilestone milestone) => milestone == BatteryMilestone.Full
        ? _settings.Current.FullChargeAlert
        : _settings.Current.LowBatteryAlert;

    /// <summary>
    /// Deliberately does not consult PlayOnPlug and PlayOnUnplug. Those two are about the cable
    /// moving, and a milestone has its own switch. Turning off the connect chime should not
    /// silently take the full-battery chime with it, which is what used to happen.
    /// </summary>
    private bool PlayMilestone(BatteryMilestone milestone)
    {
        var settings = _settings.Current;

        if (settings.IsMutedNow) return false;
        if (settings.RespectDoNotDisturb && Presence.ShouldStayQuiet()) return false;

        if (ResolveMilestone(milestone) is not { } sound) return false;

        _audio.Play(sound, settings.Volume);
        return true;
    }

    /// <summary>
    /// The half of a pack that suits the news: the rising sound for a full battery, the falling
    /// one for a flat one. With no pack chosen it borrows the charger's, so the feature works the
    /// moment it is switched on and only asks a question of people who want to answer it.
    /// </summary>
    private CachedSound? ResolveMilestone(BatteryMilestone milestone)
    {
        var settings = _settings.Current;

        string chosen = milestone == BatteryMilestone.Full
            ? settings.FullChargePackId
            : settings.LowBatteryPackId;

        var pack = _library.Find(chosen) ?? _library.Find(settings.PackId) ?? _library.Packs.FirstOrDefault();
        if (pack is null) return null;

        pack.Load();
        return milestone == BatteryMilestone.Full ? pack.Plug : pack.Unplug;
    }

    private CueOutcome Play(Cue cue, long eventTimestamp)
    {
        var settings = _settings.Current;

        bool wanted = cue == Cue.Plug ? settings.PlayOnPlug : settings.PlayOnUnplug;
        if (!wanted) return CueOutcome.SuppressedBySetting;

        if (settings.IsMutedNow) return CueOutcome.SuppressedByMute;

        if (settings.RespectDoNotDisturb && Presence.ShouldStayQuiet())
            return CueOutcome.SuppressedByFocus;

        var sound = Resolve(cue);
        if (sound is null) return CueOutcome.NoSoundLoaded;

        _audio.Play(sound, settings.Volume, eventTimestamp);
        return CueOutcome.Played;
    }

    private CachedSound? Resolve(Cue cue)
    {
        // There is no separate notion of a custom sound any more. Files the user chooses are
        // copied into a pack of their own and selected like any other, so there is exactly one
        // thing that decides what plays and no way for two of them to disagree.
        var pack = _library.Find(_settings.Current.PackId) ?? _library.Packs.FirstOrDefault();
        if (pack is null) return null;

        pack.Load();
        return cue == Cue.Plug ? pack.Plug : pack.Unplug;
    }

    /// <summary>
    public void Dispose()
    {
        _power.PowerSourceChanged -= OnPowerSourceChanged;
        _power.BatteryChanged -= OnBatteryChanged;
    }
}
