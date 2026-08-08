using System.Diagnostics;
using System.Runtime.CompilerServices;
using Chargle.Services;
using Chargle.ViewModels;
using Chargle.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Chargle;

public partial class App : Application
{
    /// <summary>Held for the life of the process. Releasing it would let a second copy start.</summary>
    private static Mutex? _instanceLock;

    private MainWindow? _window;
    private IndicatorWindow? _indicator;
    private TrayIconHost? _tray;

    public App() => InitializeComponent();

    public static new App Current => (App)Application.Current;

    public SettingsStore Settings { get; private set; } = null!;
    public SoundLibrary Library { get; private set; } = null!;
    public AudioEngine Audio { get; private set; } = null!;
    public PowerMonitor Power { get; private set; } = null!;
    public ChargeWatcher Watcher { get; private set; } = null!;

    /// <summary>The UI thread's queue, so services running on system threads can reach the window.</summary>
    public DispatcherQueue Dispatcher { get; private set; } = null!;

    /// <summary>
    /// One view model for the whole app, shared by every page. The settings pages are views onto
    /// a single set of preferences, so giving each its own copy would only create ways for them
    /// to disagree.
    /// </summary>
    public MainViewModel ViewModel { get; private set; } = null!;

    /// <summary>
    /// Pushes the chosen theme into every window. WinUI applies RequestedTheme per element tree,
    /// so each top-level window has to be told separately, including ones opened later.
    /// </summary>
    public void ApplyTheme()
    {
        var theme = ThemeForElements();

        if (_window?.Content is FrameworkElement main) main.RequestedTheme = theme;
        if (_indicator?.Content is FrameworkElement panel) panel.RequestedTheme = theme;
    }

    /// <summary>The chosen theme as WinUI wants it. Default means follow Windows.</summary>
    public ElementTheme ThemeForElements() => Settings.Current.Theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Dispatcher = DispatcherQueue.GetForCurrentThread();
        InstallCrashHandlers();

        if (RedirectToRunningInstance()) return;

        Settings = new SettingsStore();

        Library = new SoundLibrary();
        Library.Reload();

        Audio = new AudioEngine { KeepWarm = Settings.Current.InstantMode };
        Power = new PowerMonitor();
        Watcher = new ChargeWatcher(Power, Audio, Library, Settings);

        Settings.Changed += settings => Audio.KeepWarm = settings.InstantMode;
        Watcher.Reacted += OnReacted;
        Watcher.MilestoneReached += OnMilestoneReached;

        ViewModel = new MainViewModel(this);

        if (Settings.IsFirstRun) _ = RegisterStartupOnFirstRunAsync();

        // Decoding every pack and opening the audio device are the two slow things Chargle does,
        // and both exist purely so that nothing is slow later. Neither should hold up the window.
        Task.Run(() =>
        {
            Library.LoadAll();
            Audio.Prime();
        });

        TryCreateTrayIcon();

        if (!StartsHidden()) ShowMainWindow();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    /// <summary>
    /// Registers Chargle to start with Windows the first time it runs.
    ///
    /// Done once, on first launch only, and recorded in settings so that someone who turns it off
    /// does not find it switched back on next time. Failure is not worth telling anyone about;
    /// the switch is there in Rules if they want it.
    /// </summary>
    private async Task RegisterStartupOnFirstRunAsync()
    {
        var state = await StartupService.SetAsync(true);
        Settings.Update(s => s.RunAtLogin = state == StartupAvailability.Enabled);
    }

