namespace Mmd.Modern.Effects.Compilation;

/// <summary>CPU upload layout of b0. Matrices are row-major; bool occupies four bytes.</summary>
public sealed record EffectUniformBinding(string Name, string Type, int ByteOffset, int ByteSize,
    string? Semantic, string? Initializer);

public sealed record EffectTextureBinding(string Name, int Slot, string? Semantic);

/// <summary>A separate sampler register and its legacy texture association.</summary>
public sealed record EffectSamplerBinding(string Name, int Slot, string TextureName,
    string MinFilter, string MagFilter, string MipFilter, string AddressU, string AddressV);

/// <summary>Actual wrapper entry names, original source identities, and validated render states.</summary>
public sealed record EffectTranslatedPass(string Name, string VertexEntry, string PixelEntry,
    string OriginalVertexEntry, string OriginalPixelEntry, IReadOnlyDictionary<string, string> States);

/// <summary>
/// A bounded legacy FX lowering result, not a claim that HLSL compiled or executed. Backends must
/// compile every selected stage, reflect active bindings, and reject unsupported backend states.
/// </summary>
public sealed record EffectShaderTranslation(string Source, string SourceSha256, int UniformBufferSize,
    IReadOnlyList<EffectUniformBinding> UniformBindings, IReadOnlyList<EffectTextureBinding> Textures,
    IReadOnlyList<EffectSamplerBinding> Samplers, IReadOnlyList<EffectTranslatedPass> Passes,
    IReadOnlyList<string> Diagnostics)
{
    public bool Success => Diagnostics.Count == 0 && Passes.Count != 0;
}
