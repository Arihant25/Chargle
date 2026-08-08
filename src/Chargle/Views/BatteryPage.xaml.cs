using Chargle.Services;
using Chargle.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Chargle.Views;

/// <summary>
/// The battery milestones, on a page of their own.
///
/// They used to be an expander on Rules, which was the wrong home for them. Every other row on
/// that page is one switch and a sentence. A milestone needs a level, a way of announcing itself
/// and a sound, twice over, and none of that fits inside a collapsed section of another page.
/// </summary>
public sealed partial class BatteryPage : Page
{
    public BatteryPage() => InitializeComponent();

    public MainViewModel Vm => App.Current.ViewModel;

    public Visibility ShowIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public string Percent(double value) => $"{value:F0}%";

    public string AlertDescription(int alertIndex) => (MilestoneAlert)alertIndex switch
    {
        MilestoneAlert.Indicator => "The on-screen panel only, so it says nothing out loud.",
        MilestoneAlert.Both => "A sound and the on-screen panel together.",
        _ => "A sound only.",
    };

    private void OnPreviewFull(object sender, RoutedEventArgs e) => Vm.PreviewMilestone(BatteryMilestone.Full);

    private void OnPreviewLow(object sender, RoutedEventArgs e) => Vm.PreviewMilestone(BatteryMilestone.Low);
}
