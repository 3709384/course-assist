using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CourseAssist.Core;

/// <summary>Loads the course catalog and bundle files from a bundles directory.</summary>
public sealed class BundleRepository
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly string _directory;

    public BundleRepository(string directory) => _directory = directory;

    public string DirectoryPath => _directory;

    public Catalog? LoadCatalog()
    {
        var path = Path.Combine(_directory, "catalog.yaml");
        if (!File.Exists(path)) return null;
        return Deserializer.Deserialize<Catalog>(File.ReadAllText(path));
    }

    public IReadOnlyList<CourseBundle> LoadBundles()
    {
        var bundles = new List<CourseBundle>();
        if (!Directory.Exists(_directory)) return bundles;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.yaml").OrderBy(f => f))
        {
            if (string.Equals(Path.GetFileName(file), "catalog.yaml", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var bundle = Deserializer.Deserialize<CourseBundle>(File.ReadAllText(file));
                if (!string.IsNullOrWhiteSpace(bundle.Id)) bundles.Add(bundle);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"课程包文件解析失败：{Path.GetFileName(file)}：{ex.Message}", ex);
            }
        }
        return bundles;
    }
}
