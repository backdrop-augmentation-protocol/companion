using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Brush = System.Windows.Media.Brush;

namespace Bap.Companion.Desktop.Wpf;

internal sealed record CatalogPickerItem(object Value, string Title, string Source, string Summary, ImageSource? Preview, string NoPreview)
{
    public string SearchText => $"{Title} {Source} {Summary}";
}

internal static class CatalogSelectionDialog
{
    public static CatalogPickerItem? Show(Window owner, string title, IReadOnlyList<CatalogPickerItem> items, string language)
    {
        if (items.Count == 0) return null;
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var search = new TextBox
        {
            Style = (Style)owner.FindResource("DialogTextBox"),
            Margin = new Thickness(0, 0, 0, 12),
            ToolTip = UiStrings.Get("searchCatalog", language)
        };
        content.Children.Add(search);
        var list = new ListBox
        {
            Style = (Style)owner.FindResource("CatalogList"),
            MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height * 0.58),
            Margin = new Thickness(0, 0, 0, 16)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var listArea = new Grid();
        listArea.Children.Add(list);
        var noMatches = new TextBlock
        {
            Text = UiStrings.Get("noCatalogMatches", language),
            Foreground = (Brush)owner.FindResource("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        listArea.Children.Add(noMatches);
        Grid.SetRow(listArea, 1);
        content.Children.Add(listArea);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = ThemedDialog.CreateButton(owner, language == "zh-CN" ? "取消" : "Cancel", primary: false);
        var add = ThemedDialog.CreateButton(owner, language == "zh-CN" ? "添加所选项目" : "Add selected", primary: true);
        add.IsEnabled = false;
        footer.Children.Add(cancel);
        footer.Children.Add(add);
        Grid.SetRow(footer, 2);
        content.Children.Add(footer);
        var window = ThemedDialog.CreateWindow(owner, title, content, width: 780);
        window.MaxHeight = SystemParameters.WorkArea.Height * 0.9;

        void Filter()
        {
            var query = search.Text.Trim();
            list.ItemsSource = items.Where(item => query.Length == 0 || item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            add.IsEnabled = list.SelectedItem is CatalogPickerItem;
            noMatches.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItem is CatalogPickerItem;
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is CatalogPickerItem) window.DialogResult = true;
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        add.Click += (_, _) => window.DialogResult = true;
        cancel.IsCancel = true;
        add.IsDefault = true;
        Filter();
        window.Loaded += (_, _) => search.Focus();
        return window.ShowDialog() == true ? list.SelectedItem as CatalogPickerItem : null;
    }
}
