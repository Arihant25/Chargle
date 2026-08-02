using System.Diagnostics;
using System.Reflection;
using Chargle.Services;
using Chargle.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Chargle.Views;

public sealed partial class AboutPage : Page
{
    private const string RepositoryUrl = "https://github.com/Arihant25/Chargle";
    private const string WebsiteUrl = "https://arihant25.github.io";
    private const string LicenceUrl = "https://github.com/Arihant25/Chargle/blob/main/LICENSE";

    public AboutPage() => InitializeComponent();

    public MainViewModel Vm => App.Current.ViewModel;

    public string VersionLine
    {
        get
        {
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0] ?? "1.0.0";

            string kind = PackageContext.IsPackaged ? "Store build" : "Portable build";
            return $"Version {version}, {kind}";
        }
    }

    public string CopyrightLine => $"Copyright {DateTime.Now.Year} Arihant Tripathy";

    public string SettingsPath => $"Settings: {AppPaths.SettingsFile}";

    public string SoundsPath => $"Your sounds: {SoundLibrary.UserDirectory}";

    private void OnOpenRepo(object sender, RoutedEventArgs e) => Open(RepositoryUrl);

    private void OnOpenWebsite(object sender, RoutedEventArgs e) => Open(WebsiteUrl);

    private void OnOpenLicence(object sender, RoutedEventArgs e) => Open(LicenceUrl);

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not open {url}. {ex.Message}");
        }
    }
}
