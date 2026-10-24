using System.Diagnostics;

namespace CourseAssist.Core;

public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
    public string Tail(int maxChars = 800)
        => Output.Length <= maxChars ? Output.Trim() : Output[^maxChars..].Trim();
}

/// <summary>Runs external commands (winget, verify commands, bundle command steps).</summary>
public interface IProcessRunner
{
    /// <summary>Run a command line like <c>java -version</c> (first token is the program).</summary>
    Task<ProcessResult> RunAsync(string commandLine, CancellationToken ct = default);

    Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string commandLine, CancellationToken ct = default)
    {
        var (file, args) = Split(commandLine);
        return RunAsync(file, args, ct);
    }

    public async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Refresh PATH from the registry so tools installed minutes ago (in this same
        // session) resolve for verify commands and follow-up steps.
        if (OperatingSystem.IsWindows())
        {
            var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var merged = string.Join(";", new[] { machine, user }.Where(s => s.Length > 0));
            if (merged.Length > 0) psi.Environment["PATH"] = merged;
        }
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = (await stdout) + (await stderr);
        return new ProcessResult(process.ExitCode, output);
    }

    internal static (string File, string Args) Split(string commandLine)
    {
        var trimmed = commandLine.Trim();
        var idx = trimmed.IndexOf(' ');
        return idx < 0 ? (trimmed, "") : (trimmed[..idx], trimmed[(idx + 1)..].Trim());
    }
}

/// <summary>winget command-line construction and queries.</summary>
public static class WingetCommands
{
    public static string InstallArgs(string id, string? location)
    {
        var args = $"install --id {id} --exact --silent --accept-package-agreements --accept-source-agreements";
        if (!string.IsNullOrWhiteSpace(location)) args += $" --location \"{location}\"";
        return args;
    }

    public static string UninstallArgs(string id)
        => $"uninstall --id {id} --exact --silent --accept-source-agreements";

    public static string ListArgs(string id) => $"list --id {id} --exact --accept-source-agreements";
}

public sealed class WingetService
{
    private readonly IProcessRunner _runner;
    private bool _resolved;

    public WingetService(IProcessRunner runner) => _runner = runner;

    /// <summary>How to invoke winget: plain "winget" when it is on PATH, otherwise the
    /// full path found by <see cref="ResolveExecutableAsync"/>.</summary>
    public string Executable { get; private set; } = "winget";

    /// <summary>Probe the standard install locations for winget.exe (it can exist on the
    /// machine while missing from PATH) and remember the full path when found.</summary>
    public async Task<bool> ResolveExecutableAsync(CancellationToken ct = default)
    {
        if (_resolved) return Executable != "winget" || await ProbeAsync("winget", ct);
        _resolved = true;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && await ProbeAsync(candidate, ct))
            {
                Executable = candidate;
                return true;
            }
        }
        return false;
    }

    private async Task<bool> ProbeAsync(string exe, CancellationToken ct)
    {
        try
        {
            var r = await _runner.RunAsync(exe, "--version", ct);
            return r.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (await ProbeAsync(Executable, ct)) return true;
        // Not reachable by name — maybe it exists but is not on PATH. Self-heal the path.
        if (Executable == "winget" && await ResolveExecutableAsync(ct))
            return await ProbeAsync(Executable, ct);
        return false;
    }

    public async Task<bool> IsInstalledAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var r = await _runner.RunAsync(Executable, WingetCommands.ListArgs(id), ct);
            return r.Success && r.Output.Contains(id, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
