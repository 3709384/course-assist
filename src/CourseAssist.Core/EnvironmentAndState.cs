using System.Text.Json;

namespace CourseAssist.Core;

/// <summary>User-level environment variables (PATH is the variable named "PATH").</summary>
public interface IEnvironmentStore
{
    string? GetVariable(string name);
    void SetVariable(string name, string? value);
}

public sealed class SystemEnvironmentStore : IEnvironmentStore
{
    public string? GetVariable(string name)
        => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    public void SetVariable(string name, string? value)
        => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
}

public sealed class InMemoryEnvironmentStore : IEnvironmentStore
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);
    public string? GetVariable(string name) => _vars.TryGetValue(name, out var v) ? v : null;
    public void SetVariable(string name, string? value)
    {
        if (value is null) _vars.Remove(name);
        else _vars[name] = value;
    }
}

/// <summary>
/// Applies a tool's environment requirements, recording original values into a backup
/// dictionary so the whole change can be rolled back later.
/// </summary>
public sealed class EnvManager
{
    public const string InstallDirPlaceholder = "{install_dir}";

    private readonly IEnvironmentStore _store;
    public EnvManager(IEnvironmentStore store) => _store = store;

    public List<string> Apply(EnvDef env, string installDir, IDictionary<string, string?> backup)
    {
        var changed = new List<string>();
        foreach (var (name, rawValue) in env.Vars)
        {
            var value = rawValue.Replace(InstallDirPlaceholder, installDir);
            if (!backup.ContainsKey(name)) backup[name] = _store.GetVariable(name);
            _store.SetVariable(name, value);
            changed.Add(name);
        }
        if (env.PathAdditions.Count > 0)
        {
            if (!backup.ContainsKey("PATH")) backup["PATH"] = _store.GetVariable("PATH");
            var current = _store.GetVariable("PATH") ?? "";
            var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var raw in env.PathAdditions)
            {
                var entry = raw.Replace(InstallDirPlaceholder, installDir);
                if (!entries.Contains(entry, StringComparer.OrdinalIgnoreCase)) entries.Add(entry);
            }
            _store.SetVariable("PATH", string.Join(";", entries));
            changed.Add("PATH");
        }
        return changed;
    }

    public void Restore(IDictionary<string, string?> backup)
    {
        foreach (var (name, value) in backup) _store.SetVariable(name, value);
    }
}

/// <summary>One recorded installation, kept so uninstall/restore and resume can work.</summary>
public sealed class InstallRecord
{
    public string CourseId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string? VersionLabel { get; set; }
    public string? WingetId { get; set; }
    public string? InstallDir { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    public Dictionary<string, string?> EnvBackup { get; set; } = new();
}

public sealed class AppState
{
    public List<InstallRecord> Installs { get; set; } = new();
}

/// <summary>JSON persistence for install records (default: %LOCALAPPDATA%\CourseAssist\state.json).</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    public StateStore(string path) => _path = path;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CourseAssist", "state.json");

    public AppState Load()
    {
        if (!File.Exists(_path)) return new AppState();
        try
        {
            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path)) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
    }
}
