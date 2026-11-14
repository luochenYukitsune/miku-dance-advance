using System.Text;

namespace Mmd.Modern.Formats;

public enum VmdNameKind { Model, Bone, Morph, Ik }

/// <summary>A validated name and its original ANSI payload, excluding NUL and field padding.</summary>
public sealed class VmdEncodedName
{
    public string Value { get; }
    public IReadOnlyList<byte> Bytes { get; }
    public int AnsiCodePage { get; }

    public VmdEncodedName(string value, IReadOnlyList<byte> bytes, int ansiCodePage)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(bytes);
        Encoding encoding = VmdEncoding.Create(ansiCodePage);
        if (bytes.Count > 20)
            throw new ArgumentException("VMD name payload cannot exceed 20 bytes.", nameof(bytes));
        byte[] owned = bytes.ToArray();
        if (owned.Length > 20 || owned.Contains((byte)0) || value.Contains('\0'))
            throw new ArgumentException("VMD name payload must be at most 20 bytes without embedded NUL.", nameof(bytes));
        try
        {
            if (!string.Equals(encoding.GetString(owned), value, StringComparison.Ordinal))
                throw new ArgumentException("VMD name payload does not decode to the supplied value.", nameof(bytes));
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException($"VMD name payload is not valid CP{ansiCodePage}.", nameof(bytes), exception);
        }
        Value = value;
        Bytes = Array.AsReadOnly(owned);
        AnsiCodePage = ansiCodePage;
    }
}

public sealed record VmdNameOverride(VmdNameKind Kind, VmdEncodedName Name);

/// <summary>Captures explicit per-name decoding information for a mixed-encoding VMD export.</summary>
public static class VmdNameCompatibility
{
    public static VmdReadOptions CreateReadOptions(VmdMotion motion, VmdWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(motion);
        int codePage = options?.AnsiCodePage ?? 932;
        _ = VmdEncoding.Create(codePage);
        var names = new Dictionary<(VmdNameKind, string), VmdNameOverride>();
        if (options?.PreserveOriginalNames != false)
        {
            if (options?.ModelNameOverride is null)
                Add(VmdNameKind.Model, motion.ModelName, motion.OriginalModelName);
            foreach (VmdBoneKeyframe key in motion.BoneKeyframes)
                Add(VmdNameKind.Bone, key.BoneName, key.OriginalName);
            foreach (VmdMorphKeyframe key in motion.MorphKeyframes)
                Add(VmdNameKind.Morph, key.MorphName, key.OriginalName);
            foreach (VmdIkKeyframe key in motion.IkKeyframes)
                foreach (VmdIkSwitch item in key.Switches)
                    Add(VmdNameKind.Ik, item.BoneName, item.OriginalName);
        }
        return new() { AnsiCodePage = codePage, NameOverrides = names.Values.ToArray() };

        void Add(VmdNameKind kind, string value, VmdEncodedName? original)
        {
            if (original is null) return;
            VmdNameProfile.ValidateOriginal(kind, value, original);
            var key = (kind, Convert.ToHexString(original.Bytes.ToArray()));
            if (names.TryGetValue(key, out VmdNameOverride? existing))
            {
                if (!string.Equals(existing.Name.Value, original.Value, StringComparison.Ordinal))
                    throw new ArgumentException($"VMD raw name has conflicting {kind} meanings.", nameof(motion));
                return;
            }
            if (names.Count >= VmdNameProfile.MaxNames)
                throw new ArgumentException("VMD name profile exceeds safety limit.", nameof(motion));
            names.Add(key, new(kind, original));
        }
    }
}

internal sealed class VmdNameProfile
{
    internal const int MaxNames = 100_000;
    private readonly Dictionary<(VmdNameKind, string), VmdEncodedName> overrides = new();
    private readonly HashSet<string>? bones;
    private readonly HashSet<string>? morphs;

    public VmdNameProfile(VmdReadOptions? options)
    {
        if (options?.NameOverrides is { } entries)
        {
            if (entries.Count > MaxNames)
                throw new ArgumentException("VMD name profile exceeds safety limit.", nameof(options));
            foreach (VmdNameOverride entry in entries)
            {
                if (entry is null || entry.Name is null)
                    throw new ArgumentException("VMD name profile cannot contain null entries.", nameof(options));
                ValidateOriginal(entry.Kind, entry.Name.Value, entry.Name);
                var key = (entry.Kind, Convert.ToHexString(entry.Name.Bytes.ToArray()));
                if (!overrides.TryAdd(key, entry.Name))
                    throw new ArgumentException("VMD name profile has duplicate kind/payload entries.", nameof(options));
            }
        }
        bones = Candidates(options?.BoneNameCandidates);
        morphs = Candidates(options?.MorphNameCandidates);
    }

    public VmdEncodedName? Find(VmdNameKind kind, ReadOnlySpan<byte> payload) =>
        overrides.GetValueOrDefault((kind, Convert.ToHexString(payload)));

    public HashSet<string>? CandidatesFor(VmdNameKind kind) => kind switch
    {
        VmdNameKind.Bone or VmdNameKind.Ik => bones,
        VmdNameKind.Morph => morphs,
        _ => null,
    };

    internal static void ValidateOriginal(VmdNameKind kind, string value, VmdEncodedName original)
    {
        int width = kind switch
        {
            VmdNameKind.Model or VmdNameKind.Ik => 20,
            VmdNameKind.Bone or VmdNameKind.Morph => 15,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (!string.Equals(value, original.Value, StringComparison.Ordinal))
            throw new ArgumentException($"VMD {kind} original name is stale; clear it or disable preservation after renaming.", nameof(original));
        if (original.Bytes.Count > width)
            throw new ArgumentException($"VMD {kind} name payload exceeds its {width}-byte field.", nameof(original));
    }

    private static HashSet<string>? Candidates(IReadOnlyList<string>? values)
    {
        if (values is null) return null;
        if (values.Count > MaxNames)
            throw new ArgumentException("VMD model name candidates exceed safety limit.", nameof(values));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
            if (value is null || value.Contains('\0') || !result.Add(value))
                throw new ArgumentException("VMD model name candidates must be non-null, NUL-free and unique.", nameof(values));
        return result;
    }
}
