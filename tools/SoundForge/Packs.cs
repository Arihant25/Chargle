namespace Chargle.SoundForge;

public sealed record Pack(
    string Id,
    string Name,
    string Description,
    Func<AudioBuffer> Plug,
    Func<AudioBuffer> Unplug);

/// <summary>
/// The built-in sound packs. Every one of them is a pair, and every pair moves in opposite
/// directions: connecting rises, disconnecting falls. You should be able to tell which happened
/// from the next room, without having learned anything.
///
/// Every pack finishes with the same two calls, in the same order: fade the edges first, then
/// match loudness. Doing it the other way round lets the fade eat the peak of the very short
/// sounds and quietly undo the levelling.
/// </summary>
public static class Packs
{
    public static readonly Pack[] All =
    [
        Chime(), RedFruit(), Droplet(), Glass(), Swell(), Pebble(), Tick(), Blip(),
    ];

    // ---------------------------------------------------------------- Chime

    private static Pack Chime() => new(
        "chime",
        "Chime",
        "A soft mallet struck twice. Warm, short, and hard to get tired of. This is the default.",
        () => TwoNoteMallet(Note.E5, Note.B5),
        () => TwoNoteMallet(Note.B5, Note.E5));

    private static AudioBuffer TwoNoteMallet(double first, double second)
    {
        var buf = new AudioBuffer(0.85);
        const double gap = 0.075;

        Synth.Strike(buf, 0.0, first, Synth.Mallet, t60: 0.55, gain: 0.42, attackMs: 2.0, pan: -0.10);
        Synth.Transient(buf, 0.0, first * 3.2, t60: 0.006, gain: 0.10, q: 1.6, pan: -0.10);

        Synth.Strike(buf, gap, second, Synth.Mallet, t60: 0.50, gain: 0.38, attackMs: 2.0, pan: 0.10);
        Synth.Transient(buf, gap, second * 3.2, t60: 0.006, gain: 0.09, q: 1.6, pan: 0.10);

        buf.Saturate(1.4);
        buf.FadeEdges();
        buf.NormalizeLoudness();
        return buf;
    }

    // ------------------------------------------------------------ Red Fruit

    private static Pack RedFruit() => new(
        "red-fruit",
        "Red Fruit",
        "A softly struck tine with a wide room behind it. The most ceremonious sound here.",
        // A5 down a perfect fourth to E5. The interval matters more than the pitches: a fourth
        // is wide enough to be unmistakable and narrow enough not to sound like an alarm.
        () => Grand(Note.A5, t60: 0.95, seconds: 2.0),
        () => Grand(Note.E5, t60: 0.80, seconds: 1.8));

