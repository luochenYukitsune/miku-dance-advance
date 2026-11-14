using System.Collections.ObjectModel;
using System.Numerics;

namespace Mmd.Modern.Core;

public enum ViewportOverlayShape { Segment,Circle,Square,Wedge }

/// <summary>
/// Editor-only screen primitives, excluded from captures and scene lighting. Segment and Wedge
/// use normalized Start/End endpoints; Circle and Square use Start as their normalized center
/// and ignore End. Width is the stroke width in physical pixels. Radius is the outer radius
/// (Circle), half-width (Square), or base half-width (Wedge), also in physical pixels.
/// DeviceDepth optionally gives a segment's two clipped, noninverted D3D device depths.
/// These world guides test scene visibility; null keeps selection helpers always visible.
/// </summary>
public sealed record ViewportOverlayLine(Vector2 Start,Vector2 End,Vector4 Color,
    float Width=2.5f,ViewportOverlayShape Shape=ViewportOverlayShape.Segment,float Radius=5f,
    Vector2? DeviceDepth=null);
public sealed class ViewportOverlaySnapshot
{
    // 48 KiB of primitive constants remains below D3D12's 64 KiB CBV limit while
    // allowing a real character skeleton plus world and selection helpers.
    public const int MaxLines=1024;
    public ReadOnlyCollection<ViewportOverlayLine> Lines {get;}
    public ViewportOverlaySnapshot(IEnumerable<ViewportOverlayLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var copy=lines.Take(MaxLines+1).ToArray();
        if(copy.Length>MaxLines||copy.Any(l=>l is null||!Finite(l.Start)||!Finite(l.End)||!Finite(l.Color)
            ||!float.IsFinite(l.Width)||l.Width<=0||!float.IsFinite(l.Radius)||l.Radius<=0||!Enum.IsDefined(l.Shape)
            ||l.DeviceDepth is {} depth&&(!Finite(depth)||depth.X<0||depth.X>1||depth.Y<0||depth.Y>1||l.Shape!=ViewportOverlayShape.Segment)))
            throw new ArgumentException("Viewport primitives require finite coordinates/colors, positive finite sizes and a valid shape within the editor budget.",nameof(lines));
        Lines=Array.AsReadOnly(copy);
    }
    private static bool Finite(Vector2 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y);
    private static bool Finite(Vector4 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z)&&float.IsFinite(v.W);
}
