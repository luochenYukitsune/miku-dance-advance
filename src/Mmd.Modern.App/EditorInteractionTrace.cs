using System.Runtime.CompilerServices;
using Mmd.Modern.Application;

namespace Mmd.Modern.App;

// UI input identities travel with immutable snapshots, never with mutable scene storage.
// Weak keys cannot extend the lifetime of a held geometry/physics snapshot.
internal static class EditorInteractionTrace
{
    private sealed record Identity(long Session, long Input, long Generation);
    private static readonly ConditionalWeakTable<SceneProjectRenderSnapshot, Identity> snapshots = new();
    private static long currentInput, currentSession;
    internal static long CurrentInput => currentSession == InteractionDiagnostics.SessionId ? currentInput : 0;

    internal static long BeginInput(string stage, long value = 0)
    {
        if (!InteractionDiagnostics.IsEnabled) return 0;
        currentSession = InteractionDiagnostics.SessionId;
        currentInput = InteractionDiagnostics.NextId();
        InteractionDiagnostics.Record(stage, CurrentInput, value: value);
        return CurrentInput;
    }

    internal static void Published(SceneProjectRenderSnapshot snapshot, long input, long generation)
    {
        if (!InteractionDiagnostics.IsEnabled) return;
        snapshots.Remove(snapshot);
        snapshots.Add(snapshot, new(InteractionDiagnostics.SessionId, input, generation));
    }

    internal static long InputFor(SceneProjectRenderSnapshot? snapshot)
        => InteractionDiagnostics.SessionId is var session && session != 0 && snapshot is not null
            && snapshots.TryGetValue(snapshot, out var identity) && identity.Session == session ? identity.Input : 0;
}
