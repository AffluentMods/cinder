using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cinder.App.ViewModels.Tools;

namespace Cinder.App.Views.Tools;

public partial class GalleryView : UserControl
{
    public GalleryView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnThumbnailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border b && b.Tag is GalleryItem item && DataContext is GalleryTool tool)
        {
            tool.SelectedItem = item;
        }
    }
}
