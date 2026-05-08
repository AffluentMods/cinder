using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views.Tools;

public partial class GenericToolView : UserControl
{
    public GenericToolView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
