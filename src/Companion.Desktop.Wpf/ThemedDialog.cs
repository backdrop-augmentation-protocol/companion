using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Bap.Companion.Desktop.Wpf;

internal static class ThemedDialog
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(234, 243, 255));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(154, 176, 208));

    public static Window CreateWindow(Window owner, string title, UIElement content, double width = 460, double minHeight = 0)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            MinHeight = minHeight,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(7, 20, 45)),
            Foreground = Ink,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize
        };
        ApplyChrome(owner, window, content);
        return window;
    }

    public static void ApplyChrome(Window owner, Window window, UIElement content)
    {
        window.WindowStyle = WindowStyle.None;
        window.ShowInTaskbar = false;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(8, 22, 49)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(40, 69, 114)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(new TextBlock
        {
            Text = window.Title,
            Foreground = Muted,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 8, 0),
            IsHitTestVisible = false
        });
        var close = new Button
        {
            Content = "\u00D7",
            Style = (Style)owner.FindResource("CaptionButton"),
            FontSize = 22,
            ToolTip = "Close",
            FocusVisualStyle = (Style)owner.FindResource("NoFocusVisual")
        };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1);
        headerGrid.Children.Add(close);
        header.Child = headerGrid;
        root.Children.Add(header);

        var body = new Border { Padding = new Thickness(24), Child = content };
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        window.Content = root;
    }

    public static Button CreateButton(Window owner, string text, bool primary, double minWidth = 88)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)owner.FindResource(primary ? "PrimaryButton" : "QuietButton"),
            MinWidth = minWidth,
            Padding = new Thickness(14, 8, 14, 8),
            FocusVisualStyle = (Style)owner.FindResource("NoFocusVisual")
        };
        return button;
    }

    public static bool Confirm(Window owner, string title, string message, string confirmText, string cancelText)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });
        var actions = CreateActions();
        var cancel = CreateButton(owner, cancelText, primary: false);
        var confirm = CreateButton(owner, confirmText, primary: true);
        content.Children.Add(actions);
        var window = CreateWindow(owner, title, content);
        cancel.Click += (_, _) => window.DialogResult = false;
        confirm.Click += (_, _) => window.DialogResult = true;
        confirm.IsDefault = true;
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        return window.ShowDialog() == true;
    }

    public static void ShowMessage(Window owner, string title, string message, string okText)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });
        var actions = CreateActions();
        var ok = CreateButton(owner, okText, primary: true);
        actions.Children.Add(ok);
        content.Children.Add(actions);
        var window = CreateWindow(owner, title, content);
        ok.Click += (_, _) => window.DialogResult = true;
        ok.IsDefault = true;
        window.ShowDialog();
    }

    public static string? Prompt(Window owner, string title, string initialValue, string okText, string cancelText)
    {
        var content = new StackPanel();
        var input = new TextBox
        {
            Text = initialValue,
            Style = (Style)owner.FindResource("DialogTextBox"),
            Margin = new Thickness(0, 0, 0, 20)
        };
        content.Children.Add(input);
        var actions = CreateActions();
        var cancel = CreateButton(owner, cancelText, primary: false);
        var ok = CreateButton(owner, okText, primary: true);
        actions.Children.Add(cancel);
        actions.Children.Add(ok);
        content.Children.Add(actions);
        var window = CreateWindow(owner, title, content, width: 390, minHeight: 0);
        cancel.Click += (_, _) => window.DialogResult = false;
        ok.Click += (_, _) => window.DialogResult = true;
        ok.IsDefault = true;
        cancel.IsCancel = true;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }

    private static StackPanel CreateActions() => new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right
    };
}
