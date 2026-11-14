namespace Mmd.Modern.Effects.Neo;

/// <summary>
/// Translates a parsed legacy <c>.fx</c> into the C# effect API.
/// </summary>
/// <remarks>
/// This is the compatibility half of NeoMME. It never invents behaviour: anything it cannot map
/// is reported as a diagnostic instead of being approximated silently, so a legacy effect either
/// imports with a complete graph or tells the user exactly which construct was dropped.
/// </remarks>
public sealed class ImportedEffect : NeoEffect
{
    private readonly FxEffect _source;
    private readonly ICollection<MmeDiagnostic> _diagnostics;

    public ImportedEffect(FxEffect source, ICollection<MmeDiagnostic> diagnostics)
        : base(Path.GetFileNameWithoutExtension(source.Path))
    {
        _source = source;
        _diagnostics = diagnostics;
    }

    public override void Configure(EffectBuilder effect)
    {
        foreach (var parameter in _source.Parameters)
        {
            effect.TryAdd(MapParameter(parameter));
        }

        foreach (var technique in _source.Techniques)
        {
            var builder = effect.Technique(technique.Name, technique.MmdPass, technique.Annotations);
            foreach (var pass in technique.Passes)
            {
                pass.States.TryGetValue("VertexShader", out var vs);
                pass.States.TryGetValue("PixelShader", out var ps);
                builder.Pass(pass.Name, vs, ps, pass.Profiles, pass.States);
            }

            EmitGraph(builder, MmeRenderGraph.Build(_source, technique, _diagnostics), technique);
            builder.End();
        }
    }

    public override EffectDefinition Build()
    {
        var definition = base.Build();
        string original = _source.OriginalSource ?? _source.Source;
        return definition with
        {
            SourcePath = _source.Path,
            Source = _source.Source,
            OriginalSource = original,
            SourceSha256 = SourceHash(_source.Source),
            OriginalSourceSha256 = SourceHash(original),
            Includes = _source.Includes.ToArray(),
            Diagnostics = _diagnostics.ToArray(),
        };
    }

    private static string SourceHash(string text) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private void EmitGraph(TechniqueBuilder builder, IReadOnlyList<ScriptCommand> commands, FxTechnique technique)
    {
        for (int i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            switch (command.Name.ToLowerInvariant())
            {
                case "rendercolortarget0":
                case "rendercolortarget1":
                case "rendercolortarget2":
                case "rendercolortarget3":
                    builder.RenderTarget(Slot(command.Name), Texture(technique, command));
                    break;

                case "rendercolortarget":
                    builder.RenderTarget(0, Texture(technique, command));
                    break;

                case "renderdepthstenciltarget":
                    builder.DepthStencil(Texture(technique, command));
                    break;

                case "clearsetcolor":
                    builder.ClearColor(command.Argument);
                    break;

                case "clearsetdepth":
                    builder.ClearDepth(command.Argument);
                    break;

                case "clear":
                    {
                        var targets = ParseClear(command, technique);
                        if (targets != ClearTargets.None)
                        {
                            builder.Clear(targets);
                        }

                        break;
                    }

                case "scriptexternal":
                    if (command.Argument.Equals("Color", StringComparison.OrdinalIgnoreCase))
                    {
                        builder.Scene();
                    }
                    else
                    {
                        Warn(technique, command, MmeDiagnosticCodes.UnknownScriptExternal,
                            $"unknown script external type: {command.Argument}");
                    }

                    break;

                case "pass":
                    if (technique.FindPass(command.Argument) is null)
                    {
                        Error(technique, command, MmeDiagnosticCodes.UnknownPassName,
                            $"unknown pass name: {command.Argument}");
                    }

                    builder.Run(command.Argument);
                    break;

                case "loopbycount":
                    {
                        // A first-command LoopGetIndex is a prologue; later assignments stay
                        // in the body at the source position, including repeated assignments.
                        int indexParameter = -1;
                        int end = -1;
                        int depth = 0;
                        for (int j = i + 1; j < commands.Count; j++)
                        {
                            string name = commands[j].Name.ToLowerInvariant();
                            if (name == "loopbycount")
                            {
                                depth++;
                            }
                            else if (name == "loopend")
                            {
                                if (depth == 0)
                                {
                                    end = j;
                                    break;
                                }

                                depth--;
                            }
                            else if (name == "loopgetindex" && depth == 0 && indexParameter < 0)
                            {
                                indexParameter = j;
                            }
                        }

                        if (end < 0)
                        {
                            Error(technique, command, MmeDiagnosticCodes.SyntaxError,
                                "LoopByCount without LoopEnd");
                            break;
                        }

                        bool prologue = indexParameter == i + 1;
                        int bodyStart = prologue ? i + 2 : i + 1;
                        var body = commands
                            .Skip(bodyStart)
                            .Take(end - bodyStart)
                            .ToList();
                        string index = prologue ? commands[indexParameter].Argument : string.Empty;

                        builder.Loop(command.Argument, index, inner => EmitGraph(inner, body, technique));
                        i = end;
                        break;
                    }

                case "loopgetindex":
                    builder.SetLoopIndex(command.Argument);
                    break;
                case "loopend":
                    // Consumed by the LoopByCount handler.
                    break;

                default:
                    Warn(technique, command, MmeDiagnosticCodes.UnsupportedScriptCommand,
                        $"unsupported script command: {command.Name}");
                    break;
            }
        }
    }

