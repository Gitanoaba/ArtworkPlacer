using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ArtworkPlacer;

internal static class Theme
{
	public static readonly Brush Canvas = Hex("#f6f7f9");

	public static readonly Brush Panel = Hex("#ffffff");

	public static readonly Brush Line = Hex("#e6e8ec");

	public static readonly Brush LineStrong = Hex("#d7dae0");

	public static readonly Brush Ink = Hex("#1f2733");

	public static readonly Brush Muted = Hex("#7b828e");

	public static readonly Brush Accent = Hex("#2f6bff");

	public static readonly Brush AccentSoft = Hex("#eaf1ff");

	public static readonly Brush AccentStrong = Hex("#1f52d6");

	public static readonly Brush Dark = Hex("#1e2430");

	public static readonly Brush Danger = Hex("#d64545");

	public static readonly FontFamily Font = new FontFamily("Roboto, Inter, Segoe UI");

	private const string MarkData = "M25.3046 0H1.85617C0.746504 0 0 0.789 0 1.7618V25.2386C0 26.2113 0.746504 27 1.85617 27H25.3046C26.4168 27 27.1608 26.2113 27.1608 25.2386V1.7618C27.1608 0.789 26.4168 0 25.3046 0ZM23.7107 23.4602H3.66779V21.3476C3.66779 21.3476 7.33557 21.2056 9.60703 20.7727L9.76087 20.7409V20.5337C9.67008 20.5111 9.48682 20.4459 9.48682 20.4459C6.08636 19.2267 3.64929 15.9876 3.64929 12.1928C3.64929 7.35584 7.60795 3.42146 12.4728 3.42146C17.3377 3.42146 21.2964 7.35668 21.2964 12.1928C21.2964 15.8597 19.0207 19.0061 15.7993 20.3147C15.1755 20.6089 14.1642 20.8337 13.4245 21.051V21.3485H23.7091V23.4611ZM19.1775 12.1928C19.1775 8.51661 16.1688 5.52655 12.4715 5.52655C8.77434 5.52655 5.76562 8.51745 5.76562 12.1928C5.76562 15.8681 8.7735 18.859 12.4715 18.859C16.1696 18.859 19.1775 15.8689 19.1775 12.1928Z";

	private static ResourceDictionary _styles;

	private static Brush Hex(string hex)
	{
		SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
		brush.Freeze();
		return brush;
	}

	public static void Apply(Window window)
	{
		window.Resources.MergedDictionaries.Add(Styles);
		window.Background = Canvas;
		window.Foreground = Ink;
		window.FontFamily = Font;
		window.FontSize = 13.0;
		TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
	}

	public static Style Get(string key)
	{
		return (Style)Styles[key];
	}

	public static Path Mark(double size)
	{
		return new Path
		{
			Data = Geometry.Parse(MarkData),
			Fill = Dark,
			Stretch = Stretch.Uniform,
			Width = size,
			Height = size
		};
	}

