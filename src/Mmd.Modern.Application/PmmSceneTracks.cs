// PMM layout: nanoem/ext/document.c (MIT). Camera convention: original MMD 9.32
// main host RVA 0x274e0; independently sampled native WORLD and VIEW matrices.
using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public readonly record struct PmmCameraPose(Vector3 LookAt,Vector3 Angle,float Distance,float Fov,bool Perspective,int ParentModelIndex=-1,int ParentBoneIndex=0,bool IgnoreParentRotation=false,Vector2 ViewOffset=default)
{
    /// <summary>Original parented tracks store the local eye in LookAt; their raw Distance is retained but unused.</summary>
    public float CameraDepth => ParentModelIndex >= 0 ? LookAt.Z : Distance;

    public SceneCamera CreateCamera(Matrix4x4? parent=null)
    {
        // Native view orientation is Ry(y)*Rx(x)*Rz(z). Its inverse, not Saba's
        // different X/Z order, places the world eye and basis using row vectors.
        var rotation=Matrix4x4.CreateRotationZ(-Angle.Z)*Matrix4x4.CreateRotationX(-Angle.X)*Matrix4x4.CreateRotationY(-Angle.Y);
        var eye=ParentModelIndex >= 0
            ? Vector3.TransformNormal(LookAt,rotation)
            : LookAt+Vector3.TransformNormal(new Vector3(ViewOffset,Distance),rotation);
        var forward=Vector3.TransformNormal(Vector3.UnitZ,rotation);var up=Vector3.TransformNormal(Vector3.UnitY,rotation);
        if(parent is {} matrix)
        {
            if(IgnoreParentRotation)eye+=matrix.Translation;
            else {eye=Vector3.Transform(eye,matrix);forward=Vector3.TransformNormal(forward,matrix);up=Vector3.TransformNormal(up,matrix);}
        }
        float distance=MathF.Max(.01f,MathF.Abs(CameraDepth));
        return new(eye,eye+forward,up,2*distance*MathF.Tan(Fov*MathF.PI/360),.1f,10000,Perspective?Fov:null,LeftHanded:true);
    }

    /// <summary>VMD/PMM keys have no separate editor pan. Fold it into their interest without moving the camera.</summary>
    public PmmCameraPose ToKeyframePose()
    {
        var rotation=Matrix4x4.CreateRotationZ(-Angle.Z)*Matrix4x4.CreateRotationX(-Angle.X)*Matrix4x4.CreateRotationY(-Angle.Y);
        return this with { LookAt=ParentModelIndex >= 0 ? LookAt
            : LookAt+Vector3.TransformNormal(new Vector3(ViewOffset,0),rotation), ViewOffset=Vector2.Zero };
    }
}
public readonly record struct PmmLightPose(Vector3 Color,Vector3 Direction);

/// <summary>PMM keyframe mode and raw file distance. For the original shadow builder this raw
/// distance is already projection scale S; it must not receive a second UI-distance conversion.</summary>
public readonly record struct PmmSelfShadowPose(byte Mode, float Distance)
{
    public bool Enabled => Mode != 0;
}

/// <summary>The saved PMM editor section stores enabled/distance only, without a coverage mode.</summary>
public readonly record struct PmmSelfShadowSavedState(bool Enabled, float Distance);

