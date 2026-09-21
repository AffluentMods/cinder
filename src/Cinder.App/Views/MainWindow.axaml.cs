using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Cinder.App.ViewModels;

namespace Cinder.App.Views;

public partial class MainWindow : Window
{
    private const double DefaultRailWidth = 232;
    private const double DefaultInspectorWidth = 300;

    private double _railWidth = DefaultRailWidth;
    private double _inspectorWidth = DefaultInspectorWidth;
    private MainWindowViewModel? _vm;
    private readonly Services.WindowLayoutStore _layoutStore = new();

    /// <summary>Set by the screenshot harness so a dev run never overwrites the examiner's layout.</summary>
    public bool PersistLayout { get; set; } = true;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var title = this.FindControl<Border>("TitleBar")!;
        title.PointerPressed += OnTitlePointerPressed;
        title.DoubleTapped += (_, e) =>
        {
            if (IsEmptyTitleArea(e.Source))
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
        };

        Opened += (_, _) => RestoreLayout();
        Closing += (_, _) => SaveLayout();

        // Leave room for the OS caption buttons drawn over the extended title area.
        var grid = this.FindControl<Grid>("TitleGrid")!;
        if (OperatingSystem.IsWindows())
        {
            grid.Margin = new Thickness(14, 0, 140, 0);
        }
        else if (OperatingSystem.IsMacOS())
        {
            grid.Margin = new Thickness(80, 0, 8, 0);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.FocusFindRequested -= OnFocusFindRequested;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.FocusFindRequested += OnFocusFindRequested;
            ApplyPaneLayout();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsRailOpen) or nameof(MainWindowViewModel.ShowInspector))
        {
            ApplyPaneLayout();
        }
    }

    /// <summary>
    /// Collapses or restores the rail and inspector columns, remembering whatever width the
    /// user last dragged them to. Done here rather than by binding because a GridSplitter drag
    /// replaces a bound column width with a local value.
    /// </summary>
    private void ApplyPaneLayout()
    {
        if (_vm is null || this.FindControl<Grid>("BodyGrid") is not { } body)
        {
            return;
        }
        var cols = body.ColumnDefinitions;

        if (cols[0].Width.Value > 0)
        {
            _railWidth = cols[0].Width.Value;
        }
        if (cols[4].Width.Value > 0)
        {
            _inspectorWidth = cols[4].Width.Value;
        }

        cols[0].Width = _vm.IsRailOpen ? new GridLength(_railWidth) : new GridLength(0);
        cols[1].Width = _vm.IsRailOpen ? new GridLength(4) : new GridLength(0);
        cols[3].Width = _vm.ShowInspector ? new GridLength(4) : new GridLength(0);
        cols[4].Width = _vm.ShowInspector ? new GridLength(_inspectorWidth) : new GridLength(0);

        this.FindControl<Control>("Rail")!.IsVisible = _vm.IsRailOpen;
        this.FindControl<Control>("RailSplitter")!.IsVisible = _vm.IsRailOpen;
        this.FindControl<Control>("Inspector")!.IsVisible = _vm.ShowInspector;
        this.FindControl<Control>("InspectorSplitter")!.IsVisible = _vm.ShowInspector;
    }

    private void RestoreLayout()
    {
        if (!PersistLayout || _layoutStore.Load() is not { } l || _vm is null)
        {
            return;
        }
        _railWidth = Math.Clamp(l.RailWidth, 160, 480);
        _inspectorWidth = Math.Clamp(l.InspectorWidth, 240, 560);

        // Only restore a position that is still on a connected screen.
        var onScreen = Screens.All.Any(sc => sc.WorkingArea.Contains(new PixelPoint((int)l.X + 40, (int)l.Y + 20)));
        if (onScreen)
        {
            Position = new PixelPoint((int)l.X, (int)l.Y);
        }
        Width = l.Width;
        Height = l.Height;
        if (l.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        if (this.FindControl<Grid>("BodyGrid") is { } body)
        {
            body.ColumnDefinitions[0].Width = new GridLength(_railWidth);
            body.ColumnDefinitions[4].Width = new GridLength(_inspectorWidth);
        }
        _vm.IsRailOpen = l.RailOpen;
        _vm.IsInspectorOpen = l.InspectorOpen;
        ApplyPaneLayout();
    }

    private void SaveLayout()
    {
        if (!PersistLayout || _vm is null)
        {
            return;
        }
        if (this.FindControl<Grid>("BodyGrid") is { } body)
        {
            if (body.ColumnDefinitions[0].Width.Value > 0) _railWidth = body.ColumnDefinitions[0].Width.Value;
            if (body.ColumnDefinitions[4].Width.Value > 0) _inspectorWidth = body.ColumnDefinitions[4].Width.Value;
        }
        var maximized = WindowState == WindowState.Maximized;
        _layoutStore.Save(new Services.WindowLayout(
            Position.X, Position.Y,
            maximized ? Math.Max(Width, 1080) : Width,
            maximized ? Math.Max(Height, 640) : Height,
            maximized, _railWidth, _inspectorWidth, _vm.IsRailOpen, _vm.IsInspectorOpen));
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1 && IsEmptyTitleArea(e.Source))
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>True when the pointer is on the bar itself or plain text, not on a button.</summary>
    private static bool IsEmptyTitleArea(object? source) =>
        source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is null;

    /// <summary>
    /// "/" focuses the rail filter (unless you're typing somewhere), Esc clears it. Tunnelling so
    /// the rail gets it before any tool view does.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var box = this.FindControl<TextBox>("RailFilterBox");
        var focused = FocusManager?.GetFocusedElement();
        if (e.Key == Key.Oem2 && e.KeyModifiers == KeyModifiers.None && focused is not TextBox && box is not null)
        {
            if (_vm is not null && !_vm.IsRailOpen)
            {
                _vm.IsRailOpen = true;
            }
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ReferenceEquals(focused, box) && _vm is not null)
        {
            _vm.Workspace.RailFilter = null;
            Focus();
            e.Handled = true;
        }
    }

    private void OnFocusFindRequested(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var find = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "FindBox" && t.IsEffectivelyVisible);
            find?.Focus();
            find?.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
}
