namespace Mmd.Modern.Effects.Neo;

/// <summary>
/// The C# effect API: an effect is code that declares parameters and a render graph.
/// </summary>
/// <remarks>
/// Shader bodies stay HLSL; C# owns orchestration (targets, clears, passes, loops, scene
/// insertion). That split is deliberate: it is the only shape a legacy <c>.fx</c> can be
/// translated into without inventing a second language, and it keeps the graph readable and
/// debuggable as ordinary C#.
/// </remarks>
public abstract class NeoEffect
{
    protected NeoEffect(string name) => Name = name;

    public string Name { get; }

    public abstract void Configure(EffectBuilder effect);

    /// <summary>Materialises the declaration. Called once by the runtime.</summary>
    public virtual EffectDefinition Build()
    {
        var builder = new EffectBuilder(Name);
        Configure(builder);
        return builder.Build();
    }
}

/// <summary>Fluent surface used by both hand-written effects and the legacy importer.</summary>
public sealed class EffectBuilder
{
    private readonly List<EffectParameter> _parameters = [];
    private readonly List<EffectTechnique> _techniques = [];
    private readonly string _name;

    internal EffectBuilder(string name) => _name = name;

    public EffectBuilder Texture(
        string name,
        EffectTextureRole role,
        string? semantic = null,
        IReadOnlyDictionary<string, string>? annotations = null)
    {
        Add(new EffectParameter(name, EffectValueKind.Texture, semantic, role,
            annotations ?? Empty));
        return this;
    }

    public EffectBuilder Matrix(string name, string semantic) =>
        Add(new EffectParameter(name, EffectValueKind.Matrix, semantic, EffectTextureRole.Input, Empty));

    public EffectBuilder Vector4(string name, string? semantic = null) =>
        Add(new EffectParameter(name, EffectValueKind.Vector4, semantic, EffectTextureRole.Input, Empty));

    public EffectBuilder Scalar(string name, string? semantic = null) =>
        Add(new EffectParameter(name, EffectValueKind.Scalar, semantic, EffectTextureRole.Input, Empty));

    public EffectBuilder Integer(string name, string? semantic = null) =>
        Add(new EffectParameter(name, EffectValueKind.Integer, semantic, EffectTextureRole.Input, Empty));

    public EffectBuilder Boolean(string name, string? semantic = null) =>
        Add(new EffectParameter(name, EffectValueKind.Boolean, semantic, EffectTextureRole.Input, Empty));

    public EffectBuilder Add(EffectParameter parameter)
    {
        if (!TryAdd(parameter))
        {
            throw new InvalidOperationException($"duplicate effect parameter: {parameter.Name}");
        }

        return this;
    }

    /// <summary>
    /// Adds a parameter unless the name already exists.
    /// </summary>
    /// <remarks>
    /// Legacy sources legitimately repeat a declaration under <c>#if</c>/<c>#else</c>; a real
    /// compiler keeps one branch, so the importer uses this and keeps the first declaration.
    /// </remarks>
    public bool TryAdd(EffectParameter parameter)
    {
        if (_parameters.Any(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _parameters.Add(parameter);
        return true;
    }

    public TechniqueBuilder Technique(
        string name,
        string? mmdPass = null,
        IReadOnlyDictionary<string, string>? annotations = null)
    {
        return new TechniqueBuilder(this, name, mmdPass, annotations);
    }

    internal void AddTechnique(EffectTechnique technique) => _techniques.Add(technique);

    internal EffectDefinition Build() => new(_name, _parameters, _techniques);

    internal static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Declares one technique: its passes and its render graph.</summary>
public sealed class TechniqueBuilder
{
    private readonly EffectBuilder _owner;
    private readonly List<EffectOperation> _graph = [];
    private readonly List<EffectPass> _passes = [];
    private readonly Dictionary<string, string> _annotations;
    private readonly string _name;

    internal TechniqueBuilder(
        EffectBuilder owner,
        string name,
        string? mmdPass,
        IReadOnlyDictionary<string, string>? annotations)
    {
        _owner = owner;
        _name = name;
        _annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (annotations is not null)
        {
            foreach (var pair in annotations)
            {
                _annotations[pair.Key] = pair.Value;
            }
        }

        if (mmdPass is not null)
        {
            _annotations["MMDPass"] = mmdPass;
        }
    }

    public TechniqueBuilder Pass(
        string name,
        string? vertexShader = null,
        string? pixelShader = null,
        IReadOnlyList<string>? profiles = null,
        IReadOnlyDictionary<string, string>? states = null)
    {
        _passes.Add(new EffectPass(
            name,
            vertexShader,
            pixelShader,
            profiles ?? [],
            states ?? EffectBuilder.Empty));
        return this;
    }

    public TechniqueBuilder RenderTarget(int slot, string? texture)
    {
        if (slot is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "MME supports render colour targets 0..3");
        }

        _graph.Add(new EffectOperation(EffectOperationKind.SetRenderTarget, texture, slot));
        return this;
    }

    public TechniqueBuilder DepthStencil(string? texture)
    {
        _graph.Add(new EffectOperation(EffectOperationKind.SetDepthStencilTarget, texture));
        return this;
    }

    public TechniqueBuilder ClearColor(string parameter)
    {
        _graph.Add(EffectOperation.ClearColor(parameter));
        return this;
    }

    public TechniqueBuilder ClearDepth(string parameter)
    {
        _graph.Add(EffectOperation.ClearDepth(parameter));
        return this;
    }

    public TechniqueBuilder Clear(ClearTargets targets)
    {
        _graph.Add(EffectOperation.ClearBuffers(targets));
        return this;
    }

    /// <summary>Draw the host scene here (MME's <c>ScriptExternal=Color</c>).</summary>
    public TechniqueBuilder Scene()
    {
        _graph.Add(EffectOperation.Scene());
        return this;
    }

    public TechniqueBuilder Run(string passName)
    {
        _graph.Add(EffectOperation.RunPass(passName));
        return this;
    }

    public TechniqueBuilder Loop(string countParameter, string indexParameter, Action<TechniqueBuilder> body)
    {
        var inner = new TechniqueBuilder(_owner, _name, null, null);
        body(inner);
        _graph.Add(EffectOperation.Loop(countParameter, indexParameter, inner.Graph));
        return this;
    }

    /// <summary>Assign the current iteration at this exact position inside a loop body.</summary>
    public TechniqueBuilder SetLoopIndex(string parameter)
    {
        _graph.Add(new EffectOperation(EffectOperationKind.SetLoopIndex, parameter));
        return this;
    }

    public EffectBuilder End()
    {
        _owner.AddTechnique(new EffectTechnique(_name, MmdPass, _annotations, _passes, _graph));
        return _owner;
    }

    internal IReadOnlyList<EffectOperation> Graph => _graph;

    private string? MmdPass => _annotations.TryGetValue("MMDPass", out var value) ? value : null;
}
