using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

/// <summary>Detached original pass controls. Resolving a pass reads only the accepted frame;
/// no workspace, PMM evaluator, asset path, or animation/physics owner is retained.</summary>
internal sealed class EditorClassicPassInputs
{
    private sealed record ModelControls(int ReferenceBoneIndex, float? Width, bool AdditiveBlend);
    private readonly IReadOnlyDictionary<Guid, ModelControls> models;
    private readonly bool outlines, selfShadow, groundShadow, translucentGround;
    private readonly float groundBrightness;

    private EditorClassicPassInputs(Dictionary<Guid, ModelControls> models, ProjectSettings settings,
        PmmDocument? document)
    {
        this.models = models;
        outlines = settings.OutlineEnabled;
        selfShadow = settings.SelfShadowEnabled && (document?.SelfShadow.Enabled ?? true);
        groundShadow = settings.GroundShadowEnabled;
        // Original UI initialization sets Ambient=(1,1,1,1), Diffuse.a=.5 and
        // Specular.a=1. PMM loading overwrites only Ambient.rgb, not its alpha.
        // MikuDanceStudio MIT pin bfb6297: ui_init.cpp:353 and pmm_load_v2.cpp:2165.
        groundBrightness = document?.Settings.GroundShadowBrightness ?? 1;
        translucentGround = document?.Settings.TranslucentGroundShadowEnabled ?? true;
        if (!float.IsFinite(groundBrightness))
            throw new InvalidDataException("Classic ground-shadow brightness must be finite.");
    }

    internal static EditorClassicPassInputs Capture(SceneProjectSession source, IReadOnlyDictionary<Guid, PmxModel> authored)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(authored);
        var controls = source.PmmControls;
        var document = controls?.ExportDocument();
        var pmmModels = document?.Models.ToDictionary(value => value.Index);
        var models = new Dictionary<Guid, ModelControls>();
        foreach (var item in source.Objects)
        {
            if (!authored.TryGetValue(item.Id, out var model))
                throw new InvalidDataException("Classic outline controls require the exact authored model identity.");
            float? width = null;
            bool additiveBlend = false;
            // A model added after the PMM import has no PMM record and keeps the source outline defaults.
            if (controls is not null && controls.ModelIndices.TryGetValue(item.Id, out byte index))
            {
                if (!pmmModels!.TryGetValue(index, out var pmm))
                    throw new InvalidDataException("Classic PMM outline controls do not cover the live model identity.");
                width = document!.Version == 1 ? 1 : pmm.EdgeWidth;
                additiveBlend = pmm.BlendEnabled;
                if (!float.IsFinite(width.Value) || width.Value < 0)
                    throw new InvalidDataException("Classic PMM model edge width must be finite and nonnegative.");
            }
            // Original PmdEdgeDistance uses bone #1, falls back to #0, then zero
            // for a skeleton without bones; it does not select a bone by name.
            models.Add(item.Id, new(model.Bones.Count == 0 ? -1 : Math.Min(1, model.Bones.Count - 1), width, additiveBlend));
        }
        return new(models, source.Settings, document);
    }

    internal D3D9ClassicPassInputs Resolve(OfflineFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var camera = D3D9ClassicPassInputs.ResolveCamera(frame);
        var selected = new List<D3D9ClassicModelPassInputs>(models.Count);
        foreach (var (id, controls) in models)
        {
            if (!frame.SceneInputs.TryGetValue(id, out var input))
                throw new InvalidDataException("Classic pass controls require this accepted sample's model inputs.");
            var options = input.SourceOutlineOptions;
            if (input.Visible && input.SourceOutline is not null)
            {
                if (options is null || input.SourceGeometry is null)
                    throw new InvalidDataException("Classic outlines require matching source geometry and options from this sample.");
                Matrix4x4 reference;
                if (controls.ReferenceBoneIndex < 0) reference = input.SourceGeometry.World;
                else if (!input.BoneWorldMatrices.TryGetValue(controls.ReferenceBoneIndex, out reference))
                    throw new InvalidDataException("Classic outline distance requires its original reference bone from this sample.");
                // Bone worlds already include instance placement exactly once. Their origin
                // equals bind-position * original skin matrix. World-space eye distance is
                // invariant under the original frame's camera rotation/translation.
                options = options with { ReferencePoint = new(0, 0, 0, 1), DistanceTransform = reference,
                    CameraPosition = camera.Eye, FovDegrees = camera.VerticalFovDegrees ?? frame.Camera?.Fov ?? options.FovDegrees,
                    ModelWidth = controls.Width ?? options.ModelWidth, Eligible = outlines && options.Eligible };
            }
            else if (options is not null) options = options with { Eligible = false };
            selected.Add(new(id, options, controls.AdditiveBlend));
        }

        var light = frame.Light;
        // PmmLightPose.Direction is the outgoing (propagation) direction the ground projection needs, while
        // SceneSnapshot.LightDirection is the incoming L. Without a light track the fallback must be negated;
        // using L as-is pointed the default light upward (Y > 0) and silently turned the ground shadow off.
        var direction = light?.Direction ?? -frame.Scene.Surface.Snapshot.LightDirection;
        var ambient = light?.Color ?? frame.Scene.Surface.Profile.LightAmbient;
        OriginalSelfShadowOptions? shadow = null;
        bool effectFrame = selfShadow && (frame.SelfShadow?.Mode ?? (frame.Scene.Options?.SelfShadow is null ? 0 : 1)) != 0;
        if (effectFrame)
        {
            if (frame.SelfShadow is { } pose)
            {
                if (frame.Camera is not { } rawCamera || light is not { } rawLight)
                    throw new NotSupportedException("Classic animated self shadow requires the same sample's original camera and light metadata.");
                // Original d3d_init caps each map dimension at 2048; the supported D3D9
                // device path provides that extent. Select every sampler/comparison field
                // explicitly so the modern adapter's optional defaults are never used.
                var settings = new OriginalSelfShadowOptions(Matrix4x4.Identity, 2048, 2048, true, false,
                    OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual);
                shadow = PmmOriginalSelfShadowAdapter.Create(rawCamera, rawLight, pose, Matrix4x4.Identity,
                    settings, frame.CameraParentTransform);
            }
            else shadow = frame.Scene.Options!.SelfShadow;
        }
        var material = new D3D9ClassicGroundMaterial(new(0, 0, 0, .5f),
            new(groundBrightness, groundBrightness, groundBrightness, 1), new(0, 0, 0, 1), Vector4.Zero, 0);
        var ground = new D3D9ClassicGroundShadowInputs(groundShadow && direction.Y < 0, direction,
            material, ambient, groundBrightness, translucentGround, true);
        // Mode zero and the global disable select original fixed-function drawing.
        // The global switch also controls PMX edge alpha independently of the mode.
        return new(frame, effectFrame, shadow, ground, selected, globalSelfShadowEnabled: selfShadow);
    }
}
