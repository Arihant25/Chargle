using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargle.Services;

public static class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\Chargle. Under MSIX this is transparently redirected into the package's
    /// own storage, so the same code is correct packaged and unpackaged.
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Chargle");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    System,
    Light,
    Dark,
}

[JsonConverter(typeof(JsonStringEnumConverter<IndicatorPlacement>))]
public enum IndicatorPlacement
{
    BottomCentre,
    TopCentre,
    BottomRight,
    TopRight,
}

[JsonConverter(typeof(JsonStringEnumConverter<IndicatorStyle>))]
public enum IndicatorStyle
{
    /// <summary>Badge, headline and detail. Says the most.</summary>
    Panel,

    /// <summary>Badge and state, without the battery level.</summary>
    Compact,

    /// <summary>Just the mark. For people who only need the fact that something happened.</summary>
    Minimal,
}

/// <summary>Everything the user can change. Serialised verbatim to settings.json.</summary>
public sealed class ChargleSettings
{
    public string PackId { get; set; } = "chime";
    public float Volume { get; set; } = 0.65f;

    public bool PlayOnPlug { get; set; } = true;
    public bool PlayOnUnplug { get; set; } = true;

    /// <summary>Keep the audio endpoint open so playback is instant. See <see cref="AudioEngine"/>.</summary>
    public bool InstantMode { get; set; } = true;

    /// <summary>
    /// On by default. A tray app that only makes a sound when the charger moves is useless until
    /// the next time you happen to launch it, so waiting to be asked is the wrong default here.
    /// It is registered through the normal Windows mechanisms, so it stays visible and switchable
    /// in Settings and Task Manager.
    /// </summary>
    public bool RunAtLogin { get; set; } = true;
    public bool StartHidden { get; set; } = true;

    /// <summary>Respect the system's Do Not Disturb / focus session and stay quiet.</summary>
    public bool RespectDoNotDisturb { get; set; } = true;

    /// <summary>Also show a brief heads-up panel near the bottom of the screen.</summary>
    public bool ShowVisualIndicator { get; set; } = true;

    public IndicatorPlacement IndicatorPlacement { get; set; } = IndicatorPlacement.BottomCentre;

    public IndicatorStyle IndicatorStyle { get; set; } = IndicatorStyle.Panel;

    /// <summary>A name from <see cref="IndicatorPalette"/>, or empty to follow the Windows accent.</summary>
    public string IndicatorAccent { get; set; } = "";

    /// <summary>How long the panel stays before it fades, in seconds.</summary>
    public double IndicatorSeconds { get; set; } = 1.9;

    /// <summary>Light, dark, or whatever Windows is currently using.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Chime once when the battery reaches full. 0 disables.</summary>
    public int FullChargePercent { get; set; }

    /// <summary>Chime once when the battery falls to this level on battery power. 0 disables.</summary>
    public int LowBatteryPercent { get; set; }

    /// <summary>Set by "Mute for an hour" in the tray menu.</summary>
    public DateTimeOffset? MutedUntilUtc { get; set; }

    public bool IsMutedNow => MutedUntilUtc is { } until && DateTimeOffset.UtcNow < until;
}

/// <summary>
/// Loads and saves <see cref="ChargleSettings"/>. Writes are debounced and atomic. A settings
/// file truncated by a crash mid-write would silently reset someone's choices, which is a rude
/// way to lose a preference.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly Lock _gate = new();
    private readonly Timer _saveTimer;
    private bool _disposed;

    public SettingsStore()
    {
        IsFirstRun = !File.Exists(AppPaths.SettingsFile);
        Current = Load();
        _saveTimer = new Timer(_ => WriteNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// True when there was no settings file, so this is the first launch on this machine. Used
    /// to apply defaults that need doing rather than just storing, like registering for startup.
    /// </summary>
    public bool IsFirstRun { get; }

    public ChargleSettings Current { get; private set; }

    public event Action<ChargleSettings>? Changed;

    /// <summary>Mutate settings and schedule a save. The callback runs under the store's lock.</summary>
    public void Update(Action<ChargleSettings> mutate)
    {
        lock (_gate)
        {
            if (_disposed) return;
            mutate(Current);
        }

        Changed?.Invoke(Current);
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Forces a synchronous write. Called on exit so nothing is lost.</summary>
    public void Flush() => WriteNow();

    private static ChargleSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize(json, ChargleJson.Default.ChargleSettings);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable: fall back to defaults rather than refusing to start.
            Debug.WriteLine($"Chargle: could not read settings. {ex.Message}");
        }

        return new ChargleSettings();
    }

    private void WriteNow()
    {
        string json;
        lock (_gate)
        {
            if (_disposed) return;
            json = JsonSerializer.Serialize(Current, ChargleJson.Default.ChargleSettings);
        }

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);

            // Write beside the target and swap, so the real file is never half-written.
            string temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not save settings. {ex.Message}");
        }
    }

    public void Dispose()
    {
        WriteNow();

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _saveTimer.Dispose();
    }
}
