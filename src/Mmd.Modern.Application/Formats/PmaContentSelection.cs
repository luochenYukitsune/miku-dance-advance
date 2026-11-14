namespace Mmd.Modern.Application;

/// <summary>Immutable, renderer-neutral package content constraints. Save applies these constraints to a detached
/// capture; load rejects content outside them before invoking host model loaders or constructing physics.
/// Unknown optional data is retained unless explicitly excluded.</summary>
public sealed class PmaContentSelection
{
    public IReadOnlyList<string> ExcludedExtensionNames { get; }
    public IReadOnlyList<string> ExcludedResourcePrefixes { get; }
    public bool RetainNativeLighting { get; }
    public bool RetainModelPurposes { get; }
    public bool RetainRenderingStyles { get; }

    /// <param name="excludedExtensionNames">Exact, case-sensitive namespaced extension identities.</param>
    /// <param name="excludedResourcePrefixes">Canonical directory prefixes under render-resources/, including
    /// the trailing slash. Matching is case-insensitive, consistently with logical resource identities.</param>
    public PmaContentSelection(IEnumerable<string>? excludedExtensionNames = null, IEnumerable<string>? excludedResourcePrefixes = null,
        bool retainNativeLighting = true, bool retainModelPurposes = true, bool retainRenderingStyles = true)
    {
        var names = (excludedExtensionNames ?? []).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string name in names)
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || !name.Contains('.')
                || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
                throw new InvalidDataException("PMA excluded extension names must be namespaced ASCII identifiers.");
        var prefixes = (excludedResourcePrefixes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string prefix in prefixes)
        {
            if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith('/'))
                throw new InvalidDataException("PMA excluded resource prefixes must be canonical directories ending in '/'.");
            PmaAdditionalResources.ValidatePath(prefix + "_");
        }
        ExcludedExtensionNames = Array.AsReadOnly(names);
        ExcludedResourcePrefixes = Array.AsReadOnly(prefixes);
        RetainNativeLighting = retainNativeLighting;
        RetainModelPurposes = retainModelPurposes;
        RetainRenderingStyles = retainRenderingStyles;
    }

    internal PmaContentSelection Copy() => new(ExcludedExtensionNames, ExcludedResourcePrefixes,
        RetainNativeLighting, RetainModelPurposes, RetainRenderingStyles);

    /// <summary>Tests a validated optional-extension identity so a consuming codec can avoid copying excluded data.</summary>
    public bool ExcludesExtension(string name)
        => (!RetainRenderingStyles && name == SceneRenderingStyles.ExtensionName)
            || ExcludedExtensionNames.Contains(name, StringComparer.Ordinal);

    /// <summary>Tests a logical resource identity so consuming codecs can apply the same selection before byte copies.</summary>
    public bool ExcludesResource(string path)
        => ExcludedResourcePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal PmaProjectInputs Project(PmaProjectInputs source)
    {
        if (RetainNativeLighting && RetainModelPurposes && RetainRenderingStyles) return source;
        var composition = RetainModelPurposes ? source.Composition : SceneCompositionEditor.FromObjects(
            source.Composition.Objects.Select(item => item with { Role = ModelRole.Unspecified }), source.Composition.SelectedId);
        var lighting = new SceneLightingEditor();
        if (RetainNativeLighting)
        {
            foreach (var light in source.Lighting.Lights) lighting.Add(light);
            lighting.Select(source.Lighting.SelectedId);
        }
        if (RetainModelPurposes)
            foreach (var pair in source.Lighting.ModelRoles)
                lighting.SetModelRole(pair.Key, pair.Value.Role, pair.Value.EnvironmentIndoor);
        return source with
        {
            Composition = composition,
            Lighting = lighting,
            Animation = source.Animation with { Objects = Array.AsReadOnly(composition.Objects.ToArray()), Lighting = lighting.Snapshot },
            RenderingStyles = RetainRenderingStyles ? source.RenderingStyles : PmaRenderingStyleState.Default,
        };
    }
}
