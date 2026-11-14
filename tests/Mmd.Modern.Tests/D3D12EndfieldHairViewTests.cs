using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EndfieldHairViewTests
{
    private static readonly D3D12StylePresetCatalog Catalog = D3D12StylePresetCatalog.BuiltIn;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DetachedRequestsMergeExistingStrictImagesBonesAndOptionalEndfieldMask()
    {
        var id = Guid.NewGuid();
        D3D12ViewEndfieldHairBinding[] source = [new(id, 0, 2, new(.4f))];
        var detached = D3D12ViewEndfieldHairBindings.Detach(source);
        source[0] = source[0] with { LocalTextureIndex = 99 };
        Assert.Equal(2, detached[0].LocalTextureIndex);
        Assert.Same(detached, D3D12ViewEndfieldHairBindings.Detach(detached));
        var requests = D3D12ViewEndfieldHairBindings.CreateRequests(detached,
            [D3D12ViewFaceSdfBinding.GenshinSoft(id, 3, 1, 2, new D3D12ViewFaceHeadBasis.SelectedBone(4, -Vector3.UnitZ, Vector3.UnitX))],
            [new(id, 1, 3)], [new(id, 2, 4, 5)]);
        var request = Assert.Single(requests);
        Assert.Equal(new[] { 4 }, request.BoneIndices);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, request.LocalTextureIndices.Order());
        Assert.Equal(new[] { 2 }, request.OptionalLocalTextureIndices);
    }

    [Fact]
    public void ExactSourceAvailabilityRejectsPaddingFromAnotherObjectForLocalAndComposedBindings()
    {
        var layout = new SceneCompositionEditor();
        var authored = layout.Add("authored", SourcePath, ModelRole.Character, Source());
        var missing = layout.Add("missing", SourcePath, ModelRole.Character, Source(seeds: false));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new("endfield"), Token);
        D3D12ViewEndfieldHairBinding[] bindings = [new(missing.Id, 0, 0, new(.3f))];
        var snapshot = project.CaptureRenderSnapshot(D3D12ViewEndfieldHairBindings.CreateRequests(bindings));
        Assert.NotNull(snapshot.Scene.Surface.Snapshot.RawRadialSeeds);
        Assert.False(snapshot.SceneInputs[missing.Id].HasRawRadialSeeds);
        Assert.Throws<ArgumentException>(() => Resolve(snapshot, bindings));
        D3D12ViewCharacterMaterialBinding[] composed = [new(missing.Id, 0,
            new(D3D12CharacterMaterialRole.Hair, EndfieldHair: new(0, new(.3f))))];
        snapshot = project.CaptureRenderSnapshot(D3D12ViewEndfieldHairBindings.CreateRequests([], materials: composed));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(snapshot.Scene, Catalog,
            snapshot.RenderingStyles, composed, sceneInputs: snapshot.SceneInputs));
        var actual = project.CaptureRenderSnapshot([new(authored.Id)]);
        Assert.True(actual.SceneInputs[authored.Id].HasRawRadialSeeds);
        Assert.Equal(0, project.CurrentFrame);
    }

    [Fact]
    public void SameSampleLocalGreenImagesRebaseAcrossObjectsWithoutResamplingAndZeroSeedIsPresent()
    {
        var layout = new SceneCompositionEditor();
        var a = layout.Add("a", SourcePath, ModelRole.Character, Source(64));
        var b = layout.Add("b", SourcePath, ModelRole.Character, Source(192));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new("endfield"), Token);
        D3D12ViewEndfieldHairBinding[] bindings = [new(a.Id, 0, 0, new(.1f,.2f,.3f)), new(b.Id, 0, 0, new(.3f), -2)];
        var snapshot = project.CaptureRenderSnapshot(D3D12ViewEndfieldHairBindings.CreateRequests(bindings));
        var result = Resolve(snapshot, bindings);
        foreach (var instance in snapshot.RenderingStyles)
        {
            Assert.True(snapshot.SceneInputs[instance.ObjectId].HasRawRadialSeeds);
            var hair = Assert.IsType<D3D12EndfieldHairBinding>(result.SurfaceStyles.CharacterMaterials[instance.MaterialStart].EndfieldHair);
            int texture = snapshot.SceneInputs[instance.ObjectId].TextureIndices[0];
            Assert.Equal(texture, hair.CombinedTextureIndex);
            Assert.Equal(instance.ObjectId == a.Id ? 64 : 192, snapshot.Scene.Surface.Snapshot.Textures[texture].Rgba[1]);
        }
        Assert.Equal(0, project.CurrentFrame);
        Assert.Throws<ArgumentException>(() => Resolve(project.CaptureRenderSnapshot(), bindings));
        var zeroLayout = new SceneCompositionEditor(); var zero = Source();
        var zeroSnapshot = zero.Surface.Snapshot.WithRawRadialSeeds(new(new Vector3[zero.Surface.Snapshot.Vertices.Length]));
        zero = new(new(zeroSnapshot, zero.Surface.Materials, zero.Surface.Profile), zero.Outline, zero.OutlineOptions,
            zero.Options, zero.Materials, zero.Lighting);
        var z = zeroLayout.Add("zero is authored", SourcePath, ModelRole.Character, zero);
        using var zeroProject = Project(zeroLayout); zeroProject.SetDefaultRenderingStyle(new("endfield"), Token);
        D3D12ViewEndfieldHairBinding[] zeroBindings = [new(z.Id, 0, 0, new(.3f))];
        var captured = zeroProject.CaptureRenderSnapshot(D3D12ViewEndfieldHairBindings.CreateRequests(zeroBindings));
        Assert.True(captured.SceneInputs[z.Id].HasRawRadialSeeds);
        Assert.NotNull(Resolve(captured, zeroBindings).SurfaceStyles.CharacterMaterials[0].EndfieldHair);
    }

    [Theory, InlineData("mmd"), InlineData("genshin"), InlineData("star-rail"), InlineData("wuthering-waves")]
    public void DormantLocalConfigurationNeedsNeitherSeedNorMaskAndNeverChangesSelection(string preset)
    {
        var layout = new SceneCompositionEditor(); var item = layout.Add("ordinary", SourcePath, ModelRole.Character, Source(seeds: false));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new(preset), Token);
        D3D12ViewEndfieldHairBinding[] bindings = [new(item.Id, 0, 999, new(.3f))];
        var snapshot = project.CaptureRenderSnapshot(D3D12ViewEndfieldHairBindings.CreateRequests(bindings));
        Assert.False(snapshot.SceneInputs[item.Id].HasRawRadialSeeds);
        Assert.Empty(snapshot.SceneInputs[item.Id].TextureIndices);
        Assert.True(D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog, snapshot.RenderingStyles,
            endfieldHairDataBindings: bindings).IsAvailable);
        var ordinary = Resolve(snapshot, []); var dormant = Resolve(snapshot, bindings);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(ordinary.SurfaceStyles, dormant.SurfaceStyles));
        Assert.Null(dormant.SurfaceStyles.CharacterMaterials[0].EndfieldHair);
        Assert.Equal(preset, project.GetRenderingStyle(item.Id).StableId);
    }

    [Fact]
    public void ActiveMissingOptionalImageRejectsButExistingStrictImageFailureRemains()
    {
        var layout = new SceneCompositionEditor(); var item = layout.Add("selected", SourcePath, ModelRole.Character, Source());
        using var project = Project(layout); project.SetDefaultRenderingStyle(new("endfield"), Token);
        D3D12ViewEndfieldHairBinding[] bindings = [new(item.Id, 0, 99, new(.3f))];
        var snapshot = project.CaptureRenderSnapshot(D3D12ViewEndfieldHairBindings.CreateRequests(bindings));
        Assert.Throws<ArgumentException>(() => Resolve(snapshot, bindings));
        Assert.Throws<ArgumentOutOfRangeException>(() => project.CaptureRenderSnapshot([new(item.Id, localTextureIndices: [99], optionalLocalTextureIndices: [99])]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneObjectRenderRequest(item.Id, optionalLocalTextureIndices: [-1]));
    }

    [Fact]
    public void LocalConflictsAndInvalidNumbersRejectBeforeDeviceConstruction()
    {
        var id = Guid.NewGuid(); var valid = new D3D12ViewEndfieldHairBinding(id, 0, 0, new(.3f));
        D3D12ViewEndfieldHairBinding[][] invalid = [[null!], [valid, valid], [valid with { ObjectId = Guid.Empty }],
            [valid with { LocalMaterialIndex = -1 }], [valid with { LocalTextureIndex = -1 }],
            [valid with { PaletteColor = new(float.NaN) }], [valid with { Brightness = float.PositiveInfinity }]];
        foreach (var values in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => new D3D12ViewRenderer(new(97,73), endfieldHairDataBindings: values));
            Assert.ThrowsAny<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97,73), endfieldHairDataBindings: values));
        }
        foreach (var style in new[] { new D3D12CharacterMaterialStyle(), new(D3D12CharacterMaterialRole.Face), new(D3D12CharacterMaterialRole.Eye),
            new(D3D12CharacterMaterialRole.Hair, EndfieldHair: new(0,new(.3f))), new(D3D12CharacterMaterialRole.Hair, PmxMaterialTreatment: new()),
            new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0,1)), new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: new(0)) })
            Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97,73), characterMaterials: [new(id,0,style)], endfieldHairDataBindings: [valid]));
        Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97,73), hairDataBindings: [new(id,0,0)], endfieldHairDataBindings: [valid]));
        Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97,73), starMcHairDataBindings: [new(id,0,0,1)], endfieldHairDataBindings: [valid]));
    }

    [Fact]
    public void HiddenAndWrongRolePreflightRespectExistingInstanceBoundaries()
    {
        var scene = Source(); var id = Guid.NewGuid();
        var item = new SceneRenderingStyleBinding(id,new("endfield"),ModelRole.Character,0,1,true);
        foreach (var bad in new[] { item with { ObjectId = Guid.NewGuid() }, item with { Role = ModelRole.Environment }, item with { Role = ModelRole.Prop } })
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene,Catalog,[bad], endfieldHairDataBindings: [new(id,0,0,new(.3f))]));
        var hidden = new SceneRenderingStyleBinding(Guid.NewGuid(),new("endfield"),ModelRole.Character,1,0,false);
        Assert.Null(D3D12ViewRenderingStyles.Resolve(scene,Catalog,[item,hidden], endfieldHairDataBindings: [new(hidden.ObjectId,999,999,new(.3f))])
            .SurfaceStyles.CharacterMaterials[0].EndfieldHair);
        foreach (var family in new[] { "mmd", "genshin", "star-rail", "wuthering-waves" })
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene,Catalog,[item with { Preset = new(family) }],
                [new(id,0,new(D3D12CharacterMaterialRole.Hair,EndfieldHair:new(0,new(.3f))))]));
    }

    [Fact, Trait("Category","Gpu")]
    public async Task CaptureMatchesExplicitComposedBindingAndPresentConsumesSameSourceWithoutReadback()
    {
        var layout = new SceneCompositionEditor(); var item = layout.Add("selected",SourcePath,ModelRole.Character,Source());
        using var project = Project(layout); project.SetDefaultRenderingStyle(new("endfield"),Token);
        D3D12ViewEndfieldHairBinding[] host = [new(item.Id,0,0,new(.3f))]; var request = new ViewRenderRequest(97,73);
        await using var capture = new D3D12ViewRenderer(request,endfieldHairDataBindings:host);
        var snapshot = project.CaptureRenderSnapshot(capture.SceneInputRequests); host[0] = host[0] with { Brightness = 5 };
        var plan = new OfflineFramePlan(97,73,new(30),0,2);
        var frame = new OfflineFrame(plan.GetSample(0),97,73,snapshot.Scene,null,null,null)
        { RenderingStyles=snapshot.RenderingStyles,SceneInputs=snapshot.SceneInputs,ResolvedCamera=item.Source.Surface.Snapshot.Camera };
        using var image = await capture.CaptureAsync(frame,Token);
        int texture = snapshot.SceneInputs[item.Id].TextureIndices[0];
        await using var composed = new D3D12ViewRenderer(request,characterMaterials:[new(item.Id,0,new(D3D12CharacterMaterialRole.Hair,EndfieldHair:new(texture,new(.3f))))]);
        using var expected = await composed.CaptureAsync(frame,Token);
        Assert.Equal(expected.Pixels.ToArray(),image.Pixels.ToArray());
        await using var ordinary = new D3D12ViewRenderer(request);
        using var baseline = await ordinary.CaptureAsync(frame,Token);
        int changed=0;
        for(int offset=0;offset<image.Pixels.Length;offset+=4)
        {
            Assert.Equal(baseline.Pixels.Span[offset+3],image.Pixels.Span[offset+3]);
            if(!baseline.Pixels.Span.Slice(offset,3).SequenceEqual(image.Pixels.Span.Slice(offset,3))) changed++;
        }
        Assert.True(changed>100,$"Only {changed} pixels consumed the radial highlight.");
        var before=capture.Statistics;
        await Assert.ThrowsAsync<ArgumentException>(()=>capture.CaptureAsync(frame with { SceneInputs=SceneObjectRenderInputs.Empty },Token).AsTask());
        Assert.Same(before,capture.Statistics);
        nint window=CreateWindowExW(0,"STATIC","MDA Endfield local hair",0x80000000,0,0,128,96,0,0,0,0);
        Assert.NotEqual(0,window);
        try
        {
            await using var presenter=new D3D12RealtimeViewRenderer(window,request,endfieldHairDataBindings:[new(item.Id,0,0,new(.3f))]);
            var preview=new PreviewFrame(97,73,0,snapshot.Scene,null,null,null)
            { RenderingStyles=snapshot.RenderingStyles,SceneInputs=snapshot.SceneInputs,ResolvedCamera=frame.ResolvedCamera };
            presenter.ValidateSceneInputs(preview);
            await presenter.PresentAsync(preview,Token);
            var count=presenter.Statistics.PresentCount;
            await Assert.ThrowsAsync<ArgumentException>(()=>presenter.PresentAsync(preview with { SceneInputs=SceneObjectRenderInputs.Empty },Token).AsTask());
            Assert.Equal(count,presenter.Statistics.PresentCount);
            await presenter.PresentAsync(preview with { RenderingStyles=snapshot.RenderingStyles.Select(value=>value with { Preset=new("mmd") }).ToArray(),SceneInputs=SceneObjectRenderInputs.Empty },Token);
            Assert.Equal(2,presenter.Statistics.PresentCount);
            Assert.Equal(0,presenter.Statistics.ExplicitCaptureCount);
            Assert.Equal(0,presenter.Statistics.Frame!.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    [Fact]
    public async Task PreviewPreflightRejectsMissingSourceBeforeAdvancingOwnerOrPresenting()
    {
        var layout = new SceneCompositionEditor(); var item = layout.Add("missing",SourcePath,ModelRole.Character,Source(seeds:false));
        var project = Project(layout); project.SetDefaultRenderingStyle(new("endfield"),Token);
        var renderer = new CpuPreflightRenderer([new(item.Id,0,0,new(.3f))]);
        await using var preview = await PreviewSession.CreateProjectAsync(new(97,73),
            _ => ValueTask.FromResult(project), (_,_) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer),Token);
        await Assert.ThrowsAsync<ArgumentException>(() => preview.PresentAsync(.5,Token).AsTask());
        Assert.Equal(0,project.CurrentFrame); Assert.Equal(0,renderer.Presents); Assert.Equal(1,renderer.Preflights);
    }

    [Fact]
    public async Task ExportPreflightRejectsMissingGreenBeforeBeginningSinkOrCapturing()
    {
        var layout = new SceneCompositionEditor(); var item = layout.Add("missing",SourcePath,ModelRole.Character,Source());
        var renderer = new CpuPreflightRenderer([new(item.Id,0,99,new(.3f))]); var sink = new CountingSink();
        var coordinator = ExportCoordinator.ForProject(_ =>
        {
            var project = Project(layout); project.SetDefaultRenderingStyle(new("endfield"),Token);
            return ValueTask.FromResult(project);
        }, (_,_) => ValueTask.FromResult<IViewRenderer>(renderer));
        await Assert.ThrowsAsync<ArgumentException>(()=>coordinator.RunAsync(new(new(97,73,new(30),0,0)),sink,cancellationToken:Token));
        Assert.Equal(1,renderer.Preflights); Assert.Equal(0,renderer.Captures); Assert.Equal(0,sink.Begins);
        Assert.Equal(ExportTaskState.Failed,coordinator.State);
    }

    private sealed class CpuPreflightRenderer(IReadOnlyList<D3D12ViewEndfieldHairBinding> bindings)
        : IViewRenderer,IRealtimeViewRenderer,IViewSceneInputRequests,IViewSceneInputPreflight
    {
        public ViewRendererCapabilities Capabilities => new("cpu-preflight",4096,4096,true,true);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; } = D3D12ViewEndfieldHairBindings.CreateRequests(bindings);
        public int Preflights, Captures, Presents;
        public void ValidateSceneInputs(PreviewFrame frame)
        {
            Preflights++;
            _ = D3D12ViewRenderingStyles.Resolve(frame.Scene,Catalog,frame.RenderingStyles,
                sceneInputs:frame.SceneInputs,endfieldHairDataBindings:bindings);
        }
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame,CancellationToken cancellationToken=default)
        { Captures++; throw new InvalidOperationException("Preflight must precede capture."); }
        public ValueTask PresentAsync(PreviewFrame frame,CancellationToken cancellationToken=default)
        { Presents++; throw new InvalidOperationException("Preflight must precede presentation."); }
        public ValueTask ResizeAsync(int width,int height,CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class CountingSink : IFrameSink
    {
        public int Begins;
        public ValueTask BeginAsync(ExportRequest request,CancellationToken cancellationToken=default)
        { Begins++; return ValueTask.CompletedTask; }
        public ValueTask WriteAsync(OfflineFrameSample sample,FrameBufferLease frame,CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public ValueTask CompleteAsync(CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public ValueTask AbortAsync(Exception? error,CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private static D3D12ViewRenderingStylesResult Resolve(SceneProjectRenderSnapshot snapshot,IReadOnlyList<D3D12ViewEndfieldHairBinding> bindings)
        => D3D12ViewRenderingStyles.Resolve(D3D12ViewScene.Resolve(snapshot.Scene,null,null,null,
            new Dictionary<Guid,CharacterMorphChannels>(),new()),Catalog,snapshot.RenderingStyles,
            sceneInputs:snapshot.SceneInputs,endfieldHairDataBindings:bindings);
    private static string SourcePath => Path.Combine(Path.GetTempPath(), "mda-star-mc-synthetic.pmx");
    private static OriginalFrameScene Source(byte green = 128, bool seeds = true)
    {
        SceneVertex[] vertices = [new(new(-1,-1,0), -Vector3.UnitZ, Vector2.Zero), new(new(1,-1,0), -Vector3.UnitZ, Vector2.UnitX),
            new(new(1,1,0), -Vector3.UnitZ, Vector2.One), new(new(-1,1,0), -Vector3.UnitZ, Vector2.UnitY)];
        var snapshot = new SceneSnapshot(vertices, [0,2,1,0,3,2],
            [new("explicit Hair", 0, 6, new(.3f,.2f,.1f,1), new(.8f,.4f,.2f), Vector3.Zero, 1)],
            [new("highlight", 1, 1, [0, green, 37, 79]), new("shadow", 1, 1, [255, 9, 37, 0])],
            camera: new(new(0,0,-5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: -Vector3.UnitZ, rawRadialSeeds: seeds ? new SceneRawRadialSeeds(Enumerable.Repeat(Vector3.UnitX, vertices.Length).ToArray()) : null);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position,
            vertex.Normal, OriginalOutlineVertexMode.Expand, 0, 0)), [new OriginalOutlineMaterial(0, Vector4.Zero, false)]);
        return new(new(snapshot, [new(new(.2f))], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            outline, new(Vector4.Zero, Matrix4x4.Identity, snapshot.Camera.Eye, 30, 1),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }
    private static SceneProjectSession Project(SceneCompositionEditor layout) => new(layout, new(), [], new NoPhysics(), cancellationToken: Token);
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
