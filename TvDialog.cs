using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ArtworkPlacer
{
    // Opciones del diálogo de TVs; se recuerdan entre corridas dentro de la sesión de Revit
    public class TvSettings
    {
        public string Filter = "tv";
        public bool AutoSize = true;
        public string FactorText = "4.5";
        public bool UseBottom = true;
        public string BottomText;
        public string ClearText;
        public bool Preview = true;
        public HashSet<long> LastChecked = new HashSet<long>();
    }

    // ─── VENTANA WPF ─────────────────────────────────────────────────────────
    public class TvDialog : Window
    {
        private const string Title_ = "Place TV";

        // Resultados (longitudes en pies internos)
        public List<long> SelectedIds => _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
        public double? BottomHeight { get; private set; }   // null = respetar la altura de la familia
        public double Clearance { get; private set; }
        public double DepthFactor { get; private set; }

        private List<TypeItem> _items;
        private readonly TvSettings _settings;
        private readonly Func<string, double?> _parseLength;
        private readonly Func<string[], Tuple<List<TypeItem>, HashSet<long>>> _loadRfa;

        private readonly TextBox _search;
        private readonly ListBox _list;
        private readonly Label _countLabel;
        private readonly ComboBox _sizeBox;
        private readonly TextBox _factorBox;
        private readonly CheckBox _useBottom;
        private readonly TextBox _bottomBox;
        private readonly TextBox _clearBox;
        private readonly CheckBox _previewBox;

        public TvDialog(
            int wallCount,
            List<TypeItem> items,
            TvSettings settings,
            Func<string, double?> parseLength,
            Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa)
        {
            _items = items;
            _settings = settings;
            _parseLength = parseLength;
            _loadRfa = loadRfa;

            Title = Title_;
            Width = 540;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = $"{wallCount} wall(s) selected. Closed rooms formed by these walls get one TV each; " +
                       "walls that don't close a room get one TV per wall.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new Label { Content = "TV types to choose from:", FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 0, 4) });

            // Buscador + cargar .rfa
            var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var btnLoad = new Button { Content = "Load .rfa…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(btnLoad, Dock.Right);
            searchRow.Children.Add(btnLoad);
            searchRow.Children.Add(new Label { Content = "Search:", Width = 70, Padding = new Thickness(0, 4, 6, 0) });
            _search = new TextBox { Text = settings.Filter, Padding = new Thickness(4) };
            searchRow.Children.Add(_search);
            root.Children.Add(searchRow);

            _list = new ListBox { Height = 180 };
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
            for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _sizeBox = new ComboBox { Margin = new Thickness(0, 3, 0, 3) };
            _sizeBox.Items.Add("Auto: bigger rooms get bigger TVs");
            _sizeBox.Items.Add("Always the largest that fits");
            _sizeBox.SelectedIndex = settings.AutoSize ? 0 : 1;
            AddRow(grid, 0, NewLabel("TV size:"), _sizeBox);

            _factorBox = NewBox(settings.FactorText);
            _factorBox.ToolTip = "A TV is used for rooms up to (its width × this number) deep.\n" +
                                 "4.5 ≈ a 55\" TV (1.24 m wide) for rooms up to ~5.6 m deep.";
            _factorBox.IsEnabled = settings.AutoSize;
            _sizeBox.SelectionChanged += (s, e) => _factorBox.IsEnabled = _sizeBox.SelectedIndex == 0;
            AddRow(grid, 1, NewLabel("    Max. room depth = TV width ×"), _factorBox);

            _useBottom = new CheckBox { Content = "TV bottom edge height (from level):", IsChecked = settings.UseBottom, VerticalAlignment = VerticalAlignment.Center };
            _bottomBox = NewBox(settings.BottomText);
            _bottomBox.IsEnabled = settings.UseBottom;
            _useBottom.Checked += (s, e) => _bottomBox.IsEnabled = true;
            _useBottom.Unchecked += (s, e) => _bottomBox.IsEnabled = false;
            AddRow(grid, 2, _useBottom, _bottomBox);

            _clearBox = NewBox(settings.ClearText);
            AddRow(grid, 3, NewLabel("Min. clearance to doors/corners:"), _clearBox);

            _previewBox = new CheckBox
            {
                Content = "Preview with arrows before placing (click a wall to move the TV)",
                IsChecked = settings.Preview,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(_previewBox, 4); Grid.SetColumnSpan(_previewBox, 2);
            grid.Children.Add(_previewBox);
            root.Children.Add(grid);

            var btnPlace = new Button
            {
                Content = "Place TVs",
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true
            };
            root.Children.Add(btnPlace);

            Content = root;

            _search.TextChanged += (s, e) => RefreshList();
            btnAll.Click += (s, e) => { foreach (var it in _items.Where(Visible)) it.IsChecked = true; RefreshList(); };
            btnNone.Click += (s, e) => { foreach (var it in _items) it.IsChecked = false; RefreshList(); };
            btnLoad.Click += (s, e) => LoadFamilies();
            btnPlace.Click += (s, e) => Accept();

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

        // Los marcados siempre se ven, aunque no coincidan con la búsqueda
        private bool Visible(TypeItem it)
        {
            string q = _search.Text.Trim();
            return it.IsChecked || q.Length == 0 || it.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
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
            var ofd = new OpenFileDialog { Filter = "Revit families (*.rfa)|*.rfa", Multiselect = true, Title = "Load TV families" };
            if (ofd.ShowDialog(this) != true) return;

            try
            {
                var result = _loadRfa(ofd.FileNames);
                var keep = new HashSet<long>(_items.Where(i => i.IsChecked).Select(i => i.Id));
                foreach (var it in result.Item1)
                    it.IsChecked = keep.Contains(it.Id) || result.Item2.Contains(it.Id);
                _items = result.Item1;
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not load the families:\n{ex.Message}", Title_);
            }
        }

        // Acepta "4.5" y "4,5"
        private static double? ParseNumber(string s) =>
            double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;

        private void Accept()
        {
            if (!_items.Any(i => i.IsChecked))
            {
                MessageBox.Show(this, "Check at least one TV type.", Title_);
                return;
            }

            bool auto = _sizeBox.SelectedIndex == 0;
            if (auto)
            {
                double? f = ParseNumber(_factorBox.Text);
                if (f == null || f <= 0) { MessageBox.Show(this, "Invalid room depth factor.", Title_); return; }
                DepthFactor = f.Value;
            }

            if (_useBottom.IsChecked == true)
            {
                double? h = _parseLength(_bottomBox.Text);
                if (h == null || h < 0) { MessageBox.Show(this, "Invalid bottom edge height.", Title_); return; }
                BottomHeight = h;
            }
            else BottomHeight = null;

            double? c = _parseLength(_clearBox.Text);
            if (c == null || c < 0) { MessageBox.Show(this, "Invalid clearance.", Title_); return; }
            Clearance = c.Value;

            // Guardar opciones para la próxima corrida
            _settings.Filter = _search.Text;
            _settings.AutoSize = auto;
            _settings.FactorText = _factorBox.Text;
            _settings.UseBottom = BottomHeight.HasValue;
            _settings.BottomText = _bottomBox.Text;
            _settings.ClearText = _clearBox.Text;
            _settings.Preview = _previewBox.IsChecked == true;
            _settings.LastChecked = new HashSet<long>(SelectedIds);

            DialogResult = true;
            Close();
        }
    }
}
