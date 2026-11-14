namespace Mmd.Modern.App;

/// <summary>Presentation states follow operation outcomes, never translated status text.</summary>
public enum EditorActivityState { Idle, Busy, Committing, Cancelling, Cancelled, Completed, Failed }

public sealed partial class EditorShellViewModel
{
    private EditorActivityState activityState;
    private Exception? activityError;
    private bool previewFailure;
    private long operationMessageRevision;

    public EditorActivityState ActivityState => IsBusy
        ? activityState == EditorActivityState.Busy && !CanCancelOperation ? EditorActivityState.Committing : activityState
        : IsPreparingPreview && activityState is (EditorActivityState.Idle or EditorActivityState.Completed)
            ? EditorActivityState.Busy : activityState;
    public bool HasActivityDetails => activityError is not null;
    public string ActivityDetails => activityError?.ToString() ?? "";
    public string ActivityFailureSummary => activityError is Mmd.Modern.Rendering.D3D12.D3D12GiMemoryBudgetExceededException budget
        ? EditorPreviewFeedback.BudgetSummary(budget)
        : activityError is EditorLocalizedArgumentException localized ? localized.Message
        : activityError is Mmd.Modern.Physics.Runtime.ProjectPhysicsCapabilityException
            { Backend: Mmd.Modern.Application.ProjectPhysicsBackend.Bepu, Capability: "coupledSoftBodies" }
            ? EditorLocalization.Text("Mda.Text.d100dcbec02f")
        : EditorLocalization.Text(previewFailure ? "预览暂不可用。请重试，或查看技术详情。"
            : activityError is Mmd.Modern.Formats.PmxFormatException ? "无法读取模型文件。"
            : "操作未完成。请查看技术详情。");
    public string ActivitySummary => ActivityState switch
    {
        EditorActivityState.Failed => EditorLocalization.Format("操作失败 · {0}", ActivityFailureSummary),
        EditorActivityState.Cancelling => EditorLocalization.Text("正在取消…"),
        EditorActivityState.Committing => LoadingStatus + EditorLocalization.Text("（不可取消）"),
        EditorActivityState.Busy => LoadingStatus,
        _ => OperationStatus
    };
    public string CancelOperationHint => ActivityState == EditorActivityState.Cancelling
        ? EditorLocalization.Text("正在取消…")
        : CanCancelOperation ? EditorLocalization.Text("取消操作")
        : EditorLocalization.Text("此步骤完成后自动继续，无法取消。");

    private void NotifyActivityChanged()
    {
        OnPropertyChanged(nameof(ActivityState)); OnPropertyChanged(nameof(ActivitySummary));
        OnPropertyChanged(nameof(HasActivityDetails)); OnPropertyChanged(nameof(ActivityDetails));
        OnPropertyChanged(nameof(ActivityFailureSummary)); OnPropertyChanged(nameof(CancelOperationHint));
    }

    internal void ReportActivityFailure(Exception error, bool previewUnavailable = false)
    {
        activityError = error; previewFailure = previewUnavailable;
        activityState = EditorActivityState.Failed;
        operationStatusSource = null;
        operationStatus = EditorLocalization.Format("操作失败 · {0}", ActivityFailureSummary);
        OnPropertyChanged(nameof(OperationStatus)); OnPropertyChanged(nameof(Status));
        NotifyActivityChanged();
    }

    internal void MarkOperationCancelled()
    {
        activityState = EditorActivityState.Cancelled;
        activityError = null;
        operationStatusSource = null;
        operationStatus = "操作已取消。";
        OnPropertyChanged(nameof(OperationStatus)); OnPropertyChanged(nameof(Status));
        NotifyActivityChanged();
    }

    internal bool RequestOperationCancellation()
    {
        if (!IsBusy || !CanCancelOperation) return false;
        activityState = EditorActivityState.Cancelling;
        CanCancelOperation = false;
        OnPropertyChanged(nameof(LoadingStatus));
        NotifyActivityChanged();
        return true;
    }

    internal void BeginPreviewAttempt()
    {
        // Retire only the previous preview failure when a new real attempt starts.
        // A fallback frame from the failed attempt never calls this recovery path.
        if (activityState != EditorActivityState.Failed || !previewFailure) return;
        previewFailure = false; activityError = null; activityState = EditorActivityState.Idle;
        operationStatusSource = null; operationStatus = "就绪";
        OnPropertyChanged(nameof(OperationStatus)); OnPropertyChanged(nameof(Status));
        NotifyActivityChanged();
    }
}
