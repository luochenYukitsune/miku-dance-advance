namespace Mmd.Modern.Application;

/// <summary>An undo/redo stack that keeps at most <see cref="Capacity"/> entries. Each entry can hold a complete
/// project content snapshot (motions, VMD bytes, layout), so an unbounded stack grew for the whole editing session.
/// Pushing past the capacity drops the oldest entry and returns it so the owner can release what it holds.</summary>
public sealed class BoundedHistory<T> : IEnumerable<T>
{
    /// <summary>Default number of undo steps kept by the project session and the editor workspace. The workspace must
    /// not keep more steps than the session, or its oldest steps would refer to session history that was dropped.</summary>
    public const int DefaultCapacity = 100;
    private readonly LinkedList<T> items = new();

    public BoundedHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => items.Count;

    /// <summary>Pushes <paramref name="item"/>; returns true with the dropped oldest entry when the capacity was exceeded.</summary>
    public bool Push(T item, out T? dropped)
    {
        items.AddLast(item);
        if (items.Count <= Capacity) { dropped = default; return false; }
        dropped = items.First!.Value; items.RemoveFirst(); return true;
    }

    public void Push(T item) => Push(item, out _);

    public bool TryPeek(out T item)
    {
        if (items.Last is { } last) { item = last.Value; return true; }
        item = default!; return false;
    }

    public T Pop()
    {
        var last = items.Last ?? throw new InvalidOperationException("The history is empty.");
        items.RemoveLast(); return last.Value;
    }

    public void Clear() => items.Clear();

    /// <summary>Newest first, like <see cref="Stack{T}"/>.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var node = items.Last; node is not null; node = node.Previous) yield return node.Value;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
