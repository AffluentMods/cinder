using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views.Tools;

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
