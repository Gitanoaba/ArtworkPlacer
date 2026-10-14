using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ArtworkPlacer;

public class ArtworkDialog : Window
{
	private class Tile
	{
		public Border Root;

		public Border Frame;

		public Image Img;

		public Image Big;

		public TextBlock Placeholder;
	}

	private const string AllTypes = "All placeable types";

	private const string AllLevels = "All levels";

	private List<TypeItem> _items;

	private readonly List<ArtCollection> _collections;

	private readonly List<RoomChoice> _rooms;

	private readonly int _wallCount;

	private readonly DialogSettings _settings;

	private readonly Func<string, double?> _parseLength;

	private readonly Func<string[], Tuple<List<TypeItem>, HashSet<long>>> _loadRfa;

	private readonly Func<long, Thumb> _thumbs;

	private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private List<long> _scope;

	private int _refreshGen;

	private readonly ComboBox _collectionBox;

	private readonly TextBox _search;

	private readonly ItemsControl _list;

	private readonly TextBlock _shownLabel;

	private readonly ComboBox _alignBox;

	private readonly TextBox _heightBox;

	private readonly TextBox _clearBox;

	private readonly ComboBox _sideBox;

	private readonly TextBlock _sideNote;

	private readonly CheckBox _multiBox;

	private readonly TextBox _gapBox;

	private readonly TextBox _maxBox;

	private readonly CheckBox _previewBox;

	private readonly RadioButton _byRooms;

	private readonly TextBox _roomSearch;

	private readonly ComboBox _levelBox;

	private readonly StackPanel _roomList;

	private readonly TextBlock _roomCount;

	private readonly FrameworkElement _roomsView;

	private readonly FrameworkElement _wallsView;

	private readonly TextBlock _summary;

	private readonly TextBlock _status;

	private readonly Button _placeButton;

	public List<long> SelectedIds => (from i in _items
		where i.IsChecked
		select i.Id).ToList();

	public List<long> SelectedRoomIds => (from r in _rooms
		where r.IsChecked
		select r.Id).ToList();

	public bool UseRooms => _byRooms.IsChecked == true;

	public bool PickWallsRequested { get; private set; }

	public double? ArtHeight { get; private set; }

	public HeightAlign Align { get; private set; }

	public double Clearance { get; private set; }

	public double Gap { get; private set; }

	public int MaxPerFace { get; private set; }

