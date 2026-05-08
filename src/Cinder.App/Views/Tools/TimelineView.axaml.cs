using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views.Tools;

public partial class TimelineView : UserControl
{
    public TimelineView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
