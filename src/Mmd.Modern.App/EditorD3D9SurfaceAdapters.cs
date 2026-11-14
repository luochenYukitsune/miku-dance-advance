using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

/// <summary>Editor presentation adapters over the existing surface renderer. Scope validation remains
/// owned by the same preflight/encoder used by preview and export.</summary>
internal static class EditorD3D9SurfaceAdapters
{
    internal static ViewRenderRequest ValidateRequest(D3D9SurfacePreflightInputs inputs,
        ViewRenderRequest request, Vector4 background, D3D9SurfaceIsolation isolation)
        => inputs.Scope == D3D9SurfaceScope.ConfiguredEmm
            ? inputs.ValidateEmmRequest(request, background) : inputs.ValidateRequest(request, background, isolation);

    internal static void ValidateFrame(D3D9SurfacePreflightInputs inputs, PreviewFrame frame,
        ViewRenderRequest request, Vector4 background, D3D9SurfaceIsolation isolation)
    {
        if (inputs.Scope == D3D9SurfaceScope.ConfiguredEmm) inputs.ValidateEmmFrame(frame, request, background);
        else inputs.ValidateFrame(frame, request, background, isolation);
    }

    internal sealed class Preview(IRealtimeViewRenderer renderer, D3D9SurfacePreflightInputs inputs,
        ViewRenderRequest request, D3D9SurfaceIsolation isolation) : IEditorViewportRenderer
    {
        private ViewRenderRequest current = request;
        private long presents;
        private int resizes;
        public string AdapterName => "D3D9 compatibility (adapter name unavailable)";
        // HAL includes the native software vertex-processing fallback; this is not an observed adapter identity.
        public bool IsWarp => false;
        public EditorViewportPresentationStatistics Statistics => new(presents, 0, resizes, 0, current.Width, current.Height, 1, null);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => inputs.SceneInputRequests;
        public ViewRendererCapabilities Capabilities => renderer.Capabilities;
        public async ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        { await renderer.PresentAsync(frame, cancellationToken).ConfigureAwait(false); presents++; }
        public async ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = ValidateRequest(inputs, current with { Width = width, Height = height }, Vector4.One, isolation);
            // Once admitted, complete native Reset and both adapters' dimensions together.
            await renderer.ResizeAsync(width, height, CancellationToken.None).ConfigureAwait(false);
            current = next; resizes++;
            cancellationToken.ThrowIfCancellationRequested();
        }
        public ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = ValidateRequest(inputs, current with { Quality = quality }, Vector4.One, isolation);
            if (quality != current.EffectiveQuality)
                throw new NotSupportedException("Changing this experimental preview's quality requires recreating its renderer.");
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => renderer.DisposeAsync();
    }

    internal sealed class Capture(IViewRenderer renderer, D3D9SurfacePreflightInputs inputs,
        ViewRenderRequest request, Vector4 background, D3D9SurfaceIsolation isolation) : IViewRenderer,
        IViewSceneInputRequests, IViewSceneInputPreflight, IViewRenderingStylePreflight
    {
        public ViewRendererCapabilities Capabilities => renderer.Capabilities;
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => inputs.SceneInputRequests;
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
            => renderer.CaptureAsync(frame, cancellationToken);
        public void ValidateSceneInputs(PreviewFrame frame) => ValidateFrame(inputs, frame, request, background, isolation);
        public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
            => inputs.ValidateSceneStyles(scene, bindings, request.EffectiveQuality);
        public ValueTask DisposeAsync() => renderer.DisposeAsync();
    }
}
