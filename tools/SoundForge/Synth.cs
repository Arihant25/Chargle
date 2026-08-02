namespace Chargle.SoundForge;

/// <summary>
/// One component of a struck tone. <paramref name="Ratio"/> is a multiple of the fundamental.
/// Real resonators are inharmonic, which is exactly why a bell sounds like a bell and a sine
/// sounds like a test tone. <paramref name="T60Scale"/> lets high partials die away first, the
/// single most important detail in making additive synthesis sound struck rather than played.
/// </summary>
public readonly record struct Partial(double Ratio, double Amp, double T60Scale = 1.0);

public static class Synth
{
    /// <summary>Bell or handbell: inharmonic, long shimmering tail.</summary>
    public static readonly Partial[] Bell =
    [
        new(1.00, 1.00, 1.00),
        new(2.76, 0.42, 0.62),
        new(5.40, 0.22, 0.40),
        new(8.93, 0.11, 0.26),
        new(13.34, 0.05, 0.17),
    ];

    /// <summary>Marimba: bars are tuned so partials land near 1 : 4 : 10. Hollow and woody.</summary>
    public static readonly Partial[] Marimba =
    [
        new(1.00, 1.00, 1.00),
        new(3.94, 0.34, 0.42),
        new(9.20, 0.11, 0.22),
        new(15.10, 0.04, 0.14),
    ];

    /// <summary>Soft tuned mallet: mostly harmonic with a touch of stretch. The friendly default.</summary>
    public static readonly Partial[] Mallet =
    [
        new(1.00, 1.00, 1.00),
        new(2.01, 0.30, 0.55),
        new(3.02, 0.13, 0.36),
        new(4.17, 0.06, 0.25),
        new(5.44, 0.03, 0.18),
    ];

    /// <summary>
    /// Rounded and slightly inharmonic, with the top partials gone almost immediately. This is
    /// the shape behind the soft glassy "bonk" that laptop makers reach for when a cable lands.
    /// </summary>
    public static readonly Partial[] Plink =
    [
        new(1.00, 1.00, 1.00),
        new(2.02, 0.30, 0.50),
        new(3.46, 0.12, 0.30),
        new(5.15, 0.05, 0.20),
    ];

    /// <summary>
    /// Nearly a pure tone, with one quiet odd partial for sparkle. Clean rather than warm, which
    /// is the house style of phone interface sounds.
    /// </summary>
    public static readonly Partial[] Glocken =
    [
        new(1.00, 1.00, 1.00),
        new(3.00, 0.16, 0.30),
        new(5.90, 0.04, 0.18),
    ];

    /// <summary>Warm stacked harmonics with a slow attack, a pad rather than a strike.</summary>
    public static readonly Partial[] Warm =
    [
        new(1.00, 1.00, 1.00),
        new(2.00, 0.45, 0.95),
        new(3.00, 0.20, 0.85),
        new(4.00, 0.10, 0.75),
        new(5.00, 0.05, 0.65),
        new(6.00, 0.03, 0.55),
    ];

    /// <summary>
    /// Renders an additive struck tone. <paramref name="t60"/> is the time in seconds for the
    /// fundamental to fall 60 dB; <paramref name="attackMs"/> rounds off the onset so it reads as
    /// a strike instead of a click.
    /// </summary>
    public static void Strike(
        AudioBuffer buf,
        double atSeconds,
        double frequency,
        Partial[] partials,
        double t60,
        double gain = 0.5,
        double attackMs = 2.0,
        double pan = 0.0,
        double detuneCents = 0.0)
    {
        int start = buf.SampleAt(atSeconds);
        double attackTau = Math.Max(attackMs, 0.05) / 1000.0;
        (double gl, double gr) = Pan(pan);

        foreach (var p in partials)
        {
            double freq = frequency * p.Ratio;
            if (freq >= AudioBuffer.SampleRate / 2.0) continue; // never write above Nyquist

            // A hair of detune per partial keeps the tone from sounding mathematically dead.
            double detuned = freq * Math.Pow(2, detuneCents / 1200.0);
            double decay = 6.9078 / Math.Max(t60 * p.T60Scale, 1e-4); // ln(1000) / T60
            double omega = 2 * Math.PI * detuned / AudioBuffer.SampleRate;
            // A fixed, ratio-derived phase offset stops every partial from spiking on the same
            // sample. Derived rather than random so two builds produce byte-identical WAVs.
            double phase = (p.Ratio * 0.61803398875 % 1.0) * 0.02;

            for (int i = start; i < buf.Length; i++)
            {
                double t = (double)(i - start) / AudioBuffer.SampleRate;
                double env = (1 - Math.Exp(-t / attackTau)) * Math.Exp(-t * decay);
                if (env < 1e-5 && t > attackTau * 4) break; // partial is inaudible from here on

                double s = Math.Sin(omega * (i - start) + phase) * env * p.Amp * gain;
                buf.Add(i, s * gl, s * gr);
            }
        }
    }

