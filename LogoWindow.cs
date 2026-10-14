using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ArtworkPlacer;

public enum LogoAction
{
	Mark,
	Snap,
	RemoveGuides
}

public class LogoWindow : Window
{
	private readonly Func<string, double?> _parse;

	private readonly Image _preview = new Image { Width = 140.0, Height = 90.0, Stretch = Stretch.Uniform };

	private readonly TextBlock _pathText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.0 };

	private readonly RadioButton _reception = new RadioButton { GroupName = "where" };

	private readonly RadioButton _selected = new RadioButton { GroupName = "where" };

	private readonly TextBox _widthBox = new TextBox();

	private readonly TextBox _heightBox = new TextBox();

	private readonly CheckBox _alreadySet = new CheckBox { Content = "The logo decal already has this image (skip Decal Types)" };

	private readonly TextBlock _status = new TextBlock { Foreground = Theme.Danger, TextWrapping = TextWrapping.Wrap };

	public LogoAction Action { get; private set; }

	public string LogoPath { get; private set; }

	public bool UseReception => _reception.IsChecked == true;

	public bool ImageAlreadySet => _alreadySet.IsChecked == true;

	public double LogoWidth { get; private set; }

	public double CenterHeight { get; private set; }

	public LogoWindow(string logoPath, string receptionNames, int selectedWalls, int guides, string widthText, string heightText, Func<string, double?> parse)
	{
		_parse = parse;
		Title = "Place Logo";
		Width = 560.0;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Theme.Apply(this);

		StackPanel body = new StackPanel { Margin = new Thickness(24.0, 18.0, 24.0, 18.0) };

		body.Children.Add(Theme.Heading("Client logo"));
		Grid logo = new Grid { Margin = new Thickness(0.0, 8.0, 0.0, 0.0) };
		logo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		logo.ColumnDefinitions.Add(new ColumnDefinition());
		logo.Children.Add(new Border { Background = Theme.Canvas, CornerRadius = new CornerRadius(6.0), Padding = new Thickness(6.0), Child = _preview });
		StackPanel pathPanel = new StackPanel { Margin = new Thickness(14.0, 0.0, 0.0, 0.0), VerticalAlignment = VerticalAlignment.Center };
		pathPanel.Children.Add(_pathText);
		Button change = new Button { Content = "Choose another image…", Style = Theme.Get("Link"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6.0, 4.0, 0.0, 0.0) };
		change.Click += (s, e) => Browse();
		pathPanel.Children.Add(change);
		Grid.SetColumn(pathPanel, 1);
		logo.Children.Add(pathPanel);
		body.Children.Add(logo);

		TextBlock where = Theme.Heading("Where");
		where.Margin = new Thickness(0.0, 20.0, 0.0, 6.0);
		body.Children.Add(where);
		_reception.Content = (receptionNames != null) ? ("Reception / lobby: the longest free wall (" + receptionNames + ")") : "Reception / lobby (no room with that name in this model)";
		_reception.IsEnabled = receptionNames != null;
		_selected.Content = (selectedWalls > 0) ? $"The {selectedWalls} wall(s) I selected" : "Walls I select (select walls before opening Place Logo)";
		_selected.IsEnabled = selectedWalls > 0;
		_selected.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
		_selected.IsChecked = selectedWalls > 0;
		_reception.IsChecked = selectedWalls == 0 && receptionNames != null;
		body.Children.Add(_reception);
		body.Children.Add(_selected);

		Grid size = new Grid { Margin = new Thickness(0.0, 16.0, 0.0, 0.0) };
		size.ColumnDefinitions.Add(new ColumnDefinition());
		size.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16.0) });
		size.ColumnDefinitions.Add(new ColumnDefinition());
		_widthBox.Text = widthText;
		_heightBox.Text = heightText;
		size.Children.Add(Labeled("Logo width", _widthBox));
		UIElement h = Labeled("Center height (from level)", _heightBox);
		Grid.SetColumn(h, 2);
		size.Children.Add(h);
		body.Children.Add(size);

		_alreadySet.Margin = new Thickness(0.0, 16.0, 0.0, 0.0);
		body.Children.Add(_alreadySet);
		TextBlock steps = Theme.Note("Place Logo marks the spot with a pink rectangle and copies the image path. Then Revit's Decal Types opens: pick \"logo\", paste the path as its Source and its Cutout, and click OK. Place the decal inside the rectangle, and run Place Logo again to center and size it exactly.");
		steps.FontSize = 12.0;
		steps.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		body.Children.Add(steps);
		_status.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		body.Children.Add(_status);

		Button mark = new Button { Content = "Mark the spot", IsDefault = true, Padding = new Thickness(18.0, 8.0, 18.0, 8.0) };
		mark.Click += (s, e) => Finish(LogoAction.Mark);
		Button cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16.0, 8.0, 16.0, 8.0) };
		StackPanel left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		if (guides > 0)
		{
			Button snap = new Button { Content = "Fit placed logo to guide", Padding = new Thickness(14.0, 8.0, 14.0, 8.0) };
			snap.Click += (s, e) => Finish(LogoAction.Snap);
			Button remove = new Button { Content = "Remove guides", Style = Theme.Get("Link"), Margin = new Thickness(8.0, 0.0, 0.0, 0.0) };
			remove.Click += (s, e) => Finish(LogoAction.RemoveGuides);
			left.Children.Add(snap);
			left.Children.Add(remove);
		}

		DockPanel root = new DockPanel();
		Border header = Theme.Header("Place Logo", "Finds the client's logo, marks where it goes and opens Revit's decal tools.");
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		Border footer = Theme.Footer(left, cancel, mark);
		DockPanel.SetDock(footer, Dock.Bottom);
		root.Children.Add(footer);
		root.Children.Add(body);
		Content = root;

		SetLogo(logoPath);
	}

	private static UIElement Labeled(string label, TextBox box)
	{
		StackPanel p = new StackPanel();
		p.Children.Add(new TextBlock { Text = label, Foreground = Theme.Muted, FontSize = 12.0, Margin = new Thickness(0.0, 0.0, 0.0, 4.0) });
		p.Children.Add(box);
		return p;
	}

	private void SetLogo(string path)
	{
		LogoPath = (path != null && File.Exists(path)) ? path : null;
		_pathText.Text = LogoPath ?? "No logo found. The Qbiq add-in's path note isn't in this model and there's no *_client image next to the project. Choose the image.";
		_pathText.Foreground = (LogoPath != null) ? Theme.Ink : Theme.Danger;
		_preview.Source = null;
		if (LogoPath == null)
		{
			return;
		}
		try
		{
			BitmapImage img = new BitmapImage();
			img.BeginInit();
			img.CacheOption = BitmapCacheOption.OnLoad;
			img.DecodePixelWidth = 280;
			img.UriSource = new Uri(LogoPath);
			img.EndInit();
			img.Freeze();
			_preview.Source = img;
		}
		catch
		{
		}
	}

	private void Browse()
	{
		OpenFileDialog dlg = new OpenFileDialog
		{
			Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
			Title = "Choose the client logo"
		};
		if (LogoPath != null)
		{
			dlg.InitialDirectory = Path.GetDirectoryName(LogoPath);
		}
		if (dlg.ShowDialog(this) == true)
		{
			SetLogo(dlg.FileName);
		}
	}

	private void Finish(LogoAction action)
	{
		if (action == LogoAction.Mark)
		{
			if (LogoPath == null)
			{
				_status.Text = "Choose the logo image first.";
				return;
			}
			if (_reception.IsChecked != true && _selected.IsChecked != true)
			{
				_status.Text = "Choose where the logo goes. To use your own walls, close this, select them, and open Place Logo again.";
				return;
			}
			double? w = _parse(_widthBox.Text);
			double? h = _parse(_heightBox.Text);
			if (!(w > 0.0) || !(h > 0.0))
			{
				_status.Text = "Enter the width and height as lengths, like 120 cm.";
				return;
			}
			LogoWidth = w.Value;
			CenterHeight = h.Value;
		}
		Action = action;
		DialogResult = true;
		Close();
	}
}
