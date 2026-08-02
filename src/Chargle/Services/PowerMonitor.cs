using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Chargle.Services;

public enum PowerSource
{
    Unknown = -1,
    Ac = 0,      // PoAc, mains
    Battery = 1, // PoDc, battery
    Ups = 2,     // PoHot, short-term or UPS
}

public sealed record PowerState(PowerSource Source, int BatteryPercent, bool IsCharging);

/// <summary>
/// A power source transition, carrying the <see cref="System.Diagnostics.Stopwatch"/> timestamp
/// taken on the first line of the notification callback. Everything downstream measures itself
/// against this, so the reaction time the app reports is real rather than aspirational.
/// </summary>
public sealed record PowerChange(PowerState State, long Timestamp);

/// <summary>
/// Watches the AC/DC power source and reports changes as fast as Windows is willing to tell us.
///
/// Two decisions here are the whole reason Chargle reacts instantly:
///
/// 1. We subscribe to <c>GUID_ACDC_POWER_SOURCE</c>. Windows pushes this the moment the power
///    manager sees the transition. The obvious alternatives, <c>PBT_APMPOWERSTATUSCHANGE</c> or
///    polling <c>GetSystemPowerStatus</c>, are only refreshed periodically, which is where the
///    familiar "why did it take a second?" delay comes from.
///
/// 2. We register with <c>DEVICE_NOTIFY_CALLBACK</c> rather than <c>DEVICE_NOTIFY_WINDOW_HANDLE</c>.
///    The window-handle form delivers a WM_POWERBROADCAST that has to queue behind every other
///    message on that thread's pump, so a busy UI thread delays the sound. The callback form is
///    invoked directly on a system thread, independent of any message loop. It costs us nothing
///    and removes an entire class of "sometimes it's late" behaviour.
///
/// The callback runs on a thread that belongs to Windows, so <see cref="PowerSourceChanged"/> can
/// fire from anywhere. Handlers must be cheap and thread-safe; marshal to the UI yourself.
/// </summary>
public sealed unsafe partial class PowerMonitor : IDisposable
{
    // {5D3E9A59-E9D5-4B00-A6BD-FF34FF516548}
    private static readonly Guid GuidAcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    // {A7AD8041-B45A-4CAE-87A3-EECBB468A9E1}
    private static readonly Guid GuidBatteryPercentageRemaining = new("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");

    private const uint DeviceNotifyCallback = 2;
    private const uint PbtPowerSettingChange = 0x8013;

    /// <summary>
    /// The single live monitor. The unmanaged callback must be a static function pointer, so it
    /// needs a static way back to the instance. One instance per process is all we ever want.
    /// </summary>
    private static PowerMonitor? _current;

    private readonly Lock _gate = new();
    private nint _acdcRegistration;
    private nint _batteryRegistration;
    private PowerState _state;
    private bool _disposed;

    public PowerMonitor()
    {
        _current = this;
        _state = ReadCurrentState();

        var subscription = new DeviceNotifySubscribeParameters
        {
            Callback = (delegate* unmanaged<nint, uint, nint, uint>)&OnPowerSettingChanged,
            Context = 0,
        };

        uint result = PowerSettingRegisterNotification(
            in GuidAcDcPowerSource, DeviceNotifyCallback, in subscription, out _acdcRegistration);
        if (result != 0)
            throw new InvalidOperationException($"Could not subscribe to AC/DC power notifications (error {result}).");

        // Best-effort: only drives the battery readout in the UI, so a failure here is survivable.
        PowerSettingRegisterNotification(
            in GuidBatteryPercentageRemaining, DeviceNotifyCallback, in subscription, out _batteryRegistration);
    }

    /// <summary>
    /// Raised when the machine moves between mains and battery. Fires on a system thread, within
    /// roughly a millisecond of the transition.
    /// </summary>
    public event Action<PowerChange>? PowerSourceChanged;

