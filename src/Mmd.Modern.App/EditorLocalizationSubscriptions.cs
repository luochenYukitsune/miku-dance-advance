namespace Mmd.Modern.App;

/// <summary>One catalog subscription with weak targets. Callers supply static callbacks.</summary>
internal static class EditorLocalizationSubscriptions
{
    private static readonly List<IObserver> Observers = [];

    static EditorLocalizationSubscriptions() => EditorLocalization.Changed += Refresh;

    internal static void Observe<T>(T target, Action<T> refresh) where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(refresh);
        lock (Observers)
        {
            Observers.RemoveAll(observer => !observer.IsAlive);
            Observers.Add(new Observer<T>(target, refresh));
        }
    }

    private static void Refresh(object? sender, EventArgs args)
    {
        IObserver[] current;
        lock (Observers)
        {
            Observers.RemoveAll(observer => !observer.IsAlive);
            current = Observers.ToArray();
        }
        foreach (var observer in current) observer.Refresh();
    }

    private interface IObserver
    {
        bool IsAlive { get; }
        void Refresh();
    }

    private sealed class Observer<T>(T target, Action<T> refresh) : IObserver where T : class
    {
        private readonly WeakReference<T> weakTarget = new(target);
        public bool IsAlive => weakTarget.TryGetTarget(out _);
        public void Refresh() { if (weakTarget.TryGetTarget(out var value)) refresh(value); }
    }
}

internal sealed class EditorLocalizedArgumentException : ArgumentException
{
    private readonly string template;
    private readonly object?[] values;
    public EditorLocalizedArgumentException(string template, object?[] arguments) : base(template)
    { this.template = template; values = arguments.ToArray(); }
    public override string Message => EditorLocalization.Format(template, values);
}

internal sealed record EditorLocalizedLabel(string Source)
{
    public override string ToString() => EditorLocalization.Text(Source);
}
