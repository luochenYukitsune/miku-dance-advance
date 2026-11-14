namespace Mmd.Modern.Effects;

/// <summary>
/// Validates an effect technique's <c>Script</c> against MME's own rules and the effect's
/// declarations, producing the same diagnostics the shipped runtime reports
/// ("unknown pass name", "invalid render color target", "type of clear color must be 'float4'").
/// </summary>
public static class MmeRenderGraph
{
    public static IReadOnlyList<ScriptCommand> Build(
        FxEffect effect,
        FxTechnique technique,
        ICollection<MmeDiagnostic> diagnostics)
    {
        var commands = EffectScript.Parse(technique.Script);
        var textureRoles = CollectTextureRoles(effect);
        int loopDepth = 0;

        foreach (var command in commands)
        {
            switch (command.Name.ToLowerInvariant())
            {
                case "rendercolortarget0":
                case "rendercolortarget1":
                case "rendercolortarget2":
                case "rendercolortarget3":
                case "rendercolortarget":
                case "renderdepthstenciltarget":
                    RequireTexture(effect, technique, command, textureRoles, diagnostics);
                    break;

                case "clearsetcolor":
                    RequireType(effect, technique, command, "float4", MmeDiagnosticCodes.InvalidClearColorType,
                        "type of clear color must be 'float4'", diagnostics);
                    break;

                case "clearsetdepth":
                    RequireType(effect, technique, command, "float", MmeDiagnosticCodes.InvalidClearDepthType,
                        "type of clear depth must be 'float'", diagnostics);
                    break;

                case "clear":
                    if (command.Argument.Split(',').Any(part => !MmeContract.ClearTargets.Contains(part.Trim().ToLowerInvariant())))
                    {
                        Error(technique, command, MmeDiagnosticCodes.InvalidClearType,
                            $"unknown clear type: {command.Argument}", diagnostics);
                    }

                    break;

                case "loopbycount":
                    RequireLoopType(effect, technique, command, diagnostics);
                    if (loopDepth++ > 0)
                        Error(technique, command, MmeDiagnosticCodes.UnsupportedNestedLoop,
                            "nested legacy loops are unsupported", diagnostics);
                    break;

                case "loopgetindex":
                    RequireLoopType(effect, technique, command, diagnostics);
                    if (loopDepth == 0)
                        Error(technique, command, MmeDiagnosticCodes.SyntaxError,
                            "LoopGetIndex outside LoopByCount", diagnostics);
                    break;

                case "loopend":
                    if (loopDepth == 0)
                        Error(technique, command, MmeDiagnosticCodes.SyntaxError, "LoopEnd without LoopByCount", diagnostics);
                    else loopDepth--;
                    break;

                case "pass":
                    if (!technique.Passes.Any(p => string.Equals(p.Name, command.Argument, StringComparison.OrdinalIgnoreCase)))
                    {
                        Error(technique, command, MmeDiagnosticCodes.UnknownPassName,
                            $"unknown pass name: {command.Argument}", diagnostics);
                    }

                    break;

                case "scriptexternal":
                    if (!MmeContract.ScriptExternalTargets.Contains(command.Argument.ToLowerInvariant()))
                    {
                        Error(technique, command, MmeDiagnosticCodes.UnknownScriptExternal,
                            $"unknown script external type: {command.Argument}", diagnostics);
                    }

                    break;

                default:
                    if (!MmeContract.IsKnownScriptCommand(command.Name))
                    {
                        Error(technique, command, MmeDiagnosticCodes.UnsupportedScriptCommand,
                            $"unsupported script command: {command.Name}", diagnostics);
                    }

                    break;
            }
        }

        if (loopDepth > 0)
            diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error, MmeDiagnosticCodes.SyntaxError,
                $"{technique.Name}: LoopByCount without LoopEnd", effect.Path));

        return commands;
    }

    private static void RequireLoopType(FxEffect effect, FxTechnique technique, ScriptCommand command,
        ICollection<MmeDiagnostic> diagnostics)
    {
        var parameter = effect.FindParameter(command.Argument);
        if (parameter is null)
            Error(technique, command, MmeDiagnosticCodes.UnknownParameterName,
                $"unknown loop parameter: {command.Argument}", diagnostics);
        else if (parameter.Type.ToLowerInvariant() is not ("int" or "bool"))
            Error(technique, command, MmeDiagnosticCodes.InvalidLoopType,
                $"type of loop count/index must be 'int' or 'bool': {command.Argument}", diagnostics);
    }

    private static void RequireTexture(
        FxEffect effect,
        FxTechnique technique,
        ScriptCommand command,
        IReadOnlyDictionary<string, int> textureRoles,
        ICollection<MmeDiagnostic> diagnostics)
    {
        var parameter = effect.FindParameter(command.Argument);
        if (parameter is null)
        {
            if (command.Argument.Length == 0)
            {
                // `RenderColorTarget1=;` is legal: it detaches that slot. Nothing to resolve.
                return;
            }

            Error(technique, command, MmeDiagnosticCodes.UnknownTextureName,
                $"unknown texture name: {command.Argument}", diagnostics);
            return;
        }

        if (!parameter.IsTexture)
        {
            Error(technique, command, MmeDiagnosticCodes.InvalidRenderColorTarget,
                $"invalid render target (not a texture): {command.Argument}", diagnostics);
        }
        else
        {
            if (textureRoles.TryGetValue(parameter.Name, out int roles) && roles == 3)
            {
                Error(technique, command, MmeDiagnosticCodes.InvalidRenderColorTarget,
                    $"texture is used as both color and depth/stencil target: {parameter.Name}", diagnostics);
                return;
            }
            bool depth = command.Name.Equals("renderdepthstenciltarget", StringComparison.OrdinalIgnoreCase);
            string semantic = parameter.Semantic?.ToUpperInvariant() ?? string.Empty;
            if ((depth && semantic is "RENDERCOLORTARGET" or "OFFSCREENRENDERTARGET")
                || (!depth && semantic == "RENDERDEPTHSTENCILTARGET"))
                Error(technique, command, MmeDiagnosticCodes.InvalidRenderColorTarget,
                    $"render target role does not match command {command.Name}: {command.Argument}", diagnostics);
        }
    }

    // Parameters are global to the effect. Check all techniques, independent of semantic;
    // sampler references are deliberately absent because sampling an RT does not change its role.
    private static IReadOnlyDictionary<string, int> CollectTextureRoles(FxEffect effect)
    {
        var roles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var technique in effect.Techniques)
        foreach (var command in EffectScript.Parse(technique.Script))
        {
            int role = command.Name.ToLowerInvariant() switch
            {
                "rendercolortarget" or "rendercolortarget0" or "rendercolortarget1" or "rendercolortarget2" or "rendercolortarget3" => 1,
                "renderdepthstenciltarget" => 2,
                _ => 0,
            };
            if (role == 0 || command.Argument.Length == 0) continue;
            roles.TryGetValue(command.Argument, out int previous);
            roles[command.Argument] = previous | role;
        }
        return roles;
    }

    private static void RequireType(
        FxEffect effect,
        FxTechnique technique,
        ScriptCommand command,
        string expected,
        string code,
        string message,
        ICollection<MmeDiagnostic> diagnostics)
    {
        var parameter = effect.FindParameter(command.Argument);
        if (parameter is null)
        {
            Error(technique, command, MmeDiagnosticCodes.UnknownParameterName,
                $"unknown parameter name: {command.Argument}", diagnostics);
            return;
        }

        if (!string.Equals(parameter.Type, expected, StringComparison.OrdinalIgnoreCase))
        {
            Error(technique, command, code, $"{message}: {command.Argument}", diagnostics);
        }
    }

    private static void Error(FxTechnique t, ScriptCommand c, string code, string message, ICollection<MmeDiagnostic> d) =>
        d.Add(new MmeDiagnostic(DiagnosticSeverity.Error, code, $"{t.Name}: {message}"));

    private static void Warning(FxTechnique t, ScriptCommand c, string code, string message, ICollection<MmeDiagnostic> d) =>
        d.Add(new MmeDiagnostic(DiagnosticSeverity.Warning, code, $"{t.Name}: {message}"));
}
