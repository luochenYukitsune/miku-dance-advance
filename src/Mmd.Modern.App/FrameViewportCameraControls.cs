using System.Numerics;

namespace Mmd.Modern.App;

public sealed partial class FrameViewportViewModel
{
    private double panX,panY;
    public double PanX {get=>panX;set{if(!double.IsFinite(value))throw new ArgumentOutOfRangeException(nameof(value));panX=value;ProjectCameraEnabled=false;Invalidate();}}
    public double PanY {get=>panY;set{if(!double.IsFinite(value))throw new ArgumentOutOfRangeException(nameof(value));panY=value;ProjectCameraEnabled=false;Invalidate();}}
    public void PanCameraPixels(double deltaX,double deltaY,int width,int height)
    {
        if(!double.IsFinite(deltaX)||!double.IsFinite(deltaY)||width<=0||height<=0)throw new ArgumentOutOfRangeException(nameof(deltaX));
        var selectedCamera=GetFrame(width,height).Options!.CameraOverride!;
        var active=roomEnabled?roomCamera!:camera!;
        double unitsPerPixel=selectedCamera.VerticalFovDegrees is {} degrees
            ?2*Vector3.Distance(selectedCamera.Eye,selectedCamera.Target)*Math.Tan(degrees*Math.PI/360)/height
            :selectedCamera.OrthographicHeight/height;
        PanX-=deltaX*unitsPerPixel/active.Span;
        PanY+=deltaY*unitsPerPixel/active.Span;
    }
}
