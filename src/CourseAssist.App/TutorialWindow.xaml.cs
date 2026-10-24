using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CourseAssist.Core;

namespace CourseAssist.App;

/// <summary>入门教程窗口：与所装版本严格对齐的步骤 + 界面示意图（图例）+ 官方图文教程入口。
/// 版本一致性来自数据：教程挂在课程包的工具/版本条目上，用户在主界面选哪个版本，
/// 这里就打开哪个版本的教程。</summary>
public partial class TutorialWindow : Window
{
    private static readonly Brush DeepBlue = new SolidColorBrush(Color.FromRgb(0x03, 0x69, 0xA1));
    private static readonly Brush Tint = new SolidColorBrush(Color.FromRgb(0xF0, 0xF9, 0xFF));

    private readonly string? _tutorialUrl;

    public TutorialWindow(ResolvedTool tool)
    {
        InitializeComponent();
        var guide = tool.Guide;
        _tutorialUrl = guide?.TutorialUrl;

        TitleText.Text = $"{tool.Name} · 入门教程";
        VersionText.Text = tool.VersionLabel is { } v
            ? $"📌 对应版本：{tool.Name} {v} —— 本教程就是按这个版本写的，链接也是这个版本的官方文档，不会串版。"
            : "📌 本软件是持续更新的版本，教程链接到官方最新版文档，与你装的版本保持一致。";
        Title = $"{tool.Name} 入门教程";

        DiagramHost.Child = BuildDiagram(guide?.Diagram ?? "generic");

        if (guide is null || guide.Steps.Count == 0)
        {
            StepsPanel.Children.Add(new TextBlock
            {
                Text = "这项工具暂时没有内置步骤教程，点下方按钮看官方文档。",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            for (var i = 0; i < guide.Steps.Count; i++)
            {
                StepsPanel.Children.Add(new TextBlock
                {
                    Text = $"{i + 1}. {guide.Steps[i]}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6),
                    FontSize = 13,
                });
            }
        }

        NoteText.Text = guide?.Note is { } note ? $"⚠ {note}" : "";
        OpenButton.IsEnabled = _tutorialUrl is not null;
        if (guide?.TutorialTitle is { } title) OpenButton.Content = $"打开官方教程：{title}";
        if (_tutorialUrl is null) OpenButton.Content = "该工具无官方在线教程（看上方步骤）";
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tutorialUrl is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_tutorialUrl) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show($"浏览器打开失败，手动复制这个地址到浏览器：\n{_tutorialUrl}", "打开教程");
        }
    }

    // ---- Schematic UI map (图例）: labeled zones, stable across versions of each tool kind. ----

    private static Border Cell(string text, int row, int col, int rowSpan = 1, int colSpan = 1)
    {
        var border = new Border
        {
            Background = Tint,
            BorderBrush = DeepBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(2),
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        Grid.SetRowSpan(border, rowSpan);
        Grid.SetColumnSpan(border, colSpan);
        border.Child = new TextBlock
        {
            Text = text,
            Foreground = DeepBlue,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 4, 6, 4),
        };
        return border;
    }

    private static Grid GridOf(double[] rows, double[] cols)
    {
        var grid = new Grid();
        foreach (var h in rows) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h, h > 10 ? GridUnitType.Pixel : GridUnitType.Star) });
        foreach (var w in cols) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, w > 10 ? GridUnitType.Pixel : GridUnitType.Star) });
        return grid;
    }

    private static UIElement BuildDiagram(string kind)
    {
        switch (kind)
        {
            case "editor":
            {
                var g = GridOf(new double[] { 26, 1, 40 }, new double[] { 120, 1 });
                g.Children.Add(Cell("菜单栏（文件 / 编辑 / 运行都在这）", 0, 0, colSpan: 2));
                g.Children.Add(Cell("文件列表\n（你的代码文件）", 1, 0));
                g.Children.Add(Cell("代码编辑区\n（写代码的地方）", 1, 1));
                g.Children.Add(Cell("终端 / 输出面板（看运行结果和报错）", 2, 0, colSpan: 2));
                return g;
            }
            case "ide":
            {
                var g = GridOf(new double[] { 26, 1, 40 }, new double[] { 120, 1, 110 });
                g.Children.Add(Cell("菜单栏 + 运行按钮（绿色三角）", 0, 0, colSpan: 3));
                g.Children.Add(Cell("项目文件树", 1, 0));
                g.Children.Add(Cell("代码编辑区", 1, 1));
                g.Children.Add(Cell("结构 / 属性", 1, 2));
                g.Children.Add(Cell("控制台（程序的输出和报错看这里）", 2, 0, colSpan: 3));
                return g;
            }
            case "database":
            {
                var g = GridOf(new double[] { 26, 1, 1 }, new double[] { 150, 1 });
                g.Children.Add(Cell("工具栏（新建连接 / 执行 SQL）", 0, 0, colSpan: 2));
                g.Children.Add(Cell("连接与库表\n（点开看表）", 1, 0));
                g.Children.Add(Cell("SQL 编辑区\n（写查询的地方）", 1, 1));
                g.Children.Add(Cell("结果表格（查询出来的数据）", 2, 0, colSpan: 2));
                return g;
            }
            case "terminal":
            {
                var g = GridOf(new double[] { 1, 30 }, new double[] { 1 });
                g.Children.Add(Cell("命令行窗口\n在这里输入命令 → 按回车执行 → 结果在下面滚动显示", 0, 0));
                g.Children.Add(Cell("↑ 翻以前的命令 · Ctrl+C 中止当前命令", 1, 0));
                return g;
            }
            case "vm":
            {
                var g = GridOf(new double[] { 26, 1 }, new double[] { 120, 1, 110 });
                g.Children.Add(Cell("工具栏（新建 / 启动 / 设置）", 0, 0, colSpan: 3));
                g.Children.Add(Cell("虚拟机列表", 1, 0));
                g.Children.Add(Cell("虚拟机画面\n（就是那台虚拟电脑的屏幕）", 1, 1));
                g.Children.Add(Cell("快照 / 设置", 1, 2));
                return g;
            }
            case "browser":
            {
                var g = GridOf(new double[] { 26, 1, 46 }, new double[] { 1 });
                g.Children.Add(Cell("地址栏（输入网址）", 0, 0));
                g.Children.Add(Cell("网页内容区", 1, 0));
                g.Children.Add(Cell("开发者工具（按 F12)：元素 · 控制台 · 网络", 2, 0));
                return g;
            }
            default:
            {
                var g = GridOf(new double[] { 26, 1, 26 }, new double[] { 1 });
                g.Children.Add(Cell("菜单栏", 0, 0));
                g.Children.Add(Cell("主工作区", 1, 0));
                g.Children.Add(Cell("设置 / 帮助（通常在菜单里）", 2, 0));
                return g;
            }
        }
    }
}
