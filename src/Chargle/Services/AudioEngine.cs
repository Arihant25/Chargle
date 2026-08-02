using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Chargle.Services;

/// <summary>
/// Keeps a WASAPI render stream open and ready so that playing a sound costs almost nothing.
///
/// The delay people notice in a naive implementation is rarely the power event. It is the audio
/// stack: opening a device, and then waiting for an endpoint that Windows has already powered
/// down to spin back up. That resume can be a few hundred milliseconds on its own.
///
/// So Chargle opens the device once and leaves it streaming silence. When something happens, the
/// sound is already decoded (see <see cref="CachedSound"/>) and the pipe is already open, so the
/// only remaining delay is one buffer: tens of milliseconds, not a second.
///
/// That does keep the audio endpoint awake, which costs a little battery. It is a real trade-off
/// rather than a free win, so it is a setting, and the UI says so plainly.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(4);

    private readonly Lock _gate = new();
    private readonly InstantMixer _mixer = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceChangeWatcher _watcher;
    private readonly Timer _idleTimer;

    private WasapiOut? _output;
    private bool _keepWarm = true;
    private bool _disposed;

    public AudioEngine()
    {
        _watcher = new DeviceChangeWatcher(OnDefaultDeviceChanged);
        _enumerator.RegisterEndpointNotificationCallback(_watcher);
        _idleTimer = new Timer(_ => StopIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Requested buffer size. The floor for what the user can actually hear.</summary>
    public int RequestedLatencyMs { get; init; } = 20;

    /// <summary>
    /// When true the render stream stays open permanently, which is what makes playback instant.
    /// When false the stream is released after a few idle seconds to let the endpoint sleep.
    /// </summary>
    public bool KeepWarm
    {
        get { lock (_gate) return _keepWarm; }
        set
        {
            lock (_gate)
            {
                if (_keepWarm == value) return;
                _keepWarm = value;
            }

            if (value) EnsureOpen();
            else ScheduleIdleStop();
        }
    }

    /// <summary>Microseconds between the power event and the sound being handed to the mixer.</summary>
    public double LastDispatchMicroseconds { get; private set; }

    /// <summary>Name of the endpoint we are currently rendering to, for the UI.</summary>
    public string? DeviceName { get; private set; }

    public bool IsWarm
    {
        get { lock (_gate) return _output?.PlaybackState == PlaybackState.Playing; }
    }

    /// <summary>
    /// The mixer on its own, so tools/AudioProbe can render it offline and tell whether a
    /// playback problem is in the mixing or in the WASAPI layer under it.
    /// </summary>
    internal InstantMixer MixerForTesting => _mixer;

    /// <summary>Opens the device up front so the first sound is as fast as every later one.</summary>
    public void Prime() => EnsureOpen();

    /// <summary>
    /// Plays a preloaded sound. Safe to call from the power notification thread; it takes a
    /// short lock and copies a few fields, nothing more.
    /// </summary>
    /// <param name="eventTimestamp">
    /// A <see cref="Stopwatch.GetTimestamp"/> value captured when the power event arrived, used
    /// to report real end-to-end dispatch latency. Pass 0 to skip measurement.
    /// </param>
    public void Play(CachedSound sound, float volume, long eventTimestamp = 0)
    {
        if (sound.Samples.Length == 0) return;

        EnsureOpen();
        _mixer.Trigger(sound.Samples, Math.Clamp(volume, 0f, 1f));

        if (eventTimestamp != 0)
        {
            long elapsed = Stopwatch.GetTimestamp() - eventTimestamp;
            LastDispatchMicroseconds = elapsed * 1_000_000.0 / Stopwatch.Frequency;
        }

        ScheduleIdleStop();
    }

    /// <summary>
    /// Silences everything currently sounding, without closing the stream. Used by the stop
    /// button on a preview: the point is to shut up immediately, not to tear down the device
    /// and have to warm it up again.
    /// </summary>
    public void StopAll() => _mixer.StopAll();

    private void EnsureOpen()
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (_output is { PlaybackState: PlaybackState.Playing })
                return;

            if (_output is null)
            {
                try
                {
                    var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    DeviceName = device.FriendlyName;

                    // Event-driven shared mode: Windows wakes us exactly when it needs samples,
                    // which is both lower latency and lower power than polling.
                    _output = new WasapiOut(device, AudioClientShareMode.Shared, true, RequestedLatencyMs);
                    _output.PlaybackStopped += OnPlaybackStopped;
                    _output.Init(_mixer);
                }
                catch (Exception ex)
                {
                    // No render endpoint at all (headless VM, every device disabled). Chargle is
                    // still perfectly happy to sit there; it just has nowhere to play.
                    Debug.WriteLine($"Chargle: could not open an audio device. {ex.Message}");
                    _output = null;
                    DeviceName = null;
                    return;
                }
            }

            try
            {
                _output.Play();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chargle: could not start playback. {ex.Message}");
                Teardown();
            }
        }
    }

    private void ScheduleIdleStop()
    {
        lock (_gate)
        {
            if (_disposed || _keepWarm) return;
        }

        _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    private void StopIfIdle()
    {
        lock (_gate)
        {
            if (_disposed || _keepWarm || _output is null) return;
            if (_mixer.HasActiveVoices) return; // still ringing out; try again later

            try { _output.Stop(); }
            catch (Exception ex) { Debug.WriteLine($"Chargle: stop failed. {ex.Message}"); }
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;

        // The endpoint went away underneath us, usually a USB headset being unplugged, which is
        // exactly the sort of thing that happens right before someone plugs in a charger.
        Debug.WriteLine($"Chargle: playback stopped. {e.Exception.Message}");
        RebuildAsync();
    }

    private void OnDefaultDeviceChanged()
    {
        // Default endpoint changed (headphones in, dock connected). Move the stream to follow it.
        RebuildAsync();
    }

    private void RebuildAsync() => ThreadPool.UnsafeQueueUserWorkItem(static engine =>
    {
        lock (engine._gate)
        {
            if (engine._disposed) return;
            engine.Teardown();
        }

        // Give the endpoint a moment to settle before grabbing it; racing the device change
        // reliably produces an exception and a silent app.
        Thread.Sleep(250);

        lock (engine._gate)
        {
            if (engine._disposed || !engine._keepWarm) return;
        }

        engine.EnsureOpen();
    }, this, preferLocal: false);

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private void Teardown()
    {
        if (_output is null) return;

        _output.PlaybackStopped -= OnPlaybackStopped;
        try { _output.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"Chargle: dispose failed. {ex.Message}"); }

        _output = null;
        DeviceName = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Teardown();
        }

        _idleTimer.Dispose();
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { /* already gone */ }
        _enumerator.Dispose();
    }

    // ------------------------------------------------------------------ mixer

    /// <summary>
    /// A fixed pool of voices mixed into the render buffer. There is no allocation and no lock on
    /// the trigger path, so calling it from the power callback cannot stall Windows' notification
    /// thread, and cannot introduce a GC pause between the cable and the sound.
    /// </summary>
    internal sealed class InstantMixer : ISampleProvider
    {
        private const int MaxVoices = 6;
        private const int Free = 0, Claimed = 1, Playing = 2;

        private readonly Voice[] _voices = [.. Enumerable.Range(0, MaxVoices).Select(_ => new Voice())];
        private int _roundRobin;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(CachedSound.SampleRate, CachedSound.Channels);

        /// <summary>How many times the render thread has asked for samples. Diagnostics only.</summary>
        public long ReadCalls;

        /// <summary>Peak of the most recently rendered buffer. Diagnostics only.</summary>
        public float LastReadPeak;

        /// <summary>Anything the last render threw. Diagnostics only.</summary>
        public string? LastReadError;

        /// <summary>Set to record exactly what the mixer hands to WASAPI. Diagnostics only.</summary>
        public NAudio.Wave.WaveFileWriter? Tee { get; set; }

        public int ActiveVoices
        {
            get
            {
                int n = 0;
                foreach (var v in _voices) if (Volatile.Read(ref v.State) == Playing) n++;
                return n;
            }
        }

        public bool HasActiveVoices
        {
            get
            {
                foreach (var v in _voices)
                    if (Volatile.Read(ref v.State) != Free) return true;
                return false;
            }
        }

        public void Trigger(float[] samples, float volume)
        {
            foreach (var voice in _voices)
            {
                if (Interlocked.CompareExchange(ref voice.State, Claimed, Free) != Free) continue;

                voice.Samples = samples;
                voice.Volume = volume;
                voice.Position = 0;
                Volatile.Write(ref voice.State, Playing);
                return;
            }

            // Every voice busy: retarget one rather than dropping the sound. Restarting a voice
            // mid-flight can click, but silence would be the worse failure here.
            var steal = _voices[(uint)Interlocked.Increment(ref _roundRobin) % MaxVoices];
            Volatile.Write(ref steal.State, Claimed);
            steal.Samples = samples;
            steal.Volume = volume;
            steal.Position = 0;
            Volatile.Write(ref steal.State, Playing);
        }

        public void StopAll()
        {
            foreach (var voice in _voices)
            {
                voice.Samples = null;
                Volatile.Write(ref voice.State, Free);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref ReadCalls);

            try
            {
                return ReadCore(buffer, offset, count);
            }
            catch (Exception ex)
            {
                // An exception escaping a render callback stops the stream dead, taking all
                // audio with it. Far better to emit a buffer of silence and keep going.
                LastReadError = $"{ex.GetType().Name}: {ex.Message}";

                // Same reason as in ReadCore: clear by indexing, never with Array.Clear.
                for (int i = offset; i < offset + count; i++) buffer[i] = 0f;
                return count;
            }
        }

        private int ReadCore(float[] buffer, int offset, int count)
        {
            // Deliberately a loop, and not Array.Clear.
            //
            // NAudio hands us a float[] reference that actually points at a byte[]: see the
            // explicit-layout union in NAudio's WaveBuffer, which overlays the two array types
            // at the same address. Indexing through the float[] reference steps four bytes at a
            // time, because the JIT uses the static type. Array.Clear does not: it dispatches on
            // the object's *runtime* element type, so it would zero `count` bytes rather than
            // `count` floats and leave three quarters of the buffer holding the previous render.
            //
            // That stale audio is then handed to WASAPI again on every callback, which is
            // audible as a sustained buzz that never stops after any sound plays.
            for (int i = offset; i < offset + count; i++) buffer[i] = 0f;

            foreach (var voice in _voices)
            {
                if (Volatile.Read(ref voice.State) != Playing) continue;

                float[]? samples = voice.Samples;
                if (samples is null) continue;

                int position = voice.Position;
                float volume = voice.Volume;
                int n = Math.Min(count, samples.Length - position);

                for (int i = 0; i < n; i++)
                    buffer[offset + i] += samples[position + i] * volume;

                voice.Position = position + n;

                if (voice.Position >= samples.Length)
                {
                    voice.Samples = null;
                    Volatile.Write(ref voice.State, Free);
                }
            }

            // Overlapping sounds can sum past full scale; fold them instead of letting the
            // hardware clip. Only does anything when voices actually overlap.
            for (int i = offset; i < offset + count; i++)
            {
                float s = buffer[i];
                if (s > 1f || s < -1f) buffer[i] = MathF.Tanh(s);
            }

            float peak = 0;
            for (int i = offset; i < offset + count; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
            LastReadPeak = peak;
            Tee?.WriteSamples(buffer, offset, count);

            // Always return a full buffer. A partial read would let NAudio stop the stream, and
            // a stopped stream is a cold endpoint again.
            return count;
        }

        private sealed class Voice
        {
            public int State;
            public float[]? Samples;
            public float Volume;
            public int Position;
        }
    }

    /// <summary>Minimal IMMNotificationClient: we only care that the default output moved.</summary>
    private sealed class DeviceChangeWatcher(Action onDefaultDeviceChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
                onDefaultDeviceChanged();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
