using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.App;

public sealed partial class EditorProjectWorkspace
{
    private IEditorProjectMetadata? PrepareObjectEditMetadata(SceneProjectEdit edit, CancellationToken token = default)
    {
        if (RenderEngine is not EditorClassicRenderEngine classic) return ProjectMetadata;
        var previous = ProjectMetadata as EditorClassicDocumentState ?? throw new NotSupportedException(
            "Classic model edits require prepared document metadata.");
        return previous.PrepareModelEdit(edit, classic, token);
    }

    private IEditorProjectMetadata? PrepareInitialModelMetadata(SceneProjectSession candidate, CancellationToken token = default)
    {
        if (RenderEngine is not EditorClassicRenderEngine classic) return ProjectMetadata;
        var source = candidate.Objects[0].SourcePath;
        string directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var document = EmmReader.Read("[Info]\nVersion=3\n[Object]\n[Effect]\nDefault=none\n"u8);
        var bindings = candidate.Objects.Select((value, index) => new EmmObjectBinding(value.Id, true, value.SourcePath, index));
        var plan = EmmImportPlan.Create(document, Path.Combine(directory, "__mda_classic__.emm"), bindings, directory);
        return classic.CaptureDocument(candidate, classic.PrepareEffects(plan, token), token: token);
    }
}
