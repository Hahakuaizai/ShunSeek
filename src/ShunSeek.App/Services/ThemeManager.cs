using System.IO;
using System.Windows;
using System.Windows.Media;

namespace ShunSeek.App.Services;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// 双主题调色板：所有颜色都是 Application 级资源，XAML 一律 DynamicResource 引用。
///
/// 为什么切换时"换对象"而不是给同一个画刷做 ColorAnimation：
/// WPF 会把进入资源字典、又被模板/渲染用到的 Freezable 冻结，冻结后再 BeginAnimation 直接抛异常
/// （"无法在该对象上添加动画属性，因为该对象不能访问冻结状态"）。换一个新画刷最稳，
/// 所有 DynamicResource 引用者会立刻拿到新值；柔化交给窗口做一次极短的淡化过渡。
///
/// 两套主题都有渐变底 + 多层表面色 + 发丝线 + 投影，不是单色平涂。
/// </summary>
internal static class ThemeManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShunSeek", "theme.txt");

    public static AppTheme Current { get; private set; } = AppTheme.Dark;
    public static bool IsDark => Current == AppTheme.Dark;
    public static event Action? Changed;

    private static readonly Dictionary<string, Color> DarkSolids = new()
    {
        ["ShellStroke"] = C("#FF2A343A"),
        ["AccentBrush"] = C("#FFC4573F"),
        ["AccentText"] = C("#FFD97A5F"),
        ["TextHi"] = C("#FFEDE9E1"),
        ["TextMid"] = C("#FF9AA5AB"),
        ["TextLow"] = C("#FF737E84"),
        ["HairLine"] = C("#FF1E262B"),
        ["Surface"] = C("#FF161D22"),
        ["SurfaceHover"] = C("#FF1C242A"),
        ["SurfaceSel"] = C("#FF212B32"),
        ["EdgeLine"] = C("#FF242D33"),
        ["EdgeStrong"] = C("#FF303B42"),
        ["RowHover"] = C("#FF161D22"),
        ["RowSel"] = C("#FF212B32"),
        ["RowSelStroke"] = C("#FF2F3B43"),
        ["FoldFill"] = C("#FF151D22"),
        ["IconPlate"] = C("#FF1B242A"),
        ["ChipFill"] = C("#FF1A2227"),
        ["MenuFill"] = C("#FF141A1F"),
        ["MenuStroke"] = C("#FF263036"),
        ["MenuHover"] = C("#FF1E282E"),
        ["TooltipFill"] = C("#FF181F24"),
        ["ThumbFill"] = C("#FF2B353C"),
        ["PillFill"] = C("#1FC4573F"),
        ["PillHover"] = C("#3AC4573F"),
        ["PillStroke"] = C("#59C4573F"),
        ["PinMark"] = C("#B3C4573F"),
        ["CloseHover"] = C("#FFC14432"),
        ["ClosePressed"] = C("#FFA63728"),
        ["CloseText"] = C("#FFFDF7F2"),
        ["ImportanceHigh"] = C("#FFC4573F"),
        ["ImportanceNormal"] = C("#FF7C8890"),
        ["ImportanceLow"] = C("#FF4A555C"),
    };

    private static readonly Dictionary<string, Color> LightSolids = new()
    {
        ["ShellStroke"] = C("#FFC3CBC4"),
        ["AccentBrush"] = C("#FFB2432C"),
        ["AccentText"] = C("#FF9C3A24"),
        ["TextHi"] = C("#FF1B2320"),
        ["TextMid"] = C("#FF53605A"),
        ["TextLow"] = C("#FF6F7B74"),
        ["HairLine"] = C("#FFDDE2DC"),
        ["Surface"] = C("#FFFFFFFF"),
        ["SurfaceHover"] = C("#FFF4F6F3"),
        ["SurfaceSel"] = C("#FFEAEFE9"),
        ["EdgeLine"] = C("#FFCCD3CC"),
        ["EdgeStrong"] = C("#FFB0BBB2"),
        ["RowHover"] = C("#FFF2F5F1"),
        ["RowSel"] = C("#FFE8EDE7"),
        ["RowSelStroke"] = C("#FFC8D1C8"),
        ["FoldFill"] = C("#FFF0F3EF"),
        ["IconPlate"] = C("#FFEDEFEA"),
        ["ChipFill"] = C("#FFF2F4F1"),
        ["MenuFill"] = C("#FFFCFDFB"),
        ["MenuStroke"] = C("#FFD6DCD5"),
        ["MenuHover"] = C("#FFEDF1EC"),
        ["TooltipFill"] = C("#FFFCFDFB"),
        ["ThumbFill"] = C("#FFC0C8C0"),
        ["PillFill"] = C("#1AB2432C"),
        ["PillHover"] = C("#30B2432C"),
        ["PillStroke"] = C("#59B2432C"),
        ["PinMark"] = C("#B3B2432C"),
        ["CloseHover"] = C("#FFC14432"),
        ["ClosePressed"] = C("#FFA63728"),
        ["CloseText"] = C("#FFFDF7F2"),
        ["ImportanceHigh"] = C("#FFB2432C"),
        ["ImportanceNormal"] = C("#FF8A948D"),
        ["ImportanceLow"] = C("#FFC2CAC2"),
    };

    private static readonly Color[] DarkInk = { C("#FF12181C"), C("#FF0E1418"), C("#FF0A0F12") };
    private static readonly Color[] LightInk = { C("#FFFAFBF9"), C("#FFF2F4F1"), C("#FFE9ECE8") };
    private static readonly Color[] DarkGlow = { C("#14FFFFFF"), C("#00FFFFFF") };
    private static readonly Color[] LightGlow = { C("#66FFFFFF"), C("#00FFFFFF") };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>启动时调一次：把调色板铺进 Application 资源，按落盘的主题直接落色（不做动画）。</summary>
    public static void Initialize()
    {
        var saved = AppTheme.Dark;
        try
        {
            if (File.Exists(ConfigPath) &&
                string.Equals(File.ReadAllText(ConfigPath).Trim(), "light", StringComparison.OrdinalIgnoreCase))
                saved = AppTheme.Light;
        }
        catch { /* 配置坏了就用深色 */ }

        Apply(saved, animate: false);
    }

    public static void Toggle() => Apply(IsDark ? AppTheme.Light : AppTheme.Dark, animate: true);

    public static void Apply(AppTheme theme, bool animate, bool persist = true)
    {
        Current = theme;
        var solids = theme == AppTheme.Dark ? DarkSolids : LightSolids;
        var ink = theme == AppTheme.Dark ? DarkInk : LightInk;
        var glow = theme == AppTheme.Dark ? DarkGlow : LightGlow;
        var resources = System.Windows.Application.Current.Resources;

        foreach (var (key, color) in solids)
            resources[key] = new SolidColorBrush(color);

        var inkBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.45, 1),
        };
        inkBrush.GradientStops.Add(new GradientStop(ink[0], 0));
        inkBrush.GradientStops.Add(new GradientStop(ink[1], 0.55));
        inkBrush.GradientStops.Add(new GradientStop(ink[2], 1));
        resources["InkFill"] = inkBrush;

        var glowBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.2, 1),
        };
        glowBrush.GradientStops.Add(new GradientStop(glow[0], 0));
        glowBrush.GradientStops.Add(new GradientStop(glow[1], 1));
        resources["TopGlow"] = glowBrush;

        resources["ShadowColor"] = theme == AppTheme.Dark ? C("#FF000000") : C("#FF4A5A50");
        resources["ShadowOpacity"] = theme == AppTheme.Dark ? 0.55 : 0.28;

        try
        {
            if (persist)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, theme == AppTheme.Dark ? "dark" : "light");
            }
        }
        catch { /* 落盘失败只影响下次启动的默认主题 */ }

        Changed?.Invoke();
    }
}
