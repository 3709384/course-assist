namespace CourseAssist.Core;

public enum PlanStepKind
{
    WingetInstall,
    DownloadInstall,
    ManualTool,
    ApplyEnv,
    RunCommand,
    WindowsFeature,
    ManualInstruction,
    Verify,
}

public sealed class PlanStep
{
    public required PlanStepKind Kind { get; init; }
    public required string Title { get; init; }
    public string? ToolName { get; init; }
    public string? VersionLabel { get; init; }
    public string? WingetId { get; init; }
    /// <summary>Intended (or resolved) install directory of the tool this step belongs to.</summary>
    public string? Location { get; init; }
    public DownloadDef? Download { get; init; }
    public string? ManualText { get; init; }
    public EnvDef? Env { get; init; }
    public string? Command { get; init; }
    public VerifyDef? Verify { get; init; }
}

public sealed class InstallRequest
{
    public required CourseBundle Bundle { get; init; }
    public required string InstallRoot { get; init; }
    /// <summary>Tool name → chosen version label. Missing entries use the bundle default.</summary>
    public Dictionary<string, string?> VersionSelections { get; init; } = new();
}

/// <summary>Turns a course bundle + user choices into an ordered, executable plan.</summary>
public sealed class InstallPlanner
{
    public IReadOnlyList<PlanStep> BuildPlan(InstallRequest request)
    {
        var bundle = request.Bundle;
        var steps = new List<PlanStep>();

        foreach (var tool in bundle.Tools)
        {
            request.VersionSelections.TryGetValue(tool.Name, out var label);
            var resolved = tool.Resolve(label);
            var subdir = tool.InstallSubdir ?? Sanitize(tool.Name);
            var location = Path.Combine(request.InstallRoot, subdir);
            var display = resolved.VersionLabel is null ? tool.Name : $"{tool.Name} {resolved.VersionLabel}";

            if (resolved.Winget is not null)
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.WingetInstall,
                    Title = $"安装 {display}",
                    ToolName = tool.Name,
                    VersionLabel = resolved.VersionLabel,
                    WingetId = resolved.Winget,
                    Location = location,
                });
            }
            else if (resolved.Download is not null)
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.DownloadInstall,
                    Title = $"下载并安装 {display}",
                    ToolName = tool.Name,
                    VersionLabel = resolved.VersionLabel,
                    Download = resolved.Download,
                    Location = location,
                });
            }
            else if (resolved.Manual is not null)
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.ManualTool,
                    Title = $"手动安装 {display}",
                    ToolName = tool.Name,
                    VersionLabel = resolved.VersionLabel,
                    ManualText = resolved.Manual,
                    Location = location,
                });
            }

            if (tool.Env is { IsEmpty: false })
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.ApplyEnv,
                    Title = $"配置 {tool.Name} 的环境变量",
                    ToolName = tool.Name,
                    Env = tool.Env,
                    Location = location,
                });
            }

            if (resolved.Verify is not null)
            {
                steps.Add(new PlanStep
                {
                    Kind = PlanStepKind.Verify,
                    Title = $"验证 {display}",
                    ToolName = tool.Name,
                    VersionLabel = resolved.VersionLabel,
                    Verify = resolved.Verify,
                    Location = location,
                });
            }
        }

        foreach (var step in bundle.Steps)
        {
            switch (step.Type.Trim().ToLowerInvariant())
            {
                case "env" when step.Name is not null:
                    steps.Add(new PlanStep
                    {
                        Kind = PlanStepKind.ApplyEnv,
                        Title = $"设置环境变量 {step.Name}",
                        Env = new EnvDef { Vars = new Dictionary<string, string> { [step.Name] = step.Value ?? "" } },
                        Location = request.InstallRoot,
                    });
                    break;
                case "command" when step.Run is not null:
                    steps.Add(new PlanStep
                    {
                        Kind = PlanStepKind.RunCommand,
                        Title = $"执行：{step.Run}",
                        Command = step.Run,
                    });
                    break;
                case "feature":
                    steps.Add(new PlanStep
                    {
                        Kind = PlanStepKind.WindowsFeature,
                        Title = $"启用 Windows 功能：{step.Name}",
                        Command = MapFeatureCommand(step.Name),
                    });
                    break;
                case "manual":
                    steps.Add(new PlanStep
                    {
                        Kind = PlanStepKind.ManualInstruction,
                        Title = step.Title ?? "手动步骤",
                        ManualText = step.Title,
                    });
                    break;
            }
        }

        return steps;
    }

    internal static string MapFeatureCommand(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "wsl" => "wsl --install",
        _ => $"echo 未知功能：{name}",
    };

    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray();
        var cleaned = new string(chars).Trim('-');
        return cleaned.Length == 0 ? "tool" : cleaned;
    }
}
