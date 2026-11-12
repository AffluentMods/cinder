using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cinder.App.ViewModels;

namespace Cinder.App.Views.Tools;

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnHitDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SearchToolViewModel vm)
        {
            vm.OpenHitCommand.Execute(null);
        }
    }
}
