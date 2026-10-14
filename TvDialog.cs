using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ArtworkPlacer;

public class TvDialog : Window
{
	private const string Title_ = "Place TV";

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

	public List<long> SelectedIds => (from i in _items
		where i.IsChecked
		select i.Id).ToList();

	public double? BottomHeight { get; private set; }

	public double Clearance { get; private set; }

	public double DepthFactor { get; private set; }

	public TvDialog(int wallCount, List<TypeItem> items, TvSettings settings, Func<string, double?> parseLength, Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa)
	{
		_items = items;
		_settings = settings;
		_parseLength = parseLength;
		_loadRfa = loadRfa;
		Title = "Place TV";
		Width = 580.0;
		SizeToContent = SizeToContent.Height;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		ResizeMode = ResizeMode.NoResize;
		Theme.Apply(this);
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(24.0, 18.0, 24.0, 20.0)
		};
		TextBlock types = Theme.Heading("TV types");
		types.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		stackPanel.Children.Add(types);
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		Button button = new Button
		{
			Content = "Load family…",
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0)
		};
		DockPanel.SetDock(button, Dock.Right);
		dockPanel.Children.Add(button);
		_search = new TextBox
		{
			Text = settings.Filter,
		};
		dockPanel.Children.Add(Theme.WithPlaceholder(_search, "Search TV types"));
		stackPanel.Children.Add(dockPanel);
		_list = new ListBox
		{
			Height = 180.0
		};
		stackPanel.Children.Add(_list);
		DockPanel dockPanel2 = new DockPanel
		{
			Margin = new Thickness(-6.0, 6.0, 0.0, 14.0)
		};
		Button button2 = new Button
		{
			Content = "Select all shown",
			Style = Theme.Get("Link")
		};
		Button button3 = new Button
		{
			Content = "Clear",
			Style = Theme.Get("Link")
		};
		_countLabel = new Label
		{
			Foreground = Theme.Muted,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		dockPanel2.Children.Add(button2);
		dockPanel2.Children.Add(button3);
		dockPanel2.Children.Add(_countLabel);
		stackPanel.Children.Add(dockPanel2);
		Grid grid = new Grid
		{
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = new GridLength(260.0)
				},
				new ColumnDefinition()
			}
		};
		for (int i = 0; i < 5; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
		}
		_sizeBox = new ComboBox
		{
			Margin = new Thickness(0.0, 3.0, 0.0, 3.0)
		};
		_sizeBox.Items.Add("Auto: bigger rooms get bigger TVs");
		_sizeBox.Items.Add("Always the largest that fits");
		_sizeBox.SelectedIndex = ((!settings.AutoSize) ? 1 : 0);
		AddRow(grid, 0, NewLabel("TV size:"), _sizeBox);
		_factorBox = NewBox(settings.FactorText);
		_factorBox.ToolTip = "A TV is used for rooms up to (its width × this number) deep.\n4.5 ≈ a 55\" TV (1.24 m wide) for rooms up to ~5.6 m deep.";
		_factorBox.IsEnabled = settings.AutoSize;
		_sizeBox.SelectionChanged += (object s, SelectionChangedEventArgs e) =>
		{
			_factorBox.IsEnabled = _sizeBox.SelectedIndex == 0;
		};
		AddRow(grid, 1, NewLabel("    Max. room depth = TV width ×"), _factorBox);
		_useBottom = new CheckBox
		{
			Content = "TV bottom edge height (from level):",
			IsChecked = settings.UseBottom,
			VerticalAlignment = VerticalAlignment.Center
		};
		_bottomBox = NewBox(settings.BottomText);
		_bottomBox.IsEnabled = settings.UseBottom;
		_useBottom.Checked += (object s, RoutedEventArgs e) =>
		{
			_bottomBox.IsEnabled = true;
		};
		_useBottom.Unchecked += (object s, RoutedEventArgs e) =>
		{
			_bottomBox.IsEnabled = false;
		};
		AddRow(grid, 2, _useBottom, _bottomBox);
		_clearBox = NewBox(settings.ClearText);
		AddRow(grid, 3, NewLabel("Min. clearance to doors/corners:"), _clearBox);
		_previewBox = new CheckBox
		{
			Content = "Preview with arrows before placing (click a wall to move the TV)",
			IsChecked = settings.Preview,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		Grid.SetRow(_previewBox, 4);
		Grid.SetColumnSpan(_previewBox, 2);
		grid.Children.Add(_previewBox);
		stackPanel.Children.Add(grid);
		Button button4 = new Button
		{
			Content = "Place TVs",
			Padding = new Thickness(18.0, 8.0, 18.0, 8.0),
			IsDefault = true
		};
		Button cancel = new Button
		{
			Content = "Cancel",
			Padding = new Thickness(16.0, 8.0, 16.0, 8.0),
			IsCancel = true
		};
		DockPanel root = new DockPanel();
		Border header = Theme.Header("Place TVs", $"{wallCount} wall(s) selected. Each closed room formed by these walls gets one TV; walls that don't close a room get one TV each.");
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		Border footer = Theme.Footer(null, cancel, button4);
		DockPanel.SetDock(footer, Dock.Bottom);
		root.Children.Add(footer);
		root.Children.Add(stackPanel);
		Content = root;
		_search.TextChanged += (object s, TextChangedEventArgs e) =>
		{
			RefreshList();
		};
		button2.Click += (object s, RoutedEventArgs e) =>
		{
			foreach (TypeItem item in _items.Where(Visible))
			{
				item.IsChecked = true;
			}
			RefreshList();
		};
		button3.Click += (object s, RoutedEventArgs e) =>
		{
			foreach (TypeItem item2 in _items)
			{
				item2.IsChecked = false;
			}
			RefreshList();
		};
		button.Click += (object s, RoutedEventArgs e) =>
		{
			LoadFamilies();
		};
		button4.Click += (object s, RoutedEventArgs e) =>
		{
			Accept();
		};
		RefreshList();
	}

	private static TextBox NewBox(string text)
	{
		return new TextBox
		{
			Text = text,
			Margin = new Thickness(0.0, 3.0, 0.0, 3.0),
		};
	}

	private static Label NewLabel(string text)
	{
		return new Label
		{
			Content = text,
			VerticalAlignment = VerticalAlignment.Center,
			Padding = new Thickness(0.0)
		};
	}

	private static void AddRow(Grid grid, int row, UIElement left, UIElement right)
	{
		Grid.SetRow(left, row);
		Grid.SetColumn(left, 0);
		Grid.SetRow(right, row);
		Grid.SetColumn(right, 1);
		grid.Children.Add(left);
		grid.Children.Add(right);
	}

	private bool Visible(TypeItem it)
	{
		string text = _search.Text.Trim();
		if (!it.IsChecked && text.Length != 0)
		{
			return it.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return true;
	}

	private void RefreshList()
	{
		_list.Items.Clear();
		foreach (TypeItem item2 in _items.Where(Visible))
		{
			TypeItem item = item2;
			CheckBox checkBox = new CheckBox
			{
				Content = item.Label + "   [" + item.Kind + "]",
				IsChecked = item.IsChecked
			};
			checkBox.Checked += (object s, RoutedEventArgs e) =>
			{
				item.IsChecked = true;
				UpdateCount();
			};
			checkBox.Unchecked += (object s, RoutedEventArgs e) =>
			{
				item.IsChecked = false;
				UpdateCount();
			};
			_list.Items.Add(checkBox);
		}
		UpdateCount();
	}

	private void UpdateCount()
	{
		_countLabel.Content = $"{_items.Count((TypeItem i) => i.IsChecked)} selected";
	}

	private void LoadFamilies()
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "Revit families (*.rfa)|*.rfa",
			Multiselect = true,
			Title = "Load TV families"
		};
		if (openFileDialog.ShowDialog(this) != true)
		{
			return;
		}
		try
		{
			Tuple<List<TypeItem>, HashSet<long>> tuple = _loadRfa(openFileDialog.FileNames);
			HashSet<long> hashSet = new HashSet<long>(from i in _items
				where i.IsChecked
				select i.Id);
			foreach (TypeItem item in tuple.Item1)
			{
				item.IsChecked = hashSet.Contains(item.Id) || tuple.Item2.Contains(item.Id);
			}
			_items = tuple.Item1;
			RefreshList();
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "Could not load the families:\n" + ex.Message, "Place TV");
		}
	}

	private static double? ParseNumber(string s)
	{
		if (!double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return null;
		}
		return result;
	}

	private void Accept()
	{
		if (!_items.Any((TypeItem i) => i.IsChecked))
		{
			MessageBox.Show(this, "Check at least one TV type.", "Place TV");
			return;
		}
		bool flag = _sizeBox.SelectedIndex == 0;
		if (flag)
		{
			double? num = ParseNumber(_factorBox.Text);
			if (!num.HasValue || num <= 0.0)
			{
				MessageBox.Show(this, "Invalid room depth factor.", "Place TV");
				return;
			}
			DepthFactor = num.Value;
		}
		if (_useBottom.IsChecked == true)
		{
			double? num2 = _parseLength(_bottomBox.Text);
			if (!num2.HasValue || num2 < 0.0)
			{
				MessageBox.Show(this, "Invalid bottom edge height.", "Place TV");
				return;
			}
			BottomHeight = num2;
		}
		else
		{
			BottomHeight = null;
		}
		double? num3 = _parseLength(_clearBox.Text);
		if (!num3.HasValue || num3 < 0.0)
		{
			MessageBox.Show(this, "Invalid clearance.", "Place TV");
			return;
		}
		Clearance = num3.Value;
		_settings.Filter = _search.Text;
		_settings.AutoSize = flag;
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
