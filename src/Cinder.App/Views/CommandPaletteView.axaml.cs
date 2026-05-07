using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
