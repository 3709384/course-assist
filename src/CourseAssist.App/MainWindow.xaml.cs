using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CourseAssist.Core;
using Microsoft.Win32;

namespace CourseAssist.App;

public partial class MainWindow : Window
{
    private static readonly Brush DeepBlue = new SolidColorBrush(Color.FromRgb(0x2F, 0x7E, 0xA3));
    private static readonly Brush Tint = new SolidColorBrush(Color.FromRgb(0xEF, 0xF7, 0xFA));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

    private readonly BundleRepository _repo;
    private readonly SystemProcessRunner _runner = new();
    private readonly WingetService _winget;
    private readonly StateStore _stateStore;

    private readonly Dictionary<string, ComboBox> _versionPickers = new();
    private readonly Dictionary<string, TextBlock> _statusLabels = new();
    private readonly Dictionary<string, CheckBox> _includeChecks = new();

    private CourseBundle? _current;
    private IReadOnlyList<StepResult>? _lastResults;
    private IReadOnlyList<PreflightItem>? _lastPreflight;

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        Title = $"课程环境助手 CourseAssist v{version}";
        _repo = new BundleRepository(Path.Combine(AppContext.BaseDirectory, "bundles"));
        _winget = new WingetService(_runner);
        _stateStore = new StateStore(StateStore.DefaultPath());
        InstallRootBox.Text = DefaultInstallRoot();
        LoadCourses();
    }

    /// <summary>Load catalog + bundles. The bundles compiled into the program come first:
    /// they are part of the executable itself and cannot go missing. The loose bundles
    /// folder next to the exe is only a fallback.</summary>
    private (Catalog? Catalog, IReadOnlyList<CourseBundle> Bundles, string Source) LoadData()
    {
        var catalog = EmbeddedBundles.CatalogYaml is { } yaml ? BundleRepository.ParseCatalog(yaml) : null;
        var sources = EmbeddedBundles.Files
            .Where(kv => !kv.Key.Equals("catalog.yaml", StringComparison.OrdinalIgnoreCase))
            .Select(kv => (kv.Key, kv.Value));
        var bundles = _repo.ParseBundles(sources);
        if (catalog is not null && bundles.Count > 0) return (catalog, bundles, "程序内置");
        return (_repo.LoadCatalog(), _repo.LoadBundles(), "本地文件");
    }

    private static string DefaultInstallRoot()
    {
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName).ToList();
        var root = drives.FirstOrDefault(d => d.StartsWith("D:", StringComparison.OrdinalIgnoreCase)) ?? drives.FirstOrDefault() ?? "C:\\";
        return Path.Combine(root, "CourseAssist");
    }

    private void LoadCourses()
    {
        try
        {
            var (catalog, bundleList, source) = LoadData();
            var bundles = bundleList.ToDictionary(b => b.Id);
            if (catalog is null) { Log("未找到课程目录（catalog.yaml）"); return; }
            foreach (var category in catalog.Categories)
            {
                CourseList.Items.Add(new ListBoxItem
                {
                    Content = category.Name,
                    FontWeight = FontWeights.Bold,
                    Foreground = DeepBlue,
                    IsEnabled = false,
                    Margin = new Thickness(4, 8, 0, 2),
                });
                foreach (var course in category.Courses)
                {
                    bundles.TryGetValue(course.Id, out var bundle);
                    var item = new ListBoxItem
                    {
                        Content = bundle is null ? $"{course.Name}（课程包制作中）" : $"{course.Name}（{course.Status}）",
                        Tag = bundle,
                        IsEnabled = bundle is not null,
                        Margin = new Thickness(12, 1, 0, 1),
                    };
                    if (bundle is null) item.Foreground = Gray;
                    CourseList.Items.Add(item);
                }
            }
            Log($"已加载 {bundles.Count} 个课程包（{source}）。左边选一门课，先体检再一键配置。");
            foreach (var error in _repo.LastLoadErrors) Log($"课程包跳过：{error}");
        }
        catch (Exception ex)
        {
            Log($"加载课程失败：{ex.Message}");
        }
    }

    private void CourseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CourseList.SelectedItem is ListBoxItem { Tag: CourseBundle bundle })
            ShowBundle(bundle);
    }

    private void ShowBundle(CourseBundle bundle)
    {
        _current = bundle;
        _versionPickers.Clear();
        _statusLabels.Clear();
        _includeChecks.Clear();
        ToolsPanel.Children.Clear();

        CourseTitle.Text = bundle.Name;
        CourseSummary.Text = bundle.Summary ?? "";
        CourseNotes.Text = bundle.Notes is null ? "" : $"注意：{bundle.Notes}";

        foreach (var tool in bundle.Tools)
        {
            var panel = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var include = new CheckBox
            {
                Content = tool.Name,
                IsChecked = tool.Required,
                FontWeight = FontWeights.Bold,
                Foreground = DeepBlue,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _includeChecks[tool.Name] = include;
            header.Children.Add(include);
            if (tool.Versions.Count > 0)
            {
                var picker = new ComboBox { Width = 110, Margin = new Thickness(10, 0, 0, 0) };
                foreach (var v in tool.Versions) picker.Items.Add(v.Label);
                picker.SelectedItem = tool.DefaultVersion ?? tool.Versions[0].Label;
                _versionPickers[tool.Name] = picker;
                header.Children.Add(picker);
                header.Children.Add(new TextBlock { Text = "版本", Foreground = Gray, Margin = new Thickness(4, 2, 0, 0) });
            }
            if (!tool.Required)
                header.Children.Add(new TextBlock { Text = "（可选）", Foreground = Gray, Margin = new Thickness(8, 1, 0, 0) });
            panel.Children.Add(header);
            panel.Children.Add(new TextBlock { Text = tool.Purpose, TextWrapping = TextWrapping.Wrap, Foreground = Gray, Margin = new Thickness(0, 2, 0, 0) });
            var status = new TextBlock { Text = "状态：未体检", Margin = new Thickness(0, 3, 0, 0) };
            _statusLabels[tool.Name] = status;
            panel.Children.Add(status);

            ToolsPanel.Children.Add(new Border
            {
                Background = Tint,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = panel,
            });
        }
        Log($"已选择《{bundle.Name}》，共 {bundle.Tools.Count} 项软件。");
    }

    private Dictionary<string, string?> GetSelections()
        => _versionPickers.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.SelectedItem);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择安装根目录" };
        if (Directory.Exists(InstallRootBox.Text)) dialog.InitialDirectory = InstallRootBox.Text;
        if (dialog.ShowDialog() == true) InstallRootBox.Text = dialog.FolderName;
    }

    private async Task<IReadOnlyList<PreflightItem>> RunPreflightAsync(string root)
    {
        Log("开始预检…");
        var preflight = await new Preflight(_winget).CheckAsync(root);
        _lastPreflight = preflight;
        foreach (var item in preflight)
            Log($"预检 · {(item.Passed ? "✅" : "❌")} {item.Name}：{item.Detail}");
        return preflight;
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) { Log("先选一门课。"); return; }
        SetBusy(true);
        try
        {
            var engine = new CheckEngine(_runner, _winget);
            var results = await engine.CheckAsync(_current, GetSelections());
            foreach (var r in results)
            {
                if (_statusLabels.TryGetValue(r.ToolName, out var label))
                {
                    label.Text = r.Status switch
                    {
                        ToolCheckStatus.Installed => $"状态：✅ 已安装 — {r.Detail}",
                        ToolCheckStatus.Missing => $"状态：❌ 未安装 — {r.Detail}",
                        _ => $"状态：✋ 需人工确认 — {r.Detail}",
                    };
                }
                Log($"体检 · {r.ToolName}：{r.Status}（{r.Detail}）");
            }
        }
        catch (Exception ex) { Log($"体检失败：{ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) { Log("先选一门课。"); return; }
        var root = InstallRootBox.Text.Trim();
        if (root.Length == 0) { Log("请先填写安装位置。"); return; }
        SetBusy(true);
        try
        {
            var preflight = await RunPreflightAsync(root);
            if (preflight.Any(p => !p.Passed))
            {
                // 产品原则：发现问题先自己修，不把问题甩给用户。
                Log("发现问题，正在自动处理，不用你动手…");
                var healer = new SelfHealService(_runner, _winget);
                foreach (var item in preflight.Where(p => !p.Passed))
                {
                    if (item.Name.Contains("winget"))
                    {
                        var heal = await healer.EnsureWingetAsync(m => Log($"修复 · {m}"));
                        Log($"修复 · winget：{(heal.Fixed ? "✅ 已解决" : "❌ " + heal.Detail)}");
                    }
                    else if (item.Name.Contains("可写") || item.Name.Contains("磁盘"))
                    {
                        var fallback = healer.SuggestFallbackRoot(root);
                        if (fallback is not null && !fallback.Equals(root, StringComparison.OrdinalIgnoreCase))
                        {
                            root = fallback;
                            InstallRootBox.Text = fallback;
                            Log($"修复 · 原安装位置用不了，已自动改到 {fallback}");
                        }
                    }
                }
                preflight = await RunPreflightAsync(root);
            }
            var critical = preflight.Where(p => !p.Passed && (p.Name.Contains("winget") || p.Name.Contains("可写") || p.Name.Contains("系统"))).ToList();
            if (critical.Count > 0)
            {
                Log("自动修复后仍有问题没能解决，已停止。点「导出诊断报告」发给老师或同学，他们能帮你看。");
                DiagButton.IsEnabled = true;
                return;
            }

            var included = _includeChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
            if (included.Count == 0) { Log("至少勾选一项要安装的软件。"); return; }
            var plan = new InstallPlanner().BuildPlan(new InstallRequest
            {
                Bundle = _current,
                InstallRoot = root,
                VersionSelections = GetSelections(),
                IncludedTools = included,
            });
            Log($"安装计划共 {plan.Count} 步，开始执行（个别软件下载较慢，请耐心等待）…");

            var executor = new InstallExecutor(_runner, _winget, new SystemEnvironmentStore(), _stateStore);
            var results = await executor.ExecuteAsync(_current, plan,
                r => Dispatcher.Invoke(() => LogStep(r)));
            _lastResults = results;
            DiagButton.IsEnabled = true;

            var ok = results.Count(r => r.Outcome is StepOutcome.Succeeded or StepOutcome.Skipped);
            var failed = results.Count(r => r.Outcome == StepOutcome.Failed);
            Log($"执行完毕：完成 {ok} 步，失败 {failed} 步" + (failed > 0 ? "。失败不怕——点「导出诊断报告」发给老师或同学帮你看。" : "。全部搞定！"));
            Check_Click(sender, e);
        }
        catch (Exception ex) { Log($"配置过程出错：{ex.Message}"); DiagButton.IsEnabled = true; }
        finally { SetBusy(false); }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) { Log("先选一门课。"); return; }
        SetBusy(true);
        try
        {
            var executor = new InstallExecutor(_runner, _winget, new SystemEnvironmentStore(), _stateStore);
            var lines = await executor.UninstallCourseAsync(_current.Id);
            foreach (var line in lines) Log($"卸载 · {line}");
        }
        catch (Exception ex) { Log($"卸载出错：{ex.Message}"); }
        finally { SetBusy(false); }
    }

    private void Diag_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var report = DiagnosticsReport.Build(_current.Name, _lastResults ?? [], _lastPreflight);
        var dialog = new SaveFileDialog
        {
            Filter = "Markdown 文件|*.md",
            FileName = $"course-assist-诊断报告-{DateTime.Now:yyyyMMdd-HHmm}.md",
        };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, report);
            Log($"诊断报告已保存到 {dialog.FileName}");
        }
    }

    private void LogStep(StepResult r)
    {
        var mark = r.Outcome switch
        {
            StepOutcome.Succeeded => "✅",
            StepOutcome.Skipped => "⏭️",
            StepOutcome.Manual => "✋",
            _ => "❌",
        };
        Log($"{mark} {r.Step.Title}：{r.Detail}");
        if (r.Step.ToolName is not null && _statusLabels.TryGetValue(r.Step.ToolName, out var label)
            && r.Step.Kind is PlanStepKind.Verify)
        {
            label.Text = r.Outcome == StepOutcome.Succeeded ? $"状态：✅ 已验证 — {r.Detail}" : $"状态：❌ 验证未通过 — {r.Detail}";
        }
    }

    private void SetBusy(bool busy)
    {
        CheckButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        BusyBar.IsIndeterminate = busy;
    }

    private void Log(string message)
    {
        LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
