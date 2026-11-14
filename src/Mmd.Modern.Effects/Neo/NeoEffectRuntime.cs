using System.Numerics;

namespace Mmd.Modern.Effects.Neo;

/// <summary>A render target an effect asked for, with the size/format its annotations requested.</summary>
public sealed record EffectTextureAllocation(
    string Name,
    EffectTextureRole Role,
    EffectRenderTargetFormat Format,
    Vector2 ViewportRatio,
    int MipLevels);

/// <summary>One concrete command after loops are unrolled and parameters resolved.</summary>
public sealed record EffectStep(
    EffectOperationKind Kind,
    string? Texture = null,
    int Slot = -1,
    Vector4 ClearColor = default,
    float ClearDepth = 1f,
    ClearTargets ClearMask = ClearTargets.None,
    EffectPass? Pass = null,
    string? LoopIndexParameter = null,
    int LoopIndex = 0);

public sealed record EffectExecutionPlan(
    EffectTechnique Technique,
    IReadOnlyList<EffectStep> Steps,
    IReadOnlyList<EffectTextureAllocation> Textures)
{
    public IReadOnlyList<MmeDiagnostic> Diagnostics { get; init; } = [];
    public bool IsExecutable => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
    public int Count(EffectOperationKind kind) => Steps.Count(s => s.Kind == kind);
}

/// <summary>Backend surface the runtime drives. Implemented by the modern renderer and by tests.</summary>
public interface IEffectDevice
{
    void SetRenderTarget(int slot, EffectTextureAllocation? texture);

    void SetDepthStencilTarget(EffectTextureAllocation? texture);

    void ClearColor(Vector4 color);

    void ClearDepth(float depth);

    void Clear(ClearTargets targets);

    void DrawScene();

    void DrawPass(EffectPass pass);
}

/// <summary>
/// Executes an <see cref="EffectDefinition"/> against any backend.
/// </summary>
/// <remarks>
/// Planning is separated from execution on purpose: a plan can be produced, validated and asserted
/// without a GPU (which is how the compatibility claims are tested), while execution stays a thin
/// loop over steps. Loops are unrolled at plan time because MME's loop count is a parameter value,
/// not a shader-side construct.
/// </remarks>
public sealed class NeoEffectRuntime
{
    private readonly EffectDefinition _definition;
    private readonly EffectParameterValues _values;
    private readonly ICollection<MmeDiagnostic> _diagnostics;

    public NeoEffectRuntime(
        EffectDefinition definition,
        EffectParameterValues values,
        ICollection<MmeDiagnostic> diagnostics)
    {
        _definition = definition;
        _values = values;
        _diagnostics = diagnostics;
    }

    public EffectExecutionPlan Plan(EffectTechnique technique)
    {
        var steps = new List<EffectStep>();
        var textures = new List<EffectTextureAllocation>();
        if (!_definition.IsExecutable)
        {
            foreach (var diagnostic in _definition.Diagnostics) _diagnostics.Add(diagnostic);
            return new EffectExecutionPlan(technique, steps, textures) { Diagnostics = _definition.Diagnostics.ToArray() };
        }
        int before = _diagnostics.Count;
        var planningValues = new EffectParameterValues();
        foreach (var pair in _values.All) planningValues.Set(pair.Key, pair.Value);
        Emit(technique, technique.Graph, steps, textures, null, 0, planningValues);
        return new EffectExecutionPlan(technique, steps, textures)
        {
            Diagnostics = _definition.Diagnostics.Concat(_diagnostics.Skip(before)).ToArray(),
        };
    }

    public void Execute(EffectExecutionPlan plan, IEffectDevice device)
    {
        if (!plan.IsExecutable || !_definition.IsExecutable)
            throw new InvalidOperationException("Cannot execute an effect plan with errors; inspect its diagnostics.");
        var allocations = plan.Textures.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var step in plan.Steps)
        {
            if (step.LoopIndexParameter is { Length: > 0 } index)
            {
                SetIndex(_values, index, step.LoopIndex);
            }

            switch (step.Kind)
            {
                case EffectOperationKind.SetRenderTarget:
                    device.SetRenderTarget(step.Slot, Resolve(step.Texture));
                    break;
                case EffectOperationKind.SetDepthStencilTarget:
                    device.SetDepthStencilTarget(Resolve(step.Texture));
                    break;
                case EffectOperationKind.ClearColor:
                    device.ClearColor(step.ClearColor);
                    break;
                case EffectOperationKind.ClearDepth:
                    device.ClearDepth(step.ClearDepth);
                    break;
                case EffectOperationKind.Clear:
                    device.Clear(step.ClearMask);
                    break;
                case EffectOperationKind.DrawScene:
                    device.DrawScene();
                    break;
                case EffectOperationKind.RunPass:
                    device.DrawPass(step.Pass!);
                    break;
                case EffectOperationKind.Loop:
                case EffectOperationKind.SetLoopIndex:
                    break;
                default:
                    break;
            }
        }

