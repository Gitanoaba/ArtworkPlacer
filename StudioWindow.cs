using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ArtworkPlacer;

public class StudioWindow : Window
{
	private static readonly string[] MeetingWords = { "meeting", "conference", "boardroom", "board room", "ישיבות", "ישיבה" };

	private readonly IStudioHost _host;

	private readonly StudioSettings _settings;

	private readonly PlanView _plan = new PlanView();

	private readonly ComboBox _levelBox = new ComboBox { Width = 200.0 };

	private readonly StackPanel _selection = new StackPanel();

	private readonly StackPanel _artList = new StackPanel();

	private readonly StackPanel _tvList = new StackPanel();

	private readonly ComboBox _alignBox;

	private readonly TextBox _heightBox = new TextBox();

	private readonly TextBox _clearBox = new TextBox();

	private readonly TextBox _maxBox = new TextBox();

	private readonly TextBox _gapBox = new TextBox();

	private readonly TextBox _tvBottomBox = new TextBox();

	private readonly CheckBox _autoSize = new CheckBox { Content = "Bigger rooms get bigger TVs" };

	private readonly CheckBox _drywallOnly = new CheckBox { Content = "Only on drywall" };

	private readonly CheckBox _artInOffices = new CheckBox { Content = "Allow artwork in private offices" };

	private readonly ComboBox _collectionBox = new ComboBox();

	private const string TickedTypes = "Ticked types below";

	private const string NoClient = "No client: use the collection below";

	private static readonly string[] ZoneLabels = { "None", "General: corridors and open office", "Kitchen", "Meeting rooms, lounge and club", "Reception" };

	private readonly ComboBox _clientBox = new ComboBox();

