namespace Mmd.Modern.Application;

/// <summary>
/// A texture a model references inside its own folder could not be read while saving (deleted, moved or
/// temporarily unavailable after the model was loaded). The save fails and keeps the previous package.
/// A manual save may retry with <see cref="PmaSaveOptions.LeaveMissingModelDependenciesEmpty"/> to keep such
/// references as empty slots; autosave and recovery never do.
/// </summary>
/// <remarks>Raised as the <see cref="Exception.InnerException"/> of the save's <see cref="IOException"/>, whose
/// type and message are unchanged for existing callers. Use <see cref="Find"/> to recognise it.</remarks>
public sealed class MissingModelDependencyException : IOException
{
    public MissingModelDependencyException(string modelPath, string textureReference, Exception? innerException = null)
        : base($"Texture '{textureReference}' of model '{modelPath}' cannot be read.", innerException)
    {
        ModelPath = modelPath;
        TextureReference = textureReference;
    }

    /// <summary>Full path of the source model whose dependency is missing.</summary>
    public string ModelPath { get; }
    /// <summary>The texture reference exactly as the model authored it.</summary>
    public string TextureReference { get; }

    /// <summary>Returns the missing-dependency cause anywhere in the exception's inner chain, or null.</summary>
    public static MissingModelDependencyException? Find(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current is MissingModelDependencyException missing) return missing;
        return null;
    }
}
