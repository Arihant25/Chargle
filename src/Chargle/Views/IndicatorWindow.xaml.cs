using System.Diagnostics;
using System.Runtime.InteropServices;
using Chargle.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Chargle.Views;

/// <summary>
/// A small heads-up panel that appears when the charger is connected or disconnected and fades
/// away on its own.
///
/// Deliberately not a Windows notification. A toast would pile up in the notification centre,
/// need dismissing, and demand a click. This is closer to the volume overlay: it says one thing,
/// it cannot be interacted with at all, and it is gone before you would think to.
///
/// It is click-through and never takes focus, so it can appear over whatever you are doing
/// without stealing a keystroke.
/// </summary>
public sealed partial class IndicatorWindow : Window
{
    private readonly DispatcherTimer _hideTimer = new();
    private bool _styled;

    public IndicatorWindow()
    {
        InitializeComponent();

        Title = "Chargle indicator";
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // Deliberately not extending content into the title bar. Doing that keeps the window's
        // frame alive so that a custom title bar has something to sit in, and the frame is what
        // Windows draws the light border around.

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;

        AppWindow.IsShownInSwitchers = false;

        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); FadeOut(); };
    }

    /// <summary>Puts the panel on screen and starts its timer.</summary>
    public void Show(
        bool pluggedIn,
        string headline,
        string detail,
        IndicatorPlacement placement,
        TimeSpan dwell,
        IndicatorStyle style = IndicatorStyle.Panel,
        string? accentName = null)
    {
        Headline.Text = headline;
        Detail.Text = detail;

        Reposition(placement, ApplyStyle(style, pluggedIn, accentName));

        AppWindow.Show(activateWindow: false);
        ApplyExtendedStylesOnce();
        ApplyFrameAttributes();

        FadeIn();

        // Once the tree has actually been laid out, measure again and settle on the real size.
        // The first measurement of a window that has never been shown comes back empty, and the
        // backdrop also finishes configuring itself slightly after the window becomes visible.
        DispatcherQueue.TryEnqueue(() =>
        {
            Reposition(placement, ApplyStyle(style, pluggedIn, accentName));
            ApplyFrameAttributes();
        });

        _hideTimer.Stop();
        _hideTimer.Interval = dwell < TimeSpan.FromMilliseconds(600) ? TimeSpan.FromMilliseconds(600) : dwell;
        _hideTimer.Start();
    }

    /// <summary>
    /// The three styles are three densities of the same idea rather than three shapes: how much
    /// the panel is willing to say. Panel gives you the state and the battery level, Compact just
    /// the state, and Minimal only the mark, for people who need to know that something happened
    /// and nothing more.
    /// </summary>
    private SizeInt32 ApplyStyle(IndicatorStyle style, bool pluggedIn, string? accentName)
    {
        var accent = IndicatorPalette.Resolve(accentName);
        var onAccent = IndicatorPalette.ForegroundOn(accent);

        // Unplugged is deliberately not tinted. Colour means current is flowing; a grey badge
        // for "on battery" reads correctly at a glance without anyone being told the rule.
        Badge.Background = pluggedIn
            ? new SolidColorBrush(accent)
            : (Brush)Application.Current.Resources["ControlAltFillColorSecondaryBrush"];

        Bolt.Fill = pluggedIn
            ? new SolidColorBrush(onAccent)
            : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        switch (style)
        {
            case IndicatorStyle.Minimal:
                Words.Visibility = Visibility.Collapsed;
                // With the text column gone, the gap that separated it has to go too, or the
                // panel keeps a column of empty space where the words used to be.
                Layout.ColumnSpacing = 0;
                Badge.Width = Badge.Height = 42;
                Badge.CornerRadius = new CornerRadius(12);
                BoltBox.Width = 18;
                BoltBox.Height = 20;
                Layout.Padding = new Thickness(11);
                return MeasureContent(minimumWidth: 0, fallback: new SizeInt32(64, 64));

            case IndicatorStyle.Compact:
                Words.Visibility = Visibility.Visible;
                Detail.Visibility = Visibility.Collapsed;
                Layout.ColumnSpacing = 12;
                Badge.Width = Badge.Height = 28;
                Badge.CornerRadius = new CornerRadius(8);
                BoltBox.Width = 12;
                BoltBox.Height = 14;
                Layout.Padding = new Thickness(13, 10, 17, 10);
                return MeasureContent(minimumWidth: 132, fallback: new SizeInt32(150, 48));

            default:
                Words.Visibility = Visibility.Visible;
                Detail.Visibility = Visibility.Visible;
                Layout.ColumnSpacing = 13;
                Badge.Width = Badge.Height = 32;
                Badge.CornerRadius = new CornerRadius(9);
                BoltBox.Width = 14;
                BoltBox.Height = 16;
                Layout.Padding = new Thickness(14, 11, 18, 11);
                return MeasureContent(minimumWidth: 164, fallback: new SizeInt32(190, 58));
        }
    }

    /// <summary>
    /// Sizes the window to whatever the content actually needs.
    ///
    /// Fixed sizes were the wrong idea. They leave a slab of empty space next to short text like
    /// "Plugged in", and a column of nothing where the words would be in the mark-only style. A
    /// panel that hugs its contents is both smaller and better looking, and it stays correct when
    /// the text changes length.
    ///
    /// <paramref name="minimumWidth"/> is a floor against a silly narrow box, not a target. It
    /// used to be set well above what the text actually needs, which put the wide empty margin
    /// back on the right of every panel and undid the point of measuring at all.
    ///
    /// <paramref name="fallback"/> is used only when the measurement comes back as nothing, which
    /// happens on a window that has never been laid out. It has to be a whole size rather than
    /// the minimum width and a guess at the height. The mark-only style has a minimum width of
    /// zero, quite legitimately, and would otherwise fall back to a window with no width at all.
    /// </summary>
    private SizeInt32 MeasureContent(int minimumWidth, SizeInt32 fallback)
    {
        // InvalidateMeasure first, and it matters. Measure caches on the constraint it was given,
        // so asking twice with the same infinite size returns the first answer. The first ask
        // happens before the tree is laid out and comes back near zero, and without this the
        // panel keeps that size forever and the text is cut off.
        Layout.InvalidateMeasure();
        Layout.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = Layout.DesiredSize;

        if (desired.Width < 4 || desired.Height < 4) return fallback;

        // A few pixels of slack. Text measured before its font is fully resolved can come back
        // very slightly narrow, and being a hair too small is the difference between the text
        // fitting and being ellipsised.
        int width = Math.Max((int)Math.Ceiling(desired.Width) + 6, minimumWidth);
        return new SizeInt32(width, (int)Math.Ceiling(desired.Height));
    }

    /// <summary>
    /// Positioned against the work area rather than the screen, so it lands correctly whether the
    /// task bar is at the bottom, at the side, or hidden.
    /// </summary>
    private void Reposition(IndicatorPlacement placement, SizeInt32 size)
    {
        var work = DisplayArea.GetFromPoint(new PointInt32(0, 0), DisplayAreaFallback.Primary).WorkArea;

        // The work area is in physical pixels, so the panel's size and its margins have to be
        // too, or the panel comes out a fraction of its intended size on a scaled display.
        double scale = DisplayScale.For(this);
        int width = DisplayScale.Round(size.Width, scale);
        int height = DisplayScale.Round(size.Height, scale);
        int margin = DisplayScale.Round(24, scale);
        int fromEdge = DisplayScale.Round(64, scale);

        int x = placement switch
        {
            IndicatorPlacement.BottomRight or IndicatorPlacement.TopRight
                => work.X + work.Width - width - margin,
            _ => work.X + (work.Width - width) / 2,
        };

        int y = placement switch
        {
            IndicatorPlacement.TopCentre or IndicatorPlacement.TopRight
                => work.Y + fromEdge,
            _ => work.Y + work.Height - height - fromEdge,
        };

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>
    /// Applied once the window exists on screen. These make it a heads-up display rather than a
    /// window: no task bar entry, never takes focus, transparent to the mouse, rounded, and with
    /// no frame border.
    ///
    /// The border matters more than it sounds. Windows draws a hairline around every top-level
    /// window, and on something meant to look like part of the shell it reads as a stray outline.
    /// It has to be set after the window is shown, which is why this is not in the constructor.
    /// </summary>
    private void ApplyExtendedStylesOnce()
    {
        if (_styled) return;
        _styled = true;

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        nint exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, exStyle | WsExToolWindow | WsExNoActivate | WsExTransparent);

        // Strip the frame from the window itself rather than asking DWM to paint it invisibly.
        //
        // DWMWA_BORDER_COLOR reports success here and changes nothing, because the light outline
        // is the non-client frame being rendered, not the DWM accent border. A window with no
        // caption, no thick frame and no border has no non-client area for anything to draw in,
        // which is the only reliable way to be certain there is nothing round the edge.
        nint style = GetWindowLongPtr(hwnd, GwlStyle);
        style &= ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox | WsBorder | WsDlgFrame);
        style |= WsPopup;
        SetWindowLongPtr(hwnd, GwlStyle, style);

        // The frame change only takes effect once the window is told to recalculate it.
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    /// <summary>
    /// Rounding and the border colour, reapplied on every appearance rather than once.
    ///
    /// The backdrop controller sets DWM attributes of its own whenever the window is shown or
    /// the theme changes, and that puts the default light frame border back. Setting it once at
    /// startup looks correct in a debugger and produces a visible white outline in practice.
    /// </summary>
    private void ApplyFrameAttributes()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        int round = DwmwcpRound;
        Check(DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int)), "corner");

        int noBorder = unchecked((int)DwmColorNone);
        Check(DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref noBorder, sizeof(int)), "border");

        static void Check(int hr, string what)
        {
            if (hr != 0) Debug.WriteLine($"Chargle: could not set the indicator {what} (0x{hr:X8}).");
        }
    }

    private void FadeIn()
    {
        var story = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);

        // A few pixels of rise. Enough to read as arriving, not enough to be a performance.
        var rise = new DoubleAnimation
        {
            From = 10,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(rise, Slide);
        Storyboard.SetTargetProperty(rise, "Y");
        story.Children.Add(rise);

        story.Begin();
    }

    private void FadeOut()
    {
        var story = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);

        story.Completed += (_, _) => AppWindow.Hide();
        story.Begin();
    }

    // ------------------------------------------------------------------ interop

    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x00000080;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExTransparent = 0x00000020;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private const int GwlStyle = -16;
    // Not a const: nint cannot hold an unchecked cast in a constant expression.
    private static readonly nint WsPopup = unchecked((nint)0x80000000);
    private const nint WsCaption = 0x00C00000;
    private const nint WsBorder = 0x00800000;
    private const nint WsDlgFrame = 0x00400000;
    private const nint WsThickFrame = 0x00040000;
    private const nint WsSysMenu = 0x00080000;
    private const nint WsMinimizeBox = 0x00020000;
    private const nint WsMaximizeBox = 0x00010000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
