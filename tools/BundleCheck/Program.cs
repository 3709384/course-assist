using CourseAssist.Core;

// Dev tool: validate every bundle through the real Core parser and planner.
// Usage: dotnet run --project tools/BundleCheck -- bundles
var dir = args.Length > 0 ? args[0] : "bundles";
var repo = new BundleRepository(dir);
var catalog = repo.LoadCatalog() ?? throw new Exception("catalog.yaml missing");
var bundles = repo.LoadBundles();
Console.WriteLine($"目录：{catalog.Categories.Count} 个分类，{catalog.Categories.Sum(c => c.Courses.Count)} 门课；课程包 {bundles.Count} 个");

var planner = new InstallPlanner();
foreach (var bundle in bundles)
{
    var plan = planner.BuildPlan(new InstallRequest { Bundle = bundle, InstallRoot = @"C:\CourseAssist" });
    Console.WriteLine($"  {bundle.Id,-26} 工具 {bundle.Tools.Count,2} 个 → 计划 {plan.Count,2} 步");
}

// IncludedTools filter: only the JDK of the Java bundle.
var java = bundles.Single(b => b.Id == "java-programming");
var filtered = planner.BuildPlan(new InstallRequest
{
    Bundle = java,
    InstallRoot = @"C:\CourseAssist",
    IncludedTools = new HashSet<string> { "JDK" },
});
if (filtered.Any(s => s.ToolName is not null && s.ToolName != "JDK"))
    throw new Exception("IncludedTools 过滤失效");
Console.WriteLine($"勾选过滤 OK：Java 包只选 JDK 时计划 {filtered.Count} 步（装 JDK + 配环境变量 + 验证）");
Console.WriteLine("全部课程包通过 Core 解析与计划生成。");
