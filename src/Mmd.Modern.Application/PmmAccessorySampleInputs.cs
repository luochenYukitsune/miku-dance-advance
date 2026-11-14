using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Immutable inputs retained at one accessory sample. Source remains unmodified;
/// SourceGeometry shares its local streams and raw materials with the resolved World applied once.
/// Hidden samples retain these inputs independently of their empty drawable Frame.</summary>
public sealed class PmmAccessorySampleInputs
{
    public double SourceFrame { get; }
    public bool IsSavedPose { get; }
    public PmmAccessoryPose Pose { get; }
    public OriginalFrameScene Source { get; }
    public SceneSnapshot SourceGeometry { get; }
    public Matrix4x4 ParentWorld { get; }
    public Matrix4x4 World { get; }
    /// <summary>The original parsed .x document when supplied by prepared playback.
    /// Standalone neutral-frame sampling cannot reconstruct this format provenance.</summary>
    public XTextDocument? Document { get; }

    internal PmmAccessorySampleInputs(double sourceFrame, bool isSavedPose, PmmAccessoryPose pose,
        OriginalFrameScene source, Matrix4x4 parentWorld, Matrix4x4 world)
        : this(sourceFrame, isSavedPose, pose, source, source.Surface.Snapshot.WithWorld(world), parentWorld, world, null) { }

    private PmmAccessorySampleInputs(double sourceFrame, bool isSavedPose, PmmAccessoryPose pose,
        OriginalFrameScene source, SceneSnapshot sourceGeometry, Matrix4x4 parentWorld, Matrix4x4 world,
        XTextDocument? document)
    {
        SourceFrame = sourceFrame; IsSavedPose = isSavedPose; Pose = pose; Source = source;
        SourceGeometry = sourceGeometry; ParentWorld = parentWorld; World = world; Document = document;
    }

    internal PmmAccessorySampleInputs WithDocument(XTextDocument? document)
        => ReferenceEquals(Document, document) ? this
            : new(SourceFrame, IsSavedPose, Pose, Source, SourceGeometry, ParentWorld, World, document);
}
