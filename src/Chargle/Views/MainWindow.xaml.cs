using Chargle.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Chargle.Views;

public sealed partial class MainWindow : Window
{
    private readonly App _app = App.Current;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Chargle";
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Chargle.ico"));
        AppWindow.Title = "Chargle";

        // Both of these are in physical pixels, so they have to be scaled or the window is
        // far too small for its own contents on a high-DPI display.
        double scale = DisplayScale.For(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = DisplayScale.Round(620, scale);
            presenter.PreferredMinimumHeight = DisplayScale.Round(520, scale);
        }

        // Aim for a comfortable size, but never take up the whole screen. A settings window that
        // opens nearly full height on a scaled laptop display feels broken even when it is not.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(DisplayScale.Round(780, scale), (int)(work.Width * 0.9));
        int height = Math.Min(DisplayScale.Round(760, scale), (int)(work.Height * 0.85));

        AppWindow.Resize(new SizeInt32(width, height));

        if (Content is FrameworkElement root) root.RequestedTheme = _app.ThemeForElements();

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public MainViewModel Vm => _app.ViewModel;

    /// <summary>Raises the window above whatever the user was doing, for tray activation.</summary>
    public void BringToFront()
    {
        AppWindow.Show();
        AppWindow.MoveInZOrderAtTop();
    }

    public Brush HeroBackground(bool pluggedIn) => pluggedIn
        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
        : (Brush)Application.Current.Resources["ControlAltFillColorSecondaryBrush"];

    public Brush HeroForeground(bool pluggedIn) => pluggedIn
        ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
        : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    private void OnToggleMute(object sender, RoutedEventArgs e) => Vm.ToggleMute();

    private void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        Type page = tag switch
        {
            "screen" => typeof(ScreenPage),
            "rules" => typeof(RulesPage),
            "about" => typeof(AboutPage),
            _ => typeof(SoundPage),
        };

        // About is the one page that is not about what the machine is doing right now, so the
        // live status card has nothing to say there and only crowds it.
        StatusCard.Visibility = page == typeof(AboutPage) ? Visibility.Collapsed : Visibility.Visible;

        if (Shell.CurrentSourcePageType == page) return;

        Shell.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
