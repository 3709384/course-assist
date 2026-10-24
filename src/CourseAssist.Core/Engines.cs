using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CourseAssist.Core;

public static class Verification
{
    /// <summary>Runs a verify command. CommandStarted tells callers the command could not
    /// even launch (then a winget-list cross-check is more truthful than "未安装").
    /// FoundLine is the first output line — used to tell the user, in plain words,
    /// WHICH version is actually present instead of leaking regexes and raw dumps.</summary>
    public static async Task<(bool Ok, string Detail, bool CommandStarted, string FoundLine)> RunAsync(
        IProcessRunner runner, VerifyDef verify, CancellationToken ct = default)
    {
        try
        {
            var r = await runner.RunAsync(verify.Command, ct);
            var firstLine = (r.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "").Replace("\"", "");
            if (!r.Success) return (false, $"验证命令退出码 {r.ExitCode}：{r.Tail(300)}", true, firstLine);
            if (verify.Expect is not null && !Regex.IsMatch(r.Output, verify.Expect))
                return (false, $"输出与期望 /{verify.Expect}/ 不匹配：{r.Tail(300)}", true, firstLine);
            return (true, firstLine, true, firstLine);
        }
        catch (Exception ex)
        {
            return (false, $"无法执行验证命令（程序可能未安装）：{ex.Message}", false, "");
        }
    }
}

public enum ToolCheckStatus { Installed, Missing, Unknown }

public sealed record ToolCheckResult(string ToolName, string? VersionLabel, ToolCheckStatus Status, string Detail);

/// <summary>环境体检：逐个工具判断是否已正确安装。</summary>
public sealed class CheckEngine
{
    private readonly IProcessRunner _runner;
    private readonly WingetService _winget;

    public CheckEngine(IProcessRunner runner, WingetService winget)
    {
        _runner = runner;
        _winget = winget;
    }

    public async Task<IReadOnlyList<ToolCheckResult>> CheckAsync(
        CourseBundle bundle, IReadOnlyDictionary<string, string?>? selections = null, CancellationToken ct = default)
    {
        var results = new List<ToolCheckResult>();
        foreach (var tool in bundle.Tools)
        {
            string? label = null;
            selections?.TryGetValue(tool.Name, out label);
            var resolved = tool.Resolve(label);
            if (resolved.Verify is not null)
            {
                // All user-facing texts below are plain-language on purpose: beginners
                // should learn what is on their machine and what to do next — never see
                // regex patterns, exit codes or raw process errors.
                var (ok, detail, started, found) = await Verification.RunAsync(_runner, resolved.Verify, ct);
                if (ok)
                {
                    results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                        ToolCheckStatus.Installed, found));
                    continue;
                }
                if (!started && resolved.Winget is not null)
                {
                    // The verify command itself could not launch (e.g. its folder is not
                    // on PATH yet). Cross-check with winget's own records before judging.
                    if (await _winget.IsInstalledAsync(resolved.Winget, ct))
                    {
                        results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                            ToolCheckStatus.Installed, "已在电脑上（刚装好时验证命令偶尔要重启后才顺畅，不影响使用）"));
                        continue;
                    }
                    results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                        ToolCheckStatus.Missing, "还没安装，点「一键配置」就会装上"));
                    continue;
                }
                if (!started)
                {
                    results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                        ToolCheckStatus.Missing, "还没安装（或刚装完还没生效，重启电脑后再体检一次）"));
                    continue;
                }
                if (detail.StartsWith("输出与期望", StringComparison.Ordinal))
                {
                    // Something IS installed, just not the version this course wants.
                    var want = resolved.VersionLabel is { } lbl ? $"这门课要用 {tool.Name} {lbl}" : "这门课要用另一个版本";
                    results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                        ToolCheckStatus.Missing,
                        $"电脑上现在是「{found}」，{want} —— 点「一键配置」会装上需要的版本（不同版本可以共存，不用先卸载）"));
                    continue;
                }
                results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                    ToolCheckStatus.Missing, "还没安装成功，点「一键配置」试试"));
            }
            else if (resolved.Winget is not null)
            {
                var installed = await _winget.IsInstalledAsync(resolved.Winget, ct);
                results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                    installed ? ToolCheckStatus.Installed : ToolCheckStatus.Missing,
                    installed ? "已在电脑上" : "还没安装，点「一键配置」就会装上"));
            }
            else
            {
                results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                    ToolCheckStatus.Unknown, "这个是手动安装的软件，装没装请打开它确认一下"));
            }
        }
        return results;
    }
}

