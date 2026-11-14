using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

/// <summary>Optional adapter validation before a preview advances or an export seeks or opens its sink.
/// Validate all project references, including hidden objects. An unavailable preset must be resolved
/// explicitly by the host before rendering can continue; neutral owners do not choose a fallback.</summary>
public interface IViewRenderingStylePreflight
{
    void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings);
}
