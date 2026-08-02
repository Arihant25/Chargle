using Chargle.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Chargle.Views;

public sealed partial class ScreenPage : Page
{
    public ScreenPage() => InitializeComponent();

    public MainViewModel Vm => App.Current.ViewModel;

    public string StyleDescription(int index) => index switch
    {
        1 => "The state, without the battery level.",
        2 => "Only the mark, for when you just need to know something happened.",
        _ => "The state and the battery level.",
    };

    private void OnPreview(object sender, RoutedEventArgs e) => Vm.PreviewIndicator();
}