public enum StepOutcome { Succeeded, Failed, Skipped, Manual }

public sealed record StepResult(PlanStep Step, StepOutcome Outcome, string Detail);

/// <summary>执行安装计划；每完成一步就落盘记录，支持中断后继续（断点续装）。</summary>
public sealed class InstallExecutor
{
    private static readonly HttpClient Http = new();

    private readonly IProcessRunner _runner;
    private readonly WingetService _winget;
    private readonly EnvManager _envManager;
    private readonly StateStore _stateStore;

    public InstallExecutor(IProcessRunner runner, WingetService winget, IEnvironmentStore envStore, StateStore stateStore)
    {
        _runner = runner;
        _winget = winget;
        _envManager = new EnvManager(envStore);
        _stateStore = stateStore;
    }

    public async Task<IReadOnlyList<StepResult>> ExecuteAsync(
        CourseBundle bundle, IReadOnlyList<PlanStep> plan,
        Action<StepResult>? onStep = null, CancellationToken ct = default,
        Action<PlanStep>? onStepStart = null, Action<string>? onProgress = null)
    {
        var results = new List<StepResult>();
        var state = _stateStore.Load();

        foreach (var step in plan)
        {
            onStepStart?.Invoke(step);
            StepResult result;
            try
            {
                result = step.Kind switch
                {
                    PlanStepKind.WingetInstall => await InstallWithWingetAsync(bundle, step, state, ct, onProgress),
                    PlanStepKind.DownloadInstall => await InstallFromDownloadAsync(step, ct, onProgress),
                    PlanStepKind.ManualTool => new StepResult(step, StepOutcome.Manual, step.ManualText ?? "请按课程包说明手动安装。"),
                    PlanStepKind.ManualInstruction => new StepResult(step, StepOutcome.Manual, step.ManualText ?? step.Title),
                    PlanStepKind.ApplyEnv => ApplyEnvStep(bundle, step, state),
                    PlanStepKind.RunCommand => await RunCommandStepAsync(step, ct, onProgress),
                    PlanStepKind.WindowsFeature => await RunCommandStepAsync(step, ct, onProgress),
                    PlanStepKind.Verify => await VerifyStepAsync(step, ct),
                    _ => new StepResult(step, StepOutcome.Skipped, "未知步骤类型"),
                };
            }
            catch (Exception ex)
            {
                // One bad step must never abort the whole run: the remaining tools,
                // the desktop shortcuts and the final report still matter.
                result = new StepResult(step, StepOutcome.Failed, $"执行时出错（已跳过这一步，继续后面的）：{ex.Message}");
            }
            results.Add(result);
            onStep?.Invoke(result);
        }
        return results;
    }

    /// <summary>Turn a process's raw output stream into "label NN%" progress reports
    /// whenever the underlying installer prints a percentage (winget and pip do).</summary>
    private static Action<string> PercentProgress(Action<string>? onProgress, string label)
    {
        var last = -1;
        var recent = new StringBuilder();
        return chunk =>
        {
            if (onProgress is null) return;
            lock (recent)
            {
                recent.Append(chunk);
                if (recent.Length > 400) recent.Remove(0, recent.Length - 400);
                var matches = Regex.Matches(recent.ToString(), @"(\d{1,3})\s*%");
                if (matches.Count == 0) return;
                if (int.TryParse(matches[^1].Groups[1].Value, out var pct) && pct <= 100 && pct != last)
                {
                    last = pct;
                    onProgress($"{label} {pct}%");
                }
            }
        };
    }

    private async Task<StepResult> InstallWithWingetAsync(CourseBundle bundle, PlanStep step, AppState state, CancellationToken ct, Action<string>? onProgress = null)
    {
        var id = step.WingetId!;
        if (await _winget.IsInstalledAsync(id, ct))
        {
            UpsertRecord(bundle, step, state);
            _stateStore.Save(state);
            return new StepResult(step, StepOutcome.Skipped, "已安装，跳过（断点续装）");
        }
        var r = await _runner.RunAsync(_winget.Executable, WingetCommands.InstallArgs(id, step.Location), ct,
            PercentProgress(onProgress, "下载/安装"));
        if (r.Success || r.Output.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            UpsertRecord(bundle, step, state);
            _stateStore.Save(state);
            return new StepResult(step, StepOutcome.Succeeded, $"已安装到 {step.Location}（如安装器忽略指定位置，以验证结果为准）");
        }
        return new StepResult(step, StepOutcome.Failed, $"winget 退出码 {r.ExitCode}：{r.Tail()}");
    }

