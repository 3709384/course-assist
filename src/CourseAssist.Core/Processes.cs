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
    public WingetService(IProcessRunner runner) => _runner = runner;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _runner.RunAsync("winget", "--version", ct);
            return r.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsInstalledAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var r = await _runner.RunAsync("winget", WingetCommands.ListArgs(id), ct);
            return r.Success && r.Output.Contains(id, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
