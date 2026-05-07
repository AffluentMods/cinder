using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views;

public partial class HexView : UserControl
{
    public HexView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