    private async Task<StepResult> InstallFromDownloadAsync(PlanStep step, CancellationToken ct, Action<string>? onProgress = null)
    {
        var download = step.Download!;
        try
        {
            var fileName = Path.GetFileName(new Uri(download.Url).AbsolutePath);
            var dir = Path.Combine(Path.GetTempPath(), "CourseAssist", "downloads");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, fileName);
            using (var response = await Http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fs = File.Create(file);
                var buf = new byte[81920];
                long done = 0;
                var lastPct = -1;
                int read;
                while ((read = await stream.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    if (total is > 0 && onProgress is not null)
                    {
                        var pct = (int)(done * 100 / total.Value);
                        if (pct != lastPct) { lastPct = pct; onProgress($"下载 {pct}%"); }
                    }
                }
            }

            await using (var fs = File.OpenRead(file))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!string.Equals(hash, download.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new StepResult(step, StepOutcome.Failed, "安装包 SHA256 校验不通过，已中止（文件可能被篡改或下载不完整）");
            }
            var r = await _runner.RunAsync(file, "", ct);
            return r.Success
                ? new StepResult(step, StepOutcome.Succeeded, "安装程序已完成")
                : new StepResult(step, StepOutcome.Failed, $"安装程序退出码 {r.ExitCode}：{r.Tail()}");
        }
        catch (Exception ex)
        {
            return new StepResult(step, StepOutcome.Failed, $"下载或安装失败：{ex.Message}");
        }
    }

    private StepResult ApplyEnvStep(CourseBundle bundle, PlanStep step, AppState state)
    {
        try
        {
            var record = FindOrCreateRecord(bundle, step, state);
            var changed = _envManager.Apply(step.Env!, step.Location ?? "", record.EnvBackup);
            _stateStore.Save(state);
            return new StepResult(step, StepOutcome.Succeeded, $"已设置：{string.Join("、", changed)}（原值已备份，可在卸载时还原）");
        }
        catch (Exception ex)
        {
            return new StepResult(step, StepOutcome.Failed, $"环境变量设置失败：{ex.Message}");
        }
    }

    private async Task<StepResult> RunCommandStepAsync(PlanStep step, CancellationToken ct, Action<string>? onProgress = null)
    {
        var r = await _runner.RunAsync(step.Command!, ct, PercentProgress(onProgress, "运行中"));
        return r.Success
            ? new StepResult(step, StepOutcome.Succeeded, r.Tail(200))
            : new StepResult(step, StepOutcome.Failed, $"退出码 {r.ExitCode}：{r.Tail()}");
    }

    private async Task<StepResult> VerifyStepAsync(PlanStep step, CancellationToken ct)
    {
        var (ok, detail, started, found) = await Verification.RunAsync(_runner, step.Verify!, ct);
        if (ok) return new StepResult(step, StepOutcome.Succeeded, found);
        if (!started)
            return new StepResult(step, StepOutcome.Failed,
                "验证命令暂时跑不起来（可能刚装完环境变量还没刷新，重启电脑后再体检一次）");
        if (detail.StartsWith("输出与期望", StringComparison.Ordinal))
            return new StepResult(step, StepOutcome.Failed,
                $"装的版本不是课程要求的（电脑上现在是：{found}）");
        return new StepResult(step, StepOutcome.Failed, "验证没通过，可能没有安装成功");
    }

    /// <summary>按安装记录卸载某门课装过的软件并还原环境变量。返回日志行。</summary>
    public async Task<IReadOnlyList<string>> UninstallCourseAsync(string courseId, CancellationToken ct = default)
    {
        var log = new List<string>();
        var state = _stateStore.Load();
        var records = state.Installs.Where(r => r.CourseId == courseId).ToList();
        foreach (var record in records.AsEnumerable().Reverse())
        {
            if (record.EnvBackup.Count > 0)
            {
                _envManager.Restore(record.EnvBackup);
                log.Add($"已还原 {record.ToolName} 改动的环境变量");
            }
            if (record.WingetId is not null)
            {
                var r = await _runner.RunAsync(_winget.Executable, WingetCommands.UninstallArgs(record.WingetId), ct);
                log.Add(r.Success ? $"已卸载 {record.ToolName}" : $"卸载 {record.ToolName} 失败（退出码 {r.ExitCode}），可手动在系统设置中卸载");
            }
            state.Installs.Remove(record);
            _stateStore.Save(state);
        }
        if (records.Count == 0) log.Add("没有找到这门课的安装记录。");
        return log;
    }