    /// <summary>
    /// Grandness is made of three things stacked in order of how consciously you notice them.
    ///
    /// The tine is the only part you would describe if asked: a very short, very bright metallic
    /// ping, gone in seventy milliseconds, which is what makes the sound read as struck metal
    /// rather than as a synthesiser.
    ///
    /// Underneath it, an octave below and a fifth and octave above turn a single note into a
    /// chord. That is the difference between a sound that is loud and one that is large.
    ///
    /// And behind all of it, a reverb tail. Nobody hears the reverb; everybody hears the room.
    /// </summary>
    private static AudioBuffer Grand(double f0, double t60, double seconds)
    {
        var buf = new AudioBuffer(seconds);

        // Body, doubled and detuned so it beats slightly instead of sitting still. The 4.5 ms
        // attack is most of what makes this feel soft: the ear reads a rounded onset as a soft
        // object and a sharp one as a hard one, well before it gets around to judging brightness.
        Synth.Strike(buf, 0.0, f0, Synth.Mallet, t60: t60, gain: 0.38, attackMs: 4.5, pan: -0.12);
        Synth.Strike(buf, 0.0, f0, Synth.Mallet, t60: t60 * 0.94, gain: 0.16, attackMs: 5.4, pan: 0.12, detuneCents: 7);

        // The tine: brief, bright, and the whole identity of the sound. Kept very quiet, because
        // at full strength it is also the only harsh thing here.
        Synth.Strike(buf, 0.0, f0 * 6.5, Synth.Glocken, t60: 0.05, gain: 0.06, attackMs: 1.6);

        // Weight below, air above. Both entering a few milliseconds late so the chord blooms
        // after the strike rather than arriving with it.
        Synth.Strike(buf, 0.000, f0 * 0.5, Synth.Mallet, t60: t60 * 0.55, gain: 0.18, attackMs: 5.4);
        Synth.Strike(buf, 0.012, f0 * 1.5, Synth.Bell, t60: t60 * 0.80, gain: 0.11, attackMs: 3.6, pan: 0.22);
        Synth.Strike(buf, 0.020, f0 * 2.0, Synth.Bell, t60: t60 * 0.65, gain: 0.07, attackMs: 3.2, pan: -0.22);

        // A 5.2 kHz ceiling is low enough that nothing here can ever get sharp, and the reverb
        // is damped hard so the tail darkens as it goes rather than hissing.
        buf.LowPass(5200);
        buf.Reverb(mix: 0.32, size: 0.78, damping: 0.42);
        buf.FadeEdges(fadeInMs: 1.0, fadeOutMs: 140);
        buf.NormalizeLoudness();
        return buf;
    }

    // -------------------------------------------------------------- Droplet

    private static Pack Droplet() => new(
        "droplet",
        "Droplet",
        "A single drop of water. Sweeps up when the cable lands and down when it leaves.",
        () => Drop(420, 980),
        () => Drop(980, 420));

    /// <summary>
    /// A very fast pitch sweep through a narrow resonance, which is close to what a real droplet
    /// is: a small cavity changing shape faster than you can hear it as pitch. Short, wet, and
    /// completely unlike anything else in the set.
    /// </summary>
    private static AudioBuffer Drop(double from, double to)
    {
        var buf = new AudioBuffer(0.30);

        Synth.Glide(buf, 0.0, from, to, glideSeconds: 0.035, t60: 0.13, gain: 0.55, attackMs: 1.5);
        Synth.Glide(buf, 0.006, from * 1.5, to * 1.5, glideSeconds: 0.035, t60: 0.08, gain: 0.20, attackMs: 1.5);
        Synth.Transient(buf, 0.0, to * 1.2, t60: 0.004, gain: 0.08, q: 3.0);

        buf.LowPass(6000);
        buf.Saturate(1.5);
        buf.FadeEdges(fadeOutMs: 20);
        buf.NormalizeLoudness();
        return buf;
    }

    // ---------------------------------------------------------------- Glass

    private static Pack Glass() => new(
        "glass",
        "Glass",
        "Struck crystal, with the long inharmonic shimmer a real bell leaves behind.",
        () => TwoNoteBell(Note.A5, Note.E6),
        () => TwoNoteBell(Note.E6, Note.A5));

    private static AudioBuffer TwoNoteBell(double first, double second)
    {
        var buf = new AudioBuffer(1.5);
        const double gap = 0.09;

        Synth.Strike(buf, 0.0, first, Synth.Bell, t60: 1.10, gain: 0.30, attackMs: 1.2, pan: -0.18);
        Synth.Transient(buf, 0.0, first * 5.0, t60: 0.004, gain: 0.07, q: 1.2, pan: -0.18);

        Synth.Strike(buf, gap, second, Synth.Bell, t60: 1.00, gain: 0.26, attackMs: 1.2, pan: 0.18);
        Synth.Transient(buf, gap, second * 5.0, t60: 0.004, gain: 0.06, q: 1.2, pan: 0.18);

        buf.Saturate(1.2);
        buf.FadeEdges(fadeOutMs: 60);
        buf.NormalizeLoudness();
        return buf;
    }

    // ---------------------------------------------------------------- Swell

