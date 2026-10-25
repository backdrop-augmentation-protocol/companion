namespace Bap.Companion.Desktop;

internal static class UiStrings
{
    private static readonly Dictionary<string, (string English, string Chinese)> Values = new()
    {
        ["appSubtitle"] = ("Your Wallpaper Engine workspace", "Wallpaper Engine 壁纸工作台"),
        ["installed"] = ("Installed", "已安装"),
        ["presets"] = ("Presets", "预设"),
        ["settings"] = ("Settings", "设置"),
        ["hosts"] = ("Host wallpapers", "宿主壁纸"),
        ["components"] = ("Components", "组件"),
        ["catalogHint"] = ("Local projects and Workshop subscriptions can be used together.", "本地项目与创意工坊订阅内容可以混合使用。"),
        ["refresh"] = ("Refresh library", "刷新内容库"),
        ["details"] = ("Details", "详情"),
        ["selectItem"] = ("Select an item to view its details.", "选择一个项目以查看详情。"),
        ["source"] = ("Source", "来源"),
        ["local"] = ("Local project", "本地项目"),
        ["workshop"] = ("Workshop subscription", "创意工坊订阅"),
        ["status"] = ("Status", "状态"),
        ["version"] = ("Version", "版本"),
        ["bapId"] = ("BAP ID", "BAP 标识"),
        ["workshopId"] = ("Workshop ID", "创意工坊 ID"),
        ["location"] = ("Project folder", "项目目录"),
        ["monitor"] = ("Display", "显示器"),
        ["switchDisplay"] = ("Display for wallpaper switching", "切换壁纸使用的显示器"),
        ["switchDisplayHint"] = ("Host wallpapers opened from Companion will appear on this display.", "从伴侣软件切换的宿主壁纸将显示在此显示器上。"),
        ["switchHost"] = ("Use this host wallpaper", "切换到此宿主壁纸"),
        ["noPreview"] = ("No preview", "暂无预览"),
        ["noHosts"] = ("No host wallpapers found", "未发现宿主壁纸"),
        ["noComponents"] = ("No components found", "未发现组件"),
        ["presetIntro"] = ("Choose components and decide where the preset applies.", "选择组件并指定预设的应用范围。"),
        ["newPreset"] = ("New preset", "新建预设"),
        ["rename"] = ("Rename", "重命名"),
        ["delete"] = ("Delete", "删除"),
        ["selectedComponents"] = ("Components in this preset · top to bottom is display order", "此预设中的组件 · 从上到下为显示顺序"),
        ["addComponent"] = ("Add component", "添加组件"),
        ["remove"] = ("Remove", "移除"),
        ["moveUp"] = ("Move up", "上移"),
        ["moveDown"] = ("Move down", "下移"),
        ["scope"] = ("Apply preset", "应用预设"),
        ["setGlobal"] = ("Set as global", "设为全局预设"),
        ["hostOverride"] = ("Host override", "宿主专用预设"),
        ["setHost"] = ("Apply to selected host", "应用到所选宿主"),
        ["clearHost"] = ("Clear host override", "清除宿主专用设置"),
        ["global"] = ("Global", "全局"),
        ["overrides"] = ("Host overrides", "宿主专用设置"),
        ["allInstalled"] = ("All installed (default)", "所有已安装组件（默认）"),
        ["startup"] = ("Start with Windows · minimized to tray", "开机自启动 · 最小化到托盘"),
        ["language"] = ("Language", "语言"),
        ["languageHint"] = ("Changes take effect immediately.", "切换后立即生效。"),
        ["logs"] = ("Diagnostics", "诊断"),
        ["openLog"] = ("Open current log", "打开当前日志"),
        ["openLogFolder"] = ("Open log folder", "打开日志目录"),
        ["checkService"] = ("Check service", "检查服务"),
        ["serviceOnline"] = ("Service online", "服务在线"),
        ["serviceOffline"] = ("Service offline", "服务离线"),
        ["openCompanion"] = ("Open BAP Companion", "打开 BAP Companion"),
        ["openHealth"] = ("Open health check", "打开健康检查"),
        ["exit"] = ("Exit", "退出"),
        ["selectHostFirst"] = ("Select a BAP host first.", "请先选择一个 BAP 宿主。"),
        ["duplicateComponent"] = ("Remove the existing entry before choosing another copy of this component.", "请先移除现有条目，再选择此组件的另一份副本。"),
        ["deletePreset"] = ("Delete preset '{0}'?", "删除预设“{0}”？"),
        ["switchVerified"] = ("Wallpaper Engine confirmed the switch.", "Wallpaper Engine 已确认切换。"),
        ["switchSent"] = ("Switch command sent to Wallpaper Engine.", "已向 Wallpaper Engine 发送切换命令。"),
        ["startupError"] = ("Automatic startup could not be configured: {0}", "无法设置开机自启动：{0}"),
        ["languageEnglish"] = ("English", "English"),
        ["languageChinese"] = ("简体中文", "简体中文"),
        ["valid"] = ("Ready", "可用"),
        ["invalid"] = ("Invalid", "无效"),
        ["unverified"] = ("Unverified", "未验证"),
        ["legacy"] = ("Legacy", "旧版"),
        ["missing"] = ("Missing", "缺失")
    };

    public static string Get(string key, string language, params object[] args)
    {
        if (!Values.TryGetValue(key, out var pair)) throw new ArgumentException($"Unknown UI string: {key}", nameof(key));
        var value = language == "zh-CN" ? pair.Chinese : pair.English;
        return args.Length == 0 ? value : string.Format(value, args);
    }
}
