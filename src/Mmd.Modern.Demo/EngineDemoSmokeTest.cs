using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.Demo;

/// <summary>Short opt-in executable smoke check. The normal demo needs no testing switches.</summary>
internal sealed partial class FrameViewportApplication
{
    private int demoSmokeFrames;
    private bool demoSmokePhysicsMoved,demoSmokeOverlay, demoSmokeDxr;
    private void OnDemoSmokeFrame(FrameViewportWindow window,D3D12PresentationStatistics statistics)
    {
        try
        {
            Require(!window.Viewport.IsWarp,"Demo must use the physical GPU.");
            Require(statistics.Frame is{ReadbackCount:0,SceneDrawCalls:>0},"Demo present must use GPU draws with zero image readbacks.");
            demoSmokeFrames++;
            demoSmokeDxr|=window.Viewport.RayGiStatistics is{Effective:LightingMode.HardwareRayTracing,DispatchCount:>0};
            switch(demoSmokeFrames)
            {
                case 3:viewModel.SelectLight(viewModel.LightEditor.Lights[0].Id);break;
                case 5:
                    demoSmokeOverlay=window.Viewport.EditorOverlayDrawCalls==1;
                    FrameViewportCapture.SavePng(window.Viewport.Capture(),Path.Combine(output,"demo-ray-tracing.png"));
                    viewModel.SelectLight(null);viewModel.PlayDemoPhysics();break;
                case 20:
                    demoSmokePhysicsMoved=viewModel.DemoPhysicsStepCount>0&&viewModel.DemoPhysicsPlaying&&viewModel.GiMode==LightingMode.Legacy;
                    FrameViewportCapture.SavePng(window.Viewport.Capture(),Path.Combine(output,"demo-physics.png"));viewModel.PauseDemoPhysics();break;
                case 22:window.Width=1220;window.Height=800;viewModel.ShowDemoRayTracing();break;
                case 25:viewModel.AntialiasingIndex=0;break;
                case 26:viewModel.AntialiasingIndex=1;break;
                case 30:
                    Require(demoSmokeOverlay&&demoSmokePhysicsMoved,"Demo must execute the editor overlay and physical simulation.");
                    Require(!window.Viewport.RayGiCapabilities!.HardwareImplementationReady||demoSmokeDxr,"Supported DXR must actually dispatch.");
                    File.WriteAllText(Path.Combine(output,"demo-smoke.json"),JsonSerializer.Serialize(new{passed=true,frames=demoSmokeFrames,adapter=window.Viewport.AdapterName,warp=window.Viewport.IsWarp,dxrActuallyDispatched=demoSmokeDxr,physics=demoSmokePhysicsMoved,editorOverlay=demoSmokeOverlay,statistics},new JsonSerializerOptions{WriteIndented=true}));
                    finished=true;watchdog.Stop();window.Close();desktop.Shutdown(0);break;
            }
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(output,"demo-smoke.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString(),frames=demoSmokeFrames}));
            finished=true;watchdog.Stop();window.Close();desktop.Shutdown(1);
        }
    }
}
