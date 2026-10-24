using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CourseAssist.Core;
using Microsoft.Win32;

namespace CourseAssist.App;

public partial class MainWindow : Window
{
    private static readonly Brush DeepBlue = new SolidColorBrush(Color.FromRgb(0x03, 0x69, 0xA1));
    private static readonly Brush Tint = new SolidColorBrush(Color.FromRgb(0xF0, 0xF9, 0xFF));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

    private readonly BundleRepository _repo;
    private readonly SystemProcessRunner _runner = new();
    private readonly WingetService _winget;
    private readonly StateStore _stateStore;
    private readonly ShortcutService _shortcutService;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _progressTotalWeight, _progressDoneWeight, _progressDoneSteps, _progressTotalSteps;
    private string _currentStepTitle = "";
    private string _currentSubProgress = "";
    private DateTime _stepStartedAt;

    private readonly Dictionary<string, ComboBox> _versionPickers = new();
    private readonly Dictionary<string, TextBlock> _statusLabels = new();
    private readonly Dictionary<string, Border> _statusPills = new();
    private readonly Dictionary<string, CheckBox> _includeChecks = new();

    private CourseBundle? _current;
    private IReadOnlyList<StepResult>? _lastResults;
    private IReadOnlyList<PreflightItem>? _lastPreflight;

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        Title = $"课程环境助手 CourseAssist v{version}";
        HeaderVersion.Text = $"v{version}";
        StateChanged += MainWindow_StateChanged;
        _repo = new BundleRepository(Path.Combine(AppContext.BaseDirectory, "bundles"));
        _winget = new WingetService(_runner);
        _shortcutService = new ShortcutService(_runner);
        _progressTimer.Tick += (_, _) => UpdateProgressText();
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
                    IsEnabled = false,
                    Margin = new Thickness(6, 10, 6, 1),
                });
                foreach (var course in category.Courses)
                {
                    bundles.TryGetValue(course.Id, out var bundle);
                    var item = new ListBoxItem
                    {
                        Content = bundle is null ? $"{course.Name}（课程包制作中）" : course.Name,
                        Tag = bundle,
                        IsEnabled = bundle is not null,
                    };
                    if (bundle is null)
                    {
                        item.Foreground = Gray;
                        item.FontWeight = FontWeights.Normal;
                        item.FontSize = 13;
                    }
                    CourseList.Items.Add(item);
                }
            }
            CoursesFooter.Text = $"共 {bundles.Count} 门课程 · 课程包持续完善中";
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

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void ShowBundle(CourseBundle bundle)
    {
        _current = bundle;
        _versionPickers.Clear();
        _statusLabels.Clear();
        _statusPills.Clear();
        _includeChecks.Clear();
        ToolsPanel.Children.Clear();
        HideProgress();
        EmptyState.Visibility = Visibility.Collapsed;
        ToolsScroll.Visibility = Visibility.Visible;

        CourseTitle.Text = bundle.Name;
        CourseSummary.Text = bundle.Summary ?? "";
        CourseNotes.Text = bundle.Notes is null ? "" : $"注意：{bundle.Notes}";
        var requiredCount = bundle.Tools.Count(t => t.Required);
        CourseMeta.Text = $"共 {bundle.Tools.Count} 个软件 · 必装 {requiredCount} · 可选 {bundle.Tools.Count - requiredCount} —— 勾选框决定装哪些，版本可在每张卡片右上角切换";

        foreach (var tool in bundle.Tools)
        {
            var panel = new StackPanel();
            var header = new DockPanel { LastChildFill = false };
            var include = new CheckBox
            {
                Content = tool.Name,
                IsChecked = tool.Required,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x08, 0x2F, 0x49)),
                FontSize = 14.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _includeChecks[tool.Name] = include;
            DockPanel.SetDock(include, Dock.Left);
            header.Children.Add(include);
            if (!tool.Required)
            {
                var opt = new TextBlock
                {
                    Text = "可选",
                    Foreground = Gray,
                    FontSize = 11.5,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(opt, Dock.Left);
                header.Children.Add(opt);
            }
            if (tool.Versions.Count > 0)
            {
                var rightSide = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rightSide.Children.Add(new TextBlock { Text = "版本", Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
                var picker = new ComboBox { Width = 128, Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource("ModernCombo") };
                foreach (var v in tool.Versions) picker.Items.Add(v.Label);
                picker.SelectedItem = tool.DefaultVersion ?? tool.Versions[0].Label;
                _versionPickers[tool.Name] = picker;
                rightSide.Children.Add(picker);
                DockPanel.SetDock(rightSide, Dock.Right);
                header.Children.Add(rightSide);
            }
            panel.Children.Add(header);
            panel.Children.Add(new TextBlock { Text = tool.Purpose, TextWrapping = TextWrapping.Wrap, Foreground = Gray, Margin = new Thickness(0, 3, 0, 0) });
            var status = new TextBlock { Text = "未体检", FontSize = 12 };
            _statusLabels[tool.Name] = status;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 2, 9, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 7, 0, 0),
                Child = status,
            };
            _statusPills[tool.Name] = pill;
            panel.Children.Add(pill);
            SetStatus(tool.Name, "未体检", StatusState.Neutral);
            var tutorialButton = new Button
            {
                Content = "📖 入门教程（版本对齐）",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0),
                Style = (Style)FindResource("GhostButton"),
            };
            tutorialButton.Click += (_, _) =>
            {
                string? label = null;
                if (_versionPickers.TryGetValue(tool.Name, out var combo))
                    label = combo.SelectedItem?.ToString();
                new TutorialWindow(tool.Resolve(label)) { Owner = this }.ShowDialog();
            };
            panel.Children.Add(tutorialButton);

            var cardBorder = new SolidColorBrush(Color.FromRgb(0xCD, 0xED, 0xFB));
            var cardBorderHover = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC));
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = cardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 13, 16, 13),
                Margin = new Thickness(0, 0, 0, 10),
                Child = panel,
            };
            card.MouseEnter += (_, _) => card.BorderBrush = cardBorderHover;
            card.MouseLeave += (_, _) => card.BorderBrush = cardBorder;
            ToolsPanel.Children.Add(card);
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
            Log($"预检 · {(item.Passed ? "✅" : "❌")} {item.Name}：{item.Detail}", item.Passed ? LogKind.Success : LogKind.Error);
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
                switch (r.Status)
                {
                    case ToolCheckStatus.Installed:
                        SetStatus(r.ToolName, r.Detail.Length > 0 ? $"✅ 已装好 · {r.Detail}" : "✅ 已装好", StatusState.Good);
                        Log($"体检 · {r.ToolName}：已装好（{r.Detail}）", LogKind.Success);
                        break;
                    case ToolCheckStatus.Missing:
                        SetStatus(r.ToolName, "❌ 还没装", StatusState.Bad);
                        Log($"体检 · {r.ToolName}：还没装 —— {r.Detail}", LogKind.Warn);
                        break;
                    default:
                        SetStatus(r.ToolName, "✋ 需手动确认", StatusState.Manual);
                        Log($"体检 · {r.ToolName}：需手动确认 —— {r.Detail}");
                        break;
                }
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
                Log("发现问题，正在自动处理，不用你动手…", LogKind.Warn);
                var healer = new SelfHealService(_runner, _winget);
                foreach (var item in preflight.Where(p => !p.Passed))
                {
                    if (item.Name.Contains("winget"))
                    {
                        var heal = await healer.EnsureWingetAsync(m => Log($"修复 · {m}"));
                        Log($"修复 · winget：{(heal.Fixed ? "✅ 已解决" : "❌ " + heal.Detail)}", heal.Fixed ? LogKind.Success : LogKind.Error);
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

            // 桌面课程文件夹先建好：装好一个放一个快捷方式，中途失败也不至于全丢。
            var desktopFolder = _shortcutService.EnsureCourseFolder(_current.Name);
            if (desktopFolder is not null) Log($"桌面课程文件夹已建好：{desktopFolder}（装好的软件快捷方式会陆续放进去）");
            else Log("没找到桌面位置，这次先跳过快捷方式（不影响安装本身）");

            _progressTotalWeight = plan.Sum(StepWeight);
            _progressDoneWeight = 0;
            _progressDoneSteps = 0;
            _progressTotalSteps = plan.Count;
            _currentStepTitle = "准备开始";
            _currentSubProgress = "";
            _stepStartedAt = DateTime.Now;
            BusyBar.Value = 0;
            ShowProgress();
            UpdateProgressText();
            _progressTimer.Start();

            var executor = new InstallExecutor(_runner, _winget, new SystemEnvironmentStore(), _stateStore);
            var results = await executor.ExecuteAsync(_current, plan,
                r => Dispatcher.Invoke(() =>
                {
                    LogStep(r);
                    _progressDoneWeight += StepWeight(r.Step);
                    _progressDoneSteps++;
                    UpdateProgressText();
                    if (desktopFolder is not null && r.Step.ToolName is not null
                        && r.Outcome is StepOutcome.Succeeded or StepOutcome.Skipped
                        && (r.Step.Kind is PlanStepKind.WingetInstall or PlanStepKind.DownloadInstall))
                        _ = PlaceShortcutAsync(desktopFolder, r.Step);
                }),
                onStepStart: step => Dispatcher.Invoke(() =>
                {
                    _currentStepTitle = step.Title;
                    _currentSubProgress = "";
                    _stepStartedAt = DateTime.Now;
                    UpdateProgressText();
                }),
                onProgress: text => Dispatcher.Invoke(() =>
                {
                    _currentSubProgress = text;
                    UpdateProgressText();
                }));
            _lastResults = results;
            DiagButton.IsEnabled = true;

            _progressTimer.Stop();
            HideProgress();
            if (desktopFolder is not null)
                _shortcutService.WriteReadmeIfNoShortcuts(desktopFolder, _current.Name, _current.Tools.Select(t => t.Name));

            var ok = results.Count(r => r.Outcome is StepOutcome.Succeeded or StepOutcome.Skipped);
            var failed = results.Count(r => r.Outcome == StepOutcome.Failed);
            Log($"执行完毕：完成 {ok} 步，失败 {failed} 步" + (failed > 0 ? "。失败不怕——点「导出诊断报告」发给老师或同学帮你看。" : "。全部搞定！"),
                failed > 0 ? LogKind.Warn : LogKind.Success);
            Check_Click(sender, e);
        }
        catch (Exception ex) { HideProgress(); Log($"配置过程出错：{ex.Message}", LogKind.Error); DiagButton.IsEnabled = true; }
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
            var removed = new ShortcutService(_runner).RemoveCourseFolder(_current.Name);
            if (removed is not null) Log($"卸载 · {removed}");
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
        var (mark, kind) = r.Outcome switch
        {
            StepOutcome.Succeeded => ("✅", LogKind.Success),
            StepOutcome.Skipped => ("⏭️", LogKind.Info),
            StepOutcome.Manual => ("✋", LogKind.Warn),
            _ => ("❌", LogKind.Error),
        };
        Log($"{mark} {r.Step.Title}：{r.Detail}", kind);
        if (r.Step.ToolName is not null && r.Step.Kind is PlanStepKind.Verify)
        {
            if (r.Outcome == StepOutcome.Succeeded)
                SetStatus(r.Step.ToolName, $"✅ 已装好 · {r.Detail}", StatusState.Good);
            else
                SetStatus(r.Step.ToolName, "❌ 验证没通过", StatusState.Bad);
        }
    }

    private void SetBusy(bool busy)
    {
        CheckButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        if (!busy) _progressTimer.Stop();
    }

    /// <summary>Step weights for the overall percentage: installs dominate, instant
    /// bookkeeping steps barely count, manual pointers don't count as machine work.</summary>
    private static int StepWeight(PlanStep step) => step.Kind switch
    {
        PlanStepKind.WingetInstall => 10,
        PlanStepKind.DownloadInstall => 10,
        PlanStepKind.RunCommand => 4,
        PlanStepKind.WindowsFeature => 4,
        PlanStepKind.ApplyEnv => 1,
        PlanStepKind.Verify => 1,
        _ => 0,
    };

    private void UpdateProgressText()
    {
        if (_progressTotalSteps == 0) { ProgressText.Text = ""; return; }
        var pct = _progressTotalWeight == 0 ? 0 : _progressDoneWeight * 100 / _progressTotalWeight;
        BusyBar.Value = pct;
        var elapsed = _stepStartedAt == default ? "" : $" · 本步已用 {(DateTime.Now - _stepStartedAt).ToString(@"m\:ss")}";
        var sub = _currentSubProgress.Length == 0 ? "" : $" · {_currentSubProgress}";
        ProgressText.Text = $"总进度 {pct}% · 第 {_progressDoneSteps}/{_progressTotalSteps} 步 · 正在：{_currentStepTitle}{sub}{elapsed}";
    }

    private async Task PlaceShortcutAsync(string folder, PlanStep step)
    {
        var tool = _current?.Tools.FirstOrDefault(t => t.Name == step.ToolName);
        var line = await _shortcutService.PlaceOneAsync(folder,
            new ShortcutService.Request(step.ToolName!, tool?.Shortcut, step.Location));
        if (line is not null) Log(line);
    }

    private enum LogKind { Info, Success, Error, Warn }

    private static readonly Brush LogInfoBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x4F));
    private static readonly Brush LogSuccessBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush LogErrorBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0x51, 0x46));
    private static readonly Brush LogWarnBrush = new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00));

    private void Log(string message, LogKind kind = LogKind.Info)
    {
        var brush = kind switch
        {
            LogKind.Success => LogSuccessBrush,
            LogKind.Error => LogErrorBrush,
            LogKind.Warn => LogWarnBrush,
            _ => LogInfoBrush,
        };
        var item = new ListBoxItem
        {
            Content = $"{DateTime.Now:HH:mm:ss}  {message}",
            Foreground = brush,
            Padding = new Thickness(6, 2, 6, 2),
        };
        LogList.Items.Add(item);
        LogList.ScrollIntoView(item);
    }

    private enum StatusState { Neutral, Good, Bad, Manual }

    /// <summary>Colored status pill on each tool card — readable at a glance.</summary>
    private void SetStatus(string toolName, string text, StatusState state)
    {
        if (!_statusLabels.TryGetValue(toolName, out var label)) return;
        label.Text = text;
        label.Foreground = state switch
        {
            StatusState.Good => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            StatusState.Bad => new SolidColorBrush(Color.FromRgb(0xC7, 0x51, 0x46)),
            StatusState.Manual => new SolidColorBrush(Color.FromRgb(0x9A, 0x6A, 0x00)),
            _ => new SolidColorBrush(Color.FromRgb(0x66, 0x6E, 0x73)),
        };
        if (_statusPills.TryGetValue(toolName, out var pill))
        {
            pill.Background = state switch
            {
                StatusState.Good => new SolidColorBrush(Color.FromRgb(0xE3, 0xF1, 0xE6)),
                StatusState.Bad => new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA)),
                StatusState.Manual => new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xDE)),
                _ => new SolidColorBrush(Color.FromRgb(0xEA, 0xF3, 0xF8)),
            };
        }
    }

    private void ShowProgress()
    {
        ProgressPanel.Visibility = Visibility.Visible;
    }

    private void HideProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        BusyBar.Value = 0;
        ProgressText.Text = "";
        _progressTotalSteps = 0;
    }
}
