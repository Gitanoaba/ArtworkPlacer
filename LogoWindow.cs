using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ArtworkPlacer;

public class LogoWindow : Window
{
	private readonly Func<string, double?> _parse;

	private readonly Image _preview = new Image { Width = 140.0, Height = 90.0, Stretch = Stretch.Uniform };

	private readonly TextBlock _pathText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.0 };

	private readonly RadioButton _reception = new RadioButton { GroupName = "where" };

	private readonly RadioButton _selected = new RadioButton { GroupName = "where" };

	// Solo cambiar la imagen de los logos que ya están (p. ej. el de reemplazo, cuando llega el del cliente)
	private readonly RadioButton _update = new RadioButton { GroupName = "where" };

	private readonly TextBox _widthBox = new TextBox();

	private readonly TextBox _heightBox = new TextBox();

	private readonly TextBlock _status = new TextBlock { Foreground = Theme.Danger, TextWrapping = TextWrapping.Wrap };

	public string LogoPath { get; private set; }

	public bool UseReception => _reception.IsChecked == true;

	public bool UpdateOnly => _update.IsChecked == true;

	public double LogoWidth { get; private set; }

	public double CenterHeight { get; private set; }

	public LogoWindow(string logoPath, string receptionNames, int selectedWalls, string widthText, string heightText, Func<string, double?> parse, int existing = 0)
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
		_update.Content = $"Only change the image of the {existing} logo(s) already in the model";
		_update.Visibility = (existing > 0) ? Visibility.Visible : Visibility.Collapsed;
		_update.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
		if (existing > 0 && selectedWalls == 0)
		{
			_update.IsChecked = true;
		}
		body.Children.Add(_update);
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

		TextBlock steps = Theme.Note("Builds a \"Qbiq Logo\" family the size of the image, with the image as its material, and centers it on the wall. Check the arrow, click the wall to switch faces, then press ESC to place it. Running it again with another image or size updates the logo already placed.");
		steps.FontSize = 12.0;
		steps.Margin = new Thickness(0.0, 16.0, 0.0, 0.0);
		body.Children.Add(steps);
		_status.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		body.Children.Add(_status);

		Button mark = new Button { Content = "Place logo", IsDefault = true, Padding = new Thickness(18.0, 8.0, 18.0, 8.0) };
		mark.Click += (s, e) => Finish();
		Button cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16.0, 8.0, 16.0, 8.0) };
		StackPanel left = new StackPanel();

		DockPanel root = new DockPanel();
		Border header = Theme.Header("Place Logo", "Finds the client's logo, builds it as a family and places it on the wall.");
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

	private void Finish()
	{
		if (LogoPath == null)
		{
			_status.Text = "Choose the logo image first.";
			return;
		}
		if (_reception.IsChecked != true && _selected.IsChecked != true && _update.IsChecked != true)
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
		DialogResult = true;
		Close();
	}
}
