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
// Also verify the copy compiled into the program (EmbeddedBundles) — that is the
// exact data the shipped exe loads, independent of any loose files.
var embeddedCatalog = BundleRepository.ParseCatalog(EmbeddedBundles.CatalogYaml ?? "");
var embeddedBundles = repo.ParseBundles(EmbeddedBundles.Files
    .Where(kv => !kv.Key.Equals("catalog.yaml", StringComparison.OrdinalIgnoreCase))
    .Select(kv => (kv.Key, kv.Value)));
Console.WriteLine($"程序内置副本：目录 {(embeddedCatalog is null ? "缺失" : "OK")}，课程包 {embeddedBundles.Count} 个");
if (embeddedCatalog is null || embeddedBundles.Count != bundles.Count)
    throw new Exception("内置副本与 bundles/ 文件不一致，请运行 tools/gen_bundles.py 重新生成");
Console.WriteLine("全部课程包通过 Core 解析与计划生成（文件版 + 内置版）。");

// Guide + shortcut audit: every tool must carry a version-aligned tutorial
// (>= 3 steps, known diagram kind, official link or an explaining note), and
// every auto-installed tool must declare a desktop-shortcut hint (or "none").
var diagramKinds = new HashSet<string> { "editor", "ide", "database", "terminal", "vm", "browser", "generic" };
var problems = new List<string>();
Action<GuideDef, string> auditGuide = (g, where) =>
{
    if (g.Steps.Count < 3) problems.Add($"{where}：教程步骤少于 3 步（{g.Steps.Count}）");
    if (!diagramKinds.Contains(g.Diagram)) problems.Add($"{where}：图例类型 {g.Diagram} 不认识");
    if (g.TutorialUrl is null && string.IsNullOrWhiteSpace(g.Note)) problems.Add($"{where}：没有官方教程链接，也没有说明");
};
foreach (var b in bundles)
{
    foreach (var t in b.Tools)
    {
        if ((t.Winget is not null || t.Download is not null) && t.Shortcut is null)
            problems.Add($"{b.Id}/{t.Name}：未声明 shortcut（开始菜单线索或 none）");
        if (t.Versions.Count > 0)
        {
            foreach (var v in t.Versions)
            {
                var g = v.Guide ?? t.Guide;
                if (g is null) problems.Add($"{b.Id}/{t.Name}@{v.Label}：缺教程");
                else auditGuide(g, $"{b.Id}/{t.Name}@{v.Label}");
            }
        }
        else if (t.Guide is null) problems.Add($"{b.Id}/{t.Name}：缺教程");
        else auditGuide(t.Guide, $"{b.Id}/{t.Name}");
    }
}
if (problems.Count > 0)
{
    problems.ForEach(p => Console.WriteLine("  ✗ " + p));
    throw new Exception($"教程/快捷方式审计发现 {problems.Count} 个问题");
}
Console.WriteLine($"教程审计 OK：{bundles.Sum(b => b.Tools.Count)} 个工具全部配有版本对齐教程与快捷方式声明。");
