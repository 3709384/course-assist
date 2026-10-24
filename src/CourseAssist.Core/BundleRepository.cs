using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CourseAssist.Core;

/// <summary>Loads the course catalog and bundle files (from a directory, or from YAML texts
/// supplied by the caller — e.g. resources embedded in the app executable).</summary>
public sealed class BundleRepository
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly string _directory;

    public BundleRepository(string directory) => _directory = directory;

    public string DirectoryPath => _directory;

    /// <summary>Per-file parse problems from the last LoadBundles call (bad files are skipped, not fatal).</summary>
    public List<string> LastLoadErrors { get; } = new();

    public static Catalog? ParseCatalog(string yaml) =>
        string.IsNullOrWhiteSpace(yaml) ? null : Deserializer.Deserialize<Catalog>(yaml);

    public static CourseBundle? ParseBundle(string yaml)
    {
        var bundle = Deserializer.Deserialize<CourseBundle>(yaml);
        return string.IsNullOrWhiteSpace(bundle?.Id) ? null : bundle;
    }

    public Catalog? LoadCatalog()
    {
        var path = Path.Combine(_directory, "catalog.yaml");
        if (!File.Exists(path)) return null;
        return ParseCatalog(File.ReadAllText(path));
    }

    public IReadOnlyList<CourseBundle> LoadBundles()
    {
        var bundles = new List<CourseBundle>();
        LastLoadErrors.Clear();
        if (!Directory.Exists(_directory)) return bundles;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.yaml").OrderBy(f => f))
        {
            if (string.Equals(Path.GetFileName(file), "catalog.yaml", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var bundle = ParseBundle(File.ReadAllText(file));
                if (bundle is not null) bundles.Add(bundle);
            }
            catch (Exception ex)
            {
                LastLoadErrors.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }
        return bundles;
    }

    /// <summary>Parse many bundle YAML texts (e.g. embedded resources). Bad ones are skipped and recorded.</summary>
    public IReadOnlyList<CourseBundle> ParseBundles(IEnumerable<(string Name, string Yaml)> sources)
    {
        var bundles = new List<CourseBundle>();
        LastLoadErrors.Clear();
        foreach (var (name, yaml) in sources)
        {
            try
            {
                var bundle = ParseBundle(yaml);
                if (bundle is not null) bundles.Add(bundle);
            }
            catch (Exception ex)
            {
                LastLoadErrors.Add($"{name}：{ex.Message}");
            }
        }
        return bundles;
    }
}
