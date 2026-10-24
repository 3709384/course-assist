namespace CourseAssist.Core;

/// <summary>桌面课程文件夹：一键配置一开始就在桌面建好以课程命名的文件夹，
/// 每装好一个软件就立刻把它的快捷方式放进去——装完不怕找不到软件在哪。</summary>
public sealed class ShortcutService
{
    private readonly IProcessRunner _runner;
    public ShortcutService(IProcessRunner runner) => _runner = runner;

    public sealed record Request(string ToolName, string? Hint, string? InstallDir);

    /// <summary>Resolve the user's real desktop folder (respects OneDrive redirection).</summary>
    public static string? ResolveDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (desktop.Length > 0) return desktop;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
        {
            var plain = Path.Combine(home, "Desktop");
            if (Directory.Exists(plain)) return plain;
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrWhiteSpace(oneDrive))
            {
                var od = Path.Combine(oneDrive, "Desktop");
                if (Directory.Exists(od)) return od;
            }
            return plain;
        }
        return null;
    }

    /// <summary>Create (or reuse) the desktop folder for a course. Returns its full path,
    /// or null when no desktop could be located.</summary>
    public string? EnsureCourseFolder(string courseName)
    {
        var desktop = ResolveDesktop();
        if (desktop is null) return null;
        var folder = Path.Combine(desktop, Sanitize(courseName));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Put one tool's shortcut into the course folder right after it installs.
    /// Returns a log line, or null when this tool intentionally has no shortcut.
    /// Never throws.</summary>
    public async Task<string?> PlaceOneAsync(string folder, Request req, CancellationToken ct = default)
    {
        if (string.Equals(req.Hint, "none", StringComparison.OrdinalIgnoreCase))
            return null; // command-line / service tool: no icon to give, by design
        try
        {
            var dest = Path.Combine(folder, Sanitize(req.ToolName) + ".lnk");
            var found = FindStartMenuLink(EnumerateStartMenuLinks(), req.Hint ?? req.ToolName);
            if (found is not null)
            {
                File.Copy(found, dest, overwrite: true);
                return $"快捷方式 · {req.ToolName}：已放进桌面课程文件夹";
            }
            var exe = FindExeInInstallDir(req);
            if (exe is not null)
            {
                await CreateShortcutsAsync(new[] { (dest, exe, Path.GetDirectoryName(exe)!) }, ct);
                if (File.Exists(dest))
                    return $"快捷方式 · {req.ToolName}：已放进桌面课程文件夹";
            }
            return $"快捷方式 · {req.ToolName}：没找到现成快捷方式，可在开始菜单里搜「{req.ToolName}」（文件夹里有说明文件）";
        }
        catch (Exception ex)
        {
            return $"快捷方式 · {req.ToolName}：创建时出了点问题（{ex.Message}），不影响软件使用";
        }
    }

    /// <summary>When no shortcut could be placed at all, leave a plain-language note in
    /// the folder so the folder is never a confusing empty box.</summary>
    public void WriteReadmeIfNoShortcuts(string folder, string courseName, IEnumerable<string> toolNames)
    {
        try
        {
            if (Directory.EnumerateFiles(folder, "*.lnk").Any()) return;
            var lines = new List<string>
            {
                $"《{courseName}》的软件已经装好了。",
                "",
                "这门课包含的软件：",
            };
            lines.AddRange(toolNames.Select(t => $"  · {t}"));
            lines.Add("");
            lines.Add("怎么打开它们：在屏幕左下角的开始菜单里搜索软件名（比如 VS Code、DBeaver），点开就能用。");
            lines.Add("每个软件的入门教程在 Course Assist 软件里：选这门课，点软件下面的「📖 入门教程」。");
            File.WriteAllLines(Path.Combine(folder, "软件在哪儿打开.txt"), lines);
        }
        catch { /* a note file must never break the flow */ }
    }

    /// <summary>Remove the desktop course folder (used on uninstall). Returns a log line or null.</summary>
    public string? RemoveCourseFolder(string courseName)
    {
        try
        {
            var desktop = ResolveDesktop();
            if (desktop is null) return null;
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

    private async Task CreateShortcutsAsync(IReadOnlyList<(string LinkPath, string Target, string WorkDir)> items, CancellationToken ct)
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
