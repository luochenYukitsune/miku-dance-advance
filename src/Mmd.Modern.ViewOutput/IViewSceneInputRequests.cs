using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput;

/// <summary>Optional renderer declaration of selected neutral project inputs. Definitions are immutable and
/// renderer-owned; ViewOutput captures the list once and transports results with each geometry sample.</summary>
public interface IViewSceneInputRequests
{
    IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; }
}

internal static class ViewSceneInputRequests
{
    internal static IReadOnlyList<SceneObjectRenderRequest> Capture(object renderer)
    {
        if (renderer is not IViewSceneInputRequests provider) return Array.Empty<SceneObjectRenderRequest>();
        var requests = provider.SceneInputRequests?.ToArray()
            ?? throw new InvalidOperationException("The renderer returned null scene input requests.");
        if (requests.Any(request => request is null) || requests.Select(request => request.ObjectId).Distinct().Count() != requests.Length)
            throw new ArgumentException("Renderer scene input requests require nonnull entries and unique object identities.", nameof(renderer));
        return Array.AsReadOnly(requests);
    }

    internal static void RejectNonProject(IReadOnlyList<SceneObjectRenderRequest>? requests)
    {
        if (requests is { Count: > 0 })
            throw new NotSupportedException("Selected scene inputs require a project/PMA owner with authoritative instance identities.");
    }
}