    private static InstallRecord FindOrCreateRecord(CourseBundle bundle, PlanStep step, AppState state)
    {
        var toolName = step.ToolName ?? "课程附加步骤";
        var record = state.Installs.FirstOrDefault(r => r.CourseId == bundle.Id && r.ToolName == toolName);
        if (record is null)
        {
            record = new InstallRecord
            {
                CourseId = bundle.Id,
                ToolName = toolName,
                VersionLabel = step.VersionLabel,
                InstallDir = step.Location,
                InstalledAt = DateTimeOffset.Now,
            };
            state.Installs.Add(record);
        }
        return record;
    }

    private static void UpsertRecord(CourseBundle bundle, PlanStep step, AppState state)
    {
        var record = FindOrCreateRecord(bundle, step, state);
        record.WingetId = step.WingetId;
        record.VersionLabel = step.VersionLabel;
        record.InstallDir = step.Location;
        record.InstalledAt = DateTimeOffset.Now;
    }
}

public sealed record PreflightItem(string Name, bool Passed, string Detail);

/// <summary>安装前预检：系统、磁盘、winget、目录可写。</summary>
public sealed class Preflight
{
    private readonly WingetService _winget;
    public Preflight(WingetService winget) => _winget = winget;

    public async Task<IReadOnlyList<PreflightItem>> CheckAsync(string installRoot, CancellationToken ct = default)
    {
        var items = new List<PreflightItem>();

        var isWindows = OperatingSystem.IsWindows();
        var versionOk = isWindows && Environment.OSVersion.Version.Major >= 10;
        items.Add(new PreflightItem("操作系统", versionOk,
            isWindows ? $"Windows {Environment.OSVersion.Version}" : "当前不是 Windows 系统（本工具面向 Windows）"));

        try
        {
            var full = Path.GetFullPath(installRoot);
            var root = Path.GetPathRoot(full) ?? "";
            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            items.Add(new PreflightItem("磁盘空间", freeGb >= 2, $"{root} 盘可用 {freeGb:F1} GB（建议至少 2 GB）"));
        }
        catch (Exception ex)
        {
            items.Add(new PreflightItem("磁盘空间", false, $"无法检测：{ex.Message}"));
        }

        var wingetOk = await _winget.IsAvailableAsync(ct);
        items.Add(new PreflightItem("winget 包管理器", wingetOk,
            wingetOk ? "可用" : "不可用（程序会自动尝试修复，不用你手动处理）"));

        try
        {
            Directory.CreateDirectory(installRoot);
            var probe = Path.Combine(installRoot, ".courseassist-write-test");
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);
            items.Add(new PreflightItem("安装目录可写", true, installRoot));
        }
        catch (Exception ex)
        {
            items.Add(new PreflightItem("安装目录可写", false, $"无法写入 {installRoot}：{ex.Message}"));
        }

        return items;
    }
}

/// <summary>把一次运行的结果整理成可导出的诊断报告（发给老师/同学或附到 Issue）。</summary>
public static class DiagnosticsReport
{
    public static string Build(string courseName, IReadOnlyList<StepResult> results, IReadOnlyList<PreflightItem>? preflight = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Course Assist 诊断报告");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 系统：{Environment.OSVersion}（{Environment.MachineName}）");
        sb.AppendLine($"- 课程：{courseName}");
        sb.AppendLine();
        if (preflight is { Count: > 0 })
        {
            sb.AppendLine("## 预检");
            foreach (var item in preflight)
                sb.AppendLine($"- {(item.Passed ? "✅" : "❌")} {item.Name}：{item.Detail}");
            sb.AppendLine();
        }
        sb.AppendLine("## 步骤结果");
        foreach (var r in results)
        {
            var mark = r.Outcome switch
            {
                StepOutcome.Succeeded => "✅",
                StepOutcome.Skipped => "⏭️",
                StepOutcome.Manual => "✋",
                _ => "❌",
            };
            sb.AppendLine($"- {mark} {r.Step.Title}：{r.Detail}");
        }
        sb.AppendLine();
        sb.AppendLine("把这份报告发给老师、同学，或附到 GitHub Issue 里，别人就能帮你定位问题。");
        return sb.ToString();
    }
}
