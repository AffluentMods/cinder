using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Cinder.App.Controls;

/// <summary>
/// <c>c:AutoColumns.FromFirstItem="True"</c> on a <see cref="DataGrid"/>: builds its columns from
/// the runtime type of the first row, and rebuilds them when that type changes.
///
/// <para>Why this exists: the parser tools emit anonymous objects into an
/// <c>ObservableCollection&lt;object&gt;</c>. DataGrid's own <c>AutoGenerateColumns</c> reflects
/// over the collection's element type — <see cref="object"/>, which has no properties — so it
/// generated zero columns, and every parser showed an empty grid above a non-zero row count.</para>
/// </summary>
public static class AutoColumns
{
    public static readonly AttachedProperty<bool> FromFirstItemProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("FromFirstItem", typeof(AutoColumns));

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, State?>("AutoColumnsState", typeof(AutoColumns));

    public static bool GetFromFirstItem(DataGrid grid) => grid.GetValue(FromFirstItemProperty);
    public static void SetFromFirstItem(DataGrid grid, bool value) => grid.SetValue(FromFirstItemProperty, value);

    static AutoColumns()
    {
        FromFirstItemProperty.Changed.AddClassHandler<DataGrid>((g, e) =>
        {
            if (e.NewValue is true)
            {
                var state = new State(g);
                g.SetValue(StateProperty, state);
                state.Attach();
            }
            else
            {
                g.GetValue(StateProperty)?.Detach();
                g.SetValue(StateProperty, null);
            }
        });
    }

    private sealed class State(DataGrid grid)
    {
        private INotifyCollectionChanged? _watched;
        private Type? _builtFor;

        public void Attach()
        {
            grid.AutoGenerateColumns = false;
            grid.PropertyChanged += OnGridPropertyChanged;
            Watch(grid.ItemsSource);
            Rebuild();
        }

        public void Detach()
        {
            grid.PropertyChanged -= OnGridPropertyChanged;
            Watch(null);
        }

        private void OnGridPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ItemsControl.ItemsSourceProperty || e.Property.Name == nameof(DataGrid.ItemsSource))
            {
                Watch(grid.ItemsSource);
                _builtFor = null;
                Rebuild();
            }
        }

        private void Watch(IEnumerable? source)
        {
            if (_watched is not null)
            {
                _watched.CollectionChanged -= OnCollectionChanged;
            }
            _watched = source as INotifyCollectionChanged;
            if (_watched is not null)
            {
                _watched.CollectionChanged += OnCollectionChanged;
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            object? first = null;
            if (grid.ItemsSource is IEnumerable items)
            {
                foreach (var item in items)
                {
                    first = item;
                    break;
                }
            }
            var type = first?.GetType();
            if (type is null || type == _builtFor)
            {
                return;
            }
            _builtFor = type;
            grid.Columns.Clear();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0)
                {
                    continue;
                }
                var column = new DataGridTextColumn
                {
                    Header = Humanize(p.Name),
                    Binding = new Binding(p.Name) { Mode = BindingMode.OneWay },
                    IsReadOnly = true,
                    CanUserSort = true,
                    FontSize = 12.5,
                };
                // Numbers, times and identifiers read and compare better in a fixed-width face.
                if (IsTechnical(p))
                {
                    column.FontFamily = Mono;
                }
                grid.Columns.Add(column);
            }
        }
    }

    private static readonly Avalonia.Media.FontFamily Mono = new("JetBrains Mono, Cascadia Mono, Consolas, monospace");

    private static readonly string[] TechnicalNames =
        ["hash", "sha", "md5", "offset", "usn", "lsn", "guid", "sid", "inode", "mft", "size", "path", "id"];

    private static bool IsTechnical(PropertyInfo p)
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (t.IsPrimitive || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(Guid))
        {
            return t != typeof(bool);
        }
        var n = p.Name.ToLowerInvariant();
        return TechnicalNames.Any(k => n == k || n.EndsWith(k, StringComparison.Ordinal) || n.StartsWith(k, StringComparison.Ordinal));
    }

    /// <summary>"MftRecord" → "Mft record", "IsDeleted" → "Is deleted", "SHA256" stays "SHA256".</summary>
    public static string Humanize(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var prev = i > 0 ? name[i - 1] : '\0';
            var next = i + 1 < name.Length ? name[i + 1] : '\0';
            var boundary = i > 0 && char.IsUpper(c) && (char.IsLower(prev) || (char.IsUpper(prev) && char.IsLower(next)));
            if (boundary)
            {
                sb.Append(' ');
                // Keep acronyms ("USN", "MFT") upper-case; lower-case ordinary words.
                sb.Append(char.IsUpper(next) ? c : char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
