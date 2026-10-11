using System;
using System.Windows;
using System.Windows.Controls;

namespace ArtworkPlacer
{
    // ─── VENTANA WPF: ALINEAR ALTURAS ────────────────────────────────────────
    public class AlignDialog : Window
    {
        public double ArtHeight { get; private set; }
        public HeightAlign Align { get; private set; }

        private readonly DialogSettings _settings;
        private readonly Func<string, double?> _parseLength;
        private readonly ComboBox _alignBox;
        private readonly TextBox _heightBox;

        public AlignDialog(int count, string source, DialogSettings settings, Func<string, double?> parseLength)
        {
            _settings = settings;
            _parseLength = parseLength;

            Title = "Align Artwork Heights";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = $"{count} artwork piece(s) {source}.",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Each piece moves up or down only, measured from its wall's level. Only the visible artwork counts, not hidden lines in the family.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            _alignBox = ArtworkDialog.NewAlignBox(settings.Align, includeFamilyDefault: false);
            _heightBox = new TextBox
            {
                Text = settings.Align == HeightAlign.FamilyDefault ? settings.HeightDefaults?[0] ?? settings.HeightText : settings.HeightText,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(4)
            };
            ArtworkDialog.HookAlign(_alignBox, _heightBox, settings);
            Grid.SetColumn(_alignBox, 0);
            Grid.SetColumn(_heightBox, 1);
            row.Children.Add(_alignBox);
            row.Children.Add(_heightBox);
            root.Children.Add(row);

            var btnAlign = new Button
            {
                Content = "Align heights",
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true
            };
            btnAlign.Click += (s, e) => Accept();
            root.Children.Add(btnAlign);

            Content = root;
        }

        private void Accept()
        {
            double? h = _parseLength(_heightBox.Text);
            if (h == null || h <= 0) { MessageBox.Show(this, "Invalid height.", Title); return; }

            ArtHeight = h.Value;
            Align = (HeightAlign)_alignBox.SelectedIndex;

            // Misma altura para la próxima colocación
            _settings.Align = Align;
            _settings.HeightText = _heightBox.Text;

            DialogResult = true;
            Close();
        }
    }
}
