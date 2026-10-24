namespace CourseAssist.Core;

/// <summary>安装完成后，在桌面建一个以课程命名的文件夹，把这门课装好的软件的
/// 快捷方式放进去——初学者不用再满电脑找软件在哪。</summary>
public sealed class ShortcutService
{
    private readonly IProcessRunner _runner;
    public ShortcutService(IProcessRunner runner) => _runner = runner;

    public sealed record Request(string ToolName, string? Hint, string? InstallDir);

    /// <summary>Stock the desktop course folder. Returns a log line per tool.
    /// Never throws: a missing shortcut must not fail an otherwise good install.</summary>
    public async Task<IReadOnlyList<string>> PlaceCourseShortcutsAsync(
        string courseName, IReadOnlyList<Request> requests, CancellationToken ct = default)
    {
        var lines = new List<string>();
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (desktop.Length == 0) return new[] { "快捷方式：找不到桌面位置，已跳过" };
            var folder = Path.Combine(desktop, Sanitize(courseName));
            Directory.CreateDirectory(folder);

            var startMenuLinks = EnumerateStartMenuLinks();
            var createScript = new List<(string LinkPath, string Target, string WorkDir)>();

            foreach (var req in requests)
            {
                if (string.Equals(req.Hint, "none", StringComparison.OrdinalIgnoreCase))
                    continue; // command-line / service tool: no icon to give
                var dest = Path.Combine(folder, Sanitize(req.ToolName) + ".lnk");
                var found = FindStartMenuLink(startMenuLinks, req.Hint ?? req.ToolName);
                if (found is not null)
                {
                    File.Copy(found, dest, overwrite: true);
                    lines.Add($"快捷方式 · {req.ToolName}：已放进桌面「{courseName}」文件夹");
                    continue;
                }
                var exe = FindExeInInstallDir(req);
                if (exe is not null)
                {
                    createScript.Add((dest, exe, Path.GetDirectoryName(exe)!));
                    lines.Add($"快捷方式 · {req.ToolName}：已放进桌面「{courseName}」文件夹");
                    continue;
                }
                lines.Add($"快捷方式 · {req.ToolName}：没找到它的快捷方式，可以在开始菜单里搜「{req.ToolName}」");
            }

            if (createScript.Count > 0)
                await CreateShortcutsAsync(createScript, ct);
            if (requests.Any(r => !string.Equals(r.Hint, "none", StringComparison.OrdinalIgnoreCase)))
                lines.Insert(0, $"桌面已建文件夹「{courseName}」，这门课的软件都在里面，点开就能用");
        }
        catch (Exception ex)
        {
            lines.Add($"快捷方式：创建时出了点问题（{ex.Message}），不影响软件本身使用");
        }
        return lines;
    }

    /// <summary>Remove the desktop course folder (used on uninstall). Returns a log line or null.</summary>
    public string? RemoveCourseFolder(string courseName)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var folder = Path.Combine(desktop, Sanitize(courseName));
            if (!Directory.Exists(folder)) return null;
            Directory.Delete(folder, recursive: true);
            return $"已删除桌面上的「{courseName}」快捷方式文件夹";
        }
        catch
        {
            return null;
        }
    }

    private static List<string> EnumerateStartMenuLinks()
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        };
        var links = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try { links.AddRange(Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories)); }
            catch { /* a broken subfolder must not stop the rest */ }
        }
        return links;
    }

    private static string? FindStartMenuLink(List<string> links, string hint)
    {
        var needle = hint.Trim();
        if (needle.Length == 0) return null;
        // Exact-ish match first (file name without extension), then "contains".
        var exact = links.FirstOrDefault(l =>
            string.Equals(Path.GetFileNameWithoutExtension(l), needle, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return links
            .Where(l => Path.GetFileNameWithoutExtension(l).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => Path.GetFileNameWithoutExtension(l).Length)
            .FirstOrDefault();
    }

    private static string? FindExeInInstallDir(Request req)
    {
        if (req.InstallDir is null || !Directory.Exists(req.InstallDir)) return null;
        List<string> exes;
        try { exes = Directory.EnumerateFiles(req.InstallDir, "*.exe", SearchOption.TopDirectoryOnly).ToList(); }
        catch { return null; }
        if (exes.Count == 0) return null;
        var key = new string((req.Hint ?? req.ToolName).Where(char.IsLetterOrDigit).ToArray());
        var match = exes.FirstOrDefault(e =>
            Path.GetFileNameWithoutExtension(e).Contains(key, StringComparison.OrdinalIgnoreCase));
        // Only guess when there is exactly one candidate — never hand the user a wrong icon.
        return match ?? (exes.Count == 1 ? exes[0] : null);
    }

    private async Task CreateShortcutsAsync(List<(string LinkPath, string Target, string WorkDir)> items, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CourseAssist");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "make-shortcuts.ps1");
        var lines = new List<string> { "$ws = New-Object -ComObject WScript.Shell" };
        foreach (var (link, target, workDir) in items)
        {
            lines.Add($"$s = $ws.CreateShortcut('{Esc(link)}')");
            lines.Add($"$s.TargetPath = '{Esc(target)}'");
            lines.Add($"$s.WorkingDirectory = '{Esc(workDir)}'");
            lines.Add("$s.Save()");
        }
        await File.WriteAllLinesAsync(script, lines, ct);
        await _runner.RunAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"", ct);
    }

    private static string Esc(string s) => s.Replace("'", "''");

    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length == 0 ? "课程" : clean;
    }
}
