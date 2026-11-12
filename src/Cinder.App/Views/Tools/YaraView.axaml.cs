using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cinder.App.ViewModels.Tools;

namespace Cinder.App.Views.Tools;

public partial class YaraView : UserControl
{
    public YaraView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnHitDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is YaraTool vm)
        {
            vm.OpenHitCommand.Execute(null);
        }
    }
}
