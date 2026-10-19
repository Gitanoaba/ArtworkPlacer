using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ArtworkPlacer;

// Mapa del piso para elegir en qué muros (y de qué lado) va el logo: un clic en la cara del muro lo marca
public class LogoMapWindow : Window
{
	private readonly List<PlanLevel> _levels;

	private readonly List<PlanFace> _faces;

	private readonly PlanView _plan = new PlanView();

	private readonly ComboBox _levelBox = new ComboBox { MinWidth = 160.0 };

	private readonly TextBlock _status = new TextBlock { Foreground = Theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

	private readonly HashSet<int> _picked;

	private PlanLevel _level;

	public List<int> Picked => _picked.ToList();

	public LogoMapWindow(List<PlanLevel> levels, long startLevel, List<PlanFace> faces, IEnumerable<int> preselected)
	{
		_levels = levels;
		_faces = faces;
		_picked = new HashSet<int>(preselected);
		Title = "Place Logo: pick the walls";
		Width = Math.Min(1000.0, SystemParameters.WorkArea.Width - 80.0);
		Height = Math.Min(720.0, SystemParameters.WorkArea.Height - 80.0);
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Theme.Apply(this);
		// En el mapa se puede elegir cualquier cara, menos ventanas, fachada y el interior del core
		foreach (PlanFace f in _faces)
		{
			f.Allowed = f.Reason == null || !(f.Reason.StartsWith("Window") || f.Reason.StartsWith("Facade") || f.Reason.StartsWith("Inside the core"));
			f.On = false;
			f.TvWall = false;
			f.Accent = false;
			f.Problem = null;
			f.AddAt = null;
			f.Logo = _picked.Contains(f.Key);
		}
		DockPanel root = new DockPanel();
		Border header = Theme.Header("Pick the logo walls", "Click the side of each wall where the logo goes. Click it again to take it off.");
		Grid headerGrid = (Grid)header.Child;
		headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel levelPick = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		levelPick.Children.Add(new TextBlock { Text = "Level", Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 10.0, 0.0) });
		levelPick.Children.Add(_levelBox);
		Grid.SetColumn(levelPick, 2);
		headerGrid.Children.Add(levelPick);
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		Button cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16.0, 8.0, 16.0, 8.0) };
		Button ok = new Button { Content = "Use these walls", IsDefault = true, Padding = new Thickness(18.0, 8.0, 18.0, 8.0) };
		ok.Click += (s, e) =>
		{
			if (_picked.Count == 0)
			{
				_status.Foreground = Theme.Danger;
				_status.Text = "Click at least one wall.";
				return;
			}
			DialogResult = true;
			Close();
		};
		Border footer = Theme.Footer(_status, cancel, ok);
		DockPanel.SetDock(footer, Dock.Bottom);
		root.Children.Add(footer);
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
		StackPanel view = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12.0) };
		Button zoomOut = new Button { Content = "−", Width = 32.0, Padding = new Thickness(0.0, 5.0, 0.0, 5.0) };
		Button zoomIn = new Button { Content = "+", Width = 32.0, Padding = new Thickness(0.0, 5.0, 0.0, 5.0), Margin = new Thickness(4.0, 0.0, 0.0, 0.0) };
		Button fit = new Button { Content = "Fit plan", Margin = new Thickness(8.0, 0.0, 0.0, 0.0), Padding = new Thickness(12.0, 5.0, 12.0, 5.0) };
		zoomOut.Click += (s, e) => _plan.ZoomBy(1.0 / 1.25);
		zoomIn.Click += (s, e) => _plan.ZoomBy(1.25);
		fit.Click += (s, e) => _plan.Fit();
		view.Children.Add(zoomOut);
		view.Children.Add(zoomIn);
		view.Children.Add(fit);
		area.Children.Add(view);
		root.Children.Add(area);
		Content = root;

		foreach (PlanLevel level in levels)
		{
			_levelBox.Items.Add(level.Name);
		}
		// Arranca en el nivel de un muro ya marcado, si no en el de la vista
		long first = _faces.FirstOrDefault((PlanFace f) => _picked.Contains(f.Key))?.LevelId ?? startLevel;
		_levelBox.SelectedIndex = Math.Max(0, levels.FindIndex((PlanLevel l) => l.Id == first));
		_levelBox.SelectionChanged += (s, e) => ShowLevel();
		_plan.FaceClicked += Toggle;
		_plan.HintFor = (PlanFace f) => (!f.Allowed) ? ((f.Reason ?? "No logo here") + ".") : (f.Logo ? "The logo goes here. Click to take it off." : "Click to put the logo on this side.");
		ShowLevel();
		ShowCount();
	}

	private void ShowLevel()
	{
		_level = _levels.ElementAtOrDefault(_levelBox.SelectedIndex);
		if (_level != null)
		{
			_plan.Show(_level, new List<PlanMarker>(), _faces);
		}
	}

	private void Toggle(PlanFace f)
	{
		if (!f.Allowed)
		{
			_status.Foreground = Theme.Danger;
			_status.Text = (f.Reason ?? "No logo here") + ".";
			return;
		}
		if (!_picked.Remove(f.Key))
		{
			_picked.Add(f.Key);
		}
		f.Logo = _picked.Contains(f.Key);
		_plan.Show(_level, new List<PlanMarker>(), _faces);
		ShowCount();
	}

	private void ShowCount()
	{
		_status.Foreground = Theme.Muted;
		_status.Text = (_picked.Count == 0) ? "No walls picked yet." : $"{_picked.Count} wall(s) picked.";
	}
}
