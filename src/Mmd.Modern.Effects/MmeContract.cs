namespace Mmd.Modern.Effects;

/// <summary>
/// Where a contract item was observed. Nothing enters the contract without evidence.
/// </summary>
public enum ContractSource
{
    /// <summary>String literal recovered from the shipped <c>MMEffect.dll</c> (see tools/mme_strings.py).</summary>
    MmeBinary,

    /// <summary>Observed in the real effect corpus under <c>D:\desktop\mmd渲染</c>.</summary>
    Corpus,

    /// <summary>Standard fx_2_0 effect semantics defined by D3DX itself, not by MME.</summary>
    DxStandard,
}

public sealed record ContractItem(string Name, ContractSource Source);

/// <summary>
/// The MME compatibility contract: the names an effect may legally use.
/// </summary>
/// <remarks>
/// Binding is by <b>semantic</b>, not by parameter name: <c>MMEffect.dll</c> imports
/// <c>D3DXGetShaderInputSemantics</c>, so <c>float4x4 WVP : WORLDVIEWPROJECTION;</c> binds no
/// matter what the parameter is called. The names below are therefore the authoritative
/// identifier set, matched case-insensitively (the binary compares against a lowercased
/// table such as <c>rendercolortarget0</c>).
/// </remarks>
public static class MmeContract
{
    /// <summary>Parameter/global semantics MME can fill.</summary>
    public static readonly IReadOnlyList<ContractItem> Semantics = Build(
        ContractSource.MmeBinary,
        // Matrix family: every combination MME exposes.
        "World", "WorldInverse", "WorldTranspose", "WorldInverseTranspose",
        "View", "ViewInverse", "ViewTranspose", "ViewInverseTranspose",
        "Projection", "ProjectionInverse", "ProjectionTranspose", "ProjectionInverseTranspose",
        "ViewProjection", "ViewProjectionInverse", "ViewProjectionTranspose", "ViewProjectionInverseTranspose",
        "WorldView", "WorldViewInverse", "WorldViewTranspose", "WorldViewInverseTranspose",
        "WorldViewProjection", "WorldViewProjectionInverse", "WorldViewProjectionTranspose",
        "WorldViewProjectionInverseTranspose",
        // Material inputs.
        "MaterialTexture", "MaterialSphereMap", "MaterialToonTexture",
        "ToonColor", "EdgeColor", "GroundShadowColor",
        "AddingTexture", "AddingSphereTexture", "MultiplyingTexture", "MultiplyingSphereTexture",
        // Frame state.
        "Position", "LightDir", "Time", "Time2", "VertexCount",
        "ViewportPixelSize", "ViewportRatio", "VERTEXDATA");

    /// <summary>Semantics carried by texture declarations themselves; MME allocates the target from them.</summary>
    public static readonly IReadOnlyList<ContractItem> ResourceSemantics = Build(
        ContractSource.Corpus,
        "RENDERCOLORTARGET", "RENDERDEPTHSTENCILTARGET", "OFFSCREENRENDERTARGET");

    /// <summary>
    /// fx_2_0 standard effect semantics. D3DX defines these, so they bind through the same
    /// mechanism as MME's own names and must never be rejected.
    /// </summary>
    public static readonly IReadOnlyList<ContractItem> StandardEffectSemantics = Build(
        ContractSource.DxStandard,
        "DIFFUSE", "SPECULAR", "AMBIENT", "EMISSIVE", "SPECULARPOWER", "TEXTURE", "DIRECTION");

    /// <summary>MMD-side effect extensions the corpus relies on.</summary>
    public static readonly IReadOnlyList<ContractItem> MmdExtensionSemantics = Build(
        ContractSource.Corpus,
        "STANDARDSGLOBAL", "CONTROLOBJECT", "ADDINGSPHERE", "MULTIPLYINGSPHERE");

    /// <summary>Semantics that only ever appear in the corpus, not in the shipped binary's string table.</summary>
    /// <remarks>
    /// These are MMD's own vertex/pass inputs that MME forwards. <c>EDGEWIDTH</c> and
    /// <c>_INDEX</c> live in MMD's vertex declaration, so reproducing them is a renderer
    /// obligation, not an MME one. Kept separate so the gap stays visible.
    /// </remarks>
    public static readonly IReadOnlyList<ContractItem> MmdProvidedSemantics = Build(
        ContractSource.Corpus,
        "EDGEWIDTH", "_INDEX", "VERTEXINDEXOFFSET");

    /// <summary>Technique annotation names.</summary>
    public static readonly IReadOnlyList<ContractItem> TechniqueAnnotations = Build(
        ContractSource.MmeBinary,
        "MMDPass", "UseTexture", "UseSpheremap", "UseToon", "Subset", "SubsetCount", "Script");

    /// <summary>Annotation names seen in the corpus but not recovered from the binary string table.</summary>
    public static readonly IReadOnlyList<ContractItem> CorpusTechniqueAnnotations = Build(
        ContractSource.Corpus,
        "UseSelfShadow");

    /// <summary>Script render-graph commands.</summary>
    public static readonly IReadOnlyList<ContractItem> ScriptCommands = Build(
        ContractSource.MmeBinary,
        "RenderColorTarget0", "RenderColorTarget1", "RenderColorTarget2", "RenderColorTarget3",
        "RenderColorTarget", "RenderDepthStencilTarget",
        "ClearSetColor", "ClearSetDepth",
        "Clear", "Pass", "ScriptExternal",
        // Loop control, observed in AutoLuminous; the binary reports
        // "type of loop count must be 'int' or 'bool'" / "type of loop index must be 'int' or 'bool'".
        "LoopByCount", "LoopGetIndex", "LoopEnd");

    /// <summary>MMDPass values seen in a real corpus. MME has no enumeration; unknown values are warned about.</summary>
    public static readonly IReadOnlyList<ContractItem> MmdPassValues = Build(
        ContractSource.Corpus,
        "object", "object_ss", "object_t", "edge", "shadow", "zplot");

    /// <summary>Clear targets accepted by <c>Clear=</c>.</summary>
    public static readonly IReadOnlyList<string> ClearTargets = ["color", "depth", "stencil"];

    /// <summary>ScriptExternal targets accepted by <c>ScriptExternal=</c>.</summary>
    public static readonly IReadOnlyList<string> ScriptExternalTargets = ["color"];

    private static readonly HashSet<string> SemanticsSet = Set(Semantics);
    private static readonly HashSet<string> MmdSemanticsSet = Set(MmdProvidedSemantics);
    private static readonly HashSet<string> ResourceSemanticsSet = Set(ResourceSemantics);
    private static readonly HashSet<string> StandardSemanticsSet = Set(StandardEffectSemantics);
    private static readonly HashSet<string> ExtensionSemanticsSet = Set(MmdExtensionSemantics);
    private static readonly HashSet<string> CommandsSet = Set(ScriptCommands);

    public static bool IsKnownSemantic(string name) =>
        SemanticsSet.Contains(name)
        || MmdSemanticsSet.Contains(name)
        || ResourceSemanticsSet.Contains(name)
        || StandardSemanticsSet.Contains(name)
        || ExtensionSemanticsSet.Contains(name);

    public static bool IsKnownScriptCommand(string name) => CommandsSet.Contains(name);

    private static ContractItem[] Build(ContractSource source, params string[] names) =>
        names.Select(n => new ContractItem(n, source)).ToArray();

    private static HashSet<string> Set(IReadOnlyList<ContractItem> items) =>
        new(items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
}