	public ArtworkDialog(int wallCount, List<RoomChoice> rooms, List<TypeItem> items, List<ArtCollection> collections, DialogSettings settings, Func<string, double?> parseLength, Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa, Func<long, Thumb> thumbs)
	{
		_wallCount = wallCount;
		_rooms = rooms;
		_items = items;
		_collections = collections;
		_settings = settings;
		_parseLength = parseLength;
		_loadRfa = loadRfa;
		_thumbs = thumbs;
		Title = "Place Artwork";
		Width = 1120.0;
		Height = Math.Min(840.0, SystemParameters.WorkArea.Height - 40.0);
		MinWidth = 920.0;
		MinHeight = 600.0;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		ResizeMode = ResizeMode.CanResize;
		Theme.Apply(this);

		_byRooms = new RadioButton { Content = "Rooms", GroupName = "source", Style = Theme.Get("Segment") };
		RadioButton byWalls = new RadioButton { Content = "Selected walls", GroupName = "source", Style = Theme.Get("Segment") };
		_roomSearch = new TextBox();
		_levelBox = new ComboBox { Margin = new Thickness(0.0, 8.0, 0.0, 0.0) };
		_roomList = new StackPanel();
		_roomCount = new TextBlock { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
		_roomsView = BuildRoomsView();
		_wallsView = BuildWallsView();

		_collectionBox = new ComboBox { Width = 220.0, Margin = new Thickness(0.0, 0.0, 8.0, 0.0) };
		_collectionBox.Items.Add(AllTypes);
		foreach (ArtCollection collection in collections)
		{
			_collectionBox.Items.Add($"{collection.Name} ({collection.Ids.Count})");
		}
		_search = new TextBox { Text = settings.Filter, Width = 200.0 };
		_list = new ItemsControl
		{
			ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))),
			Margin = new Thickness(6.0)
		};
		_shownLabel = new TextBlock { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 0.0, 1.0) };

		_alignBox = NewAlignBox(settings.Align, includeFamilyDefault: true);
		_heightBox = new TextBox { Text = settings.HeightText, IsEnabled = settings.Align != HeightAlign.FamilyDefault };
		HookAlign(_alignBox, _heightBox, settings);
		_clearBox = new TextBox { Text = settings.ClearText };
		_sideBox = new ComboBox();
		_sideBox.Items.Add("Auto (faces the room)");
		_sideBox.Items.Add("Interior");
		_sideBox.Items.Add("Exterior");
		_sideBox.Items.Add("Both faces");
		_sideBox.SelectedIndex = (int)settings.Side;
		_sideNote = new TextBlock { Text = "Inside each room", Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
		_multiBox = new CheckBox { Content = "Several pieces on long walls", IsChecked = settings.Multiple };
		_gapBox = new TextBox { Text = settings.GapText };
		_maxBox = new TextBox { Text = settings.MaxPerFaceText };
		_previewBox = new CheckBox { Content = "Check each spot on the plan before placing", IsChecked = settings.Preview };

		_summary = new TextBlock { FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
		_status = new TextBlock { Foreground = Theme.Danger, Visibility = Visibility.Collapsed, Margin = new Thickness(0.0, 2.0, 0.0, 0.0) };
		_placeButton = new Button { IsDefault = true, Padding = new Thickness(18.0, 8.0, 18.0, 8.0) };
		Button cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16.0, 8.0, 16.0, 8.0) };
		StackPanel footerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		footerText.Children.Add(_summary);
		footerText.Children.Add(_status);

		DockPanel root = new DockPanel();
		Border header = Theme.Header("Place artwork", "Choose rooms, pick the artwork, then check every spot on the plan before anything is placed.");
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		Border footer = Theme.Footer(footerText, cancel, _placeButton);
		DockPanel.SetDock(footer, Dock.Bottom);
		root.Children.Add(footer);
		Border side = BuildSourcePanel(byWalls);
		DockPanel.SetDock(side, Dock.Left);
		root.Children.Add(side);
		root.Children.Add(BuildMain());
		Content = root;

		int num = collections.FindIndex((ArtCollection c) => c.Name == settings.Collection);
		_scope = (num >= 0) ? collections[num].Ids : null;
		_collectionBox.SelectedIndex = num + 1;
		bool byRooms = wallCount == 0 && settings.ByRooms;
		_byRooms.IsChecked = byRooms;
		byWalls.IsChecked = !byRooms;
		ShowSource();

		_byRooms.Checked += (s, e) => ShowSource();
		byWalls.Checked += (s, e) => ShowSource();
		_roomSearch.TextChanged += (s, e) => RefreshRooms();
		_levelBox.SelectionChanged += (s, e) => RefreshRooms();
		_collectionBox.SelectionChanged += (s, e) => SelectCollection(_collectionBox.SelectedIndex);
		_search.TextChanged += (s, e) => RefreshList();
		_multiBox.Checked += (s, e) => SyncMultiple();
		_multiBox.Unchecked += (s, e) => SyncMultiple();
		_previewBox.Checked += (s, e) => UpdateSummary();
		_previewBox.Unchecked += (s, e) => UpdateSummary();
		_placeButton.Click += (s, e) => Accept();
		SyncMultiple();
		SortByScope();
		RefreshRooms();
		RefreshList();
	}

	private Border BuildSourcePanel(RadioButton byWalls)
	{
		UniformGrid segments = new UniformGrid { Columns = 2 };
		segments.Children.Add(_byRooms);
		segments.Children.Add(byWalls);
		Border segmented = new Border
		{
			Background = Theme.Canvas,
			BorderBrush = Theme.Line,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(7.0),
			Padding = new Thickness(3.0),
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
			Child = segments
		};
		DockPanel dock = new DockPanel();
		TextBlock heading = Theme.Heading("Place in");
		DockPanel.SetDock(heading, Dock.Top);
		dock.Children.Add(heading);
		DockPanel.SetDock(segmented, Dock.Top);
		dock.Children.Add(segmented);
		Grid views = new Grid();
		views.Children.Add(_roomsView);
		views.Children.Add(_wallsView);
		dock.Children.Add(views);
		return new Border
		{
			Width = 320.0,
			Background = Theme.Panel,
			BorderBrush = Theme.Line,
			BorderThickness = new Thickness(0.0, 0.0, 1.0, 0.0),
			Padding = new Thickness(20.0, 20.0, 20.0, 16.0),
			Child = dock
		};
	}

	private FrameworkElement BuildRoomsView()
	{
		DockPanel dock = new DockPanel { Margin = new Thickness(0.0, 16.0, 0.0, 0.0) };
		if (_rooms.Count == 0)
		{
			TextBlock empty = Theme.Note("This model has no placed rooms. Add rooms in Revit, or switch to Selected walls.");
			DockPanel.SetDock(empty, Dock.Top);
			dock.Children.Add(empty);
			return dock;
		}
		Grid search = Theme.WithPlaceholder(_roomSearch, "Search room name or number");
		DockPanel.SetDock(search, Dock.Top);
		dock.Children.Add(search);
		List<string> levels = _rooms.Select((RoomChoice r) => r.Level).Where((string l) => l.Length > 0).Distinct().OrderBy((string l) => l).ToList();
		if (levels.Count > 1)
		{
			_levelBox.Items.Add(AllLevels);
			levels.ForEach((string l) => _levelBox.Items.Add(l));
			int index = _levelBox.Items.IndexOf(_settings.Level);
			_levelBox.SelectedIndex = Math.Max(0, index);
			DockPanel.SetDock(_levelBox, Dock.Top);
			dock.Children.Add(_levelBox);
		}
		Button all = new Button { Content = "Select all shown", Style = Theme.Get("Link") };
		Button none = new Button { Content = "Clear", Style = Theme.Get("Link") };
		all.Click += (s, e) => SetRooms(VisibleRooms(), value: true);
		none.Click += (s, e) => SetRooms(_rooms, value: false);
		DockPanel actions = new DockPanel { Margin = new Thickness(-6.0, 8.0, 0.0, 0.0) };
		actions.Children.Add(all);
		actions.Children.Add(none);
		DockPanel.SetDock(_roomCount, Dock.Right);
		_roomCount.HorizontalAlignment = HorizontalAlignment.Right;
		actions.Children.Insert(0, _roomCount);
		DockPanel.SetDock(actions, Dock.Bottom);
		dock.Children.Add(actions);
		dock.Children.Add(new ScrollViewer
		{
			Margin = new Thickness(-6.0, 10.0, -12.0, 0.0),
			Padding = new Thickness(0.0, 0.0, 6.0, 0.0),
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = _roomList
		});
		return dock;
	}

	private FrameworkElement BuildWallsView()
	{
		StackPanel panel = new StackPanel { Margin = new Thickness(0.0, 16.0, 0.0, 0.0) };
		StackPanel card = new StackPanel();
		card.Children.Add(new TextBlock
		{
			Text = (_wallCount == 0) ? "No walls selected" : $"{_wallCount} {Plural(_wallCount, "wall")} selected in Revit",
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold
		});
		TextBlock hint = Theme.Note("The artwork goes on the wall face set under Placement. You can switch faces on the plan before placing.");
		hint.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
		card.Children.Add(hint);
		panel.Children.Add(new Border
		{
			Background = Theme.Canvas,
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(14.0),
			Child = card
		});
		Button pick = new Button
		{
			Content = (_wallCount == 0) ? "Pick walls in the model" : "Pick walls again",
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
		};
		pick.Click += (s, e) => RequestPick();
		panel.Children.Add(pick);
		TextBlock how = Theme.Note("This window closes while you pick. Click Finish in Revit's options bar to come back.");
		how.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
		how.FontSize = 12.0;
		panel.Children.Add(how);
		return panel;
	}

	private Grid BuildMain()
	{
		Grid grid = new Grid { Margin = new Thickness(24.0, 20.0, 24.0, 20.0) };
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star), MinHeight = 180.0 });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		DockPanel top = new DockPanel { Margin = new Thickness(0.0, 0.0, 0.0, 12.0) };
		Button load = new Button { Content = "Load family…", Margin = new Thickness(8.0, 0.0, 0.0, 0.0) };
		load.Click += (s, e) => LoadFamilies();
		StackPanel tools = new StackPanel { Orientation = Orientation.Horizontal };
		tools.Children.Add(_collectionBox);
		tools.Children.Add(Theme.WithPlaceholder(_search, "Search artwork"));
		tools.Children.Add(load);
		DockPanel.SetDock(tools, Dock.Right);
		top.Children.Add(tools);
		StackPanel title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		title.Children.Add(Theme.Heading("Artwork"));
		_shownLabel.Margin = new Thickness(10.0, 1.0, 0.0, 0.0);
		title.Children.Add(_shownLabel);
		top.Children.Add(title);
		Grid.SetRow(top, 0);
		grid.Children.Add(top);

		Border gallery = new Border
		{
			Background = Theme.Panel,
			BorderBrush = Theme.Line,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(2.0),
			Child = new ScrollViewer
			{
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				Content = _list
			}
		};
		Grid.SetRow(gallery, 1);
		grid.Children.Add(gallery);

		Button all = new Button { Content = "Select all shown", Style = Theme.Get("Link") };
		Button none = new Button { Content = "Clear", Style = Theme.Get("Link") };
		all.Click += (s, e) =>
		{
			foreach (TypeItem item in _items.Where(Visible))
			{
				item.IsChecked = true;
			}
			RefreshList();
		};
		none.Click += (s, e) =>
		{
			_items.ForEach((TypeItem i) => i.IsChecked = false);
			RefreshList();
		};
		StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6.0, 6.0, 0.0, 0.0) };
		actions.Children.Add(all);
		actions.Children.Add(none);
		Grid.SetRow(actions, 2);
		grid.Children.Add(actions);

		TextBlock placement = Theme.Heading("Placement");
		placement.Margin = new Thickness(0.0, 16.0, 0.0, 10.0);
		Grid.SetRow(placement, 3);
		grid.Children.Add(placement);
		Border card = new Border
		{
			Background = Theme.Panel,
			BorderBrush = Theme.Line,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(18.0, 12.0, 18.0, 12.0),
			Child = BuildPlacementGrid()
		};
		Grid.SetRow(card, 4);
		grid.Children.Add(card);
		return grid;
	}

	private Grid BuildPlacementGrid()
	{
		Grid grid = new Grid();
		foreach (GridLength w in new[]
		{
			new GridLength(130.0),
			new GridLength(1.0, GridUnitType.Star),
			new GridLength(36.0),
			new GridLength(190.0),
			new GridLength(1.0, GridUnitType.Star)
		})
		{
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
		}
		for (int i = 0; i < 3; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		}
		Place(grid, _alignBox, 0, 0);
		Place(grid, _heightBox, 0, 1);
		Place(grid, Label("Clear of doors and corners"), 0, 3);
		Place(grid, _clearBox, 0, 4);
		Place(grid, Label("Wall face"), 1, 0);
		Place(grid, _sideBox, 1, 1);
		Place(grid, _sideNote, 1, 1);
		Place(grid, _multiBox, 1, 3, 2);
		Place(grid, _previewBox, 2, 0, 2);
		Grid pieces = new Grid();
		pieces.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		pieces.ColumnDefinitions.Add(new ColumnDefinition());
		pieces.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		pieces.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64.0) });
		Place(pieces, Label("Gap"), 0, 0);
		_gapBox.Margin = new Thickness(8.0, 0.0, 12.0, 0.0);
		Place(pieces, _gapBox, 0, 1);
		Place(pieces, Label("Max per face"), 0, 2);
		_maxBox.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		Place(pieces, _maxBox, 0, 3);
		pieces.Margin = new Thickness(24.0, 4.0, 0.0, 4.0);
		Place(grid, pieces, 2, 3, 2);
		return grid;
	}

	private static void Place(Grid grid, FrameworkElement e, int row, int col, int span = 1)
	{
		if (e.Margin == default(Thickness))
		{
			e.Margin = new Thickness(0.0, 4.0, (col == 0 && span == 1) ? 8.0 : 0.0, 4.0);
		}
		e.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetRow(e, row);
		Grid.SetColumn(e, col);
		Grid.SetColumnSpan(e, span);
		grid.Children.Add(e);
	}

	private static TextBlock Label(string text)
	{
		return new TextBlock { Text = text, Foreground = Theme.Muted };
	}

	private static string Plural(int n, string word)
	{
		return (n == 1) ? word : (word + "s");
	}

	internal static ComboBox NewAlignBox(HeightAlign current, bool includeFamilyDefault)
	{
		ComboBox comboBox = new ComboBox();
		comboBox.Items.Add("Center at");
		comboBox.Items.Add("Bottom edge at");
		comboBox.Items.Add("Top edge at");
		if (includeFamilyDefault)
		{
			comboBox.Items.Add("Family default");
		}
		comboBox.ToolTip = "Height measured from each wall's level";
		comboBox.SelectedIndex = (int)((includeFamilyDefault || current != HeightAlign.FamilyDefault) ? current : HeightAlign.Center);
		return comboBox;
	}

	internal static void HookAlign(ComboBox alignBox, TextBox heightBox, DialogSettings settings)
	{
		int previous = alignBox.SelectedIndex;
		alignBox.SelectionChanged += (object s, SelectionChangedEventArgs e) =>
		{
			int selectedIndex = alignBox.SelectedIndex;
			string[] heightDefaults = settings.HeightDefaults;
			if (heightDefaults != null && previous >= 0 && previous < 3 && selectedIndex < 3 && heightBox.Text.Trim() == heightDefaults[previous])
			{
				heightBox.Text = heightDefaults[selectedIndex];
			}
			heightBox.IsEnabled = selectedIndex != 3;
			previous = selectedIndex;
		};
	}

	private void ShowSource()
	{
		bool rooms = UseRooms;
		_roomsView.Visibility = rooms ? Visibility.Visible : Visibility.Collapsed;
		_wallsView.Visibility = rooms ? Visibility.Collapsed : Visibility.Visible;
		_sideBox.Visibility = rooms ? Visibility.Collapsed : Visibility.Visible;
		_sideNote.Visibility = rooms ? Visibility.Visible : Visibility.Collapsed;
		UpdateSummary();
	}

	private void SyncMultiple()
	{
		bool on = _multiBox.IsChecked == true;
		_gapBox.IsEnabled = on;
		_maxBox.IsEnabled = on;
	}

	private bool LevelShown(RoomChoice r)
	{
		string level = _levelBox.SelectedItem as string;
		return level == null || level == AllLevels || r.Level == level;
	}

	private static bool Has(string text, string term)
	{
		return text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private List<IGrouping<string, RoomChoice>> VisibleGroups(out bool forceOpen)
	{
		string term = _roomSearch.Text.Trim();
		forceOpen = term.Length > 0;
		return _rooms.Where((RoomChoice r) => LevelShown(r) && (term.Length == 0 || Has(r.Name, term) || Has(r.Number, term)))
			.GroupBy((RoomChoice r) => r.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private List<RoomChoice> VisibleRooms()
	{
		return VisibleGroups(out var _).SelectMany((IGrouping<string, RoomChoice> g) => g).ToList();
	}

	private void SetRooms(IEnumerable<RoomChoice> rooms, bool value)
	{
		foreach (RoomChoice room in rooms)
		{
			room.IsChecked = value;
		}
		RefreshRooms();
	}

	private void RefreshRooms()
	{
		if (_rooms.Count == 0)
		{
			UpdateSummary();
			return;
		}
		_roomList.Children.Clear();
		List<IGrouping<string, RoomChoice>> groups = VisibleGroups(out var forceOpen);
		foreach (IGrouping<string, RoomChoice> group in groups)
		{
			List<RoomChoice> members = group.ToList();
			bool open = members.Count > 1 && (forceOpen || _expanded.Contains(group.Key));
			_roomList.Children.Add(GroupRow(group.Key, members, open));
			if (open)
			{
				members.ForEach((RoomChoice r) => _roomList.Children.Add(RoomRow(r)));
			}
		}
		if (groups.Count == 0)
		{
			TextBlock none = Theme.Note($"No rooms match “{_roomSearch.Text.Trim()}”.");
			none.Margin = new Thickness(6.0, 4.0, 0.0, 0.0);
			_roomList.Children.Add(none);
		}
		int chosen = _rooms.Count((RoomChoice r) => r.IsChecked);
		_roomCount.Text = (chosen == 0) ? $"{_rooms.Count} {Plural(_rooms.Count, "room")}" : $"{chosen} of {_rooms.Count} chosen";
		UpdateSummary();
	}

	private Border GroupRow(string name, List<RoomChoice> members, bool open)
	{
		int chosen = members.Count((RoomChoice r) => r.IsChecked);
		CheckBox check = new CheckBox
		{
			IsThreeState = true,
			IsChecked = (chosen == 0) ? false : ((chosen == members.Count) ? new bool?(true) : null),
			IsHitTestVisible = false,
			Focusable = false,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, 2.0, 10.0, 0.0)
		};
		StackPanel text = new StackPanel();
		text.Children.Add(new TextBlock
		{
			Text = name,
			FontWeight = FontWeights.Medium,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		if (members.Count == 1)
		{
			text.Children.Add(new TextBlock
			{
				Text = RoomDetail(members[0]),
				Foreground = Theme.Muted,
				FontSize = 12.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
		}
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.Children.Add(check);
		Grid.SetColumn(text, 1);
		grid.Children.Add(text);
		Border row = RowShell(grid, 0.0);
		row.ToolTip = (members.Count > 1) ? $"Select every {name} ({members.Count} rooms)" : null;
		if (members.Count > 1)
		{
			StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
			right.Children.Add(new Border
			{
				Background = (chosen > 0) ? Theme.AccentSoft : Theme.Canvas,
				CornerRadius = new CornerRadius(9.0),
				Padding = new Thickness(8.0, 1.0, 8.0, 2.0),
				Child = new TextBlock
				{
					Text = (chosen > 0 && chosen < members.Count) ? $"{chosen} of {members.Count}" : members.Count.ToString(),
					FontSize = 12.0,
					Foreground = (chosen > 0) ? Theme.AccentStrong : Theme.Muted
				}
			});
			Button chevron = new Button
			{
				Style = Theme.Get("Link"),
				Padding = new Thickness(6.0),
				Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
				ToolTip = open ? "Hide rooms" : "Show rooms",
				Content = new Path
				{
					Data = Geometry.Parse(open ? "M0,0 L4,4 L8,0" : "M0,0 L4,4 L0,8"),
					Stroke = Theme.Muted,
					StrokeThickness = 1.5,
					StrokeStartLineCap = PenLineCap.Round,
					StrokeEndLineCap = PenLineCap.Round,
					StrokeLineJoin = PenLineJoin.Round,
					Width = 8.0,
					Height = 8.0
				}
			};
			chevron.Click += (s, e) =>
			{
				if (!_expanded.Remove(name))
				{
					_expanded.Add(name);
				}
				RefreshRooms();
			};
			right.Children.Add(chevron);
			Grid.SetColumn(right, 2);
			grid.Children.Add(right);
		}
		HookToggle(row, () => SetRooms(members, chosen < members.Count));
		return row;
	}

	private Border RoomRow(RoomChoice room)
	{
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.Children.Add(new CheckBox
		{
			IsChecked = room.IsChecked,
			IsHitTestVisible = false,
			Focusable = false,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		});
		TextBlock text = new TextBlock
		{
			Text = RoomDetail(room),
			Foreground = room.IsChecked ? Theme.Ink : Theme.Muted,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		Grid.SetColumn(text, 1);
		grid.Children.Add(text);
		Border row = RowShell(grid, 26.0);
		HookToggle(row, () => SetRooms(new[] { room }, !room.IsChecked));
		return row;
	}

	private static string RoomDetail(RoomChoice r)
	{
		string where = string.Join(", ", new[] { r.Level, r.AreaText }.Where((string s) => !string.IsNullOrEmpty(s)));
		if (r.Number.Length == 0)
		{
			return where;
		}
		return (where.Length == 0) ? r.Number : (r.Number + "  –  " + where);
	}

	private static Border RowShell(UIElement child, double indent)
	{
		Border row = new Border
		{
			Background = Brushes.Transparent,
			CornerRadius = new CornerRadius(6.0),
			Padding = new Thickness(8.0, 6.0, 6.0, 6.0),
			Margin = new Thickness(indent, 0.0, 0.0, 1.0),
			Cursor = Cursors.Hand,
			Focusable = true,
			FocusVisualStyle = Theme.Get("Focus"),
			Child = child
		};
		row.MouseEnter += (s, e) => row.Background = Theme.Canvas;
		row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
		return row;
	}

	private static void HookToggle(Border row, Action toggle)
	{
		row.MouseLeftButtonUp += (s, e) =>
		{
			if (!e.Handled)
			{
				toggle();
			}
		};
		row.KeyDown += (s, e) =>
		{
			if (e.Key == Key.Space || e.Key == Key.Enter)
			{
				e.Handled = true;
				toggle();
			}
		};
	}

	private void SelectCollection(int index)
	{
		_scope = ((index <= 0) ? null : _collections[index - 1].Ids);
		if (_scope != null)
		{
			HashSet<long> hashSet = new HashSet<long>(_scope);
			foreach (TypeItem item in _items)
			{
				item.IsChecked = hashSet.Contains(item.Id);
			}
			_search.Text = "";
		}
		SortByScope();
		RefreshList();
	}

	private void SortByScope()
	{
		if (_scope == null)
		{
			_items = _items.OrderBy((TypeItem i) => i.Label).ToList();
			return;
		}
		Dictionary<long, int> order = _scope.Select((long id, int i) => new { id, i }).ToDictionary(x => x.id, x => x.i);
		_items = (from i in _items
			orderby (!order.TryGetValue(i.Id, out var value)) ? int.MaxValue : value, i.Label
			select i).ToList();
	}

	private bool Visible(TypeItem it)
	{
		if (_scope != null && !_scope.Contains(it.Id))
		{
			return false;
		}
		string text = _search.Text.Trim();
		if ((_scope != null || !it.IsChecked) && text.Length != 0)
		{
			return Has(it.Label, text);
		}
		return true;
	}

	private void RefreshList()
	{
		_list.Items.Clear();
		int gen = ++_refreshGen;
		Queue<Tuple<TypeItem, Tile>> queue = new Queue<Tuple<TypeItem, Tile>>();
		foreach (TypeItem item in _items.Where(Visible))
		{
			Tile tile = BuildTile(item);
			_list.Items.Add(tile.Root);
			queue.Enqueue(Tuple.Create(item, tile));
		}
		_shownLabel.Text = $"{queue.Count} shown";
		if (queue.Count == 0)
		{
			_list.Items.Add(new TextBlock
			{
				Text = (_items.Count == 0) ? "No placeable families in this model yet. Use Load family to add artwork." : "Nothing matches this search. Clear it or pick another collection.",
				Foreground = Theme.Muted,
				Margin = new Thickness(12.0)
			});
		}
		UpdateSummary();
		LoadThumbs(gen, queue);
	}

	private Tile BuildTile(TypeItem item)
	{
		Tile t = new Tile
		{
			Img = new Image { Stretch = Stretch.Uniform },
			Big = new Image { Width = 280.0, Stretch = Stretch.Uniform },
			Placeholder = new TextBlock
			{
				Text = "Loading preview",
				Foreground = Theme.Muted,
				FontSize = 11.0,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		t.Frame = new Border
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Child = t.Img
		};
		Grid thumb = new Grid();
		thumb.Children.Add(new Border
		{
			Background = Theme.Canvas,
			CornerRadius = new CornerRadius(6.0),
			Height = 98.0,
			Padding = new Thickness(9.0),
			Child = new Grid { Children = { t.Placeholder, t.Frame } }
		});
		Border badge = new Border
		{
			Width = 20.0,
			Height = 20.0,
			CornerRadius = new CornerRadius(10.0),
			Background = Theme.Accent,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, 6.0, 6.0, 0.0),
			Child = new Path
			{
				Data = Geometry.Parse("M5.5,10 L8.5,13 L14.5,7"),
				Stroke = Brushes.White,
				StrokeThickness = 2.0,
				StrokeStartLineCap = PenLineCap.Round,
				StrokeEndLineCap = PenLineCap.Round,
				StrokeLineJoin = PenLineJoin.Round,
				Margin = new Thickness(-0.5)
			}
		};
		thumb.Children.Add(badge);
		int num = item.Label.LastIndexOf(" : ", StringComparison.Ordinal);
		string family = (num > 0) ? item.Label.Substring(0, num) : item.Label;
		string type = (num > 0) ? item.Label.Substring(num + 3) : "";
		StackPanel body = new StackPanel();
		body.Children.Add(thumb);
		body.Children.Add(new TextBlock
		{
			Text = (type.Length > 0) ? type : family,
			FontWeight = FontWeights.Medium,
			FontSize = 12.0,
			Margin = new Thickness(2.0, 8.0, 2.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		body.Children.Add(new TextBlock
		{
			Text = (type.Length > 0) ? family : item.Kind,
			Foreground = Theme.Muted,
			FontSize = 11.0,
			Margin = new Thickness(2.0, 1.0, 2.0, 2.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		t.Root = new Border
		{
			Width = 128.0,
			Margin = new Thickness(5.0),
			CornerRadius = new CornerRadius(8.0),
			Cursor = Cursors.Hand,
			Focusable = true,
			FocusVisualStyle = Theme.Get("Focus"),
			Child = body
		};
		StackPanel tip = new StackPanel();
		tip.Children.Add(t.Big);
		tip.Children.Add(new TextBlock { Text = item.Label, FontWeight = FontWeights.Medium, Margin = new Thickness(0.0, 8.0, 0.0, 0.0) });
		tip.Children.Add(new TextBlock { Text = item.Kind, Foreground = Theme.Muted });
		t.Root.ToolTip = tip;
		ToolTipService.SetInitialShowDelay(t.Root, 400);
		bool hover = false;
		Paint();
		HookToggle(t.Root, () =>
		{
			item.IsChecked = !item.IsChecked;
			Paint();
			UpdateSummary();
		});
		t.Root.MouseEnter += (s, e) =>
		{
			hover = true;
			Paint();
		};
		t.Root.MouseLeave += (s, e) =>
		{
			hover = false;
			Paint();
		};
		return t;
		void Paint()
		{
			bool on = item.IsChecked;
			t.Root.Background = on ? Theme.AccentSoft : Theme.Panel;
			t.Root.BorderBrush = on ? Theme.Accent : (hover ? Theme.Muted : Theme.Line);
			t.Root.BorderThickness = new Thickness(on ? 2.0 : 1.0);
			t.Root.Padding = new Thickness(on ? 7.0 : 8.0);
			badge.Visibility = on ? Visibility.Visible : Visibility.Hidden;
		}
	}

	private void LoadThumbs(int gen, Queue<Tuple<TypeItem, Tile>> pending)
	{
		if (_thumbs == null)
		{
			foreach (Tuple<TypeItem, Tile> item2 in pending)
			{
				item2.Item2.Placeholder.Text = "No preview";
			}
			return;
		}
		Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
		{
			if (gen == _refreshGen && pending.Count != 0)
			{
				Tuple<TypeItem, Tile> tuple = pending.Dequeue();
				Thumb thumb = _thumbs(tuple.Item1.Id);
				Tile item = tuple.Item2;
				item.Img.Source = thumb?.Image;
				item.Big.Source = thumb?.Image;
				item.Placeholder.Text = ((thumb?.Image == null) ? "No preview" : "");
				if (thumb != null && thumb.Frame.HasValue)
				{
					item.Frame.BorderBrush = new SolidColorBrush(thumb.Frame.Value);
					item.Frame.BorderThickness = new Thickness(3.0);
				}
				LoadThumbs(gen, pending);
			}
		}));
	}

	private void UpdateSummary()
	{
		if (_placeButton == null)
		{
			return;
		}
		int art = _items.Count((TypeItem i) => i.IsChecked);
		int where = UseRooms ? _rooms.Count((RoomChoice r) => r.IsChecked) : _wallCount;
		string target = UseRooms ? Plural(where, "room") : Plural(where, "wall");
		_summary.Text = $"{where} {target} and {art} {Plural(art, "artwork")} chosen";
		_placeButton.Content = (_previewBox.IsChecked == true) ? "Review on plan" : "Place artwork";
		_status.Visibility = Visibility.Collapsed;
	}

	private void Fail(string message)
	{
		_status.Text = message;
		_status.Visibility = Visibility.Visible;
	}

	private void LoadFamilies()
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "Revit families (*.rfa)|*.rfa",
			Multiselect = true,
			Title = "Load artwork families"
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
			_collectionBox.SelectedIndex = 0;
			SortByScope();
			RefreshList();
		}
		catch (Exception ex)
		{
			Fail("Revit couldn't load those families: " + ex.Message);
		}
	}

	private void RequestPick()
	{
		SaveSettings();
		PickWallsRequested = true;
		DialogResult = true;
		Close();
	}

	private void SaveSettings()
	{
		_settings.Collection = ((_collectionBox.SelectedIndex > 0) ? _collections[_collectionBox.SelectedIndex - 1].Name : null);
		_settings.Filter = _search.Text;
		_settings.HeightText = _heightBox.Text;
		_settings.Align = (HeightAlign)_alignBox.SelectedIndex;
		_settings.ClearText = _clearBox.Text;
		_settings.Side = (SideMode)_sideBox.SelectedIndex;
		_settings.Multiple = _multiBox.IsChecked == true;
		_settings.GapText = _gapBox.Text;
		_settings.MaxPerFaceText = _maxBox.Text;
		_settings.Preview = _previewBox.IsChecked == true;
		_settings.LastChecked = new HashSet<long>(SelectedIds);
		_settings.ByRooms = UseRooms;
		_settings.Level = _levelBox.SelectedItem as string;
		_settings.LastRooms = new HashSet<long>(SelectedRoomIds);
	}

	private void Accept()
	{
		if (UseRooms && !_rooms.Any((RoomChoice r) => r.IsChecked))
		{
			Fail((_rooms.Count == 0) ? "This model has no rooms. Switch to Selected walls." : "Choose at least one room on the left.");
			return;
		}
		if (!UseRooms && _wallCount == 0)
		{
			Fail("Pick walls in the model first, or switch to Rooms.");
			return;
		}
		if (!_items.Any((TypeItem i) => i.IsChecked))
		{
			Fail("Choose at least one artwork.");
			return;
		}
		Align = (HeightAlign)_alignBox.SelectedIndex;
		ArtHeight = null;
		if (Align != HeightAlign.FamilyDefault)
		{
			double? num = _parseLength(_heightBox.Text);
			if (!num.HasValue || num <= 0.0)
			{
				Fail("Enter the height as a length, like " + (_settings.HeightDefaults?[0] ?? "150 cm") + ".");
				_heightBox.Focus();
				return;
			}
			ArtHeight = num;
		}
		double? num2 = _parseLength(_clearBox.Text);
		if (!num2.HasValue || num2 < 0.0)
		{
			Fail("Enter the clearance as a length, or 0 for none.");
			_clearBox.Focus();
			return;
		}
		Clearance = num2.Value;
		Gap = 0.0;
		MaxPerFace = 1;
		if (_multiBox.IsChecked == true)
		{
			double? num3 = _parseLength(_gapBox.Text);
			if (!num3.HasValue || num3 < 0.0)
			{
				Fail("Enter the gap between pieces as a length.");
				_gapBox.Focus();
				return;
			}
			if (!int.TryParse(_maxBox.Text.Trim(), out var result) || result < 1)
			{
				Fail("Max per face must be a whole number, 1 or more.");
				_maxBox.Focus();
				return;
			}
			Gap = num3.Value;
			MaxPerFace = result;
		}
		SaveSettings();
		DialogResult = true;
		Close();
	}
}
