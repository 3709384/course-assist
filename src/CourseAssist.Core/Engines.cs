using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CourseAssist.Core;

public static class Verification
{
    public static async Task<(bool Ok, string Detail)> RunAsync(
        IProcessRunner runner, VerifyDef verify, CancellationToken ct = default)
    {
        try
        {
            var r = await runner.RunAsync(verify.Command, ct);
            if (!r.Success) return (false, $"验证命令退出码 {r.ExitCode}：{r.Tail(300)}");
            if (verify.Expect is not null && !Regex.IsMatch(r.Output, verify.Expect))
                return (false, $"输出与期望 /{verify.Expect}/ 不匹配：{r.Tail(300)}");
            var firstLine = r.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return (true, firstLine);
        }
        catch (Exception ex)
        {
            return (false, $"无法执行验证命令（程序可能未安装）：{ex.Message}");
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
                var (ok, detail) = await Verification.RunAsync(_runner, resolved.Verify, ct);
                results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                    ok ? ToolCheckStatus.Installed : ToolCheckStatus.Missing, detail));
            }
            else if (resolved.Winget is not null)
            {
                var installed = await _winget.IsInstalledAsync(resolved.Winget, ct);
                results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                    installed ? ToolCheckStatus.Installed : ToolCheckStatus.Missing,
                    installed ? "winget 显示已安装" : "winget 显示未安装"));
            }
            else
            {
                results.Add(new ToolCheckResult(tool.Name, resolved.VersionLabel,
                    ToolCheckStatus.Unknown, "该工具为手动安装项，无法自动检测"));
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
        Action<StepResult>? onStep = null, CancellationToken ct = default)
    {
        var results = new List<StepResult>();
        var state = _stateStore.Load();

        foreach (var step in plan)
        {
            var result = step.Kind switch
            {
                PlanStepKind.WingetInstall => await InstallWithWingetAsync(bundle, step, state, ct),
                PlanStepKind.DownloadInstall => await InstallFromDownloadAsync(step, ct),
                PlanStepKind.ManualTool => new StepResult(step, StepOutcome.Manual, step.ManualText ?? "请按课程包说明手动安装。"),
                PlanStepKind.ManualInstruction => new StepResult(step, StepOutcome.Manual, step.ManualText ?? step.Title),
                PlanStepKind.ApplyEnv => ApplyEnvStep(bundle, step, state),
                PlanStepKind.RunCommand => await RunCommandStepAsync(step, ct),
                PlanStepKind.WindowsFeature => await RunCommandStepAsync(step, ct),
                PlanStepKind.Verify => await VerifyStepAsync(step, ct),
                _ => new StepResult(step, StepOutcome.Skipped, "未知步骤类型"),
            };
            results.Add(result);
            onStep?.Invoke(result);
        }
        return results;
    }

    private async Task<StepResult> InstallWithWingetAsync(CourseBundle bundle, PlanStep step, AppState state, CancellationToken ct)
    {
        var id = step.WingetId!;
        if (await _winget.IsInstalledAsync(id, ct))
        {
            UpsertRecord(bundle, step, state);
            _stateStore.Save(state);
            return new StepResult(step, StepOutcome.Skipped, "已安装，跳过（断点续装）");
        }
        var r = await _runner.RunAsync("winget", WingetCommands.InstallArgs(id, step.Location), ct);
        if (r.Success || r.Output.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            UpsertRecord(bundle, step, state);
            _stateStore.Save(state);
            return new StepResult(step, StepOutcome.Succeeded, $"已安装到 {step.Location}（如安装器忽略指定位置，以验证结果为准）");
        }
        return new StepResult(step, StepOutcome.Failed, $"winget 退出码 {r.ExitCode}：{r.Tail()}");
    }

    private async Task<StepResult> InstallFromDownloadAsync(PlanStep step, CancellationToken ct)
    {
        var download = step.Download!;
        try
        {
            var fileName = Path.GetFileName(new Uri(download.Url).AbsolutePath);
            var dir = Path.Combine(Path.GetTempPath(), "CourseAssist", "downloads");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, fileName);
            await using (var stream = await Http.GetStreamAsync(download.Url, ct))
            await using (var fs = File.Create(file))
                await stream.CopyToAsync(fs, ct);

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

    private async Task<StepResult> RunCommandStepAsync(PlanStep step, CancellationToken ct)
    {
        var r = await _runner.RunAsync(step.Command!, ct);
        return r.Success
            ? new StepResult(step, StepOutcome.Succeeded, r.Tail(200))
            : new StepResult(step, StepOutcome.Failed, $"退出码 {r.ExitCode}：{r.Tail()}");
    }

    private async Task<StepResult> VerifyStepAsync(PlanStep step, CancellationToken ct)
    {
        var (ok, detail) = await Verification.RunAsync(_runner, step.Verify!, ct);
        return new StepResult(step, ok ? StepOutcome.Succeeded : StepOutcome.Failed, detail);
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
                var r = await _runner.RunAsync("winget", WingetCommands.UninstallArgs(record.WingetId), ct);
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
            wingetOk ? "可用" : "不可用：请先从 Microsoft Store 安装/更新「应用安装程序」"));

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
