using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;

namespace Bap.Companion.Desktop.Wpf;

internal static class ColorPickerDialog
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(255, 255, 255), Color.FromRgb(220, 220, 220), Color.FromRgb(160, 160, 160), Color.FromRgb(64, 64, 64), Color.FromRgb(0, 0, 0),
        Color.FromRgb(255, 64, 64), Color.FromRgb(255, 145, 48), Color.FromRgb(255, 210, 55), Color.FromRgb(90, 205, 95), Color.FromRgb(40, 190, 205),
        Color.FromRgb(55, 125, 245), Color.FromRgb(145, 85, 230), Color.FromRgb(230, 75, 170), Color.FromRgb(145, 65, 65), Color.FromRgb(170, 110, 55),
        Color.FromRgb(155, 165, 65), Color.FromRgb(50, 135, 90), Color.FromRgb(45, 130, 145), Color.FromRgb(65, 90, 165), Color.FromRgb(115, 75, 155),
        Color.FromRgb(170, 75, 115), Color.FromRgb(255, 175, 175), Color.FromRgb(255, 205, 155), Color.FromRgb(255, 235, 155), Color.FromRgb(175, 235, 175),
        Color.FromRgb(160, 230, 235), Color.FromRgb(165, 195, 255), Color.FromRgb(205, 180, 250), Color.FromRgb(245, 180, 220), Color.FromRgb(125, 155, 190)
    ];

    public static Color? Show(Window owner, Color initial, bool chinese)
    {
        var selected = initial;
        var content = new StackPanel();
        var preview = new Border
        {
            Height = 48,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(40, 69, 114)),
            Background = new SolidColorBrush(selected),
            Margin = new Thickness(0, 0, 0, 12)
        };
        content.Children.Add(preview);
        var hex = new TextBox
        {
            Text = ToHex(selected),
            Style = (Style)owner.FindResource("DialogTextBox"),
            Margin = new Thickness(0, 0, 0, 12),
            MaxLength = 7
        };
        content.Children.Add(hex);
        var palette = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 14) };
        content.Children.Add(palette);

        void Update(Color color)
        {
            selected = color;
            preview.Background = new SolidColorBrush(color);
            hex.Text = ToHex(color);
        }

        foreach (var color in Palette)
        {
            var swatch = new Button
            {
                Width = 38,
                Height = 30,
                Margin = new Thickness(3),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromRgb(75, 104, 148)),
                BorderThickness = new Thickness(1),
                ToolTip = ToHex(color),
                FocusVisualStyle = (Style)owner.FindResource("NoFocusVisual")
            };
            swatch.Click += (_, _) => Update(color);
            palette.Children.Add(swatch);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var sampleButton = ThemedDialog.CreateButton(owner, chinese ? "屏幕取色" : "Pick from screen", primary: false);
        var cancel = ThemedDialog.CreateButton(owner, chinese ? "取消" : "Cancel", primary: false);
        var apply = ThemedDialog.CreateButton(owner, chinese ? "应用" : "Apply", primary: true);
        actions.Children.Add(sampleButton);
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        content.Children.Add(actions);

        var window = ThemedDialog.CreateWindow(owner, chinese ? "选择颜色" : "Choose color", content, width: 390);
        hex.TextChanged += (_, _) =>
        {
            if (TryParseHex(hex.Text, out var parsed))
            {
                selected = parsed;
                preview.Background = new SolidColorBrush(parsed);
            }
        };
        hex.LostKeyboardFocus += (_, _) =>
        {
            if (!TryParseHex(hex.Text, out var parsed)) hex.Text = ToHex(selected);
            else Update(parsed);
        };
        sampleButton.Click += (_, _) =>
        {
            var picked = ScreenSampler.Pick(owner, chinese);
            if (picked is { } color) Update(color);
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        apply.Click += (_, _) =>
        {
            if (!TryParseHex(hex.Text, out var parsed))
            {
                hex.Text = ToHex(selected);
                return;
            }
            selected = parsed;
            window.DialogResult = true;
        };
        apply.IsDefault = true;
        cancel.IsCancel = true;
        return window.ShowDialog() == true ? selected : null;
    }

    internal static string FormatWeColor(Color color) => string.Join(" ",
        (color.R / 255d).ToString("0.######", CultureInfo.InvariantCulture),
        (color.G / 255d).ToString("0.######", CultureInfo.InvariantCulture),
        (color.B / 255d).ToString("0.######", CultureInfo.InvariantCulture));

    internal static Color ParseWeColor(string? value)
    {
        var parts = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is not { Length: 3 }) return Colors.White;
        var rgb = new byte[3];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var channel) || channel is < 0 or > 1)
                return Colors.White;
            rgb[index] = (byte)Math.Round(channel * 255, MidpointRounding.AwayFromZero);
        }
        return Color.FromRgb(rgb[0], rgb[1], rgb[2]);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.White;
        if (text is null) return false;
        var value = text.StartsWith('#') ? text[1..] : text;
        if (value.Length != 6 || !byte.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(value[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue)) return false;
        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private sealed class ScreenSampler
    {
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
        private const uint InvalidPixel = 0xFFFFFFFF;

        public static Color? Pick(Window owner, bool chinese)
        {
            Color? result = null;
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                Topmost = true,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Cursor = Cursors.Cross
            };
            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 7, 20, 45)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(34, 211, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 12, 18, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 30, 0, 0),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = chinese ? "单击屏幕任意位置取色 · Esc 取消" : "Click anywhere to sample · Esc to cancel",
                    Foreground = Brushes.White,
                    FontSize = 15
                }
            };
            overlay.Content = new Grid { Children = { hint } };
            overlay.PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape) overlay.Close();
            };
            overlay.MouseLeftButtonDown += async (_, args) =>
            {
                var point = Forms.Cursor.Position;
                overlay.Hide();
                await Task.Delay(100);
                var dc = GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) { overlay.Close(); return; }
                try
                {
                    var pixel = GetPixel(dc, point.X, point.Y);
                    if (pixel != InvalidPixel)
                        result = Color.FromRgb((byte)(pixel & 0xFF), (byte)((pixel >> 8) & 0xFF), (byte)((pixel >> 16) & 0xFF));
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
                overlay.Close();
                args.Handled = true;
            };
            overlay.ShowDialog();
            owner.Activate();
            return result;
        }
    }
}