    /// <summary>
    /// Three separate places an exception can escape, all of which end the process by default.
    ///
    /// The XAML one is marked handled: a glitch drawing a settings page is not a reason to stop
    /// watching the charger, and the window can be reopened from the tray. The other two cannot
    /// be recovered from, so they are only recorded on the way out.
    /// </summary>
    private void InstallCrashHandlers()
    {
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("UI thread", e.Exception, fatal: false);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("background thread", e.ExceptionObject as Exception, fatal: true);

        // A faulted Task nobody awaited. Harmless in itself, but it is usually the first sign of
        // something wrong in the preview or startup paths, so it is worth a line in the log.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("unobserved task", e.Exception, fatal: false);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Fires on a system thread the instant the cable moves, so it does nothing but decide and
    /// hop to the UI. The indicator is checked against Do Not Disturb separately from the sound:
    /// a panel appearing over a full-screen presentation is worse than a noise, not better.
    /// </summary>
    private void OnReacted(CueResult result)
    {
        if (!Settings.Current.ShowVisualIndicator) return;
        if (Settings.Current.RespectDoNotDisturb && Presence.ShouldStayQuiet()) return;

        var state = Power.State;
        Dispatcher.TryEnqueue(() => ShowIndicator(state));
    }

    /// <summary>
    /// A milestone the user asked to see. If they also asked to hear it, the watcher has already
    /// played that on the thread the event arrived on.
    ///
    /// Not gated on <see cref="ChargleSettings.ShowVisualIndicator"/>: that switch is about the
    /// cable moving, and a milestone asking for a panel has already said so on its own page.
    /// </summary>
    private void OnMilestoneReached(MilestoneResult result)
    {
        var settings = Settings.Current;

        var alert = result.Milestone == BatteryMilestone.Full
            ? settings.FullChargeAlert
            : settings.LowBatteryAlert;

        if (alert == MilestoneAlert.Chime) return;
        if (settings.RespectDoNotDisturb && Presence.ShouldStayQuiet()) return;

        Dispatcher.TryEnqueue(() => ShowIndicator(
            charged: result.Milestone == BatteryMilestone.Full,
            PowerStrings.MilestoneHeadline(result.Milestone),
            PowerStrings.MilestoneDetail(result.Milestone, result.Percent)));
    }

    /// <summary>Shows the heads-up panel for the current power state. Must be called on the UI thread.</summary>
    public void ShowIndicator(PowerState state) => ShowIndicator(
        state.Source == PowerSource.Ac,
        PowerStrings.Headline(state.Source),
        PowerStrings.Detail(state));

    /// <summary>
    /// Shows the heads-up panel. Must be called on the UI thread.
    ///
    /// <paramref name="charged"/> only decides whether the badge is tinted or grey. Colour here
    /// means "there is power in it", which is true of a connected charger and of a full battery
    /// alike, and false of both an unplugged laptop and one about to run out.
    /// </summary>
    private void ShowIndicator(bool charged, string headline, string detail)
    {
        try
        {
            if (_indicator is null)
            {
                _indicator = new IndicatorWindow();
                ApplyTheme();
            }

            var settings = Settings.Current;
            _indicator.Show(
                charged,
                headline,
                detail,
                settings.IndicatorPlacement,
                TimeSpan.FromSeconds(settings.IndicatorSeconds),
                settings.IndicatorStyle,
                settings.IndicatorAccent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not show the indicator. {ex.Message}");
        }
    }

    /// <summary>
    /// The settings window, for the handful of APIs that need an owner handle: file pickers and
    /// content dialogs both refuse to work from a desktop app without one.
    /// </summary>
    public Window? SettingsWindow => _window;

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
        }

        _window.Activate();
        _window.BringToFront();
    }

    /// <summary>
    /// Whether this launch was Windows starting us at login rather than the user asking for the
    /// app. At login there is nothing to show, which is the point of a tray app; a settings window
    /// in your face every time you sign in is the fastest way to get an app uninstalled.
    ///
    /// Either signal means a login start, because the two kinds of build register for startup
    /// differently: the portable one gets a Run key we control, the packaged one a StartupTask
    /// we do not.
    /// </summary>
    private bool StartsHidden()
    {
        if (!Settings.Current.StartHidden) return false;

        string[] argv = Environment.GetCommandLineArgs();
        bool requested = argv.Any(a =>
            a.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/background", StringComparison.OrdinalIgnoreCase));

        return requested || LaunchedByStartupTask();
    }

    /// <summary>
    /// The packaged build's run-at-login is a StartupTask declared in the manifest, and a
    /// StartupTask takes no arguments: there is nowhere to put --background. Windows says so in
    /// the activation instead, so that is where we have to ask.
    /// </summary>
    private static bool LaunchedByStartupTask()
    {
        if (!PackageContext.IsPackaged) return false;

        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        }
        catch (Exception ex)
        {
            // Showing the window is the safe way to be wrong here. A window nobody asked for is a
            // nuisance; an app that starts invisible when the user double-clicked it looks broken.
            Debug.WriteLine($"Chargle: could not read the activation kind. {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A second launch should surface the window that already exists rather than start a rival
    /// copy: two instances would mean two tray icons and the sound played twice.
    /// </summary>
    private static bool RedirectToRunningInstance()
    {
        // A named mutex is the authoritative check, and it has to be, because AppInstance keys
        // off the executable path for unpackaged apps. Two copies in different folders look like
        // two different apps to it and both run happily, which sounds exactly like the app
        // playing every sound twice. The mutex is tied to the name instead, so it catches a
        // second copy wherever it was started from.
        //
        // Local\ rather than Global\ so that two people signed in at once each get their own.
        try
        {
            _instanceLock = new Mutex(initiallyOwned: true, @"Local\Chargle.SingleInstance", out bool first);
            if (first) return false;
        }
        catch (Exception ex)
        {
            // Without the mutex there is no safe way to tell, and refusing to start would be the
            // worse failure, so carry on.
            Debug.WriteLine($"Chargle: single instance lock failed. {ex.Message}");
            return false;
        }

        // Something else is already running. Surface its window rather than dying silently, so a
        // second launch from the Start menu feels like it did something.
        try
        {
            var instance = AppInstance.FindOrRegisterForKey("Chargle.SingleInstance");
            if (!instance.IsCurrent)
            {
                instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                    .AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not hand over to the running instance. {ex.Message}");
        }

        Process.GetCurrentProcess().Kill();
        return true;
    }

    /// <summary>
    /// Isolated in its own method so that a binary incompatibility in the tray library surfaces
    /// as a caught exception here rather than as a type load failure that takes down startup.
    /// Chargle without a tray icon is diminished; Chargle that will not start is useless.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TryCreateTrayIcon()
    {
        try
        {
            _tray = new TrayIconHost(this);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: tray icon unavailable. {ex.Message}");
            _tray = null;
        }
    }

    private void Shutdown()
    {
        _tray?.Dispose();
        Watcher?.Dispose();
        Power?.Dispose();
        Audio?.Dispose();
        Settings?.Dispose();
    }

    public void Quit()
    {
        Shutdown();
        Exit();
    }
}
