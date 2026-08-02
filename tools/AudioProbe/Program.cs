using System.Diagnostics;
using Chargle.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Chargle.AudioProbe;

/// <summary>
/// Exercises the decode and playback path on its own, away from the UI, and says out loud what
/// it finds. Kept in the repo because "it makes a noise but the wrong one" is the hardest class
/// of bug to reason about from a description.
///
///     dotnet run --project tools/AudioProbe -- &lt;path to a wav&gt;
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Debug.WriteLine inside the engine goes to the trace listeners, so surface them.
        Trace.Listeners.Add(new ConsoleTraceListener());

        if (args.Contains("--dnd")) return DndProbe.Run();
        if (args.Contains("--benchmark")) return Benchmark(args.FirstOrDefault(a => !a.StartsWith("--")));

        string path = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "chime", "plug.wav");

        if (!File.Exists(path))
        {
            Console.WriteLine($"No such file: {path}");
            return 1;
        }

        Console.WriteLine("=== decode ===");
        var sound = CachedSound.Load(path);
        Console.WriteLine($"  file        {path}");
        Console.WriteLine($"  samples     {sound.Samples.Length} floats ({sound.Samples.Length / 2} frames)");
        Console.WriteLine($"  duration    {sound.Duration.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  peak        {sound.PeakAmplitude:F4}");
        Console.WriteLine($"  first 8     {string.Join(", ", sound.Samples.Take(8).Select(s => s.ToString("F4")))}");

        Console.WriteLine();
        Console.WriteLine("=== device ===");
        using (var enumerator = new MMDeviceEnumerator())
        {
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = device.AudioClient.MixFormat;
            Console.WriteLine($"  name        {device.FriendlyName}");
            Console.WriteLine($"  mix format  {mix.SampleRate} Hz, {mix.Channels} ch, {mix.BitsPerSample}-bit, {mix.Encoding}");
            Console.WriteLine($"  our format  {CachedSound.SampleRate} Hz, {CachedSound.Channels} ch, 32-bit, IeeeFloat");
            if (mix.SampleRate != CachedSound.SampleRate)
                Console.WriteLine("  NOTE        rates differ, so WasapiOut has to resample.");
        }

        Console.WriteLine();
        Console.WriteLine("=== offline mixer render ===");
        RenderOffline(sound);

        // Three measurements of the same length, so the source of any noise is unambiguous:
        // what the machine sounds like with Chargle absent, with its stream merely open, and
        // with a sound actually playing.
        Console.WriteLine();
        Console.WriteLine("=== A. baseline, no engine ===");
        Analyse(Capture(1500, null), LoopbackFormat());

        Console.WriteLine();
        Console.WriteLine("=== B. stream open, nothing played ===");
        using var engine = new AudioEngine { KeepWarm = true };
        engine.Prime();
        Console.WriteLine($"  device      {engine.DeviceName ?? "(none opened)"}");
        Console.WriteLine($"  warm        {engine.IsWarm}");
        Analyse(Capture(1500, null), LoopbackFormat());

        Console.WriteLine();
        Console.WriteLine("=== C. one sound played ===");
        long readsBefore = engine.MixerForTesting.ReadCalls;
        var mixer = engine.MixerForTesting;

        string teePath = Path.Combine(Path.GetTempPath(), "chargle-live-mixer.wav");
        mixer.Tee = new WaveFileWriter(
            teePath, WaveFormat.CreateIeeeFloatWaveFormat(CachedSound.SampleRate, CachedSound.Channels));

        using var watcher = new Timer(_ =>
                Console.WriteLine(
                    $"    mixer peak {mixer.LastReadPeak:F4}, voices {mixer.ActiveVoices}, " +
                    $"error {mixer.LastReadError ?? "none"}"),
            null, 600, 600);

        float[] captured = Capture(4000, () =>
        {
            engine.Play(sound, 0.6f, Stopwatch.GetTimestamp());
            Console.WriteLine($"  dispatch    {engine.LastDispatchMicroseconds:F1} us");
        });

        watcher.Change(Timeout.Infinite, Timeout.Infinite);

        var tee = mixer.Tee;
        mixer.Tee = null;
        Thread.Sleep(100); // let any in-flight render finish before the file is closed
        tee.Dispose();
        Console.WriteLine($"  mixer tee   {teePath}");
        Analyse(ReadAll(teePath), WaveFormat.CreateIeeeFloatWaveFormat(CachedSound.SampleRate, CachedSound.Channels));

        Console.WriteLine($"  reads       {engine.MixerForTesting.ReadCalls - readsBefore} during capture");
        Analyse(captured, LoopbackFormat());
        Characterise(captured, LoopbackFormat());

        Console.WriteLine();
        Console.WriteLine("=== D. after the engine is closed entirely ===");
        engine.Dispose();
        Thread.Sleep(1000);
        float[] after = Capture(2000, null);
        Analyse(after, LoopbackFormat());
        Characterise(after, LoopbackFormat());

        return 0;
    }

    /// <summary>
    /// Describes the last second of a capture. A DC offset, a pure tone and broadband noise all
    /// read as the same RMS but are completely different bugs, and the zero-crossing rate plus
    /// the mean separate them immediately.
    /// </summary>
    private static void Characterise(float[] captured, NAudio.Wave.WaveFormat format)
    {
        int channels = format.Channels;
        int frames = captured.Length / channels;
        int start = Math.Max(0, frames - format.SampleRate);
        int count = frames - start;
        if (count < 100) { Console.WriteLine("  tail        too short to characterise"); return; }

        double sum = 0, peak = 0;
        int crossings = 0;
        float previous = 0;

        for (int f = start; f < frames; f++)
        {
            float value = captured[f * channels];
            sum += value;
            peak = Math.Max(peak, Math.Abs(value));
            if ((previous < 0 && value >= 0) || (previous >= 0 && value < 0)) crossings++;
            previous = value;
        }

        double mean = sum / count;
        double frequency = crossings / 2.0 * format.SampleRate / count;

        Console.WriteLine($"  tail mean   {mean:F6}   (a large value here means a DC offset)");
        Console.WriteLine($"  tail peak   {peak:F4}");
        Console.WriteLine($"  tail pitch  {frequency:F0} Hz from zero crossings");
    }

    /// <summary>
    /// Times how long it takes for a sound to actually reach the speakers, with the stream held
    /// open and with it released, so the cost of Instant mode is a number rather than a claim.
    ///
    /// Both figures are measured the same way, through WASAPI loopback, which adds a fixed
    /// offset of its own. That offset cancels in the comparison, so the difference between the
    /// two columns is the honest result. The absolute values are an upper bound.
    /// </summary>
    private static int Benchmark(string? path)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "chime", "plug.wav");
        if (!File.Exists(path)) { Console.WriteLine($"No such file: {path}"); return 1; }

        var sound = CachedSound.Load(path);
        const int trials = 6;

        Console.WriteLine($"Benchmarking {Path.GetFileName(path)}, {trials} trials each.");
        Console.WriteLine();

        // Part one, measured with nothing else touching the audio stack.
        //
        // This has to come first and on its own. Loopback capture holds the endpoint open, so
        // once it is running the device is never cold again and this cost becomes invisible.
        Console.WriteLine("Part 1: opening the audio device from cold, no capture running");
        var opens = new List<double>();
        for (int i = 0; i < trials; i++)
        {
            using var engine = new AudioEngine { KeepWarm = false };
            var clock = Stopwatch.StartNew();
            engine.Prime();
            double ms = clock.Elapsed.TotalMilliseconds;
            opens.Add(ms);
            Console.WriteLine($"  open {i + 1}: {ms,7:F1} ms   device {(engine.IsWarm ? "ready" : "not ready")}");
            Thread.Sleep(1200);
        }

        Console.WriteLine();
        Console.WriteLine("Part 2: time from Play to sound at the speakers, endpoint already live");
        var warm = Run(keepWarm: true);
        var cold = Run(keepWarm: false);

        Console.WriteLine();
        Console.WriteLine("  measurement                            median      mean       min       max");
        Report("Device open from cold", opens);
        Report("Play to speakers, warm", warm);
        Report("Play to speakers, cold", cold);

        Console.WriteLine();
        Console.WriteLine("  Reading this honestly:");
        Console.WriteLine($"  Instant mode avoids the {Median(opens):F0} ms device open, which is the cost that matters");
        Console.WriteLine("  when the charger is plugged in after the machine has been quiet for a while.");

        if (warm.Count > 0 && cold.Count > 0 && Median(warm) > Median(cold))
        {
            Console.WriteLine();
            Console.WriteLine($"  It is not free. Holding the stream open costs {Median(warm) - Median(cold):F0} ms, because");
            Console.WriteLine("  WASAPI has already buffered silence ahead that has to drain before the sound.");
        }

        Console.WriteLine();
        Console.WriteLine("  Both Part 2 figures include a fixed loopback capture offset, so compare them");
        Console.WriteLine("  with each other rather than treating either as an absolute.");

        return 0;

        List<double> Run(bool keepWarm)
        {
            var results = new List<double>();

            // Warm reuses one engine, which is the whole point of it. Cold builds a fresh engine
            // per trial so the device really is opened from nothing each time, rather than being
            // a warm stream that happens to have been stopped.
            AudioEngine? shared = null;
            if (keepWarm)
            {
                shared = new AudioEngine { KeepWarm = true };
                shared.Prime();
                Thread.Sleep(1500);
            }

            for (int i = 0; i < trials; i++)
            {
                // Cold is deliberately not primed. In the real path Play opens the device itself,
                // so priming here would measure everything except the cost being asked about.
                AudioEngine engine = shared ?? new AudioEngine { KeepWarm = false };
                if (shared is not null) Thread.Sleep(900);

                double? latency = MeasureOne(engine, sound);
                string label = keepWarm ? "warm" : "cold";

                if (latency is { } value)
                {
                    results.Add(value);
                    Console.WriteLine($"  {label} trial {i + 1}: {value,7:F1} ms");
                }
                else
                {
                    Console.WriteLine($"  {label} trial {i + 1}: no audio detected");
                }

                if (shared is null) engine.Dispose();
            }

            shared?.Dispose();
            return results;
        }

        static void Report(string label, List<double> values)
        {
            if (values.Count == 0) { Console.WriteLine($"  {label,-24} no data"); return; }

            Console.WriteLine(
                $"  {label,-36} {Median(values),7:F1} {values.Average(),9:F1} {values.Min(),9:F1} {values.Max(),9:F1}");
        }

        static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }

    /// <summary>
    /// One measurement: start recording, play, and find how far into the recording the sound
    /// first appears. Sample counting rather than wall clock, so the answer is not limited by
    /// how often the capture callback happens to fire.
    /// </summary>
    private static double? MeasureOne(AudioEngine engine, CachedSound sound)
    {
        using var capture = new WasapiLoopbackCapture();
        int channels = capture.WaveFormat.Channels;
        int rate = capture.WaveFormat.SampleRate;

        var samples = new List<float>(rate * channels * 3);
        long samplesAtPlay = -1;
        var gate = new object();

        capture.DataAvailable += (_, e) =>
        {
            lock (gate)
            {
                for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
                    samples.Add(BitConverter.ToSingle(e.Buffer, i));
            }
        };

        capture.StartRecording();
        Thread.Sleep(700); // let the capture stream settle so the zero point is real silence

        lock (gate) samplesAtPlay = samples.Count;
        engine.Play(sound, 0.7f);

        Thread.Sleep(2000);
        capture.StopRecording();
        Thread.Sleep(200);

        float[] captured;
        lock (gate) captured = [.. samples];

        // The first sample clearly above the noise floor after the moment Play was called.
        const float threshold = 0.002f;
        for (long i = samplesAtPlay; i < captured.Length; i++)
        {
            if (Math.Abs(captured[i]) <= threshold) continue;

            long frames = (i - samplesAtPlay) / channels;
            return frames * 1000.0 / rate;
        }

        return null;
    }

    private static float[] ReadAll(string path)
    {
        using var reader = new AudioFileReader(path);
        var samples = new float[reader.Length / 4];
        int read = reader.Read(samples, 0, samples.Length);
        return samples[..read];
    }

    private static NAudio.Wave.WaveFormat LoopbackFormat()
    {
        using var capture = new WasapiLoopbackCapture();
        return capture.WaveFormat;
    }

    /// <summary>
    /// Records the system mix, optionally doing something 400 ms in, once the capture stream
    /// has settled.
    /// </summary>
    private static float[] Capture(int milliseconds, Action? afterSettle)
    {
        using var capture = new WasapiLoopbackCapture();
        var captured = new List<float>(CachedSound.SampleRate * 8);

        capture.DataAvailable += (_, e) =>
        {
            for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
                captured.Add(BitConverter.ToSingle(e.Buffer, i));
        };

        capture.StartRecording();
        Thread.Sleep(400);
        afterSettle?.Invoke();
        Thread.Sleep(milliseconds);
        capture.StopRecording();
        Thread.Sleep(250);

        return [.. captured];
    }

    /// <summary>
    /// Splits the captured loopback into 50 ms slices and reports the level of each. A correct
    /// playback shows a burst that decays to nothing. A stuck buffer shows a level that never
    /// falls, which is exactly what "buzzing forever" would look like as numbers.
    /// </summary>
    private static void Analyse(float[] captured, NAudio.Wave.WaveFormat format)
    {
        int channels = format.Channels;
        int frames = captured.Length / channels;

        Console.WriteLine($"  format      {format.SampleRate} Hz, {channels} ch, {format.Encoding}");
        Console.WriteLine($"  captured    {frames * 1000.0 / format.SampleRate:F0} ms");

        int slice = format.SampleRate / 20; // 50 ms
        var levels = new List<double>();

        for (int start = 0; start + slice <= frames; start += slice)
        {
            double sum = 0;
            for (int f = start; f < start + slice; f++)
                for (int c = 0; c < channels; c++)
                    sum += captured[f * channels + c] * (double)captured[f * channels + c];

            levels.Add(Math.Sqrt(sum / (slice * channels)));
        }

        Console.WriteLine("  level per 50 ms slice (dBFS):");
        for (int i = 0; i < levels.Count; i += 4)
        {
            var row = levels.Skip(i).Take(4).Select(v => v < 1e-7 ? "  -inf" : $"{20 * Math.Log10(v),6:F1}");
            Console.WriteLine($"    {i * 50,5} ms  {string.Join("  ", row)}");
        }

        double tail = levels.Count >= 4 ? levels.TakeLast(4).Max() : 0;
        Console.WriteLine();
        Console.WriteLine(tail < 1e-5
            ? "  VERDICT     output returned to silence. Playback is clean."
            : $"  VERDICT     still at {20 * Math.Log10(tail):F1} dBFS at the end. Something is stuck.");
    }

    /// <summary>
    /// Pumps the engine's own mixer by hand and writes the result to a wav. If this file sounds
    /// correct, the mixer is innocent and the problem is in the WASAPI layer beneath it.
    /// </summary>
    private static void RenderOffline(CachedSound sound)
    {
        var engine = new AudioEngine();
        var mixer = engine.MixerForTesting;

        mixer.Trigger(sound.Samples, 0.8f);

        int frames = (int)(CachedSound.SampleRate * 3.0);
        var buffer = new float[480 * CachedSound.Channels];
        string output = Path.Combine(Path.GetTempPath(), "chargle-mixer-render.wav");

        using (var writer = new WaveFileWriter(
                   output, WaveFormat.CreateIeeeFloatWaveFormat(CachedSound.SampleRate, CachedSound.Channels)))
        {
            int written = 0;
            while (written < frames * CachedSound.Channels)
            {
                int read = mixer.Read(buffer, 0, buffer.Length);
                writer.WriteSamples(buffer, 0, read);
                written += read;
            }
        }

        // Report what actually came out, so a wrong answer is visible without listening.
        using var reader = new AudioFileReader(output);
        var check = new float[reader.Length / 4];
        int total = reader.Read(check, 0, check.Length);

        float peak = 0;
        int lastNonZero = -1;
        for (int i = 0; i < total; i++)
        {
            peak = Math.Max(peak, Math.Abs(check[i]));
            if (Math.Abs(check[i]) > 1e-5f) lastNonZero = i;
        }

        Console.WriteLine($"  wrote       {output}");
        Console.WriteLine($"  peak        {peak:F4}");
        Console.WriteLine($"  silent from {(lastNonZero < 0 ? 0 : lastNonZero / 2 * 1000.0 / CachedSound.SampleRate):F0} ms");
        Console.WriteLine($"  expected    {sound.Duration.TotalMilliseconds:F0} ms");

        engine.Dispose();
    }
}
