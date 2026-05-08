using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views.Tools;

public partial class CloudView : UserControl
{
    public CloudView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
