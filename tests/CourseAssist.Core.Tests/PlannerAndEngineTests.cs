using CourseAssist.Core;

namespace CourseAssist.Core.Tests;

/// <summary>Fake process runner driven by a handler over (fileName, arguments).</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<string, string, ProcessResult> _handler;
    public FakeProcessRunner(Func<string, string, ProcessResult> handler) => _handler = handler;

    public Task<ProcessResult> RunAsync(string commandLine, CancellationToken ct = default)
    {
        var idx = commandLine.IndexOf(' ');
        var file = idx < 0 ? commandLine : commandLine[..idx];
        var args = idx < 0 ? "" : commandLine[(idx + 1)..];
        return Task.FromResult(_handler(file, args));
    }

    public Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
        => Task.FromResult(_handler(fileName, arguments));
}

public class PlannerTests
{
    private static CourseBundle JavaBundle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "bundles", "catalog.yaml")))
            dir = dir.Parent;
        var repo = new BundleRepository(Path.Combine(dir!.FullName, "bundles"));
        return repo.LoadBundles().Single(b => b.Id == "java-programming");
    }

    [Fact]
    public void Plan_UsesDefaultVersion_AndInstallRootSubdir()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-root");
        var plan = new InstallPlanner().BuildPlan(new InstallRequest { Bundle = JavaBundle(), InstallRoot = root });

        var install = plan.Single(s => s.Kind == PlanStepKind.WingetInstall && s.ToolName == "JDK");
        Assert.Equal("Microsoft.OpenJDK.17", install.WingetId);
        Assert.Equal(Path.Combine(root, "JDK"), install.Location);

        Assert.Contains(plan, s => s.Kind == PlanStepKind.ApplyEnv && s.ToolName == "JDK");
        Assert.Contains(plan, s => s.Kind == PlanStepKind.Verify && s.ToolName == "JDK");
    }

    [Fact]
    public void Plan_HonorsVersionSelection()
    {
        var plan = new InstallPlanner().BuildPlan(new InstallRequest
        {
            Bundle = JavaBundle(),
            InstallRoot = Path.Combine(Path.GetTempPath(), "ca-root"),
            VersionSelections = new Dictionary<string, string?> { ["JDK"] = "8" },
        });
        var install = plan.Single(s => s.Kind == PlanStepKind.WingetInstall && s.ToolName == "JDK");
        Assert.Equal("EclipseAdoptium.Temurin.8.JDK", install.WingetId);
        Assert.Equal("8", install.VersionLabel);
    }

    [Fact]
    public void WingetInstallArgs_IncludeQuotedLocation()
    {
        var args = WingetCommands.InstallArgs("Microsoft.OpenJDK.17", @"D:\My Dev\JDK");
        Assert.Contains("--location \"D:\\My Dev\\JDK\"", args);
        Assert.Contains("--id Microsoft.OpenJDK.17", args);
        Assert.Contains("--silent", args);
    }
}

public class EngineTests
{
    [Fact]
    public async Task CheckEngine_UsesVerifyCommand()
    {
        var runner = new FakeProcessRunner((file, _) => file == "java"
            ? new ProcessResult(0, "openjdk version \"17.0.9\" 2023-10-17")
            : new ProcessResult(1, ""));
        var check = new CheckEngine(runner, new WingetService(runner));
        var bundle = new CourseBundle
        {
            Id = "t", Name = "测试课",
            Tools = [new ToolDef { Name = "JDK", Winget = "X", Verify = new VerifyDef { Command = "java -version", Expect = "17\\." } }],
        };
        var results = await check.CheckAsync(bundle);
        Assert.Equal(ToolCheckStatus.Installed, results[0].Status);
    }

