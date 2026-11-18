namespace Mmd.Modern.App;

/// <summary>Collects property notifications raised while a view model is deferred. The model state is
/// already exact; flushing only tells bindings to read it. Order of first occurrence is preserved.</summary>
internal sealed class EditorDeferredPresentation
{
    private readonly List<string?> pending = [];
    private bool commandsPending;
    public bool IsDeferring { get; set; }
    public bool HasPending => pending.Count != 0 || commandsPending;

    public bool TryDefer(string? name)
    {
        if (!IsDeferring) return false;
        if (!pending.Contains(name)) pending.Add(name);
        return true;
    }

    public bool TryDeferCommands()
    {
        if (!IsDeferring) return false;
        commandsPending = true;
        return true;
    }

    public void Flush(Action<string?> raise, Action? refreshCommands = null)
    {
        if (pending.Count == 0 && !commandsPending) return;
        var names = pending.Count == 0 ? [] : pending.ToArray();
        bool refresh = commandsPending;
        pending.Clear();
        commandsPending = false;
        foreach (var name in names) raise(name);
        if (refresh) refreshCommands?.Invoke();
    }
}
