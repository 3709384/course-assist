using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace CourseAssist.Core;

/// <summary>One course bundle (bundles/&lt;id&gt;.yaml).</summary>
public sealed class CourseBundle
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Summary { get; set; }
    public string Status { get; set; } = "draft";
    public List<ToolDef> Tools { get; set; } = new();
    public List<BundleStep> Steps { get; set; } = new();
    public string? Notes { get; set; }
}

public sealed class ToolDef
{
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? Winget { get; set; }
    public DownloadDef? Download { get; set; }
    public string? Manual { get; set; }
    public string? Version { get; set; }
    public VerifyDef? Verify { get; set; }
    public string? DefaultVersion { get; set; }
    public List<VersionDef> Versions { get; set; } = new();
    public EnvDef? Env { get; set; }
    public string? InstallSubdir { get; set; }
    public GuideDef? Guide { get; set; }
    /// <summary>Start-Menu shortcut search hint used to stock the desktop course folder
    /// after install. "none" means: command-line/service tool, do not create one.</summary>
    public string? Shortcut { get; set; }

    /// <summary>Resolve the effective install source for a chosen version label (or the bundle default).</summary>
    public ResolvedTool Resolve(string? versionLabel)
    {
        if (Versions.Count > 0)
        {
            var label = versionLabel ?? DefaultVersion ?? Versions[0].Label;
            var v = Versions.FirstOrDefault(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase))
                    ?? Versions[0];
            return new ResolvedTool(Name, Purpose, Required, v.Label,
                v.Winget, v.Download, v.Manual, v.Verify ?? Verify, Env, InstallSubdir,
                v.Guide ?? Guide);
        }
        return new ResolvedTool(Name, Purpose, Required, null,
            Winget, Download, Manual, Verify, Env, InstallSubdir, Guide);
    }
}

public sealed class VersionDef
{
    public string Label { get; set; } = "";
    public string? Winget { get; set; }
    public DownloadDef? Download { get; set; }
    public string? Manual { get; set; }
    public VerifyDef? Verify { get; set; }
    public GuideDef? Guide { get; set; }
}

/// <summary>Beginner tutorial for a tool (or for one specific version of it).
/// Version-consistency rule: a guide attached to a version entry must describe exactly
/// that version, and its official-doc link must point at that version's documentation;
/// guides for evergreen tools link to the vendor's current docs instead.</summary>
public sealed class GuideDef
{
    /// <summary>Official tutorial/docs URL, version-matched (see rule above).</summary>
    public string? TutorialUrl { get; set; }
    public string? TutorialTitle { get; set; }
    /// <summary>Schematic diagram kind rendered by the app: editor | ide | database | terminal | vm | browser | generic.</summary>
    public string Diagram { get; set; } = "generic";
    /// <summary>First-use steps, written against the exact version this guide belongs to.</summary>
    public List<string> Steps { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class DownloadDef
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class VerifyDef
{
    public string Command { get; set; } = "";
    public string? Expect { get; set; }
}

/// <summary>
/// Environment variables a tool needs. Arbitrary variable names become <see cref="Vars"/>;
/// the reserved key <c>path_additions</c> is a list of PATH entries. Values may contain
/// the <c>{install_dir}</c> placeholder, substituted with the tool's actual install path.
/// </summary>
public sealed class EnvDef : IYamlConvertible
{
    public Dictionary<string, string> Vars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PathAdditions { get; set; } = new();

    public bool IsEmpty => Vars.Count == 0 && PathAdditions.Count == 0;

    public void Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
    {
        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            if (string.Equals(key, "path_additions", StringComparison.OrdinalIgnoreCase))
            {
                parser.Consume<SequenceStart>();
                while (!parser.TryConsume<SequenceEnd>(out _))
                    PathAdditions.Add(parser.Consume<Scalar>().Value);
            }
            else
            {
                Vars[key] = parser.Consume<Scalar>().Value;
            }
        }
    }

    public void Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer)
        => throw new NotSupportedException("Serializing bundles is not supported.");
}

public sealed class BundleStep
{
    /// <summary>env | command | feature | manual</summary>
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? Run { get; set; }
    public string? Title { get; set; }
}

/// <summary>A tool with its version choice resolved to a concrete install source.</summary>
public sealed record ResolvedTool(
    string Name,
    string Purpose,
    bool Required,
    string? VersionLabel,
    string? Winget,
    DownloadDef? Download,
    string? Manual,
    VerifyDef? Verify,
    EnvDef? Env,
    string? InstallSubdir,
    GuideDef? Guide);

public sealed class Catalog
{
    public List<CatalogCategory> Categories { get; set; } = new();
}

public sealed class CatalogCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<CatalogCourse> Courses { get; set; } = new();
}

public sealed class CatalogCourse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "planned";
}