/// <summary>Project scene tracks are independent of character motion and GPU backends.</summary>
public sealed class PmmSceneTracks
{
    private readonly PmmDocument document;
    private readonly PmmCameraKeyframe[] cameras;
    private readonly PmmLightKeyframe[] lights;
    private readonly PmmSelfShadowKeyframe[] shadows;
    public PmmSelfShadowSavedState SavedSelfShadow {get;}
    public uint DurationFrames {get;}
    public PmmSceneTracks(PmmDocument document)
    {
        this.document=document??throw new ArgumentNullException(nameof(document));
        var shadow=document.SelfShadow;
        if(shadow is null||shadow.InitialKeyframe is null||shadow.Keyframes is null||!float.IsFinite(shadow.Distance))
            throw new InvalidDataException("PMM self-shadow requires a saved finite distance and non-null keyframes.");
        foreach(var key in new[]{shadow.InitialKeyframe}.Concat(shadow.Keyframes))
            if(key is null||key.Frame<0||key.Mode>2||!float.IsFinite(key.Distance))
                throw new InvalidDataException("PMM self-shadow keys require nonnegative frames, mode 0, 1 or 2 and finite raw distance.");
        SavedSelfShadow=new(shadow.Enabled,shadow.Distance);
        // PmmKeyframe.Frame is mutable: isolate playback from edits to the imported document.
        shadows=Keys(shadow.InitialKeyframe,shadow.Keyframes).Select(k=>new PmmSelfShadowKeyframe
            {Frame=k.Frame,Mode=k.Mode,Distance=k.Distance}).ToArray();
        cameras=Keys(document.Camera.InitialKeyframe,document.Camera.Keyframes);
        lights=Keys(document.Light.InitialKeyframe,document.Light.Keyframes);
        DurationFrames=(uint)Math.Max(Math.Max(cameras[^1].Frame,lights[^1].Frame),shadows[^1].Frame);
    }
    private static T[] Keys<T>(T first,IReadOnlyList<T> extra) where T:PmmKeyframe=>new[]{first}.Concat(extra).GroupBy(k=>k.Frame).Select(g=>g.Last()).OrderBy(k=>k.Frame).ToArray();
    private static (T A,T B,double T) Segment<T>(T[] keys,double frame) where T:PmmKeyframe
    {
        if(!double.IsFinite(frame)||frame<0)throw new ArgumentOutOfRangeException(nameof(frame));
        if(frame<=keys[0].Frame)return(keys[0],keys[0],0);
        for(int i=1;i<keys.Length;i++)if(frame<keys[i].Frame)return(keys[i-1],keys[i],(frame-keys[i-1].Frame)/(keys[i].Frame-keys[i-1].Frame));
        return(keys[^1],keys[^1],0);
    }
    public PmmCameraPose SavedCamera=>new(document.Camera.LookAt,document.Camera.Angle,document.Camera.Position.Z,document.CameraFov,document.Camera.Perspective,
        document.Version == 1 ? -1 : document.Settings.CameraLookAtModelIndex,document.Version == 1 ? -1 : document.Settings.CameraLookAtBoneIndex,
        document.Version != 1 && document.Settings.FollowingLookAtRaw != 0,
        document.Version != 1 && document.Settings.CameraLookAtModelIndex >= 0 ? Vector2.Zero : new(document.Camera.Position.X,document.Camera.Position.Y));
    public PmmLightPose SavedLight=>new(document.Light.Color,document.Light.Direction);
    public PmmCameraPose CameraAt(double frame)
    {
        var(a,b,t)=Segment(cameras,frame);
        // Adjacent integer camera keys describe a cut. Their values do not blend between frames.
        if(b.Frame-a.Frame<=1)t=0;
        float Curve(int channel)
        {
            int i=channel*4;var c=a.Interpolation;
            if(c.Count!=24)throw new InvalidDataException("PMM camera requires six interpolation curves.");
            return(float)new BezierCurve(c[i]/127f,c[i+1]/127f,c[i+2]/127f,c[i+3]/127f).Evaluate(t);
        }
        float L(float x,float y,float weight)=>x+(y-x)*weight;
        return new(new(L(a.LookAt.X,b.LookAt.X,Curve(0)),L(a.LookAt.Y,b.LookAt.Y,Curve(1)),L(a.LookAt.Z,b.LookAt.Z,Curve(2))),
            Vector3.Lerp(a.Angle,b.Angle,Curve(3)),L(a.Distance,b.Distance,Curve(4)),L(a.Fov,b.Fov,Curve(5)),a.Perspective,a.ParentModelIndex,a.ParentBoneIndex,
            document.Version != 1 && document.Settings.FollowingLookAtRaw != 0);
    }
    public PmmLightPose LightAt(double frame)
    {var(a,b,t)=Segment(lights,frame);return new(Vector3.Lerp(a.Color,b.Color,(float)t),Vector3.Lerp(a.Direction,b.Direction,(float)t));}

    /// <summary>
    /// Explicit step/hold playback policy. Exact original MMD distance-transition behavior remains
    /// an oracle check; the PMM layout itself has no shadow interpolation control points.
    /// </summary>
    public PmmSelfShadowPose SelfShadowAt(double frame)
    {
        var(a,_,_)=Segment(shadows,frame);
        return new(a.Mode,a.Distance);
    }
}
