using Chargle.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Chargle.Views;

public sealed partial class RulesPage : Page
{
    public RulesPage() => InitializeComponent();

    public MainViewModel Vm => App.Current.ViewModel;

    public Visibility ShowIfText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public bool Not(bool value) => !value;
}
