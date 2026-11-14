using Mmd.Modern.Core;
using System.Numerics;

namespace Mmd.Modern.Application;

/// <summary>One project instance's effective preset and role at its exact range in the combined frame.
/// Hidden instances retain their identity and preset with a zero-length range at the current material cursor.</summary>
public sealed record SceneRenderingStyleBinding(Guid ObjectId, RenderingPresetReference Preset, ModelRole Role,
    int MaterialStart, int MaterialCount, bool Visible);

/// <summary>One already evaluated project frame and detached rendering metadata from the same animation clock.
/// Preset definitions, capability resolution and renderer handles remain outside the project owner.</summary>
public sealed class SceneProjectRenderSnapshot
{
    public double SourceFrame { get; }
    public OriginalFrameScene Scene { get; }
    public PmmCameraPose? Camera { get; }
    public PmmLightPose? Light { get; }
    public VmdSelfShadowPose? SelfShadow { get; }
    public SceneCamera? ResolvedCamera { get; }
    /// <summary>Current solved parent transform for the local camera fields, captured with this exact scene sample.</summary>
    public Matrix4x4? CameraParentTransform { get; }
    public IReadOnlyDictionary<Guid, CharacterMorphChannels> MorphChannels { get; }
    /// <summary>All project instances in composition order. Visible ranges cover Scene.Materials exactly.</summary>
    public IReadOnlyList<SceneRenderingStyleBinding> RenderingStyles { get; }
    /// <summary>Only explicitly requested per-instance inputs, captured at SourceFrame with Scene.</summary>
    public IReadOnlyDictionary<Guid, SceneObjectRenderInputs> SceneInputs { get; }
    /// <summary>Only explicitly requested complete source-local material tables, including hidden models.</summary>
    public IReadOnlyDictionary<Guid, SceneObjectMaterialInputs> ObjectMaterialInputs { get; }
    public IReadOnlyList<SceneProjectAccessoryFrame> Accessories { get; }
    public IReadOnlyList<SceneProjectDrawItem> MixedDrawOrder { get; }
    public int? AccessoryIndexAfterModels { get; }
    public IReadOnlyList<string> DrawOrderDiagnostics { get; }
    public IReadOnlyList<byte> UnpreparedAccessoryIndices { get; }
    /// <summary>Actual solved bones for current accessory parents, including hidden parents. Contains all
    /// explicitly requested BoneIndices for those parents plus each directly referenced bone.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>> AccessoryParentBoneWorldMatrices { get; }

    internal SceneProjectRenderSnapshot(double sourceFrame, OriginalFrameScene scene, PmmCameraPose? camera,
        PmmLightPose? light, VmdSelfShadowPose? selfShadow, SceneCamera? resolvedCamera,
        IReadOnlyDictionary<Guid, CharacterMorphChannels> morphChannels, IReadOnlyList<SceneRenderingStyleBinding> renderingStyles,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs = null, Matrix4x4? cameraParentTransform = null,
        IReadOnlyList<SceneProjectAccessoryFrame>? accessories = null, IReadOnlyList<SceneProjectDrawItem>? mixedDrawOrder = null,
        int? accessoryIndexAfterModels = null, IReadOnlyList<string>? drawOrderDiagnostics = null,
        IReadOnlyList<byte>? unpreparedAccessoryIndices = null,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>>? accessoryParentBoneWorldMatrices = null,
        IReadOnlyDictionary<Guid, SceneObjectMaterialInputs>? objectMaterialInputs = null)
    {
        SourceFrame = sourceFrame; Scene = scene; Camera = camera; Light = light; SelfShadow = selfShadow;
        ResolvedCamera = resolvedCamera; MorphChannels = morphChannels; RenderingStyles = renderingStyles;
        CameraParentTransform = cameraParentTransform;
        SceneInputs = sceneInputs ?? SceneObjectRenderInputs.Empty;
        ObjectMaterialInputs = objectMaterialInputs ?? SceneObjectMaterialInputs.Empty;
        Accessories = accessories ?? Array.AsReadOnly(Array.Empty<SceneProjectAccessoryFrame>());
        MixedDrawOrder = mixedDrawOrder ?? Array.AsReadOnly(Array.Empty<SceneProjectDrawItem>());
        AccessoryIndexAfterModels = accessoryIndexAfterModels;
        DrawOrderDiagnostics = drawOrderDiagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        UnpreparedAccessoryIndices = unpreparedAccessoryIndices ?? Array.AsReadOnly(Array.Empty<byte>());
        AccessoryParentBoneWorldMatrices = accessoryParentBoneWorldMatrices
            ?? new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>>(new Dictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>>());
    }
}
