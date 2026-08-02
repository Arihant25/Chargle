namespace Chargle.SoundForge;

/// <summary>A stereo float buffer that everything in the forge renders into.</summary>
public sealed class AudioBuffer
{
    public const int SampleRate = 48_000; // matches the app's mixer, so nothing resamples at play time

    public readonly float[] L;
    public readonly float[] R;

    public AudioBuffer(double seconds)
    {
        Length = (int)Math.Round(seconds * SampleRate);
        L = new float[Length];
        R = new float[Length];
    }

    public int Length { get; }

    public double Duration => (double)Length / SampleRate;

    public int SampleAt(double seconds) => (int)Math.Round(seconds * SampleRate);

    public void Add(int index, double left, double right)
    {
        if ((uint)index >= (uint)Length) return;
        L[index] += (float)left;
        R[index] += (float)right;
    }

    public double Peak
    {
        get
        {
            double peak = 0;
            for (int i = 0; i < Length; i++)
                peak = Math.Max(peak, Math.Max(Math.Abs(L[i]), Math.Abs(R[i])));
            return peak;
        }
    }

    /// <summary>
    /// The loudest 100 ms of the sound. Plain full-length RMS is the wrong measure here: Glass is
    /// mostly a quiet tail and Tick is over in a twentieth of a second, so averaging across the
    /// whole buffer would make the short sounds look loud and the long ones look quiet, which is
    /// the opposite of how they are heard.
    /// </summary>
    public double ShortTermRms(double windowMs = 100)
    {
        int window = Math.Min(SampleAt(windowMs / 1000.0), Length);
        if (window <= 0) return 0;

        double sum = 0;
        for (int i = 0; i < window; i++)
            sum += Energy(i);

        double best = sum;
        for (int i = window; i < Length; i++)
        {
            sum += Energy(i) - Energy(i - window);
            best = Math.Max(best, sum);
        }

        return Math.Sqrt(Math.Max(best, 0) / window);

        double Energy(int i) => (L[i] * (double)L[i] + R[i] * (double)R[i]) * 0.5;
    }

    /// <summary>
    /// Matches every pack to the same perceived loudness, then backs off if that would push the
    /// peak too high. Without this, the square-wave pack is dramatically louder than the mallet
    /// one at the same peak level, and changing sound would double as changing volume.
    /// </summary>
    public void NormalizeLoudness(double targetRmsDbfs = -20.0, double peakCeilingDbfs = -3.0)
    {
        double rms = ShortTermRms();
        double peak = Peak;
        if (rms < 1e-9 || peak < 1e-9) return;

        double gain = Math.Pow(10, targetRmsDbfs / 20.0) / rms;

        double ceiling = Math.Pow(10, peakCeilingDbfs / 20.0);
        if (peak * gain > ceiling) gain = ceiling / peak;

        for (int i = 0; i < Length; i++)
        {
            L[i] = (float)(L[i] * gain);
            R[i] = (float)(R[i] * gain);
        }
    }

    /// <summary>
    /// Ramps the very start and end to zero. Without this you hear a click, not a chime.
    /// The fade-in is deliberately tiny: a percussive sound is almost entirely its attack, and a
    /// couple of milliseconds of ramp is enough to remove the click while audibly blunting it.
    /// </summary>
    public void FadeEdges(double fadeInMs = 0.5, double fadeOutMs = 12.0)
    {
        int fin = Math.Min(SampleAt(fadeInMs / 1000.0), Length);
        for (int i = 0; i < fin; i++)
        {
            double g = (double)i / fin;
            L[i] = (float)(L[i] * g);
            R[i] = (float)(R[i] * g);
        }

        int fout = Math.Min(SampleAt(fadeOutMs / 1000.0), Length);
        for (int i = 0; i < fout; i++)
        {
            int idx = Length - 1 - i;
            double g = (double)i / fout;
            g = g * g * (3 - 2 * g); // smoothstep
            L[idx] = (float)(L[idx] * g);
            R[idx] = (float)(R[idx] * g);
        }
    }

    /// <summary>
    /// One-pole low-pass. Takes the glare off the top end. It is the difference between a sound that
    /// feels designed and one that feels like a synthesiser left on a default patch.
    /// </summary>
    public void LowPass(double cutoffHz)
    {
        double dt = 1.0 / SampleRate;
        double rc = 1.0 / (2 * Math.PI * cutoffHz);
        double alpha = dt / (rc + dt);

        double l = 0, r = 0;
        for (int i = 0; i < Length; i++)
        {
            l += alpha * (L[i] - l);
            r += alpha * (R[i] - r);
            L[i] = (float)l;
            R[i] = (float)r;
        }
    }

