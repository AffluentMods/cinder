using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views;

public partial class HashDialog : Window
{
    public HashDialog() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
