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
        out IReadOnlyCollection<string> grantedPermissions, out bool remember)
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
                    : "Companion cannot verify this self-reported identity or installation source. Approve only if you can independently identify the active Wallpaper Engine wallpaper. Remembering trusts this BAP ID on this Windows account; it does not authenticate the publisher.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 202))
            }
        });
        ((TextBlock)((Border)content.Children[2]).Child).Text = chinese
            ? "\u65e0\u6cd5\u6838\u9a8c\u58c1\u7eb8\u7684\u81ea\u62a5\u8eab\u4efd\u6216\u5b89\u88c5\u6765\u6e90\u3002\u8bf7\u5148\u72ec\u7acb\u786e\u8ba4 Wallpaper Engine \u5f53\u524d\u58c1\u7eb8\u3002\u8bb0\u4f4f\u4fe1\u4efb\u53ea\u4f9d\u636e BAP ID\uff0c\u4e0d\u4f1a\u9a8c\u8bc1\u53d1\u884c\u8005\u8eab\u4efd\u3002"
            : "Companion cannot verify this self-reported identity or installation source. Approve only if you can independently identify the active Wallpaper Engine wallpaper. Remembering trusts this BAP ID on this Windows account; it does not authenticate the publisher.";
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

        var rememberCheck = new CheckBox
        {
            Content = new TextBlock
            {
                Text = chinese
                    ? "\u8bb0\u4f4f\u6b64\u5bbf\u4e3b\u53ca\u5df2\u6279\u51c6\u7684\u6743\u9650\uff08\u4ec5\u9650\u6b64 Windows \u8d26\u6237\uff09"
                    : "Remember this Host and approved permissions on this Windows account",
                TextWrapping = TextWrapping.Wrap
            },
            IsChecked = true,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = Brushes.White
        };
        content.Children.Add(rememberCheck);

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
        remember = approved && rememberCheck.IsChecked == true;
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
