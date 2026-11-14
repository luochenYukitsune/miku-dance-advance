namespace Mmd.Modern.App;

/// <summary>Display data only; startup resolves the stable ID after the user confirms.</summary>
internal sealed record EditorRenderEngineChoice(string Id, string DisplayName, string Purpose,
    bool IsAvailable, string? UnavailableReason = null);

/// <summary>Detached selection state; it never constructs an engine or a graphics device.</summary>
internal sealed class EditorRenderEngineSelectionModel
{
    internal EditorRenderEngineSelectionModel(IEnumerable<EditorRenderEngineChoice> choices, string defaultChoiceId = "modern")
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultChoiceId);
        var copy = choices.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in copy)
        {
            if (choice is null || string.IsNullOrWhiteSpace(choice.Id) || string.IsNullOrWhiteSpace(choice.DisplayName)
                || string.IsNullOrWhiteSpace(choice.Purpose) || (!choice.IsAvailable && string.IsNullOrWhiteSpace(choice.UnavailableReason)))
                throw new ArgumentException("Each engine choice needs an ID, display name, purpose and a reason when unavailable.", nameof(choices));
            if (!ids.Add(choice.Id)) throw new ArgumentException("Engine choice IDs must be unique.", nameof(choices));
        }
        Choices = Array.AsReadOnly(copy);
        SelectedChoice = copy.FirstOrDefault(choice => choice.IsAvailable && StringComparer.Ordinal.Equals(choice.Id, defaultChoiceId))
            ?? copy.FirstOrDefault(choice => choice.IsAvailable);
    }

    internal IReadOnlyList<EditorRenderEngineChoice> Choices { get; }
    internal EditorRenderEngineChoice? SelectedChoice { get; private set; }
    internal bool CanConfirm => SelectedChoice is { IsAvailable: true };

    internal bool TrySelect(string? id)
    {
        var choice = Choices.FirstOrDefault(choice => choice.IsAvailable && StringComparer.Ordinal.Equals(choice.Id, id));
        if (choice is null) return false;
        SelectedChoice = choice;
        return true;
    }

    internal bool TryConfirm(out string? id)
    {
        id = CanConfirm ? SelectedChoice!.Id : null;
        return id is not null;
    }
}
