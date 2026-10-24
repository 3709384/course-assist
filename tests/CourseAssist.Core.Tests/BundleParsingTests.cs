using CourseAssist.Core;

namespace CourseAssist.Core.Tests;

public class BundleParsingTests
{
    private static string FindBundlesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "bundles");
            if (File.Exists(Path.Combine(candidate, "catalog.yaml"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到 bundles 目录");
    }

    private static BundleRepository Repo => new(FindBundlesDir());

    [Fact]
    public void LoadsAllBundles()
    {
        var bundles = Repo.LoadBundles();
        Assert.Equal(21, bundles.Count);
        Assert.All(bundles, b => Assert.False(string.IsNullOrWhiteSpace(b.Id)));
        Assert.All(bundles, b => Assert.NotEmpty(b.Tools));
    }

    [Fact]
    public void JavaBundle_HasMultiVersionJdkWithEnv()
    {
        var java = Repo.LoadBundles().Single(b => b.Id == "java-programming");
        var jdk = java.Tools.Single(t => t.Name == "JDK");

        Assert.Equal(4, jdk.Versions.Count);
        Assert.Equal("17", jdk.DefaultVersion);
        Assert.Equal("Microsoft.OpenJDK.17", jdk.Resolve(null).Winget);
        Assert.Equal("EclipseAdoptium.Temurin.8.JDK", jdk.Resolve("8").Winget);
        Assert.Equal("Microsoft.OpenJDK.21", jdk.Resolve("21").Winget);

        Assert.NotNull(jdk.Env);
        Assert.Equal("{install_dir}", jdk.Env!.Vars["JAVA_HOME"]);
        Assert.Equal(["{install_dir}\\bin"], jdk.Env.PathAdditions);
    }

    [Fact]
    public void PythonBundle_HasPipMirrorStep()
    {
        var python = Repo.LoadBundles().Single(b => b.Id == "python-programming");
        Assert.Contains(python.Steps, s => s.Type == "command" && (s.Run?.Contains("pip config") ?? false));
        Assert.Contains(python.Tools, t => t.Winget == "Python.Python.3.12");
    }

    [Fact]
    public void Catalog_CoversAllPlannedCourses_AndMatchesBundles()
    {
        var catalog = Repo.LoadCatalog();
        Assert.NotNull(catalog);
        Assert.Equal(5, catalog!.Categories.Count);
        var courses = catalog.Categories.SelectMany(c => c.Courses).ToList();
        Assert.Equal(21, courses.Count);
        Assert.Equal(21, courses.Count(c => c.Status == "draft"));

        var bundleIds = Repo.LoadBundles().Select(b => b.Id).ToHashSet();
        Assert.All(bundleIds, id => Assert.Contains(courses, c => c.Id == id));
    }
}
