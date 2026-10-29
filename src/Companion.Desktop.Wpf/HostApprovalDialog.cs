using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bap.Companion.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace Bap.Companion.Desktop.Wpf;

internal static class HostApprovalDialog
{
    public static bool Show(Window owner, HostAuthorizationRequest request, string language,
        out IReadOnlyCollection<string> grantedPermissions)
    {
        var chinese = language == "zh-CN";
        var selected = new List<CheckBox>();
        var window = new Window
        {
            Title = chinese ? "BAP 宿主访问请求" : "BAP Host access request",
            Owner = owner,
            Width = 510,
            SizeToContent = SizeToContent.Height,
            MinHeight = 370,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(8, 22, 48)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 243, 255)),
            FontFamily = owner.FontFamily,
            FontSize = 14
        };

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = chinese ? "宿主身份由壁纸自行声明，尚未验证" : "Host identity is self-reported and unverified",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 255)),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{Display(request.ClaimedIdentity.Name)}\n{Display(request.ClaimedIdentity.Id)}\n{(chinese ? "版本" : "Version")}: {Display(request.ClaimedIdentity.Version)}",
            Margin = new Thickness(0, 14, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(48, 31, 44)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(182, 85, 111)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = chinese
                    ? "安装来源无法由 Companion 验证。只在你能独立确认当前 Wallpaper Engine 壁纸身份时批准；批准仅适用于本次会话。"
                    : "Companion cannot verify the installation source. Approve only if you can independently identify the active Wallpaper Engine wallpaper. Approval applies to this session only.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 202))
            }
        });
        content.Children.Add(new TextBlock
        {
            Text = chinese ? "请求权限（可按需勾选）" : "Requested permissions (select only what you need)",
            Margin = new Thickness(0, 18, 0, 6),
            FontWeight = FontWeights.SemiBold
        });

        Button? approveButton = null;
        foreach (var permission in request.RequestedPermissions)
        {
            var checkBox = new CheckBox
            {
                Tag = permission,
                Content = PermissionLabel(permission, chinese),
                IsChecked = false,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = Brushes.White
            };
            checkBox.Checked += (_, _) => { if (approveButton is not null) approveButton.IsEnabled = true; };
            checkBox.Unchecked += (_, _) => { if (approveButton is not null) approveButton.IsEnabled = selected.Any(item => item.IsChecked == true); };
            selected.Add(checkBox);
            content.Children.Add(checkBox);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var deny = new Button { Content = chinese ? "拒绝" : "Deny", MinWidth = 100, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 10, 0) };
        var approve = new Button { Content = chinese ? "批准所选权限" : "Approve selected", MinWidth = 140, Padding = new Thickness(14, 8, 14, 8), IsDefault = true, IsEnabled = false };
        approveButton = approve;
        deny.Click += (_, _) => window.DialogResult = false;
        approve.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(deny);
        buttons.Children.Add(approve);
        content.Children.Add(buttons);
        window.Content = content;

        var approved = window.ShowDialog() == true;
        grantedPermissions = approved
            ? selected.Where(checkBox => checkBox.IsChecked == true).Select(checkBox => (string)checkBox.Tag!).ToArray()
            : [];
        return approved;
    }

    private static string PermissionLabel(string permission, bool chinese) => permission switch
    {
        "component.discovery" => chinese ? "发现组件并读取组件资源" : "Discover components and read their resources",
        "config.read" => chinese ? "读取配置" : "Read configuration",
        "config.write" => chinese ? "写入配置" : "Write configuration",
        _ => permission
    };

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value)
        ? "—"
        : value.Length <= 180 ? value : value[..180] + "…";
}
