namespace Mmd.Modern.Effects.Neo;

/// <summary>What a declared effect parameter holds.</summary>
public enum EffectValueKind
{
    Scalar,
    Vector2,
    Vector3,
    Vector4,
    Matrix,
    Texture,
    Integer,
    Boolean,
    String,
}

/// <summary>What a texture parameter is used for; drives allocation, not sampling.</summary>
public enum EffectTextureRole
{
    /// <summary>A plain input texture supplied by the host (material map, noise, LUT...).</summary>
    Input,

    /// <summary>A colour render target allocated and managed by the effect runtime.</summary>
    RenderTarget,

    /// <summary>A depth/stencil target.</summary>
    DepthStencilTarget,
}

public enum ClearTargets
{
    None = 0,
    Color = 1,
    Depth = 2,
    Stencil = 4,
}

/// <summary>One declaration the host binds by semantic (never by name).</summary>
public sealed record EffectParameter(
    string Name,
    EffectValueKind Kind,
    string? Semantic,
    EffectTextureRole Role,
    IReadOnlyDictionary<string, string> Annotations,
    string? Initializer = null,
    string? DeclaredType = null,
    EffectValue? DefaultValue = null)
{
    public bool IsTexture => Kind == EffectValueKind.Texture;
}

/// <summary>An operation in a technique's render graph.</summary>
public sealed record EffectOperation(
    EffectOperationKind Kind,
    string? Target = null,
    int Slot = -1,
    ClearTargets ClearMask = ClearTargets.None,
    IReadOnlyList<EffectOperation>? Body = null,
    string? Source = null)
{
    public static EffectOperation RenderTarget(int slot, string texture) =>
        new(EffectOperationKind.SetRenderTarget, texture, slot);

    public static EffectOperation DepthStencil(string texture) =>
        new(EffectOperationKind.SetDepthStencilTarget, texture);

    public static EffectOperation ClearColor(string parameter) =>
        new(EffectOperationKind.ClearColor, parameter);

    public static EffectOperation ClearDepth(string parameter) =>
        new(EffectOperationKind.ClearDepth, parameter);

    public static EffectOperation ClearBuffers(ClearTargets targets) =>
        new(EffectOperationKind.Clear, null, -1, targets);

    public static EffectOperation Scene() => new(EffectOperationKind.DrawScene);

    public static EffectOperation RunPass(string name) =>
        new(EffectOperationKind.RunPass, name);

    public static EffectOperation Loop(string count, string index, IReadOnlyList<EffectOperation> body) =>
        new(EffectOperationKind.Loop, count, -1, ClearTargets.None, body, index);
}

public enum EffectOperationKind
{
    SetRenderTarget,
    SetDepthStencilTarget,
    ClearColor,
    ClearDepth,
    Clear,
    DrawScene,
    RunPass,
    Loop,
    SetLoopIndex,
}

/// <summary>A pass: one shader program plus fixed-function state.</summary>
public sealed record EffectPass(
    string Name,
    string? VertexShader,
    string? PixelShader,
    IReadOnlyList<string> Profiles,
    IReadOnlyDictionary<string, string> States);

/// <summary>A technique: a graph plus the passes it runs, tagged with an MMD dispatch stage.</summary>
public sealed record EffectTechnique(
    string Name,
    string? MmdPass,
    IReadOnlyDictionary<string, string> Annotations,
    IReadOnlyList<EffectPass> Passes,
    IReadOnlyList<EffectOperation> Graph)
{
    public EffectPass? FindPass(string name) =>
        Passes.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A complete effect: parameters plus techniques.</summary>
public sealed record EffectDefinition(
    string Name,
    IReadOnlyList<EffectParameter> Parameters,
    IReadOnlyList<EffectTechnique> Techniques,
    string? SourcePath = null)
{
    public string Source { get; init; } = string.Empty;
    public string OriginalSource { get; init; } = string.Empty;
    public string? SourceSha256 { get; init; }
    public string? OriginalSourceSha256 { get; init; }
    public IReadOnlyList<string> Includes { get; init; } = [];
    public IReadOnlyList<MmeDiagnostic> Diagnostics { get; init; } = [];
    public bool IsExecutable => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);

    public EffectParameter? FindParameter(string name) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public EffectTechnique? FindTechnique(string name) =>
        Techniques.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
