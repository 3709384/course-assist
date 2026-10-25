using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CourseAssist.Core;

public sealed record HealResult(bool Fixed, string Detail);

/// <summary>自动修复：预检发现问题时，程序先自己动手解决，而不是把问题甩给用户。
/// winget 缺失 → 先定位已有安装，再从微软官方渠道下载「应用安装程序」装上；
/// 安装位置不可用 → 自动换一个可用位置。</summary>
public sealed class SelfHealService
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly IProcessRunner _runner;
    private readonly WingetService _winget;

    public SelfHealService(IProcessRunner runner, WingetService winget)
    {
        _runner = runner;
        _winget = winget;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CourseAssist/1.0");
        return http;
    }

    /// <summary>Make winget usable: locate an existing copy, or install Microsoft's
    /// App Installer from the official winget-cli releases. Progress goes to <paramref name="log"/>.</summary>
    public async Task<HealResult> EnsureWingetAsync(Action<string> log, CancellationToken ct = default)
    {
        if (await _winget.IsAvailableAsync(ct))
            return new HealResult(true, "winget 本来就可用");

        log("没找到能用的 winget，正在定位它可能藏身的位置…");
        if (await _winget.ResolveExecutableAsync(ct) && await _winget.IsAvailableAsync(ct))
            return new HealResult(true, $"已找到 winget（{_winget.Executable}），改用完整路径调用");

        log("本机确实没有 winget，正在从微软官方下载「应用安装程序」…");
        try
        {
            await InstallAppInstallerAsync(log, ct);
        }
        catch (Exception ex)
        {
            return new HealResult(false, $"自动安装 winget 没成功：{ex.Message}。已尝试：定位现有 winget、从微软官方 GitHub 下载应用安装程序。可以把诊断报告发给老师/同学求助");
        }

        if (await _winget.ResolveExecutableAsync(ct) || await _winget.IsAvailableAsync(ct))
            return new HealResult(true, "已自动装好「应用安装程序」，winget 现在可用了");
        return new HealResult(false, "应用安装程序已下载并执行安装，但 winget 仍不可用（可能需要重启电脑后再试）");
    }

    private async Task InstallAppInstallerAsync(Action<string> log, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CourseAssist", "winget-fix");
        Directory.CreateDirectory(dir);

        // Latest App Installer bundle from the official winget-cli releases.
        // If the GitHub API is unreachable (common on flaky networks), fall back to
        // Microsoft's own short link, which redirects to the same bundle.
        string bundleUrl;
        try
        {
            bundleUrl = await FindLatestBundleUrlAsync(ct);
        }
        catch (Exception)
        {
            log("官方发布接口暂时连不上，改走微软短链接 aka.ms/getwinget…");
            bundleUrl = "https://aka.ms/getwinget";
        }
        var bundle = Path.Combine(dir, "AppInstaller.msixbundle");
        log("正在下载应用安装程序（约几十 MB，视网速等待一会儿）…");
        await DownloadAsync(bundleUrl, bundle, ct, log);

        // Dependencies of the bundle (x64 / ARM64).
        var arch = RuntimeInformation.ProcessArchitecture;
        var (vclibsUrl, xamlArchPath) = arch == Architecture.Arm64
            ? ("https://aka.ms/Microsoft.VCLibs.arm64.14.00.Desktop.appx", "tools/AppX/arm64/Release/Microsoft.UI.Xaml.2.8.appx")
            : ("https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx", "tools/AppX/x64/Release/Microsoft.UI.Xaml.2.8.appx");

        var vclibs = Path.Combine(dir, "VCLibs.appx");
        await DownloadAsync(vclibsUrl, vclibs, ct, log);

        var xaml = Path.Combine(dir, "UI.Xaml.appx");
        var nupkg = Path.Combine(dir, "UI.Xaml.zip");
        await DownloadAsync("https://www.nuget.org/api/v2/package/Microsoft.UI.Xaml/2.8.6", nupkg, ct, log);
        using (var zip = ZipFile.OpenRead(nupkg))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(xamlArchPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("UI.Xaml 依赖包内容不完整");
            entry.ExtractToFile(xaml, overwrite: true);
        }

        // Install per-user via PowerShell Add-AppxPackage.
        var script = Path.Combine(dir, "install.ps1");
        await File.WriteAllTextAsync(script,
            "$ErrorActionPreference = 'Stop'\n" +
            $"Add-AppxPackage -Path '{bundle}' -DependencyPath '{vclibs}','{xaml}'\n", ct);
        log("正在安装（系统可能会闪一下窗口，属于正常现象）…");
        var r = await _runner.RunAsync("powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"", ct);
        if (!r.Success)
            throw new InvalidOperationException($"系统拒绝安装（退出码 {r.ExitCode}）：{r.Tail(300)}");
    }

    private static async Task<string> FindLatestBundleUrlAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync("https://api.github.com/repos/microsoft/winget-cli/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase))
                return asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidOperationException("发布信息不完整");
        }
        throw new InvalidOperationException("官方最新版里没找到应用安装程序安装包");
    }

    private static async Task DownloadAsync(string url, string file, CancellationToken ct, Action<string>? log = null)
    {
        // One automatic retry: a single dropped connection should not fail the heal.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = await Http.GetStreamAsync(url, ct);
                await using var fs = File.Create(file);
                await stream.CopyToAsync(fs, ct);
                return;
            }
            catch (Exception) when (attempt < 2)
            {
                try { File.Delete(file); } catch { /* partial file cleanup is best-effort */ }
                log?.Invoke("下载中断，正在重试一次…");
            }
        }
    }

    /// <summary>Pick a different install root when the chosen one is unwritable or the
    /// drive is nearly full: the fixed drive with the most free space (needs ≥ 5 GB),
    /// else the user's home folder. Null when there is no better place.</summary>
    public string? SuggestFallbackRoot(string currentRoot)
    {
        string? currentDrive = null;
        try { currentDrive = Path.GetPathRoot(Path.GetFullPath(currentRoot)); } catch { /* ignore */ }

        var candidates = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Where(d => !string.Equals(d.RootDirectory.FullName, currentDrive, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.AvailableFreeSpace >= 5L * 1024 * 1024 * 1024)
            .OrderByDescending(d => d.AvailableFreeSpace)
            .ToList();
        if (candidates.Count > 0)
            return Path.Combine(candidates[0].RootDirectory.FullName, "CourseAssist");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && !home.StartsWith(currentRoot, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(home, "CourseAssist");
        return null;
    }
}