    private static Pack Swell() => new(
        "swell",
        "Swell",
        "A warm synth pad that fades up rather than hitting. For people who dislike being pinged.",
        () => TwoNotePad(Note.A4, Note.E5),
        () => TwoNotePad(Note.E5, Note.A4));

    private static AudioBuffer TwoNotePad(double first, double second)
    {
        var buf = new AudioBuffer(1.35);
        const double gap = 0.12;

        // Two slightly detuned layers per note: the beating between them is the whole warmth.
        foreach (double cents in new[] { -5.0, 5.0 })
        {
            double pan = cents < 0 ? -0.30 : 0.30;
            Synth.Strike(buf, 0.0, first, Synth.Warm, t60: 0.85, gain: 0.20, attackMs: 90, pan: pan, detuneCents: cents);
            Synth.Strike(buf, gap, second, Synth.Warm, t60: 0.90, gain: 0.20, attackMs: 110, pan: -pan, detuneCents: cents);
        }

        buf.Saturate(1.8);
        buf.FadeEdges(fadeInMs: 4, fadeOutMs: 80);
        buf.NormalizeLoudness();
        return buf;
    }

    // --------------------------------------------------------------- Pebble

    private static Pack Pebble() => new(
        "pebble",
        "Pebble",
        "A dry wooden tap with a tiny pitch bend. Nearly silent, but you feel it land.",
        () => WoodTap(300, 430),
        () => WoodTap(430, 300));

    private static AudioBuffer WoodTap(double from, double to)
    {
        var buf = new AudioBuffer(0.22);

        Synth.Transient(buf, 0.0, 1800, t60: 0.005, gain: 0.30, q: 1.0);
        Synth.Strike(buf, 0.001, (from + to) / 2, Synth.Marimba, t60: 0.09, gain: 0.32, attackMs: 0.8);
        Synth.Glide(buf, 0.0, from, to, glideSeconds: 0.035, t60: 0.10, gain: 0.30, attackMs: 0.6);

        buf.Saturate(1.6);
        buf.FadeEdges(fadeOutMs: 20);
        buf.NormalizeLoudness();
        return buf;
    }

    // ----------------------------------------------------------------- Tick

    private static Pack Tick() => new(
        "tick",
        "Tick",
        "One frame of sound. The least this app can possibly say while still saying it.",
        () => Click(2600, Note.C7),
        () => Click(1700, Note.G5));

    private static AudioBuffer Click(double noiseHz, double pingHz)
    {
        var buf = new AudioBuffer(0.09);

        Synth.Transient(buf, 0.0, noiseHz, t60: 0.004, gain: 0.45, q: 1.4);
        Synth.Strike(buf, 0.0, pingHz, Synth.Mallet, t60: 0.030, gain: 0.16, attackMs: 0.5);

        // Almost all of this sound happens in its first two milliseconds, so the fade-in has to
        // be shorter still or there is nothing left to hear.
        buf.FadeEdges(fadeInMs: 0.2, fadeOutMs: 8);
        buf.NormalizeLoudness();
        return buf;
    }

    // ----------------------------------------------------------------- Blip

    private static Pack Blip() => new(
        "blip",
        "Blip",
        "Three square waves in a row, the way a machine would have told you in 1989.",
        () => Arpeggio([Note.C5, Note.G5, Note.C6]),
        () => Arpeggio([Note.C6, Note.G5, Note.C5]));

    private static AudioBuffer Arpeggio(double[] notes)
    {
        var buf = new AudioBuffer(0.24);
        double t = 0;

        for (int i = 0; i < notes.Length; i++)
        {
            bool last = i == notes.Length - 1;
            double dur = last ? 0.085 : 0.045;
            Synth.Square(buf, t, notes[i], dur, gain: 0.40);
            t += dur;
        }

        buf.FadeEdges(fadeOutMs: 10);
        buf.NormalizeLoudness();
        return buf;
    }
}
