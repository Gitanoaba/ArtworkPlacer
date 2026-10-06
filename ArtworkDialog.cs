using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ArtworkPlacer
{
    // Un tipo de familia que se puede colocar (sin dependencias de Revit para mantener la UI separada)
    public class TypeItem
    {
        public long Id;
        public string Label;
        public string Kind;
        public bool IsChecked;
    }

    // Colección de artwork = una sección "Artwork_*" del template, con sus tipos en orden de izquierda a derecha
    public class ArtCollection
    {
        public string Name;
        public List<long> Ids;
    }

    public enum SideMode { Auto = 0, Interior = 1, Exterior = 2, Both = 3 }

    // Opciones del diálogo; se recuerdan entre corridas dentro de la sesión de Revit
    public class DialogSettings
    {
        public string Collection;             // null = todos los tipos
        public string Filter = "art";
        public string HeightText;
        public bool UseHeight = true;
        public string ClearText;
        public SideMode Side = SideMode.Auto;
        public bool Multiple = true;
        public string GapText;
        public string MaxPerFaceText = "3";
        public bool Preview = true;
        public HashSet<long> LastChecked = new HashSet<long>();
    }

    // ─── VENTANA WPF ─────────────────────────────────────────────────────────
    public class ArtworkDialog : Window
    {
        private const string AllTypes = "All placeable types";

        // Resultados (longitudes en pies internos)
        public List<long> SelectedIds => _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
        public double? CenterHeight { get; private set; }   // null = respetar la altura de la familia
        public double Clearance { get; private set; }
        public double Gap { get; private set; }
        public int MaxPerFace { get; private set; }         // 1 = una pieza por cara

        private List<TypeItem> _items;
        private readonly List<ArtCollection> _collections;
        private readonly DialogSettings _settings;
        private readonly Func<string, double?> _parseLength;
        private readonly Func<string[], Tuple<List<TypeItem>, HashSet<long>>> _loadRfa;
        private List<long> _scope;                          // null = todos los tipos

        private readonly ComboBox _collectionBox;
        private readonly TextBox _search;
        private readonly ListBox _list;
        private readonly Label _countLabel;
        private readonly CheckBox _useHeight;
        private readonly TextBox _heightBox;
        private readonly TextBox _clearBox;
        private readonly ComboBox _sideBox;
        private readonly CheckBox _multiBox;
        private readonly TextBox _gapBox;
        private readonly TextBox _maxBox;
        private readonly CheckBox _previewBox;

        public ArtworkDialog(
            int wallCount,
            List<TypeItem> items,
            List<ArtCollection> collections,
            DialogSettings settings,
            Func<string, double?> parseLength,
            Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa)
        {
            _items = items;
            _collections = collections;
            _settings = settings;
            _parseLength = parseLength;
            _loadRfa = loadRfa;

            Title = "Place Artwork";
            Width = 540;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new Label
            {
                Content = $"{wallCount} wall(s) selected. Artwork types (rotated in order):",
                FontWeight = FontWeights.SemiBold
            });

            // Colección + cargar .rfa
            var collectionRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var btnLoad = new Button { Content = "Load .rfa…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(btnLoad, Dock.Right);
            collectionRow.Children.Add(btnLoad);
            collectionRow.Children.Add(new Label { Content = "Collection:", Width = 70, Padding = new Thickness(0, 4, 6, 0) });
            _collectionBox = new ComboBox();
            _collectionBox.Items.Add(AllTypes);
            foreach (var c in collections) _collectionBox.Items.Add($"{c.Name}  ({c.Ids.Count} types)");
            collectionRow.Children.Add(_collectionBox);
            root.Children.Add(collectionRow);

            // Buscador
            var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            searchRow.Children.Add(new Label { Content = "Search:", Width = 70, Padding = new Thickness(0, 4, 6, 0) });
            _search = new TextBox { Text = settings.Filter, Padding = new Thickness(4) };
            searchRow.Children.Add(_search);
            root.Children.Add(searchRow);

            _list = new ListBox { Height = 220 };
            root.Children.Add(_list);

            var listButtons = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
            var btnAll = new Button { Content = "Check visible", Padding = new Thickness(8, 2, 8, 2) };
            var btnNone = new Button { Content = "Uncheck all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            _countLabel = new Label { Foreground = System.Windows.Media.Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Right };
            listButtons.Children.Add(btnAll);
            listButtons.Children.Add(btnNone);
            listButtons.Children.Add(_countLabel);
            root.Children.Add(listButtons);

            // Parámetros
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _useHeight = new CheckBox { Content = "Artwork center height (from level):", IsChecked = settings.UseHeight, VerticalAlignment = VerticalAlignment.Center };
            _heightBox = NewBox(settings.HeightText);
            _heightBox.IsEnabled = settings.UseHeight;
            _useHeight.Checked += (s, e) => _heightBox.IsEnabled = true;
            _useHeight.Unchecked += (s, e) => _heightBox.IsEnabled = false;
            AddRow(grid, 0, _useHeight, _heightBox);

            _clearBox = NewBox(settings.ClearText);
            AddRow(grid, 1, NewLabel("Min. clearance to doors/corners:"), _clearBox);

            _sideBox = new ComboBox { Margin = new Thickness(0, 3, 0, 3) };
            _sideBox.Items.Add("Auto: facing the room (larger room if both sides)");
            _sideBox.Items.Add("Interior");
            _sideBox.Items.Add("Exterior");
            _sideBox.Items.Add("Both faces");
            _sideBox.SelectedIndex = (int)settings.Side;
            AddRow(grid, 2, NewLabel("Initial wall face:"), _sideBox);

            _multiBox = new CheckBox
            {
                Content = "Multiple pieces on long walls (evenly spaced)",
                IsChecked = settings.Multiple,
                Margin = new Thickness(0, 8, 0, 3)
            };
            Grid.SetRow(_multiBox, 3); Grid.SetColumnSpan(_multiBox, 2);
            grid.Children.Add(_multiBox);

            _gapBox = NewBox(settings.GapText);
            AddRow(grid, 4, NewLabel("    Min. gap between pieces:"), _gapBox);
            _maxBox = NewBox(settings.MaxPerFaceText);
            AddRow(grid, 5, NewLabel("    Max. pieces per wall face:"), _maxBox);
            _gapBox.IsEnabled = _maxBox.IsEnabled = settings.Multiple;
            _multiBox.Checked += (s, e) => _gapBox.IsEnabled = _maxBox.IsEnabled = true;
            _multiBox.Unchecked += (s, e) => _gapBox.IsEnabled = _maxBox.IsEnabled = false;

            _previewBox = new CheckBox
            {
                Content = "Preview faces with arrows before placing (click a wall to switch its face)",
                IsChecked = settings.Preview,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(_previewBox, 6); Grid.SetColumnSpan(_previewBox, 2);
            grid.Children.Add(_previewBox);
            root.Children.Add(grid);

            var btnPlace = new Button
            {
                Content = "Place artwork",
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true
            };
            root.Children.Add(btnPlace);

            Content = root;

            // Colección inicial: la última usada, si todavía existe
            int start = collections.FindIndex(c => c.Name == settings.Collection);
            if (start >= 0)
            {
                _scope = collections[start].Ids;
                _collectionBox.SelectedIndex = start + 1;
            }
            else _collectionBox.SelectedIndex = 0;
            _collectionBox.SelectionChanged += (s, e) => SelectCollection(_collectionBox.SelectedIndex);

            _search.TextChanged += (s, e) => RefreshList();
            btnAll.Click += (s, e) => { foreach (var it in _items.Where(Visible)) it.IsChecked = true; RefreshList(); };
            btnNone.Click += (s, e) => { foreach (var it in _items) it.IsChecked = false; RefreshList(); };
            btnLoad.Click += (s, e) => LoadFamilies();
            btnPlace.Click += (s, e) => Accept();

            SortByScope();
            RefreshList();
        }

        private static TextBox NewBox(string text) => new TextBox { Text = text, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(4) };

        private static Label NewLabel(string text) => new Label { Content = text, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };

        private static void AddRow(Grid grid, int row, UIElement left, UIElement right)
        {
            Grid.SetRow(left, row); Grid.SetColumn(left, 0);
            Grid.SetRow(right, row); Grid.SetColumn(right, 1);
            grid.Children.Add(left);
            grid.Children.Add(right);
        }

        // Al elegir una sección se marcan exactamente sus tipos, en el orden en que aparecen
        private void SelectCollection(int index)
        {
            _scope = index <= 0 ? null : _collections[index - 1].Ids;
            if (_scope != null)
            {
                var ids = new HashSet<long>(_scope);
                foreach (var it in _items) it.IsChecked = ids.Contains(it.Id);
                _search.Text = "";
            }
            SortByScope();
            RefreshList();
        }

        // La rotación sigue el orden de la lista: dentro de una sección, de izquierda a derecha
        private void SortByScope()
        {
            if (_scope == null) { _items = _items.OrderBy(i => i.Label).ToList(); return; }
            var order = _scope.Select((id, i) => new { id, i }).ToDictionary(x => x.id, x => x.i);
            _items = _items.OrderBy(i => order.TryGetValue(i.Id, out int o) ? o : int.MaxValue).ThenBy(i => i.Label).ToList();
        }

        // Con una sección: solo sus tipos. Sin sección: los marcados siempre se ven, aunque no coincidan con la búsqueda
        private bool Visible(TypeItem it)
        {
            if (_scope != null && !_scope.Contains(it.Id)) return false;
            string q = _search.Text.Trim();
            return (_scope == null && it.IsChecked) || q.Length == 0 || it.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefreshList()
        {
            _list.Items.Clear();
            foreach (var it in _items.Where(Visible))
            {
                var item = it;
                var cb = new CheckBox { Content = $"{item.Label}   [{item.Kind}]", IsChecked = item.IsChecked };
                cb.Checked += (s, e) => { item.IsChecked = true; UpdateCount(); };
                cb.Unchecked += (s, e) => { item.IsChecked = false; UpdateCount(); };
                _list.Items.Add(cb);
            }
            UpdateCount();
        }

        private void UpdateCount() => _countLabel.Content = $"{_items.Count(i => i.IsChecked)} type(s) checked";

        private void LoadFamilies()
        {
            var ofd = new OpenFileDialog { Filter = "Revit families (*.rfa)|*.rfa", Multiselect = true, Title = "Load artwork families" };
            if (ofd.ShowDialog(this) != true) return;

            try
            {
                var result = _loadRfa(ofd.FileNames);
                var keep = new HashSet<long>(_items.Where(i => i.IsChecked).Select(i => i.Id));
                foreach (var it in result.Item1)
                    it.IsChecked = keep.Contains(it.Id) || result.Item2.Contains(it.Id);
                _items = result.Item1;
                _collectionBox.SelectedIndex = 0;   // las familias nuevas no están en ninguna sección
                SortByScope();
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not load the families:\n{ex.Message}", "Place Artwork");
            }
        }

        private void Accept()
        {
            if (!_items.Any(i => i.IsChecked))
            {
                MessageBox.Show(this, "Check at least one artwork type.", "Place Artwork");
                return;
            }

            if (_useHeight.IsChecked == true)
            {
                double? h = _parseLength(_heightBox.Text);
                if (h == null || h <= 0) { MessageBox.Show(this, "Invalid height.", "Place Artwork"); return; }
                CenterHeight = h;
            }
            else CenterHeight = null;

            double? c = _parseLength(_clearBox.Text);
            if (c == null || c < 0) { MessageBox.Show(this, "Invalid clearance.", "Place Artwork"); return; }
            Clearance = c.Value;

            if (_multiBox.IsChecked == true)
            {
                double? g = _parseLength(_gapBox.Text);
                if (g == null || g < 0) { MessageBox.Show(this, "Invalid gap between pieces.", "Place Artwork"); return; }
                if (!int.TryParse(_maxBox.Text.Trim(), out int max) || max < 1)
                {
                    MessageBox.Show(this, "Max. pieces per wall face must be a whole number ≥ 1.", "Place Artwork");
                    return;
                }
                Gap = g.Value;
                MaxPerFace = max;
            }
            else
            {
                Gap = 0;
                MaxPerFace = 1;
            }

            // Guardar opciones para la próxima corrida
            _settings.Collection = _collectionBox.SelectedIndex > 0 ? _collections[_collectionBox.SelectedIndex - 1].Name : null;
            _settings.Filter = _search.Text;
            _settings.HeightText = _heightBox.Text;
            _settings.UseHeight = CenterHeight.HasValue;
            _settings.ClearText = _clearBox.Text;
            _settings.Side = (SideMode)_sideBox.SelectedIndex;
            _settings.Multiple = _multiBox.IsChecked == true;
            _settings.GapText = _gapBox.Text;
            _settings.MaxPerFaceText = _maxBox.Text;
            _settings.Preview = _previewBox.IsChecked == true;
            _settings.LastChecked = new HashSet<long>(SelectedIds);

            DialogResult = true;
            Close();
        }
    }
}