    private string? Texture(FxTechnique technique, ScriptCommand command)
    {
        if (command.Argument.Length == 0)
        {
            // `RenderColorTarget1=;` detaches the slot on purpose.
            return null;
        }

        var parameter = _source.FindParameter(command.Argument);
        if (parameter is null)
        {
            Error(technique, command, MmeDiagnosticCodes.UnknownTextureName,
                $"unknown texture name: {command.Argument}");
            return command.Argument;
        }

        if (!parameter.IsTexture)
        {
            Error(technique, command, MmeDiagnosticCodes.InvalidRenderColorTarget,
                $"invalid render target (not a texture): {command.Argument}");
        }

        return command.Argument;
    }

    private ClearTargets ParseClear(ScriptCommand command, FxTechnique technique)
    {
        var targets = ClearTargets.None;
        foreach (string part in command.Argument.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "color":
                    targets |= ClearTargets.Color;
                    break;
                case "depth":
                    targets |= ClearTargets.Depth;
                    break;
                case "stencil":
                    targets |= ClearTargets.Stencil;
                    break;
                default:
                    Warn(technique, command, MmeDiagnosticCodes.InvalidClearType,
                        $"unknown clear type: {part.Trim()}");
                    break;
            }
        }

        return targets;
    }

    private static int Slot(string commandName) => commandName[^1] - '0';

    private EffectParameter MapParameter(FxParameter parameter)
    {
        string type = parameter.Type.ToLowerInvariant();
        string? semantic = parameter.Semantic;

        if (parameter.IsTexture)
        {
            var role = semantic?.ToUpperInvariant() switch
            {
                "RENDERCOLORTARGET" or "OFFSCREENRENDERTARGET" => EffectTextureRole.RenderTarget,
                "RENDERDEPTHSTENCILTARGET" => EffectTextureRole.DepthStencilTarget,
                _ => EffectTextureRole.Input,
            };
            return WithInitializer(new EffectParameter(parameter.Name, EffectValueKind.Texture, semantic, role, parameter.Annotations,
                parameter.Initializer, parameter.Type));
        }

        var kind = type switch
        {
            "float" or "half" or "double" => EffectValueKind.Scalar,
            "float2" or "half2" => EffectValueKind.Vector2,
            "float3" or "half3" => EffectValueKind.Vector3,
            "float4" or "half4" => EffectValueKind.Vector4,
            "float2x2" or "float3x3" or "float4x4" or "matrix" => EffectValueKind.Matrix,
            "int" or "uint" or "dword" => EffectValueKind.Integer,
            "bool" => EffectValueKind.Boolean,
            "string" => EffectValueKind.String,
            _ => EffectValueKind.Scalar,
        };

        return WithInitializer(new EffectParameter(parameter.Name, kind, semantic, EffectTextureRole.Input, parameter.Annotations,
            parameter.Initializer, parameter.Type));
    }

    private EffectParameter WithInitializer(EffectParameter parameter)
    {
        if (parameter.Initializer is null) return parameter;
        if (EffectLiteralInitializer.TryParse(parameter, out var value)) return parameter with { DefaultValue = value };
        _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Warning, MmeDiagnosticCodes.UnsupportedInitializer,
            $"unsupported initializer for '{parameter.Name}': {parameter.Initializer}; expression preserved and value unbound", _source.Path));
        return parameter;
    }

    private void Warn(FxTechnique technique, ScriptCommand command, string code, string message) =>
        _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Warning, code,
            $"{_source.Path}::{technique.Name}: {message}"));

    private void Error(FxTechnique technique, ScriptCommand command, string code, string message) =>
        _diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error, code,
            $"{_source.Path}::{technique.Name}: {message}"));
}