	public static Border Header(string title, string subtitle)
	{
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		Path mark = Mark(28.0);
		mark.VerticalAlignment = VerticalAlignment.Top;
		mark.Margin = new Thickness(0.0, 2.0, 14.0, 0.0);
		grid.Children.Add(mark);
		StackPanel text = new StackPanel();
		text.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 18.0,
			FontWeight = FontWeights.SemiBold
		});
		if (subtitle != null)
		{
			text.Children.Add(new TextBlock
			{
				Text = subtitle,
				Foreground = Muted,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
			});
		}
		Grid.SetColumn(text, 1);
		grid.Children.Add(text);
		return new Border
		{
			Background = Panel,
			BorderBrush = Line,
			BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0),
			Padding = new Thickness(24.0, 16.0, 24.0, 16.0),
			Child = grid
		};
	}

	public static Border Footer(UIElement left, params Button[] buttons)
	{
		DockPanel dock = new DockPanel { LastChildFill = true };
		StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
		foreach (Button b in buttons)
		{
			b.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
			actions.Children.Add(b);
		}
		DockPanel.SetDock(actions, Dock.Right);
		dock.Children.Add(actions);
		dock.Children.Add(left ?? new Border());
		return new Border
		{
			Background = Panel,
			BorderBrush = Line,
			BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0),
			Padding = new Thickness(24.0, 14.0, 24.0, 14.0),
			Child = dock
		};
	}

	public static TextBlock Heading(string text)
	{
		return new TextBlock
		{
			Text = text,
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold
		};
	}

	public static TextBlock Note(string text)
	{
		return new TextBlock
		{
			Text = text,
			Foreground = Muted,
			TextWrapping = TextWrapping.Wrap
		};
	}

	public static Grid WithPlaceholder(TextBox box, string placeholder)
	{
		TextBlock hint = new TextBlock
		{
			Text = placeholder,
			Foreground = Muted,
			IsHitTestVisible = false,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(11.0, 0.0, 0.0, 0.0)
		};
		void Sync() => hint.Visibility = (box.Text.Length == 0) ? Visibility.Visible : Visibility.Collapsed;
		box.TextChanged += (s, e) => Sync();
		Sync();
		Grid grid = new Grid();
		grid.Children.Add(box);
		grid.Children.Add(hint);
		return grid;
	}

	private static ResourceDictionary Styles => _styles ?? (_styles = (ResourceDictionary)XamlReader.Parse(Xaml));

	private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <SolidColorBrush x:Key='Canvas' Color='#f6f7f9'/>
  <SolidColorBrush x:Key='Panel' Color='#ffffff'/>
  <SolidColorBrush x:Key='Line' Color='#e6e8ec'/>
  <SolidColorBrush x:Key='LineStrong' Color='#d7dae0'/>
  <SolidColorBrush x:Key='Ink' Color='#1f2733'/>
  <SolidColorBrush x:Key='Muted' Color='#7b828e'/>
  <SolidColorBrush x:Key='Accent' Color='#2f6bff'/>
  <SolidColorBrush x:Key='AccentSoft' Color='#eaf1ff'/>
  <SolidColorBrush x:Key='AccentStrong' Color='#1f52d6'/>

  <Style x:Key='Focus'>
    <Setter Property='Control.Template'>
      <Setter.Value>
        <ControlTemplate>
          <Border Margin='-3' CornerRadius='8' BorderThickness='2' BorderBrush='#802f6bff'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='Label'>
    <Setter Property='Foreground' Value='{StaticResource Ink}'/>
    <Setter Property='Padding' Value='0'/>
  </Style>

  <Style TargetType='Button'>
    <Setter Property='Background' Value='{StaticResource Panel}'/>
    <Setter Property='Foreground' Value='{StaticResource Ink}'/>
    <Setter Property='BorderBrush' Value='{StaticResource LineStrong}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='14,7'/>
    <Setter Property='FontWeight' Value='Medium'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{StaticResource Focus}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Canvas}'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Line}'/>
            </Trigger>
            <Trigger Property='IsDefault' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Accent}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource Accent}'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
            <MultiTrigger>
              <MultiTrigger.Conditions>
                <Condition Property='IsDefault' Value='True'/>
                <Condition Property='IsMouseOver' Value='True'/>
              </MultiTrigger.Conditions>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource AccentStrong}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource AccentStrong}'/>
            </MultiTrigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='bd' Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Link' TargetType='Button'>
    <Setter Property='Foreground' Value='{StaticResource Accent}'/>
    <Setter Property='Padding' Value='6,4'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{StaticResource Focus}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='{TemplateBinding Padding}'>
            <ContentPresenter VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource AccentSoft}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{StaticResource Muted}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Segment' TargetType='RadioButton'>
    <Setter Property='Foreground' Value='{StaticResource Muted}'/>
    <Setter Property='FontWeight' Value='Medium'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{StaticResource Focus}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Border x:Name='bd' Background='Transparent' CornerRadius='5' Padding='10,6'>
            <ContentPresenter HorizontalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter Property='Foreground' Value='{StaticResource Ink}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Panel}'/>
              <Setter TargetName='bd' Property='Effect'>
                <Setter.Value>
                  <DropShadowEffect BlurRadius='4' ShadowDepth='1' Direction='270' Opacity='0.10' Color='#101828'/>
                </Setter.Value>
              </Setter>
              <Setter Property='Foreground' Value='{StaticResource Ink}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='TextBox'>
    <Setter Property='Background' Value='{StaticResource Panel}'/>
    <Setter Property='Foreground' Value='{StaticResource Ink}'/>
    <Setter Property='BorderBrush' Value='{StaticResource LineStrong}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='8,4'/>
    <Setter Property='MinHeight' Value='34'/>
    <Setter Property='CaretBrush' Value='{StaticResource Ink}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource Accent}'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' SnapsToDevicePixels='True'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='Center'
                          Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource Muted}'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource Accent}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Canvas}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource Line}'/>
              <Setter Property='Foreground' Value='{StaticResource Muted}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='CheckBox'>
    <Setter Property='Foreground' Value='{StaticResource Ink}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='FocusVisualStyle' Value='{StaticResource Focus}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <Grid Background='Transparent'>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width='Auto'/>
              <ColumnDefinition/>
            </Grid.ColumnDefinitions>
            <Border x:Name='box' Width='16' Height='16' CornerRadius='4' BorderThickness='1.5' VerticalAlignment='Center'
                    Background='{StaticResource Panel}' BorderBrush='{StaticResource LineStrong}' SnapsToDevicePixels='True'>
              <Grid>
                <Path x:Name='tick' Data='M3,8 L6.5,11.5 L13,4.5' Stroke='White' StrokeThickness='2' Visibility='Collapsed'
                      StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Margin='-1.5'/>
                <Rectangle x:Name='dash' Width='8' Height='2' Fill='White' RadiusX='1' RadiusY='1' Visibility='Collapsed'/>
              </Grid>
            </Border>
            <ContentPresenter x:Name='content' Grid.Column='1' Margin='8,0,0,0' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='True'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='Content' Value='{x:Null}'>
              <Setter TargetName='content' Property='Margin' Value='0'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='box' Property='BorderBrush' Value='{StaticResource Accent}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='box' Property='Background' Value='{StaticResource Accent}'/>
              <Setter TargetName='box' Property='BorderBrush' Value='{StaticResource Accent}'/>
              <Setter TargetName='tick' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='{x:Null}'>
              <Setter TargetName='box' Property='Background' Value='{StaticResource Accent}'/>
              <Setter TargetName='box' Property='BorderBrush' Value='{StaticResource Accent}'/>
              <Setter TargetName='dash' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='ComboToggle' TargetType='ToggleButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToggleButton'>
          <Border x:Name='bd' Background='{StaticResource Panel}' BorderBrush='{StaticResource LineStrong}' BorderThickness='1' CornerRadius='6' SnapsToDevicePixels='True'>
            <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,10,0' Data='M0,0 L4,4 L8,0'
                  Stroke='{StaticResource Muted}' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource Muted}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource Accent}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Canvas}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource Line}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='{StaticResource Ink}'/>
    <Setter Property='MinHeight' Value='34'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{StaticResource Focus}'/>
    <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Disabled'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton Style='{StaticResource ComboToggle}' IsEnabled='{TemplateBinding IsEnabled}'
                          IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'/>
            <ContentPresenter x:Name='content' IsHitTestVisible='False' Margin='10,0,28,0' VerticalAlignment='Center'
                              Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'/>
            <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False' PopupAnimation='None'>
              <Border Margin='0,4,8,10' MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='320'
                      Background='{StaticResource Panel}' BorderBrush='{StaticResource Line}' BorderThickness='1' CornerRadius='8' Padding='4'>
                <Border.Effect>
                  <DropShadowEffect BlurRadius='16' ShadowDepth='4' Direction='270' Opacity='0.10' Color='#101828'/>
                </Border.Effect>
                <ScrollViewer>
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='content' Property='Opacity' Value='0.5'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{StaticResource Ink}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='bd' Background='Transparent' CornerRadius='5' Padding='8,6'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Canvas}'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource AccentSoft}'/>
              <Setter Property='Foreground' Value='{StaticResource AccentStrong}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ListBox'>
    <Setter Property='Background' Value='{StaticResource Panel}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='4'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ListBox'>
          <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8'>
            <ScrollViewer Padding='{TemplateBinding Padding}' Focusable='False'>
              <ItemsPresenter/>
            </ScrollViewer>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ListBoxItem'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ListBoxItem'>
          <Border x:Name='bd' Background='Transparent' CornerRadius='5' Padding='8,5'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Canvas}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ToolTip'>
    <Setter Property='Foreground' Value='{StaticResource Ink}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='{StaticResource Panel}' BorderBrush='{StaticResource Line}' BorderThickness='1' CornerRadius='8' Padding='10' Margin='0,0,10,10'>
            <Border.Effect>
              <DropShadowEffect BlurRadius='16' ShadowDepth='4' Direction='270' Opacity='0.10' Color='#101828'/>
            </Border.Effect>
            <ContentPresenter/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='ThumbStyle' TargetType='Thumb'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Border x:Name='bd' Background='{StaticResource LineStrong}' CornerRadius='3'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource Muted}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='10'/>
    <Setter Property='MinWidth' Value='10'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True' Margin='2'>
            <Track.Thumb>
              <Thumb Style='{StaticResource ThumbStyle}'/>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='MinWidth' Value='0'/>
        <Setter Property='Height' Value='10'/>
        <Setter Property='MinHeight' Value='10'/>
        <Setter Property='Template'>
          <Setter.Value>
            <ControlTemplate TargetType='ScrollBar'>
              <Track x:Name='PART_Track' IsDirectionReversed='False' Margin='2'>
                <Track.Thumb>
                  <Thumb Style='{StaticResource ThumbStyle}'/>
                </Track.Thumb>
              </Track>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>";
}
