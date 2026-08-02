using System.Diagnostics;
using Chargle.Services;
using Chargle.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Chargle.Views;

public sealed partial class SoundPage : Page
{
    private readonly App _app = App.Current;

    public SoundPage() => InitializeComponent();

    public MainViewModel Vm => _app.ViewModel;

    public string Percent(double value) => $"{value:F0}%";

    public Visibility ShowIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowIfText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public string ImportStatus { get; private set; } = "";

    private async void OnPreviewPair(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PackViewModel item }) return;

        // Second click on a playing pack stops it, rather than restarting the thing you are
        // already listening to.
        if (item.IsPlaying)
        {
            _app.Watcher.StopPreview();
            item.IsPlaying = false;
            return;
        }

        // Clicking play on a sound you are auditioning almost always means you are considering
        // it, so select it too rather than making that a second click.
        Vm.SelectedPack = item;

        foreach (var pack in Vm.Packs) pack.IsPlaying = false;
        item.IsPlaying = true;

        try
        {
            await _app.Watcher.PreviewPairAsync(item.Pack);
        }
        finally
        {
            item.IsPlaying = false;
        }
    }

    private async void OnPickCustomPlug(object sender, RoutedEventArgs e) => await ImportAsync(isPlug: true);

    private async void OnPickCustomUnplug(object sender, RoutedEventArgs e) => await ImportAsync(isPlug: false);

    /// <summary>
    /// Copies the chosen file into the user's sounds folder, then selects the resulting pack.
    ///
    /// Selecting it matters. Previously a chosen file quietly overrode whatever pack was
    /// highlighted, so the list said one thing and the app played another. Now what is selected
    /// is what plays, with no second opinion anywhere.
    /// </summary>
    private async Task ImportAsync(bool isPlug)
    {
        string? path = await PickSoundAsync();
        if (path is null) return;

        var seed = Vm.SelectedPack?.Pack
                   ?? _app.Library.Find(_app.Settings.Current.PackId)
                   ?? _app.Library.Packs.FirstOrDefault();
        if (seed is null) return;

        try
        {
            var pack = _app.Library.ImportUserPack(
                seed,
                isPlug ? path : null,
                isPlug ? null : path);

            if (pack is null)
            {
                ImportStatus = "That file could not be imported.";
            }
            else
            {
                pack.Load();
                Vm.RefreshPacks();
                Vm.SelectedPack = Vm.Packs.FirstOrDefault(p => p.Id == pack.Id);

                ImportStatus = pack.LoadError is null
                    ? $"Copied {Path.GetFileName(path)} into My sounds and selected it."
                    : $"Copied, but it could not be decoded: {pack.LoadError}";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not import a sound. {ex.Message}");
            ImportStatus = $"Could not copy that file: {ex.Message}";
        }

        Bindings.Update();
    }

    private async Task<string?> PickSoundAsync()
    {
        try
        {
            if (_app.SettingsWindow is not { } owner) return null;

            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            foreach (string extension in new[] { ".wav", ".mp3", ".m4a", ".flac", ".wma", ".aiff", ".ogg" })
                picker.FileTypeFilter.Add(extension);

            // A picker created from a desktop app has no idea which window owns it until told.
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: file picker failed. {ex.Message}");
            return null;
        }
    }

    private void OnOpenSoundsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            // Created on demand: a folder that only appears once you go looking for it is
            // friendlier than one sitting in AppData from the first launch.
            Directory.CreateDirectory(SoundLibrary.UserDirectory);
            Process.Start(new ProcessStartInfo(SoundLibrary.UserDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not open the sounds folder. {ex.Message}");
        }
    }
}
