using Bap.Companion.Core;

namespace Bap.Companion.Desktop;

internal sealed class CatalogView : UserControl
{
    private readonly Func<string, string> translate;
    private readonly Func<InstalledItem, int, Task> switchHost;
    private readonly FlowLayoutPanel cards = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, AutoScroll = true, Padding = new Padding(10)
    };
    private readonly Panel details = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Surface };
    private readonly Dictionary<string, Image> thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Panel> cardPanels = [];
    private IReadOnlyList<InstalledItem> items = [];
    private InstalledItem? selected;

    public CatalogView(Func<string, string> translate, Func<InstalledItem, int, Task> switchHost)
    {
        this.translate = translate;
        this.switchHost = switchHost;
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
            SplitterWidth = 7, BackColor = UiTheme.Background
        };
        split.Panel1.Controls.Add(cards);
        split.Panel2.Controls.Add(details);
        Controls.Add(split);
        SizeChanged += (_, _) =>
        {
            if (split.Width > 750 && split.SplitterDistance < 360)
                split.SplitterDistance = Math.Max(360, split.Width * 55 / 100);
            ResizeCards();
        };
        cards.ClientSizeChanged += (_, _) => ResizeCards();
        RenderDetails();
    }

    public void SetItems(IReadOnlyList<InstalledItem> items)
    {
        this.items = items;
        selected = items.FirstOrDefault(item => item.Key == selected?.Key);
        foreach (Control control in cards.Controls.Cast<Control>().ToArray()) control.Dispose();
        cards.Controls.Clear();
        cardPanels.Clear();
        foreach (var image in thumbnails.Values) image.Dispose();
        thumbnails.Clear();
        AddSection("hosts", "host", "noHosts");
        AddSection("components", "component", "noComponents");
        ResizeCards();
        RenderDetails();
    }

    public void RefreshLanguage() => SetItems(items);

    private void AddSection(string titleKey, string role, string emptyKey)
    {
        var heading = UiTheme.Heading(translate(titleKey));
        cards.Controls.Add(heading);
        var matching = items.Where(item => item.Role == role).ToArray();
        if (matching.Length == 0) cards.Controls.Add(UiTheme.Caption(translate(emptyKey)));
        foreach (var item in matching) cards.Controls.Add(CreateCard(item));
    }

    private Panel CreateCard(InstalledItem item)
    {
        var card = new Panel
        {
            Height = 116, BackColor = item.Key == selected?.Key ? UiTheme.Selected : UiTheme.Surface,
            Margin = new Padding(4, 3, 12, 8), Padding = new Padding(12), Cursor = Cursors.Hand,
            Tag = item.Key
        };
        cardPanels.Add(card);
        var image = CreatePreview(item, 150, 90);
        image.Location = new Point(12, 12);
        card.Controls.Add(image);
        var title = new Label
        {
            Text = item.Title, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = UiTheme.Text,
            Location = new Point(174, 12), Width = 290, Height = 38, AutoEllipsis = true
        };
        var source = new Label
        {
            Text = translate(item.SourceKind), Font = new Font("Segoe UI", 9), ForeColor = UiTheme.Muted,
            Location = new Point(174, 54), Width = 290, Height = 22, AutoEllipsis = true
        };
        var status = new Label
        {
            Text = $"{translate(item.Status)}  ·  {item.Version ?? "—"}",
            Font = new Font("Segoe UI", 9), ForeColor = item.Status == "invalid" ? Color.Firebrick : UiTheme.Accent,
            Location = new Point(174, 79), Width = 290, Height = 22, AutoEllipsis = true
        };
        card.Controls.Add(title);
        card.Controls.Add(source);
        card.Controls.Add(status);
        void Select(object? _, EventArgs __) => SelectItem(item);
        card.Click += Select;
        image.Click += Select;
        title.Click += Select;
        source.Click += Select;
        status.Click += Select;
        card.Resize += (_, _) =>
        {
            var width = Math.Max(80, card.ClientSize.Width - 188);
            title.Width = source.Width = status.Width = width;
        };
        return card;
    }

    private Control CreatePreview(InstalledItem item, int width, int height)
    {
        if (item.PreviewPath is not null && GetThumbnail(item.PreviewPath) is { } thumbnail)
            return new PictureBox { Width = width, Height = height, Image = thumbnail, SizeMode = PictureBoxSizeMode.Zoom, BackColor = UiTheme.Background };
        return new Label
        {
            Width = width, Height = height, Text = translate("noPreview"),
            TextAlign = ContentAlignment.MiddleCenter, BackColor = UiTheme.Background, ForeColor = UiTheme.Muted
        };
    }

    private Image? GetThumbnail(string path)
    {
        if (thumbnails.TryGetValue(path, out var cached)) return cached;
        try
        {
            using var stream = File.OpenRead(path);
            using var original = Image.FromStream(stream);
            var bitmap = new Bitmap(320, 180);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(UiTheme.Background);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var scale = Math.Min(320f / original.Width, 180f / original.Height);
                var width = (int)(original.Width * scale);
                var height = (int)(original.Height * scale);
                graphics.DrawImage(original, (320 - width) / 2, (180 - height) / 2, width, height);
            }
            thumbnails[path] = bitmap;
            return bitmap;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        { return null; }
    }

    private void SelectItem(InstalledItem item)
    {
        selected = item;
        foreach (var card in cardPanels)
        {
            var itemKey = card.Tag as string;
            card.BackColor = itemKey == item.Key ? UiTheme.Selected : UiTheme.Surface;
        }
        RenderDetails();
    }

    private void RenderDetails()
    {
        foreach (Control control in details.Controls.Cast<Control>().ToArray()) control.Dispose();
        details.Controls.Clear();
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20), BackColor = UiTheme.Surface
        };
        details.Controls.Add(stack);
        stack.Controls.Add(UiTheme.Heading(translate("details")));
        if (selected is null)
        {
            stack.Controls.Add(UiTheme.Caption(translate("selectItem")));
            return;
        }
        var item = selected;
        var image = CreatePreview(item, 320, 180);
        image.Margin = new Padding(4, 12, 4, 18);
        stack.Controls.Add(image);
        var heading = UiTheme.Heading(item.Title);
        heading.MaximumSize = new Size(340, 0);
        stack.Controls.Add(heading);
        AddDetail(stack, "source", translate(item.SourceKind));
        AddDetail(stack, "status", translate(item.Status));
        AddDetail(stack, "bapId", item.BapId ?? "—");
        AddDetail(stack, "version", item.Version ?? "—");
        if (item.WorkshopId is not null) AddDetail(stack, "workshopId", item.WorkshopId);
        AddDetail(stack, "location", item.ProjectDirectory);
        if (item.Role != "host" || item.Status == "invalid") return;
        var monitorLabel = UiTheme.Caption(translate("monitor"));
        monitorLabel.Margin = new Padding(4, 20, 4, 4);
        stack.Controls.Add(monitorLabel);
        var monitor = new ComboBox { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
        for (var index = 0; index < Screen.AllScreens.Length; index++) monitor.Items.Add($"{translate("monitor")} {index + 1}");
        if (monitor.Items.Count > 0) monitor.SelectedIndex = 0;
        stack.Controls.Add(monitor);
        var button = UiTheme.Button(translate("switchHost"), primary: true);
        button.Click += async (_, _) => await switchHost(item, monitor.SelectedIndex);
        stack.Controls.Add(button);
    }

    private void AddDetail(FlowLayoutPanel stack, string key, string value)
    {
        var label = UiTheme.Caption(translate(key).ToUpperInvariant());
        label.Margin = new Padding(4, 8, 4, 2);
        stack.Controls.Add(label);
        var content = new Label { Text = value, AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = UiTheme.Text, Margin = new Padding(4, 0, 4, 2) };
        stack.Controls.Add(content);
    }

    private void ResizeCards()
    {
        var width = Math.Max(330, cards.ClientSize.Width - cards.Padding.Horizontal - 25);
        foreach (var card in cardPanels) card.Width = width;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var image in thumbnails.Values) image.Dispose();
        base.Dispose(disposing);
    }
}
