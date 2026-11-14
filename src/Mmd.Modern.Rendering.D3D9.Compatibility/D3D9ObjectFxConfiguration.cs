using System.Collections.ObjectModel;
using System.Text;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

public enum D3D9ObjectFxAssignmentMode { Inherit = 0, Standard = 1, File = 2 }

/// <summary>An immutable override for a stable FRM1 object ID and material subset (-1 means the whole object).</summary>
public sealed class D3D9ObjectFxAssignment
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public D3D9ObjectFxAssignment(ulong objectId, int subset, D3D9ObjectFxAssignmentMode assignment, string? absoluteFxPath = null)
    {
        if (objectId == 0) throw new ArgumentOutOfRangeException(nameof(objectId));
        if (subset < -1) throw new ArgumentOutOfRangeException(nameof(subset));
        if (assignment is not (D3D9ObjectFxAssignmentMode.Inherit or D3D9ObjectFxAssignmentMode.Standard or D3D9ObjectFxAssignmentMode.File))
            throw new ArgumentOutOfRangeException(nameof(assignment));
        if (assignment == D3D9ObjectFxAssignmentMode.File)
        {
            if (string.IsNullOrEmpty(absoluteFxPath) || absoluteFxPath.Length < 3 ||
                !char.IsAsciiLetter(absoluteFxPath[0]) || absoluteFxPath[1] != ':' ||
                absoluteFxPath[2] is not ('\\' or '/') || absoluteFxPath.Contains('\0'))
                throw new ArgumentException("FILE assignments require an absolute Windows drive path without NUL.", nameof(absoluteFxPath));
            try { Utf8Path = StrictUtf8.GetBytes(absoluteFxPath); }
            catch (EncoderFallbackException error) { throw new ArgumentException("The FX path must be valid Unicode for strict UTF-8 encoding.", nameof(absoluteFxPath), error); }
            if (Utf8Path.Length > 32767) throw new ArgumentException("The UTF-8 FX path exceeds 32767 bytes.", nameof(absoluteFxPath));
        }
        else if (absoluteFxPath is not null)
            throw new ArgumentException("Only FILE assignments may carry a path.", nameof(absoluteFxPath));
        ObjectId = objectId;
        Subset = subset;
        Assignment = assignment;
        AbsoluteFxPath = absoluteFxPath;
    }

    public ulong ObjectId { get; }
    public int Subset { get; }
    public D3D9ObjectFxAssignmentMode Assignment { get; }
    public string? AbsoluteFxPath { get; }
    internal byte[]? Utf8Path { get; }
}

/// <summary>
/// Copies immutable assignment rows, but does not copy or freeze files. The caller MUST freeze and hold the
/// complete transitive FX include/resource tree byte-immutable and present until session Dispose succeeds,
/// including failed-Destroy retries. Resource mappings additionally require every materialized file in that
/// lease and the V2 resource-capable native export; origin identities are never opened. Native configuration
/// still validates file existence, lossless system-code-page paths and the pinned loader's path limits; compiled
/// object/standard SAS admission occurs when drawing. This is not a general MME/EMM or scene-effects contract.
/// </summary>
public sealed class D3D9ObjectFxConfiguration
{
    public D3D9ObjectFxConfiguration(IEnumerable<D3D9ObjectFxAssignment> assignments) : this(assignments, null) { }

    public D3D9ObjectFxConfiguration(IEnumerable<D3D9ObjectFxAssignment> assignments, D3D9EffectResourceMap? resources = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        var rows = new List<D3D9ObjectFxAssignment>();
        var keys = new HashSet<(ulong ObjectId, int Subset)>();
        long pathBytes = 0;
        foreach (var row in assignments)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (rows.Count == 65536) throw new ArgumentException("At most 65536 object FX assignments are supported.", nameof(assignments));
            if (!keys.Add((row.ObjectId, row.Subset))) throw new ArgumentException("Duplicate stable object ID/subset assignment.", nameof(assignments));
            pathBytes += row.Utf8Path?.Length ?? 0;
            if (pathBytes + (resources?.Utf8Bytes ?? 0) > 16 * 1024 * 1024)
                throw new ArgumentException("Object FX assignment and resource paths exceed the combined 16 MiB copied-path budget.", nameof(assignments));
            if (resources is not null && row.Assignment == D3D9ObjectFxAssignmentMode.File &&
                !resources.ContainsMaterializedPath(row.AbsoluteFxPath!))
                throw new ArgumentException("Every FILE assignment must identify a materialized resource in the map.", nameof(assignments));
            rows.Add(row);
        }
        Assignments = new ReadOnlyCollection<D3D9ObjectFxAssignment>(rows);
        Resources = resources;
    }

    public IReadOnlyList<D3D9ObjectFxAssignment> Assignments { get; }
    public D3D9EffectResourceMap? Resources { get; }
}
