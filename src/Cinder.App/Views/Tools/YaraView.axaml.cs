using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views.Tools;

public partial class YaraView : UserControl
{
    public YaraView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