    [Fact]
    public async Task CheckEngine_ReportsMissingWhenVerifyFails()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, "not found"));
        var check = new CheckEngine(runner, new WingetService(runner));
        var bundle = new CourseBundle
        {
            Id = "t", Name = "测试课",
            Tools = [new ToolDef { Name = "JDK", Verify = new VerifyDef { Command = "java -version" } }],
        };
        var results = await check.CheckAsync(bundle);
        Assert.Equal(ToolCheckStatus.Missing, results[0].Status);
    }

    [Fact]
    public void EnvManager_ApplyAndRestore_RoundTrips()
    {
        var store = new InMemoryEnvironmentStore();
        store.SetVariable("JAVA_HOME", @"C:\old-jdk");
        store.SetVariable("PATH", @"C:\Windows");
        var manager = new EnvManager(store);
        var backup = new Dictionary<string, string?>();
        var env = new EnvDef
        {
            Vars = new Dictionary<string, string> { ["JAVA_HOME"] = "{install_dir}" },
            PathAdditions = ["{install_dir}\\bin"],
        };

        manager.Apply(env, @"D:\Dev\JDK", backup);
        Assert.Equal(@"D:\Dev\JDK", store.GetVariable("JAVA_HOME"));
        Assert.Contains(@"D:\Dev\JDK\bin", store.GetVariable("PATH")!);

        // Applying again must not duplicate the PATH entry.
        manager.Apply(env, @"D:\Dev\JDK", backup);
        Assert.Equal(1, store.GetVariable("PATH")!.Split(';').Count(e => e == @"D:\Dev\JDK\bin"));

        manager.Restore(backup);
        Assert.Equal(@"C:\old-jdk", store.GetVariable("JAVA_HOME"));
        Assert.Equal(@"C:\Windows", store.GetVariable("PATH"));
    }

    [Fact]
    public async Task Executor_InstallsRecordsState_AndSkipsOnRerun()
    {
        var installed = new HashSet<string>();
        var runner = new FakeProcessRunner((file, args) =>
        {
            if (file == "winget" && args.StartsWith("list"))
                return new ProcessResult(installed.Contains("Microsoft.OpenJDK.17") ? 0 : 1,
                    installed.Contains("Microsoft.OpenJDK.17") ? "Microsoft.OpenJDK.17  17.0.9" : "No installed package found");
            if (file == "winget" && args.StartsWith("install"))
            {
                installed.Add("Microsoft.OpenJDK.17");
                return new ProcessResult(0, "Successfully installed");
            }
            if (file == "java")
                return new ProcessResult(installed.Contains("Microsoft.OpenJDK.17") ? 0 : 1, "openjdk version \"17.0.9\"");
            return new ProcessResult(0, "");
        });

        var statePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.json");
        var executor = new InstallExecutor(runner, new WingetService(runner), new InMemoryEnvironmentStore(), new StateStore(statePath));
        var bundle = new CourseBundle
        {
            Id = "t", Name = "测试课",
            Tools = [new ToolDef
            {
                Name = "JDK", Winget = "Microsoft.OpenJDK.17",
                Verify = new VerifyDef { Command = "java -version", Expect = "17\\." },
            }],
        };
        var plan = new InstallPlanner().BuildPlan(new InstallRequest { Bundle = bundle, InstallRoot = Path.GetTempPath() });

        var first = await executor.ExecuteAsync(bundle, plan);
        Assert.Contains(first, r => r.Step.Kind == PlanStepKind.WingetInstall && r.Outcome == StepOutcome.Succeeded);
        Assert.Contains(first, r => r.Step.Kind == PlanStepKind.Verify && r.Outcome == StepOutcome.Succeeded);

        var state = new StateStore(statePath).Load();
        Assert.Contains(state.Installs, r => r.WingetId == "Microsoft.OpenJDK.17");

        var second = await executor.ExecuteAsync(bundle, plan);
        Assert.Contains(second, r => r.Step.Kind == PlanStepKind.WingetInstall && r.Outcome == StepOutcome.Skipped);
    }

    [Fact]
    public void Diagnostics_IncludesFailures()
    {
        var step = new PlanStep { Kind = PlanStepKind.WingetInstall, Title = "安装 JDK 17", ToolName = "JDK" };
        var report = DiagnosticsReport.Build("Java 程序设计",
            [new StepResult(step, StepOutcome.Failed, "winget 退出码 1：网络错误")]);
        Assert.Contains("Java 程序设计", report);
        Assert.Contains("安装 JDK 17", report);
        Assert.Contains("网络错误", report);
    }
}