    /// <summary>Raised when the battery level or charging flag changes. UI only.</summary>
    public event Action<PowerState>? BatteryChanged;

    public PowerState State
    {
        get { lock (_gate) return _state; }
    }

    [UnmanagedCallersOnly]
    private static uint OnPowerSettingChanged(nint context, uint type, nint setting)
    {
        // Anything that can throw would be tearing across a native frame, so the whole body is
        // guarded. Returning ERROR_SUCCESS regardless is what the API expects.
        // Taken on the very first line, before anything else can add to it. This is the zero
        // that the app's reported reaction time is measured from.
        long arrivedAt = Stopwatch.GetTimestamp();

        try
        {
            if (type == PbtPowerSettingChange && setting != 0)
                _current?.Handle((PowerBroadcastSetting*)setting, arrivedAt);
        }
        catch
        {
            // Deliberately swallowed: a failure here must never take down the process.
        }

        return 0; // ERROR_SUCCESS
    }

    private void Handle(PowerBroadcastSetting* payload, long arrivedAt)
    {
        Guid which = payload->PowerSetting;

        if (which == GuidAcDcPowerSource)
        {
            if (payload->DataLength < 4) return;
            var source = (PowerSource)Unsafe.ReadUnaligned<uint>(&payload->Data);

            PowerState updated;
            lock (_gate)
            {
                // Windows can re-announce the same source (for example after resume). Only a real
                // transition should make a sound.
                if (_state.Source == source) return;
                _state = _state with { Source = source };
                updated = _state;
            }

            PowerSourceChanged?.Invoke(new PowerChange(updated, arrivedAt));

            // The percentage and charging flag come from a slower subsystem; refresh them off the
            // hot path so they never sit between the cable and the sound.
            ThreadPool.UnsafeQueueUserWorkItem(static m => m.RefreshDetails(), this, preferLocal: false);
        }
        else if (which == GuidBatteryPercentageRemaining)
        {
            if (payload->DataLength < 4) return;
            int percent = (int)Unsafe.ReadUnaligned<uint>(&payload->Data);

            PowerState updated;
            lock (_gate)
            {
                if (_state.BatteryPercent == percent) return;
                _state = _state with { BatteryPercent = percent };
                updated = _state;
            }

            BatteryChanged?.Invoke(updated);
        }
    }

    private void RefreshDetails()
    {
        var fresh = ReadCurrentState();
        PowerState updated;

        lock (_gate)
        {
            if (_disposed) return;
            // Keep the source we were pushed: it is more current than the polled status.
            _state = _state with { BatteryPercent = fresh.BatteryPercent, IsCharging = fresh.IsCharging };
            updated = _state;
        }

        BatteryChanged?.Invoke(updated);
    }

    private static PowerState ReadCurrentState()
    {
        if (!GetSystemPowerStatus(out var status))
            return new PowerState(PowerSource.Unknown, -1, false);

        var source = status.ACLineStatus switch
        {
            0 => PowerSource.Battery,
            1 => PowerSource.Ac,
            _ => PowerSource.Unknown,
        };

        // 255 means "unknown", which is what a desktop without a battery reports.
        int percent = status.BatteryLifePercent == 255 ? -1 : status.BatteryLifePercent;
        bool charging = (status.BatteryFlag & 0x08) != 0;

        return new PowerState(source, percent, charging);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_acdcRegistration != 0) PowerSettingUnregisterNotification(_acdcRegistration);
        if (_batteryRegistration != 0) PowerSettingUnregisterNotification(_batteryRegistration);
        _acdcRegistration = _batteryRegistration = 0;

        if (ReferenceEquals(_current, this)) _current = null;
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public delegate* unmanaged<nint, uint, nint, uint> Callback;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data; // first byte of a variable-length payload
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingRegisterNotification(
        in Guid settingGuid,
        uint flags,
        in DeviceNotifySubscribeParameters recipient,
        out nint registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingUnregisterNotification(nint registrationHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