        EffectTextureAllocation? Resolve(string? name) =>
            name is not null && allocations.TryGetValue(name, out var allocation) ? allocation : null;
    }

    private void Emit(
        EffectTechnique technique,
        IReadOnlyList<EffectOperation> operations,
        List<EffectStep> steps,
        List<EffectTextureAllocation> textures,
        string? loopIndexParameter,
        int loopIndex,
        EffectParameterValues planningValues)
    {
        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case EffectOperationKind.SetRenderTarget:
                    AddTexture(textures, operation.Target, EffectTextureRole.RenderTarget);
                    steps.Add(new EffectStep(EffectOperationKind.SetRenderTarget, operation.Target, operation.Slot,
                        LoopIndexParameter: loopIndexParameter, LoopIndex: loopIndex));
                    break;

                case EffectOperationKind.SetDepthStencilTarget:
                    AddTexture(textures, operation.Target, EffectTextureRole.DepthStencilTarget);
                    steps.Add(new EffectStep(EffectOperationKind.SetDepthStencilTarget, operation.Target,
                        LoopIndexParameter: loopIndexParameter, LoopIndex: loopIndex));
                    break;

                case EffectOperationKind.ClearColor:
                    {
                        Vector4 color = default;
                        if (operation.Target is null)
                        {
                            Error(technique, "ClearSetColor without a parameter");
                        }
                        else if (planningValues.TryGet(operation.Target, out var value))
                        {
                            color = value.AsVector4();
                        }
                        else
                        {
                            Error(technique, $"clear colour parameter '{operation.Target}' has no value");
                        }

                        steps.Add(new EffectStep(EffectOperationKind.ClearColor, ClearColor: color,
                            LoopIndexParameter: loopIndexParameter, LoopIndex: loopIndex));
                        break;
                    }

                case EffectOperationKind.ClearDepth:
                    {
                        float depth = 1f;
                        if (operation.Target is null)
                        {
                            Error(technique, "ClearSetDepth without a parameter");
                        }
                        else if (planningValues.TryGet(operation.Target, out var value))
                        {
                            depth = value.AsFloat();
                        }
                        else
                        {
                            Error(technique, $"clear depth parameter '{operation.Target}' has no value");
                        }

                        steps.Add(new EffectStep(EffectOperationKind.ClearDepth, ClearDepth: depth,
                            LoopIndexParameter: loopIndexParameter, LoopIndex: loopIndex));
                        break;
                    }

                case EffectOperationKind.Clear:
                    steps.Add(new EffectStep(EffectOperationKind.Clear, ClearMask: operation.ClearMask,
                        LoopIndexParameter: loopIndexParameter, LoopIndex: loopIndex));
                    break;

                case EffectOperationKind.DrawScene:
                    steps.Add(new EffectStep(EffectOperationKind.DrawScene,
                        LoopIndexParameter: loopIndexParameter, LoopIndex: loopIndex));
                    break;

                case EffectOperationKind.RunPass:
                    {
                        var pass = operation.Target is null ? null : technique.FindPass(operation.Target);
                        if (pass is null)
                        {
                            Error(technique, $"unknown pass name: {operation.Target}");
                            break;
                        }

                        steps.Add(new EffectStep(EffectOperationKind.RunPass, operation.Target, Pass: pass,
                            LoopIndexParameter: loopIndexParameter, LoopIndex: loopIndex));
                        break;
                    }

                case EffectOperationKind.Loop:
                    {
                        int count = ResolveLoopCount(technique, operation, planningValues);
                        for (int i = 0; i < count; i++)
                        {
                            if (operation.Source is { Length: > 0 } index) SetIndex(planningValues, index, i);
                            int beforeBody = steps.Count;
                            Emit(technique, operation.Body ?? [], steps, textures, operation.Source, i, planningValues);
                            if (steps.Count == beforeBody && operation.Source is { Length: > 0 } emptyBodyIndex)
                                steps.Add(new EffectStep(EffectOperationKind.SetLoopIndex,
                                    LoopIndexParameter: emptyBodyIndex, LoopIndex: i));
                        }

                        break;
                    }

                case EffectOperationKind.SetLoopIndex:
                    if (operation.Target is not { Length: > 0 } parameter)
                        Error(technique, "LoopGetIndex without a parameter");
                    else
                    {
                        SetIndex(planningValues, parameter, loopIndex);
                        steps.Add(new EffectStep(EffectOperationKind.SetLoopIndex,
                            LoopIndexParameter: parameter, LoopIndex: loopIndex));
                    }
                    break;

                default:
                    Error(technique, $"unhandled operation {operation.Kind}");
                    break;
            }
        }
    }

    private int ResolveLoopCount(EffectTechnique technique, EffectOperation operation, EffectParameterValues planningValues)
    {
        if (operation.Target is null || !planningValues.TryGet(operation.Target, out var value))
        {
            Error(technique, $"loop count parameter '{operation.Target}' has no value");
            return 0;
        }

        return value.Kind switch
        {
            EffectValueKind.Integer => Math.Max(0, value.Integer),
            EffectValueKind.Boolean => value.Boolean ? 1 : 0,
            _ => InvalidLoopValue(),
        };

        int InvalidLoopValue() { Error(technique, $"loop count parameter '{operation.Target}' must contain int or bool"); return 0; }
    }

    private void SetIndex(EffectParameterValues values, string parameter, int index) =>
        values.Set(parameter, _definition.FindParameter(parameter)?.Kind == EffectValueKind.Boolean
            ? EffectValue.FromBoolean(index != 0) : EffectValue.FromInteger(index));

    private void AddTexture(List<EffectTextureAllocation> textures, string? name, EffectTextureRole role)
    {
        if (name is null)
        {
            return;
        }

        var existing = textures.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.Role != role)
        {
            TextureRoleError(name);
            return;
        }

        var parameter = _definition.FindParameter(name);
        if (parameter is null)
        {
            _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error,
                MmeDiagnosticCodes.UnknownTextureName, $"unknown texture name: {name}", _definition.SourcePath));
            return;
        }

        if (!parameter.IsTexture)
        {
            _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error,
                MmeDiagnosticCodes.InvalidRenderColorTarget,
                $"invalid render target (not a texture): {name}", _definition.SourcePath));
            return;
        }

        if ((parameter.Role != EffectTextureRole.Input && parameter.Role != role)
            || _definition.Techniques.Any(t => HasConflictingTextureRole(t.Graph, name, role)))
        {
            TextureRoleError(name);
            return;
        }
        if (existing is not null) return;

        parameter.Annotations.TryGetValue("Format", out var formatText);
        var format = EffectFormats.Parse(formatText);
        if (format == EffectRenderTargetFormat.Unknown)
        {
            format = role == EffectTextureRole.DepthStencilTarget
                ? EffectRenderTargetFormat.D24S8
                : EffectRenderTargetFormat.R8G8B8A8_UNorm;

            if (!string.IsNullOrWhiteSpace(formatText))
            {
                _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Warning,
                    MmeDiagnosticCodes.UnknownTextureName,
                    $"unknown texture format '{formatText}' for '{name}', using {format}", _definition.SourcePath));
            }
        }

        parameter.Annotations.TryGetValue("ViewPortRatio", out var ratioText);
        var ratio = EffectAnnotationValues.TryParseVector2(ratioText, out var parsed) ? parsed : Vector2.One;

        int mipLevels = 1;
        if (parameter.Annotations.TryGetValue("MipLevels", out var mipText)
            && int.TryParse(mipText.Trim(), out int parsedMips))
        {
            mipLevels = Math.Max(1, parsedMips);
        }

        textures.Add(new EffectTextureAllocation(name, role, format, ratio, mipLevels));
    }

    private static bool HasConflictingTextureRole(IReadOnlyList<EffectOperation> operations, string name, EffectTextureRole role)
    {
        foreach (var operation in operations)
        {
            bool targetsTexture = string.Equals(operation.Target, name, StringComparison.OrdinalIgnoreCase);
            if (targetsTexture && ((operation.Kind == EffectOperationKind.SetRenderTarget && role == EffectTextureRole.DepthStencilTarget)
                || (operation.Kind == EffectOperationKind.SetDepthStencilTarget && role == EffectTextureRole.RenderTarget))) return true;
            if (operation.Body is not null && HasConflictingTextureRole(operation.Body, name, role)) return true;
        }
        return false;
    }

    private void TextureRoleError(string name) =>
        _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error, MmeDiagnosticCodes.InvalidRenderColorTarget,
            $"texture has incompatible color and depth/stencil target roles: {name}", _definition.SourcePath));

    private void Error(EffectTechnique technique, string message) =>
        _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error, MmeDiagnosticCodes.SyntaxError,
            $"{_definition.Name}::{technique.Name}: {message}", _definition.SourcePath));

    private void Warning(EffectTechnique technique, string message) =>
        _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Warning, MmeDiagnosticCodes.SyntaxError,
            $"{_definition.Name}::{technique.Name}: {message}", _definition.SourcePath));
}
