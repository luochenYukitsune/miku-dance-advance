namespace Mmd.Modern.ViewOutput;

/// <summary>Optional same-sample input validation before the owner advances or an export sink begins.
/// Implementations must not evaluate another pose, submit GPU work or mutate the supplied snapshot.</summary>
public interface IViewSceneInputPreflight
{
    void ValidateSceneInputs(PreviewFrame frame);
}
