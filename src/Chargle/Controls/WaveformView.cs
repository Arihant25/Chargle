using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

// Implicit usings pull in System.IO, which also has a Path. This one is the shape.
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Chargle.Controls;

/// <summary>
/// Draws the actual waveform of a sound.
///
/// This could have been a generic speaker glyph, and it would have taken five minutes. Drawing
/// the real samples means the list of sounds shows you the difference between them before you
/// play anything: Tick is a single spike, Swell is a slow hill, Red Fruit has a visible tail.
/// The picture is true, which is the only reason it is worth having.
/// </summary>
public sealed class WaveformView : ContentControl
{
    /// <summary>Columns to draw. Enough to show the shape, few enough to stay cheap to re-render.</summary>
    private const int Resolution = 96;

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(float[]), typeof(WaveformView),
        new PropertyMetadata(null, (d, _) => ((WaveformView)d).Rebuild()));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(WaveformView),
        new PropertyMetadata(null, (d, _) => ((WaveformView)d).Rebuild()));

    private readonly Path _path = new();
    private float[]? _envelope;

    public WaveformView()
    {
        IsTabStop = false;

        // Purely decorative. The pack's name and description already say everything a screen
        // reader needs, so announcing a picture of a waveform as well is just noise in the way.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = _path;
        SizeChanged += (_, _) => Rebuild();
    }

    /// <summary>Interleaved stereo samples, exactly as the audio engine holds them.</summary>
    public float[]? Samples
    {
        get => (float[]?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private void Rebuild()
    {
        float[]? samples = Samples;
        double width = ActualWidth;
        double height = ActualHeight;

        if (samples is null || samples.Length == 0 || width <= 1 || height <= 1)
        {
            _path.Data = null;
            return;
        }

        _envelope = BuildEnvelope(samples);

        // A filled mirror of the peak envelope: louder moments are taller, and the sound reads
        // left to right the way it is heard.
        var figure = new PathFigure { StartPoint = new Point(0, height / 2), IsClosed = true, IsFilled = true };
        double mid = height / 2;
        double columnWidth = width / Resolution;

        for (int i = 0; i < Resolution; i++)
        {
            double x = i * columnWidth;
            double y = mid - _envelope[i] * mid * 0.92;
            figure.Segments.Add(new LineSegment { Point = new Point(x, y) });
        }

        for (int i = Resolution - 1; i >= 0; i--)
        {
            double x = i * columnWidth;
            double y = mid + _envelope[i] * mid * 0.92;
            figure.Segments.Add(new LineSegment { Point = new Point(x, y) });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        _path.Data = geometry;
        _path.Fill = Stroke ?? Foreground;
    }

    /// <summary>
    /// Peak per column rather than average. An average would flatten every percussive sound into
    /// the same low mound, which is exactly the information worth keeping.
    /// </summary>
    private static float[] BuildEnvelope(float[] samples)
    {
        var envelope = new float[Resolution];
        int frames = samples.Length / 2;
        if (frames == 0) return envelope;

        float peak = 0;

        for (int column = 0; column < Resolution; column++)
        {
            int start = (int)((long)column * frames / Resolution);
            int end = (int)((long)(column + 1) * frames / Resolution);
            if (end <= start) end = Math.Min(start + 1, frames);

            float columnPeak = 0;
            for (int frame = start; frame < end; frame++)
            {
                float l = Math.Abs(samples[frame * 2]);
                float r = Math.Abs(samples[frame * 2 + 1]);
                columnPeak = Math.Max(columnPeak, Math.Max(l, r));
            }

            envelope[column] = columnPeak;
            peak = Math.Max(peak, columnPeak);
        }

        // Normalise per sound so quiet packs are still legible. This is a picture of shape, not
        // of level; the level is already matched across every pack anyway.
        if (peak > 1e-6)
        {
            for (int i = 0; i < Resolution; i++) envelope[i] /= peak;
        }

        return envelope;
    }
}
