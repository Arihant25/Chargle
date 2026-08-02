using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Chargle.Services;

/// <summary>
/// A sound decoded all the way down to raw interleaved floats, at exactly the rate and channel
/// count the output stream wants.
///
/// This is half the reason Chargle is instant. Playing a sound "from a file" means opening it,
/// parsing headers, allocating buffers and possibly resampling, which is tens of milliseconds of work,
/// on the wrong side of the event. Doing all of it once at startup means that when the cable
/// actually moves, playback is a pointer and an index.
/// </summary>
public sealed class CachedSound
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    private CachedSound(float[] samples, string source)
    {
        Samples = samples;
        Source = source;
    }

    /// <summary>Interleaved L,R at 48 kHz.</summary>
    public float[] Samples { get; }

    public string Source { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / Channels / SampleRate);

    public float PeakAmplitude { get; private init; }

    /// <summary>Decodes any format NAudio can open (wav, mp3, m4a, wma, aiff, flac via MF).</summary>
    public static CachedSound Load(string path)
    {
        using var reader = new AudioFileReader(path);

        ISampleProvider provider = reader;

        if (provider.WaveFormat.SampleRate != SampleRate)
        {
            // WDL's resampler is fully managed, so it behaves the same on every machine and
            // inside the MSIX sandbox, unlike the Media Foundation one.
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }

        provider = provider.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(provider),
            2 => provider,
            // Surround source: just take the front pair rather than fold it down.
            _ => new MultiplexingSampleProvider([provider], Channels),
        };

        var buffer = new List<float>(SampleRate * Channels);
        var chunk = new float[SampleRate * Channels / 4];
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.AddRange(chunk.AsSpan(0, read));

            // A stuck or absurdly long file must not eat all available memory.
            if (buffer.Count > SampleRate * Channels * 30) break;
        }

        float[] samples = [.. buffer];
        float peak = 0;
        foreach (float s in samples) peak = Math.Max(peak, Math.Abs(s));

        return new CachedSound(samples, path) { PeakAmplitude = peak };
    }
}
