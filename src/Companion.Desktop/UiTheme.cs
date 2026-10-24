namespace Bap.Companion.Desktop;

internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(244, 247, 251);
    public static readonly Color Surface = Color.White;
    public static readonly Color Text = Color.FromArgb(30, 41, 59);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
    public static readonly Color Accent = Color.FromArgb(38, 99, 235);
    public static readonly Color Selected = Color.FromArgb(232, 240, 255);
    public static readonly Color Border = Color.FromArgb(224, 231, 239);

    public static Button Button(string title = "", bool primary = false)
    {
        var button = new Button
        {
            Text = title, AutoSize = true, MinimumSize = new Size(120, 34),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface,
            ForeColor = primary ? Surface : Text, Cursor = Cursors.Hand,
            Margin = new Padding(4, 4, 8, 4), Padding = new Padding(10, 3, 10, 3)
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(29, 78, 216) : Selected;
        return button;
    }

    public static Label Heading(string text = "") => new()
    {
        Text = text, AutoSize = true, ForeColor = Text,
        Font = new Font("Segoe UI", 14, FontStyle.Bold), Margin = new Padding(4, 8, 4, 8)
    };

    public static Label Caption(string text = "") => new()
    {
        Text = text, AutoSize = true, ForeColor = Muted,
        Font = new Font("Segoe UI", 9), Margin = new Padding(4, 2, 4, 8)
    };
}