    /// <summary>
    /// A Schroeder reverb: parallel comb filters for density, then allpasses to smear the result
    /// into something that stops sounding like distinct echoes.
    ///
    /// This is what "grand" actually is. Pitch and harmonics decide what the sound is made of,
    /// but the impression of size comes almost entirely from a tail that arrives slightly late
    /// and slightly differently in each ear. The left and right delay lines are deliberately
    /// mismatched by a prime number of samples; matched ones collapse to a mono blur.
    /// </summary>
    /// <param name="mix">0 is dry, 1 is all tail. Around 0.3 reads as a large room.</param>
    /// <param name="size">Feedback, 0 to just under 1. Higher rings for longer.</param>
    /// <param name="damping">0 to 1. How fast the high frequencies die away inside the tail.</param>
    public void Reverb(double mix, double size = 0.72, double damping = 0.35)
    {
        // Freeverb's tuning, rescaled from its original 44.1 kHz to our 48 kHz.
        const double rescale = SampleRate / 44100.0;
        int[] combs = [1116, 1188, 1277, 1356];
        int[] allpasses = [556, 441];
        const int stereoSpread = 23;

        float[] wetL = Process(L, 0);
        float[] wetR = Process(R, stereoSpread);

        double dry = 1 - mix;
        for (int i = 0; i < Length; i++)
        {
            L[i] = (float)(L[i] * dry + wetL[i] * mix);
            R[i] = (float)(R[i] * dry + wetR[i] * mix);
        }

        float[] Process(float[] input, int spread)
        {
            var output = new float[Length];

            foreach (int delay in combs)
            {
                int d = (int)(delay * rescale) + spread;
                var line = new float[d];
                int index = 0;
                double filterStore = 0;

                for (int i = 0; i < Length; i++)
                {
                    double delayed = line[index];
                    output[i] += (float)delayed;

                    // One-pole lowpass inside the feedback loop: without it the tail stays
                    // bright forever and sounds metallic rather than large.
                    filterStore = delayed * (1 - damping) + filterStore * damping;
                    line[index] = (float)(input[i] + filterStore * size);

                    if (++index >= d) index = 0;
                }
            }

            for (int i = 0; i < Length; i++) output[i] /= combs.Length;

            foreach (int delay in allpasses)
            {
                int d = (int)(delay * rescale) + spread;
                var line = new float[d];
                int index = 0;

                for (int i = 0; i < Length; i++)
                {
                    double buffered = line[index];
                    double value = output[i];
                    output[i] = (float)(buffered - value);
                    line[index] = (float)(value + buffered * 0.5);

                    if (++index >= d) index = 0;
                }
            }

            return output;
        }
    }

    /// <summary>Gentle tanh saturation. Rounds off transients the way a real resonator would.</summary>
    public void Saturate(double drive)
    {
        if (drive <= 0) return;
        double norm = Math.Tanh(drive);
        for (int i = 0; i < Length; i++)
        {
            L[i] = (float)(Math.Tanh(L[i] * drive) / norm);
            R[i] = (float)(Math.Tanh(R[i] * drive) / norm);
        }
    }
}

/// <summary>Minimal RIFF/WAVE writer: 16-bit PCM, stereo, 48 kHz.</summary>
public static class Wav
{
    public static void Write(string path, AudioBuffer buffer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const int channels = 2;
        const int bitsPerSample = 16;
        int dataBytes = buffer.Length * channels * (bitsPerSample / 8);

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);

        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);

        w.Write("fmt "u8);
        w.Write(16);                                    // PCM chunk size
        w.Write((short)1);                              // PCM
        w.Write((short)channels);
        w.Write(AudioBuffer.SampleRate);
        w.Write(AudioBuffer.SampleRate * channels * bitsPerSample / 8); // byte rate
        w.Write((short)(channels * bitsPerSample / 8)); // block align
        w.Write((short)bitsPerSample);

        w.Write("data"u8);
        w.Write(dataBytes);

        for (int i = 0; i < buffer.Length; i++)
        {
            w.Write(ToPcm16(buffer.L[i]));
            w.Write(ToPcm16(buffer.R[i]));
        }
    }

    private static short ToPcm16(float sample)
    {
        double clamped = Math.Clamp(sample, -1.0, 1.0);
        return (short)Math.Round(clamped * 32767.0);
    }
}