	// Barra grande sobre el plano con lo que se puede hacer con los rooms seleccionados
	private readonly Border _actionBar = new Border
	{
		HorizontalAlignment = HorizontalAlignment.Center,
		VerticalAlignment = VerticalAlignment.Top,
		Margin = new Thickness(0.0, 14.0, 0.0, 0.0),
		Padding = new Thickness(14.0, 10.0, 14.0, 10.0),
		CornerRadius = new CornerRadius(12.0),
		BorderThickness = new Thickness(1.0),
		Visibility = Visibility.Collapsed,
		Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18.0, ShadowDepth = 2.0, Opacity = 0.18 }
	};

	private readonly StackPanel _actionContent = new StackPanel { Orientation = Orientation.Horizontal };

	private readonly TextBlock _clientNote = new TextBlock { Foreground = Theme.Muted, FontSize = 12.0, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0.0, 4.0, 0.0, 0.0) };

	private UIElement _collectionField;

	private UIElement _typeBox;

	private const string SameAsPlan = "Same as the plan";

	private readonly TextBlock _summary = new TextBlock { FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };

	private readonly TextBlock _status = new TextBlock { Foreground = Theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0.0, 2.0, 0.0, 0.0) };

	private readonly Button _suggestButton = new Button { Content = "Suggest artwork", Padding = new Thickness(16.0, 8.0, 16.0, 8.0) };

	private readonly Button _placeButton = new Button { Content = "Place in model", IsDefault = true, IsEnabled = false, Padding = new Thickness(18.0, 8.0, 18.0, 8.0) };

	private readonly DispatcherTimer _replan;

	private readonly HashSet<int> _skipped = new HashSet<int>();

	private PlanLevel _level;

	private PreviewResult _state;

	public HashSet<int> Skipped => new HashSet<int>(_skipped);

	private IEnumerable<PlanRoom> AllRooms => _host.Levels.SelectMany((PlanLevel l) => l.Rooms);

	private List<PlanRoom> Selected => _level.Rooms.Where((PlanRoom r) => r.Selected).ToList();

	public StudioWindow(IStudioHost host, StudioSettings settings)
	{
		_host = host;
		_settings = settings;
		Title = "Room Studio";
		Width = Math.Min(1180.0, SystemParameters.WorkArea.Width - 80.0);
		Height = Math.Min(780.0, SystemParameters.WorkArea.Height - 80.0);
		MinWidth = 960.0;
		MinHeight = 620.0;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Theme.Apply(this);
		_alignBox = ArtworkDialog.NewAlignBox(settings.Align, includeFamilyDefault: true);
		_heightBox.Text = settings.HeightText;
		_heightBox.IsEnabled = settings.Align != HeightAlign.FamilyDefault;
		_clearBox.Text = settings.ClearText;
		_maxBox.Text = settings.MaxText;
		_gapBox.Text = settings.GapText;
		_tvBottomBox.Text = settings.TvBottomText;
		_autoSize.IsChecked = settings.TvAutoSize;
		_drywallOnly.IsChecked = settings.DrywallOnly;
		_artInOffices.IsChecked = settings.ArtInOffices;
		foreach (TypeItem t in host.ArtTypes)
		{
			t.IsChecked = (settings.ArtChecked.Count > 0) ? settings.ArtChecked.Contains(t.Id) : t.IsChecked;
		}
		_collectionBox.Items.Add(TickedTypes);
		host.Collections.ForEach((ArtCollection c) => _collectionBox.Items.Add(c.Name));
		int startCollection = host.Collections.FindIndex((ArtCollection c) => c.Name == settings.Collection);
		_collectionBox.SelectedIndex = startCollection + 1;
		if (startCollection >= 0)
		{
			TickCollection(host.Collections[startCollection]);
		}
		foreach (PlanRoom room in host.Levels.SelectMany((PlanLevel l) => l.Rooms))
		{
			room.Collection = (settings.RoomCollections.TryGetValue(room.Id, out var rc) && host.Collections.Any((ArtCollection c) => c.Name == rc)) ? rc : null;
			room.Zone = settings.RoomZones.TryGetValue(room.Id, out var rz) ? new ArtZone?(rz) : null;
		}
		_clientBox.Items.Add(NoClient);
		host.Boards.ForEach((ClientBoard b) => _clientBox.Items.Add(b.Name));
		_clientBox.SelectedIndex = host.Boards.FindIndex((ClientBoard b) => b.Name == settings.Client) + 1;
		foreach (TypeItem t in host.TvTypes)
		{
			t.IsChecked = (settings.TvChecked.Count > 0) ? settings.TvChecked.Contains(t.Id) : t.IsChecked;
		}

		DockPanel root = new DockPanel();
		Border header = Theme.Header("Room Studio", "Click rooms or walls to plan TVs and artwork. Everything shows on the plan before it goes in the model.");
		Grid headerGrid = (Grid)header.Child;
		headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel levelPick = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		levelPick.Children.Add(new TextBlock { Text = "Level", Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 10.0, 0.0) });
		levelPick.Children.Add(_levelBox);
		Grid.SetColumn(levelPick, 2);
		headerGrid.Children.Add(levelPick);
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		StackPanel footerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		footerText.Children.Add(_summary);
		footerText.Children.Add(_status);
		Button cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16.0, 8.0, 16.0, 8.0) };
		Border footer = Theme.Footer(footerText, _suggestButton, cancel, _placeButton);
		DockPanel.SetDock(footer, Dock.Bottom);
		root.Children.Add(footer);
		Border right = BuildInspector();
		DockPanel.SetDock(right, Dock.Right);
		root.Children.Add(right);
		root.Children.Add(BuildPlanArea());
		Content = root;

		foreach (PlanLevel level in host.Levels)
		{
			_levelBox.Items.Add(level.Name);
		}
		_levelBox.SelectedIndex = Math.Max(0, host.Levels.FindIndex((PlanLevel l) => l.Id == host.StartLevel));
		_level = host.Levels.ElementAtOrDefault(_levelBox.SelectedIndex) ?? new PlanLevel { Name = "" };
		_levelBox.SelectionChanged += (s, e) =>
		{
			_level = _host.Levels[_levelBox.SelectedIndex];
			RefreshSelection();
			ShowPlan();
		};
		_plan.RoomClicked += (room, add) => Select(room, add);
		_plan.BackgroundClicked += () => Select(null, add: false);
		_plan.MarkerClicked += OnMarker;
		_plan.MarkerMoved += (m, center) => Run(() => _host.MovePiece(m.Key, center));
		_plan.AddClicked += (face) => Run(() => _host.AddPiece(face.Key));
		_plan.FaceClicked += (face) => Run(() => _host.ChangeFace(face.Key));
		_suggestButton.Click += (s, e) => Run(() => _host.Suggest());
		_collectionBox.SelectionChanged += (s, e) =>
		{
			if (_collectionBox.SelectedIndex > 0)
			{
				TickCollection(_host.Collections[_collectionBox.SelectedIndex - 1]);
				RefreshTypes();
				Later();
			}
		};
		_placeButton.Click += (s, e) => Accept();
		_replan = new DispatcherTimer(TimeSpan.FromMilliseconds(600.0), DispatcherPriority.Background, (s, e) => Replan(), Dispatcher);
		_replan.Stop();
		_alignBox.SelectionChanged += (s, e) =>
		{
			_heightBox.IsEnabled = _alignBox.SelectedIndex != (int)HeightAlign.FamilyDefault;
			Later();
		};
		foreach (TextBox box in new[] { _heightBox, _clearBox, _maxBox, _gapBox, _tvBottomBox })
		{
			box.TextChanged += (s, e) => Later();
		}
		_clientBox.SelectionChanged += (s, e) =>
		{
			ShowClient();
			RefreshSelection();
			Later();
		};
		foreach (CheckBox box in new[] { _autoSize, _drywallOnly, _artInOffices })
		{
			box.Checked += (s, e) => Later();
			box.Unchecked += (s, e) => Later();
		}
		RefreshTypes();
		RefreshSelection();
		ShowPlan();
		_summary.Text = "Planning…";
		Loaded += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Start);
		_actionBar.Background = Theme.Panel;
		_actionBar.BorderBrush = Theme.Line;
		PreviewKeyDown += OnKey;
	}

	// Elegir una sección Artwork_* marca exactamente sus tipos, como en Place Artwork
	private void TickCollection(ArtCollection c)
	{
		HashSet<long> ids = new HashSet<long>(c.Ids);
		_host.ArtTypes.ForEach((TypeItem t) => t.IsChecked = ids.Contains(t.Id));
	}

	private ClientBoard SelectedClient => (_clientBox.SelectedIndex > 0) ? _host.Boards[_clientBox.SelectedIndex - 1] : null;

	// Con un cliente, cada room usa las piezas de su zona: la colección y los tipos marcados no cuentan
	private void ShowClient()
	{
		ClientBoard c = SelectedClient;
		_clientNote.Text = (c == null) ? "Pick a client to use its pieces zone by zone." : $"General {c.Count(ArtZone.General)} · Kitchen {c.Count(ArtZone.Kitchen)} · Meeting/lounge {c.Count(ArtZone.Meeting)} · Reception {c.Count(ArtZone.Reception)}. Each room gets the pieces of its zone; set a room's zone in its panel.";
		if (_collectionField != null)
		{
			_collectionField.IsEnabled = c == null;
			_collectionField.Opacity = (c == null) ? 1.0 : 0.45;
		}
		if (_typeBox != null)
		{
			_typeBox.IsEnabled = c == null;
			_typeBox.Opacity = (c == null) ? 1.0 : 0.45;
		}
	}

	internal static bool IsMeeting(string name)
	{
		return MeetingWords.Any((string w) => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
	}

	private Grid BuildPlanArea()
	{
		Grid area = new Grid { Margin = new Thickness(16.0) };
		area.Children.Add(new Border
		{
			Background = Theme.Panel,
			BorderBrush = Theme.Line,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(1.0),
			Child = _plan
		});
		StackPanel legend = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(14.0),
			IsHitTestVisible = false
		};
		legend.Children.Add(Swatch(Theme.Accent, dashed: false, "Artwork wall"));
		legend.Children.Add(Swatch(Theme.Dark, dashed: false, "TV wall"));
		legend.Children.Add(Swatch(Theme.Accent, dashed: true, "Free wall, click to use"));
		legend.Children.Add(Swatch(Theme.Danger, dashed: false, "Door"));
		legend.Children.Add(new TextBlock
		{
			Text = "Scroll to zoom, drag to move.",
			Foreground = Theme.Muted,
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0)
		});
		area.Children.Add(legend);
		Button fit = new Button
		{
			Content = "Fit plan",
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(12.0),
			Padding = new Thickness(12.0, 5.0, 12.0, 5.0)
		};
		fit.Click += (s, e) => _plan.Fit();
		area.Children.Add(fit);
		_actionBar.Child = _actionContent;
		area.Children.Add(_actionBar);
		return area;
	}

	private static StackPanel Swatch(Brush color, bool dashed, string text)
	{
		StackPanel p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0.0, 0.0, 16.0, 0.0) };
		p.Children.Add(new System.Windows.Shapes.Line
		{
			X1 = 0.0,
			X2 = 18.0,
			Y1 = 1.5,
			Y2 = 1.5,
			Stroke = dashed ? new SolidColorBrush(Color.FromRgb(147, 176, 255)) : color,
			StrokeThickness = 3.0,
			StrokeDashArray = dashed ? new DoubleCollection { 1.2, 0.8 } : null,
			VerticalAlignment = VerticalAlignment.Center
		});
		p.Children.Add(new TextBlock { Text = text, FontSize = 12.0, Margin = new Thickness(6.0, 0.0, 0.0, 0.0), VerticalAlignment = VerticalAlignment.Center });
		return p;
	}

	private Border BuildInspector()
	{
		StackPanel stack = new StackPanel();
		stack.Children.Add(_selection);
		_drywallOnly.Margin = new Thickness(0.0, 24.0, 0.0, 0.0);
		stack.Children.Add(_drywallOnly);
		TextBlock dryNote = Theme.Note("TVs and artwork go only on walls whose finish on that side is gypsum or drywall.");
		dryNote.FontSize = 12.0;
		dryNote.Margin = new Thickness(24.0, 2.0, 0.0, 0.0);
		stack.Children.Add(dryNote);
		_artInOffices.Margin = new Thickness(0.0, 12.0, 0.0, 0.0);
		stack.Children.Add(_artInOffices);
		TextBlock officeNote = Theme.Note("Offices, restrooms, storage and service rooms get no artwork. Window walls and the facade never do. To use one office anyway, choose Artwork for that room.");
		officeNote.FontSize = 12.0;
		officeNote.Margin = new Thickness(24.0, 2.0, 0.0, 0.0);
		stack.Children.Add(officeNote);
		stack.Children.Add(Section("Artwork"));
		if (_host.Boards.Count > 0)
		{
			StackPanel client = Field("Client", _clientBox);
			client.Margin = new Thickness(0.0, 0.0, 0.0, 4.0);
			client.Children.Add(_clientNote);
			stack.Children.Add(client);
		}
		if (_host.Collections.Count > 0)
		{
			StackPanel collection = Field("Collection", _collectionBox);
			collection.Margin = new Thickness(0.0, 8.0, 0.0, 8.0);
			stack.Children.Add(collection);
			_collectionField = collection;
		}
		_typeBox = TypeBox(_artList);
		stack.Children.Add(_typeBox);
		ShowClient();
		stack.Children.Add(Field("Height", Pair(_alignBox, _heightBox)));
		stack.Children.Add(Field("Clear of doors and corners", _clearBox));
		stack.Children.Add(Field("Pieces per wall, max", Pair(_maxBox, null)));
		stack.Children.Add(Field("Gap between pieces", _gapBox));
		stack.Children.Add(Section("TV"));
		stack.Children.Add(TypeBox(_tvList));
		stack.Children.Add(Field("Bottom edge height", _tvBottomBox));
		_autoSize.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		stack.Children.Add(_autoSize);
		return new Border
		{
			Width = 380.0,
			Background = Theme.Panel,
			BorderBrush = Theme.Line,
			BorderThickness = new Thickness(1.0, 0.0, 0.0, 0.0),
			Child = new ScrollViewer
			{
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				Padding = new Thickness(20.0, 18.0, 20.0, 18.0),
				Content = stack
			}
		};
	}

	private UIElement Section(string title)
	{
		DockPanel dock = new DockPanel { Margin = new Thickness(0.0, 26.0, 0.0, 8.0) };
		Button load = new Button { Content = "Load family…", Style = Theme.Get("Link"), Margin = new Thickness(0.0, 0.0, -6.0, 0.0) };
		load.Click += (s, e) => LoadFamilies();
		DockPanel.SetDock(load, Dock.Right);
		dock.Children.Add(load);
		dock.Children.Add(new TextBlock { Text = title, FontSize = 14.0, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
		return dock;
	}

	private static Border TypeBox(StackPanel list)
	{
		return new Border
		{
			BorderBrush = Theme.Line,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(4.0),
			Child = new ScrollViewer
			{
				MaxHeight = 160.0,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				Content = list
			}
		};
	}

	private static StackPanel Field(string label, UIElement input)
	{
		StackPanel p = new StackPanel { Margin = new Thickness(0.0, 10.0, 0.0, 0.0) };
		p.Children.Add(new TextBlock { Text = label, Foreground = Theme.Muted, FontSize = 12.0, Margin = new Thickness(0.0, 0.0, 0.0, 4.0) });
		p.Children.Add(input);
		return p;
	}

	private static Grid Pair(FrameworkElement a, FrameworkElement b)
	{
		Grid g = new Grid();
		g.ColumnDefinitions.Add(new ColumnDefinition());
		g.ColumnDefinitions.Add(new ColumnDefinition());
		a.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		g.Children.Add(a);
		if (b != null)
		{
			Grid.SetColumn(b, 1);
			g.Children.Add(b);
		}
		return g;
	}

	private static Border Row(UIElement child)
	{
		Border row = new Border
		{
			Background = Brushes.Transparent,
			CornerRadius = new CornerRadius(6.0),
			Padding = new Thickness(8.0, 5.0, 8.0, 5.0),
			Margin = new Thickness(-8.0, 0.0, -8.0, 1.0),
			Cursor = Cursors.Hand,
			Focusable = true,
			FocusVisualStyle = Theme.Get("Focus"),
			Child = child
		};
		row.MouseEnter += (s, e) => row.Background = Theme.Canvas;
		row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
		return row;
	}

	private static void Hook(Border row, Action act)
	{
		row.MouseLeftButtonUp += (s, e) => act();
		row.KeyDown += (s, e) =>
		{
			if (e.Key == Key.Space || e.Key == Key.Enter)
			{
				e.Handled = true;
				act();
			}
		};
	}

	private void ShowPlan()
	{
		_plan.Show(_level, _state?.Markers, _state?.Faces);
	}

	private void Start()
	{
		StudioRequest req = BuildRequest();
		if (req == null)
		{
			return;
		}
		foreach (PlanRoom room in AllRooms)
		{
			RoomUse saved;
			if ((_settings.Uses.TryGetValue(room.Id, out saved) && saved == RoomUse.Tv) || (!_settings.Uses.ContainsKey(room.Id) && IsMeeting(room.Name)))
			{
				req.Uses[room.Id] = RoomUse.Tv;
			}
		}
		if (Run(() => _host.Plan(req)) && (_host.ArtTypes.Any((TypeItem t) => t.IsChecked) || SelectedClient != null))
		{
			Run(() => _host.Suggest());
		}
		if (_host.StartNote != null)
		{
			Problem(_host.StartNote);
		}
	}

	private void Later()
	{
		_placeButton.IsEnabled = false;
		_status.Foreground = Theme.Muted;
		_status.Text = "Updating the plan…";
		_replan.Stop();
		_replan.Start();
	}

	private void Replan()
	{
		_replan.Stop();
		StudioRequest req = BuildRequest();
		if (req != null)
		{
			Run(() => _host.Plan(req));
		}
	}

	private bool Run(Func<PreviewResult> call)
	{
		Cursor = Cursors.Wait;
		try
		{
			Apply(call());
			return true;
		}
		catch (Exception ex)
		{
			Problem("Planning failed: " + ex.Message);
			return false;
		}
		finally
		{
			Cursor = null;
		}
	}

	private void Apply(PreviewResult state)
	{
		if (state == null)
		{
			return;
		}
		_state = state;
		foreach (PlanRoom room in AllRooms)
		{
			room.Use = state.Uses.TryGetValue(room.Id, out var use) ? use : RoomUse.None;
			room.Problem = state.Problems.TryGetValue(room.Id, out var p) ? p : null;
		}
		HashSet<int> keys = new HashSet<int>(state.Markers.Select((PlanMarker m) => m.Key));
		_skipped.RemoveWhere((int k) => !keys.Contains(k));
		state.Markers.ForEach((PlanMarker m) => m.Skipped = _skipped.Contains(m.Key));
		_placeButton.IsEnabled = state.Markers.Any((PlanMarker m) => !m.Skipped);
		RefreshSelection();
		ShowPlan();
		UpdateSummary(state.Message);
	}

	private void Select(PlanRoom room, bool add)
	{
		if (!add)
		{
			bool only = room != null && room.Selected && Selected.Count == 1;
			_level.Rooms.ForEach((PlanRoom r) => r.Selected = false);
			if (room != null && !only)
			{
				room.Selected = true;
			}
		}
		else if (room != null)
		{
			room.Selected = !room.Selected;
		}
		RefreshSelection();
		ShowPlan();
	}

	// A = Artwork, T = TV, N = Nothing para los rooms seleccionados; Esc deselecciona; F encuadra el plano
	private void OnKey(object sender, KeyEventArgs e)
	{
		if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is ComboBoxItem)
		{
			return;
		}
		List<PlanRoom> sel = Selected;
		switch (e.Key)
		{
		case Key.A:
		case Key.T:
		case Key.N:
			if (sel.Count > 0)
			{
				RoomUse use = (e.Key == Key.A) ? RoomUse.Artwork : ((e.Key == Key.T) ? RoomUse.Tv : RoomUse.None);
				Run(() => _host.SetRoomUse(sel.Select((PlanRoom r) => r.Id).ToList(), use));
				e.Handled = true;
			}
			break;
		case Key.Escape:
			if (sel.Count > 0)
			{
				Select(null, add: false);
				e.Handled = true;
			}
			break;
		case Key.F:
			_plan.Fit();
			e.Handled = true;
			break;
		}
	}

	private void RefreshActionBar()
	{
		_actionContent.Children.Clear();
		List<PlanRoom> sel = (_level == null) ? new List<PlanRoom>() : Selected;
		_actionBar.Visibility = (sel.Count == 0) ? Visibility.Collapsed : Visibility.Visible;
		if (sel.Count == 0)
		{
			return;
		}
		StackPanel title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 18.0, 0.0), MaxWidth = 260.0 };
		title.Children.Add(new TextBlock { Text = (sel.Count == 1) ? sel[0].Title : $"{sel.Count} rooms", FontSize = 16.0, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
		title.Children.Add(new TextBlock { Text = "Keys: A artwork · T TV · N nothing · Esc", FontSize = 11.5, Foreground = Theme.Muted });
		_actionContent.Children.Add(title);
		RoomUse? common = (sel.Select((PlanRoom r) => r.Use).Distinct().Count() == 1) ? new RoomUse?(sel[0].Use) : null;
		string[] labels = { "Nothing", "Artwork", "TV" };
		for (int i = 0; i < 3; i++)
		{
			RoomUse use = (RoomUse)i;
			Button b = new Button
			{
				Content = labels[i],
				FontSize = 15.0,
				MinWidth = 104.0,
				Height = 40.0,
				Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
				FontWeight = (common == use) ? FontWeights.SemiBold : FontWeights.Normal,
				Background = (common == use) ? Theme.AccentSoft : Theme.Panel,
				BorderBrush = (common == use) ? Theme.Accent : Theme.LineStrong
			};
			b.Click += (s, e) => Run(() => _host.SetRoomUse(Selected.Select((PlanRoom r) => r.Id).ToList(), use));
			_actionContent.Children.Add(b);
		}
		if (sel.Count == 1 && SelectedClient != null)
		{
			PlanRoom room = sel[0];
			ArtZone auto = ClientBoards.ZoneOf(room.Name);
			ComboBox zone = new ComboBox { FontSize = 14.0, Height = 40.0, MinWidth = 200.0, Margin = new Thickness(14.0, 0.0, 0.0, 0.0), VerticalContentAlignment = VerticalAlignment.Center };
			zone.Items.Add("Zone: auto (" + ZoneLabels[(int)auto].Split(':')[0] + ")");
			foreach (string label in ZoneLabels)
			{
				zone.Items.Add("Zone: " + label.Split(':')[0]);
			}
			zone.SelectedIndex = room.Zone.HasValue ? ((int)room.Zone.Value + 1) : 0;
			zone.SelectionChanged += (s, e) =>
			{
				ArtZone? z = (zone.SelectedIndex > 0) ? new ArtZone?((ArtZone)(zone.SelectedIndex - 1)) : null;
				room.Zone = z;
				Run(() => _host.SetRoomZone(room.Id, z));
			};
			_actionContent.Children.Add(zone);
		}
	}

	private void RefreshSelection()
	{
		RefreshActionBar();
		_selection.Children.Clear();
		List<PlanRoom> sel = Selected;
		if (sel.Count == 0)
		{
			_selection.Children.Add(Theme.Heading("Plan your walls"));
			TextBlock hint = Theme.Note("Click a dashed wall to add artwork on it, even where there is no room. Click a room to choose Artwork or TV for the whole room. Shift-click to select more rooms.");
			hint.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
			_selection.Children.Add(hint);
			return;
		}
		_selection.Children.Add(Theme.Heading((sel.Count == 1) ? sel[0].Title : $"{sel.Count} rooms selected"));
		if (sel.Count == 1)
		{
			TextBlock sub = Theme.Note(sel[0].AreaText);
			sub.Margin = new Thickness(0.0, 2.0, 0.0, 0.0);
			_selection.Children.Add(sub);
		}
		UniformGrid segments = new UniformGrid { Columns = 3 };
		string[] labels = { "Nothing", "Artwork", "TV" };
		RoomUse? common = (sel.Select((PlanRoom r) => r.Use).Distinct().Count() == 1) ? new RoomUse?(sel[0].Use) : null;
		for (int i = 0; i < 3; i++)
		{
			RoomUse use = (RoomUse)i;
			RadioButton rb = new RadioButton { Content = labels[i], GroupName = "use", Style = Theme.Get("Segment"), IsChecked = common == use };
			rb.Checked += (s, e) =>
			{
				Run(() => _host.SetRoomUse(Selected.Select((PlanRoom r) => r.Id).ToList(), use));
			};
			segments.Children.Add(rb);
		}
		_selection.Children.Add(new Border
		{
			Background = Theme.Canvas,
			BorderBrush = Theme.Line,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(7.0),
			Padding = new Thickness(3.0),
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			Child = segments
		});
		if (sel.Count == 1 && sel[0].Problem != null)
		{
			_selection.Children.Add(new TextBlock { Text = sel[0].Problem, Foreground = Theme.Danger, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0.0, 10.0, 0.0, 0.0) });
		}
		if (sel.Count == 1 && SelectedClient != null)
		{
			AddRoomZone(sel[0]);
		}
		else if (sel.Count == 1 && sel[0].Use != RoomUse.Tv && _host.Collections.Count > 0)
		{
			AddRoomCollection(sel[0]);
		}
		if (sel.Count == 1 && _state != null)
		{
			AddWallsList(sel[0]);
		}
	}

	// Colección propia del room (p.ej. Artwork_HQ en la recepción y Artwork_Qbiq en el resto)
	private void AddRoomCollection(PlanRoom room)
	{
		ComboBox box = new ComboBox();
		box.Items.Add(SameAsPlan);
		_host.Collections.ForEach((ArtCollection c) => box.Items.Add(c.Name));
		box.SelectedIndex = _host.Collections.FindIndex((ArtCollection c) => c.Name == room.Collection) + 1;
		box.SelectionChanged += (s, e) =>
		{
			string name = (box.SelectedIndex > 0) ? _host.Collections[box.SelectedIndex - 1].Name : null;
			room.Collection = name;
			Run(() => _host.SetRoomCollection(room.Id, name));
		};
		StackPanel field = Field("Artwork collection for this room", box);
		field.Margin = new Thickness(0.0, 14.0, 0.0, 0.0);
		_selection.Children.Add(field);
	}

	// Zona del room en el tablero del cliente: la de su nombre, o la que elija el usuario
	private void AddRoomZone(PlanRoom room)
	{
		ArtZone auto = ClientBoards.ZoneOf(room.Name);
		ComboBox box = new ComboBox();
		box.Items.Add("Auto: " + ZoneLabels[(int)auto]);
		for (int i = 0; i < ZoneLabels.Length; i++)
		{
			box.Items.Add(ZoneLabels[i]);
		}
		box.SelectedIndex = room.Zone.HasValue ? ((int)room.Zone.Value + 1) : 0;
		box.SelectionChanged += (s, e) =>
		{
			ArtZone? zone = (box.SelectedIndex > 0) ? new ArtZone?((ArtZone)(box.SelectedIndex - 1)) : null;
			room.Zone = zone;
			Run(() => _host.SetRoomZone(room.Id, zone));
		};
		StackPanel field = Field(SelectedClient.Name + " zone for this room", box);
		field.Margin = new Thickness(0.0, 14.0, 0.0, 0.0);
		_selection.Children.Add(field);
	}

	private void AddWallsList(PlanRoom room)
	{
		List<PlanFace> faces = _state.Faces.Where((PlanFace f) => f.RoomId == room.Id).OrderByDescending((PlanFace f) => f.Allowed).ThenBy((PlanFace f) => f.Title).ToList();
		if (faces.Count == 0)
		{
			return;
		}
		bool tv = room.Use == RoomUse.Tv;
		TextBlock heading = Theme.Heading("Walls in this room");
		heading.Margin = new Thickness(0.0, 18.0, 0.0, 2.0);
		_selection.Children.Add(heading);
		TextBlock hint = Theme.Note(tv ? "The TV wall is marked TV; tick other walls for artwork. Click the TV on the plan to move it." : "Tick the walls that get artwork.");
		hint.FontSize = 12.0;
		hint.Margin = new Thickness(0.0, 0.0, 0.0, 6.0);
		_selection.Children.Add(hint);
		foreach (PlanFace face in faces)
		{
			Grid g = new Grid { Opacity = face.Allowed ? 1.0 : 0.55 };
			g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			g.ColumnDefinitions.Add(new ColumnDefinition());
			FrameworkElement mark = face.TvWall ? new Border
			{
				Background = Theme.Dark,
				CornerRadius = new CornerRadius(4.0),
				Padding = new Thickness(4.0, 0.0, 4.0, 0.0),
				Child = new TextBlock { Text = "TV", Foreground = Theme.Panel, FontSize = 10.5, FontWeight = FontWeights.Bold }
			} : new CheckBox { IsChecked = face.On, IsHitTestVisible = false, Focusable = false };
			mark.VerticalAlignment = VerticalAlignment.Top;
			mark.Margin = new Thickness(0.0, 2.0, 10.0, 0.0);
			g.Children.Add(mark);
			StackPanel text = new StackPanel();
			string title = face.Title.Contains(" of ") ? (face.Title.Substring(0, face.Title.IndexOf(" of ", StringComparison.Ordinal)) + face.Title.Substring(face.Title.LastIndexOf(','))) : face.Title;
			text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Medium });
			text.Children.Add(new TextBlock
			{
				Text = face.Allowed ? (face.Core ? (face.WallType + ", core wall") : face.WallType) : (face.WallType + ". " + face.Reason),
				Foreground = Theme.Muted,
				FontSize = 11.5,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
			if (face.On && face.Problem != null)
			{
				text.Children.Add(new TextBlock { Text = face.Problem, Foreground = Theme.Danger, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
			}
			Grid.SetColumn(text, 1);
			g.Children.Add(text);
			Border row = Row(g);
			row.ToolTip = face.Allowed ? null : face.Reason;
			Hook(row, () => Run(() => _host.ChangeFace(face.Key)));
			_selection.Children.Add(row);
		}
	}

	private void RefreshTypes()
	{
		Fill(_artList, _host.ArtTypes, "No artwork families in this model. Use Load family to add some.");
		Fill(_tvList, _host.TvTypes, "No TV families in this model. Use Load family to add some.");
	}

	private void Fill(StackPanel list, List<TypeItem> items, string empty)
	{
		list.Children.Clear();
		items.ForEach((TypeItem item) => list.Children.Add(TypeRow(item)));
		if (items.Count == 0)
		{
			TextBlock none = Theme.Note(empty);
			none.Margin = new Thickness(8.0);
			list.Children.Add(none);
		}
	}

	private UIElement TypeRow(TypeItem item)
	{
		int cut = item.Label.LastIndexOf(" : ", StringComparison.Ordinal);
		string family = (cut > 0) ? item.Label.Substring(0, cut) : item.Label;
		string type = (cut > 0) ? item.Label.Substring(cut + 3) : "";
		Grid g = new Grid();
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		g.ColumnDefinitions.Add(new ColumnDefinition());
		CheckBox check = new CheckBox { IsChecked = item.IsChecked, IsHitTestVisible = false, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
		g.Children.Add(check);
		Image img = new Image { Stretch = Stretch.Uniform };
		Border thumb = new Border
		{
			Width = 40.0,
			Height = 40.0,
			Margin = new Thickness(10.0, 0.0, 10.0, 0.0),
			Background = Theme.Canvas,
			CornerRadius = new CornerRadius(5.0),
			Padding = new Thickness(4.0),
			Child = img
		};
		Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => img.Source = _host.Thumb(item.Id)?.Image));
		Grid.SetColumn(thumb, 1);
		g.Children.Add(thumb);
		StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		text.Children.Add(new TextBlock { Text = (type.Length > 0) ? type : family, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
		text.Children.Add(new TextBlock { Text = (type.Length > 0) ? family : item.Kind, Foreground = Theme.Muted, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
		Grid.SetColumn(text, 2);
		g.Children.Add(text);
		Border row = Row(g);
		row.Margin = new Thickness(0.0, 0.0, 0.0, 1.0);
		row.Padding = new Thickness(8.0, 4.0, 8.0, 4.0);
		row.ToolTip = item.Label;
		Hook(row, () =>
		{
			item.IsChecked = !item.IsChecked;
			check.IsChecked = item.IsChecked;
			if (_host.ArtTypes.Contains(item))
			{
				_collectionBox.SelectedIndex = 0;
			}
			Later();
		});
		return row;
	}

	private void LoadFamilies()
	{
		OpenFileDialog dlg = new OpenFileDialog
		{
			Filter = "Revit families (*.rfa)|*.rfa",
			Multiselect = true,
			Title = "Load families"
		};
		if (dlg.ShowDialog(this) != true)
		{
			return;
		}
		try
		{
			_host.LoadFamilies(dlg.FileNames);
			RefreshTypes();
			Later();
		}
		catch (Exception ex)
		{
			Problem("Revit couldn't load those families: " + ex.Message);
		}
	}

	private void UpdateSummary(string message)
	{
		List<PlanMarker> live = _state.Markers.Where((PlanMarker m) => !m.Skipped).ToList();
		int tvs = live.Count((PlanMarker m) => m.Use == RoomUse.Tv);
		int arts = live.Count((PlanMarker m) => m.Use == RoomUse.Artwork);
		_summary.Text = (tvs + arts == 0) ? "Nothing planned yet" : $"{Count(tvs, "TV")} and {Count(arts, "artwork")} planned";
		_status.Foreground = Theme.Muted;
		int problems = _state.Problems.Count + _state.Faces.Count((PlanFace f) => f.On && f.Problem != null);
		if (message != null)
		{
			_status.Text = message;
		}
		else if (problems > 0)
		{
			_status.Foreground = Theme.Danger;
			_status.Text = $"{Count(problems, "spot")} can't be filled. They're red on the plan; hover for the reason.";
		}
		else
		{
			_status.Text = "Click a dashed wall to add artwork, a blue wall to remove it, or a TV to move it.";
		}
	}

	private static string Count(int n, string word)
	{
		return $"{n} {((n == 1) ? word : (word + "s"))}";
	}

	private void Problem(string text)
	{
		_status.Foreground = Theme.Danger;
		_status.Text = text;
	}

	private StudioRequest BuildRequest()
	{
		StudioRequest req = new StudioRequest
		{
			ArtTypes = _host.ArtTypes.Where((TypeItem t) => t.IsChecked).Select((TypeItem t) => t.Id).ToList(),
			TvTypes = _host.TvTypes.Where((TypeItem t) => t.IsChecked).Select((TypeItem t) => t.Id).ToList(),
			ArtAlign = (HeightAlign)_alignBox.SelectedIndex,
			TvAutoSize = _autoSize.IsChecked == true,
			DrywallOnly = _drywallOnly.IsChecked == true,
			ArtInOffices = _artInOffices.IsChecked == true,
			RoomCollections = AllRooms.Where((PlanRoom r) => r.Collection != null).ToDictionary((PlanRoom r) => r.Id, (PlanRoom r) => r.Collection),
			Client = SelectedClient?.Name,
			RoomZones = AllRooms.Where((PlanRoom r) => r.Zone.HasValue).ToDictionary((PlanRoom r) => r.Id, (PlanRoom r) => r.Zone.Value)
		};
		if (req.ArtAlign != HeightAlign.FamilyDefault)
		{
			req.ArtHeight = _host.ParseLength(_heightBox.Text);
			if (!(req.ArtHeight > 0.0))
			{
				Problem("Enter the artwork height as a length, like 150 cm.");
				return null;
			}
		}
		double? clear = _host.ParseLength(_clearBox.Text);
		double? gap = _host.ParseLength(_gapBox.Text);
		if (!clear.HasValue || clear < 0.0 || !gap.HasValue || gap < 0.0)
		{
			Problem("Enter clearance and gap as lengths, like 30 cm.");
			return null;
		}
		if (!int.TryParse(_maxBox.Text.Trim(), out var max) || max < 1)
		{
			Problem("Pieces per wall must be a whole number, 1 or more.");
			return null;
		}
		req.Clearance = clear.Value;
		req.Gap = gap.Value;
		req.MaxPerFace = max;
		if (_tvBottomBox.Text.Trim().Length > 0)
		{
			req.TvBottom = _host.ParseLength(_tvBottomBox.Text);
			if (!(req.TvBottom > 0.0))
			{
				Problem("Enter the TV bottom height as a length, like 100 cm.");
				return null;
			}
		}
		return req;
	}

	private void OnMarker(PlanMarker m)
	{
		if (m.Use == RoomUse.Tv && !m.Skipped)
		{
			Run(() => _host.MoveTv(m.RoomId));
			return;
		}
		if (m.Manual)
		{
			Run(() => _host.RemovePiece(m.Key));
			return;
		}
		if (!_skipped.Remove(m.Key))
		{
			_skipped.Add(m.Key);
		}
		m.Skipped = _skipped.Contains(m.Key);
		_placeButton.IsEnabled = _state.Markers.Any((PlanMarker x) => !x.Skipped);
		ShowPlan();
		UpdateSummary(null);
	}

	private void Accept()
	{
		if (_state == null || _replan.IsEnabled)
		{
			return;
		}
		SaveSettings();
		DialogResult = true;
		Close();
	}

	protected override void OnClosed(EventArgs e)
	{
		_replan.Stop();
		SaveSettings();
		base.OnClosed(e);
	}

	private void SaveSettings()
	{
		_settings.ArtChecked = new HashSet<long>(_host.ArtTypes.Where((TypeItem t) => t.IsChecked).Select((TypeItem t) => t.Id));
		_settings.TvChecked = new HashSet<long>(_host.TvTypes.Where((TypeItem t) => t.IsChecked).Select((TypeItem t) => t.Id));
		_settings.Uses = AllRooms.ToDictionary((PlanRoom r) => r.Id, (PlanRoom r) => r.Use);
		_settings.Align = (HeightAlign)_alignBox.SelectedIndex;
		_settings.HeightText = _heightBox.Text;
		_settings.ClearText = _clearBox.Text;
		_settings.MaxText = _maxBox.Text;
		_settings.GapText = _gapBox.Text;
		_settings.TvBottomText = _tvBottomBox.Text;
		_settings.TvAutoSize = _autoSize.IsChecked == true;
		_settings.DrywallOnly = _drywallOnly.IsChecked == true;
		_settings.ArtInOffices = _artInOffices.IsChecked == true;
		_settings.Collection = (_collectionBox.SelectedIndex > 0) ? _host.Collections[_collectionBox.SelectedIndex - 1].Name : null;
		_settings.RoomCollections = AllRooms.Where((PlanRoom r) => r.Collection != null).ToDictionary((PlanRoom r) => r.Id, (PlanRoom r) => r.Collection);
		_settings.Client = SelectedClient?.Name;
		_settings.RoomZones = AllRooms.Where((PlanRoom r) => r.Zone.HasValue).ToDictionary((PlanRoom r) => r.Id, (PlanRoom r) => r.Zone.Value);
	}
}
