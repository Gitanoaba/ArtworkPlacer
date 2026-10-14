using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArtworkPlacer;

public class AlignDialog : Window
{
	private readonly DialogSettings _settings;

	private readonly Func<string, double?> _parseLength;

	private readonly ComboBox _alignBox;

	private readonly TextBox _heightBox;

	public double ArtHeight { get; private set; }

	public HeightAlign Align { get; private set; }

	public AlignDialog(int count, string source, DialogSettings settings, Func<string, double?> parseLength)
	{
		_settings = settings;
		_parseLength = parseLength;
		Title = "Align Artwork Heights";
		Width = 460.0;
		SizeToContent = SizeToContent.Height;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		ResizeMode = ResizeMode.NoResize;
		Theme.Apply(this);
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(24.0, 18.0, 24.0, 22.0)
		};
		TextBlock note = Theme.Note("Each piece moves up or down only, measured from its wall's level. Only the visible artwork counts, not hidden lines in the family.");
		note.Margin = new Thickness(0.0, 0.0, 0.0, 14.0);
		stackPanel.Children.Add(note);
		Grid grid = new Grid
		{
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = new GridLength(150.0)
				},
				new ColumnDefinition()
			}
		};
		_alignBox = ArtworkDialog.NewAlignBox(settings.Align, includeFamilyDefault: false);
		_alignBox.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		TextBox textBox = new TextBox();
		object text;
		if (settings.Align != HeightAlign.FamilyDefault)
		{
			text = settings.HeightText;
		}
		else
		{
			string[] heightDefaults = settings.HeightDefaults;
			text = ((heightDefaults != null) ? heightDefaults[0] : null) ?? settings.HeightText;
		}
		textBox.Text = (string)text;
		_heightBox = textBox;
		ArtworkDialog.HookAlign(_alignBox, _heightBox, settings);
		Grid.SetColumn(_alignBox, 0);
		Grid.SetColumn(_heightBox, 1);
		grid.Children.Add(_alignBox);
		grid.Children.Add(_heightBox);
		stackPanel.Children.Add(grid);
		Button button = new Button
		{
			Content = "Align heights",
			Padding = new Thickness(18.0, 8.0, 18.0, 8.0),
			IsDefault = true
		};
		button.Click += (object s, RoutedEventArgs e) =>
		{
			Accept();
		};
		Button cancel = new Button
		{
			Content = "Cancel",
			Padding = new Thickness(16.0, 8.0, 16.0, 8.0),
			IsCancel = true
		};
		DockPanel root = new DockPanel();
		Border header = Theme.Header("Align artwork heights", $"{count} artwork piece(s) {source}.");
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		Border footer = Theme.Footer(null, cancel, button);
		DockPanel.SetDock(footer, Dock.Bottom);
		root.Children.Add(footer);
		root.Children.Add(stackPanel);
		Content = root;
	}

	private void Accept()
	{
		double? num = _parseLength(_heightBox.Text);
		if (!num.HasValue || num <= 0.0)
		{
			MessageBox.Show(this, "Enter the height as a length, like 150 cm.", Title);
			return;
		}
		ArtHeight = num.Value;
		Align = (HeightAlign)_alignBox.SelectedIndex;
		_settings.Align = Align;
		_settings.HeightText = _heightBox.Text;
		DialogResult = true;
		Close();
	}
}
