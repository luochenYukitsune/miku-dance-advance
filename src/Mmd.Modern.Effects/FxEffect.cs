namespace Mmd.Modern.Effects;

/// <summary>A top-level effect parameter: uniform, texture or sampler.</summary>
public sealed record FxParameter(
    string Type,
    string Name,
    string? Semantic,
    IReadOnlyDictionary<string, string> Annotations,
    string? Initializer)
{
    public bool IsTexture => Type.StartsWith("texture", StringComparison.OrdinalIgnoreCase);

    public bool IsSampler => Type.StartsWith("sampler", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One pass of a technique: the unit MME's <c>Pass=</c> script command selects.</summary>
public sealed record FxPass(
    string Name,
    IReadOnlyDictionary<string, string> Annotations,
    IReadOnlyDictionary<string, string> States,
    IReadOnlyList<string> Profiles)
{
    /// <summary>Entry point of <c>VertexShader = compile vs_3_0 Main();</c>, or null.</summary>
    public string? VertexEntry => ExtractEntry("VertexShader");

    /// <summary>Entry point of <c>PixelShader = compile ps_3_0 Main();</c>, or null.</summary>
    public string? PixelEntry => ExtractEntry("PixelShader");

    private string? ExtractEntry(string stateName)
    {
        if (!States.TryGetValue(stateName, out var value))
        {
            return null;
        }

        int compile = value.IndexOf("compile", StringComparison.OrdinalIgnoreCase);
        if (compile < 0)
        {
            return null;
        }

        var tokens = value[(compile + "compile".Length)..]
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // tokens: [profile, entry] e.g. ["ps_3_0", "PSMain()"]
        return tokens.Length >= 2 ? tokens[1] : null;
    }
}

/// <summary>One technique; <see cref="MmdPass"/> is the MME dispatch key.</summary>
public sealed record FxTechnique(
    string Name,
    IReadOnlyDictionary<string, string> Annotations,
    IReadOnlyList<FxPass> Passes)
{
    public string? MmdPass => Annotations.TryGetValue("MMDPass", out var v) ? v : null;

    public string? Script => Annotations.TryGetValue("Script", out var v) ? v : null;

    public bool TryGetAnnotation(string name, out string value) => Annotations.TryGetValue(name, out value!);

    public FxPass? FindPass(string name) =>
        Passes.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A parsed effect source file.</summary>
public sealed record FxEffect(
    string Path,
    IReadOnlyList<FxParameter> Parameters,
    IReadOnlyList<FxTechnique> Techniques,
    IReadOnlyList<string> Includes)
{
    /// <summary>
    /// Preprocessed source text, kept because compiling a pass needs the shader bodies and the
    /// sampler/global declarations that the structural parse deliberately skips.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Decoded root before preprocessing; Source includes expanded includes.</summary>
    public string? OriginalSource { get; init; }

    public IEnumerable<FxParameter> Textures => Parameters.Where(p => p.IsTexture);

    public FxParameter? FindParameter(string name) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public FxTechnique? FindTechnique(string name) =>
        Techniques.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