    /// <summary>
    /// A band-passed noise burst. This is the sound of the mallet head itself hitting the bar.
    /// A few milliseconds of it is the difference between "struck object" and "synthesiser".
    /// </summary>
    public static void Transient(
        AudioBuffer buf,
        double atSeconds,
        double centreHz,
        double t60,
        double gain = 0.15,
        double q = 2.0,
        double pan = 0.0)
    {
        int start = buf.SampleAt(atSeconds);
        double decay = 6.9078 / Math.Max(t60, 1e-4);
        (double gl, double gr) = Pan(pan);

        var filter = new StateVariableFilter(centreHz, q);
        var rng = new Random(start + (int)centreHz); // seeded, so the forge is reproducible

        for (int i = start; i < buf.Length; i++)
        {
            double t = (double)(i - start) / AudioBuffer.SampleRate;
            double env = Math.Exp(-t * decay);
            if (env < 1e-4) break;

            double noise = rng.NextDouble() * 2 - 1;
            double s = filter.ProcessBandPass(noise) * env * gain;
            buf.Add(i, s * gl, s * gr);
        }
    }

    /// <summary>
    /// A sine that glides between two pitches. Used for the small tactile packs, where the
    /// direction of the glide *is* the message: up means connected, down means unplugged.
    /// </summary>
    public static void Glide(
        AudioBuffer buf,
        double atSeconds,
        double fromHz,
        double toHz,
        double glideSeconds,
        double t60,
        double gain = 0.5,
        double attackMs = 1.0,
        double pan = 0.0)
    {
        int start = buf.SampleAt(atSeconds);
        double decay = 6.9078 / Math.Max(t60, 1e-4);
        double attackTau = Math.Max(attackMs, 0.05) / 1000.0;
        (double gl, double gr) = Pan(pan);
        double phase = 0;

        for (int i = start; i < buf.Length; i++)
        {
            double t = (double)(i - start) / AudioBuffer.SampleRate;
            double env = (1 - Math.Exp(-t / attackTau)) * Math.Exp(-t * decay);
            if (env < 1e-5 && t > attackTau * 4) break;

            // Exponential glide: pitch is perceived logarithmically, so interpolate in log space.
            double k = glideSeconds <= 0 ? 1 : Math.Min(t / glideSeconds, 1.0);
            k = k * k * (3 - 2 * k); // ease the glide so it doesn't sound like a siren
            double freq = fromHz * Math.Pow(toHz / fromHz, k);

            phase += 2 * Math.PI * freq / AudioBuffer.SampleRate;
            double s = Math.Sin(phase) * env * gain;
            buf.Add(i, s * gl, s * gr);
        }
    }

    /// <summary>A band-limited square wave. Additive, so it never aliases.</summary>
    public static void Square(
        AudioBuffer buf,
        double atSeconds,
        double frequency,
        double durationSeconds,
        double gain = 0.35,
        double pan = 0.0)
    {
        int start = buf.SampleAt(atSeconds);
        int end = Math.Min(start + buf.SampleAt(durationSeconds), buf.Length);
        (double gl, double gr) = Pan(pan);

        // Odd harmonics only, stopping below Nyquist.
        int maxHarmonic = (int)(AudioBuffer.SampleRate / 2.0 / frequency);

        for (int i = start; i < end; i++)
        {
            double t = (double)(i - start) / AudioBuffer.SampleRate;
            double local = (double)(i - start) / (end - start);

            // Hard-ish gate with tiny ramps: the charm of a chip blip is that it just stops.
            double env = Math.Min(local / 0.04, 1.0) * Math.Min((1 - local) / 0.12, 1.0);
            env = Math.Clamp(env, 0, 1);

            double s = 0;
            for (int h = 1; h <= maxHarmonic; h += 2)
                s += Math.Sin(2 * Math.PI * frequency * h * t) / h;

            s *= 4 / Math.PI * env * gain * 0.25;
            buf.Add(i, s * gl, s * gr);
        }
    }

    /// <summary>Equal-power pan. -1 is hard left, +1 hard right.</summary>
    private static (double L, double R) Pan(double pan)
    {
        double p = (Math.Clamp(pan, -1, 1) + 1) * 0.25 * Math.PI; // 0..pi/2
        return (Math.Cos(p), Math.Sin(p));
    }

    /// <summary>Chamberlin state-variable filter: cheap, stable, and good enough for a noise burst.</summary>
    private sealed class StateVariableFilter(double centreHz, double q)
    {
        private readonly double _f = 2 * Math.Sin(Math.PI * Math.Min(centreHz, AudioBuffer.SampleRate / 6.0) / AudioBuffer.SampleRate);
        private readonly double _q = 1.0 / Math.Max(q, 0.5);
        private double _low, _band;

        public double ProcessBandPass(double input)
        {
            double high = input - _low - _q * _band;
            _band += _f * high;
            _low += _f * _band;
            return _band;
        }
    }
}

/// <summary>Note names to frequencies, so the pack definitions read musically.</summary>
public static class Note
{
    // Declared first: the fields below run their initialisers in source order.
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static readonly double A4 = From("A4"), C5 = From("C5"), E5 = From("E5"),
        G5 = From("G5"), A5 = From("A5"), B5 = From("B5"),
        C6 = From("C6"), E6 = From("E6"), C7 = From("C7");

    /// <summary>"E5" -> 659.26 Hz, via equal temperament with A4 = 440.</summary>
    public static double From(string name)
    {
        int split = name.Length - 1;
        string pitch = name[..split];
        int octave = int.Parse(name[split..]);

        int semitone = Array.IndexOf(Names, pitch);
        if (semitone < 0) throw new ArgumentException($"Unknown note '{name}'", nameof(name));

        int midi = (octave + 1) * 12 + semitone;
        return 440.0 * Math.Pow(2, (midi - 69) / 12.0);
    }
}
