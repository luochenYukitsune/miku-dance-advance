using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Editor-like MMD left-handed camera over a floor: where a light is aimed is where it lights,
/// its helper range is where light ends, and moving it while it keeps its aim keeps the target brightness.</summary>
[Trait("Category", "Gpu")]
public sealed class D3D12LightAimFalloffTests
{
    private const int W = 320, H = 240;
    private static readonly SceneCamera Camera = new(new(0, 14, -30), Vector3.Zero, Vector3.UnitY, 20, .1f, 200, 30, LeftHanded: true);
    private static readonly Vector3 LightPosition = new(6, 8, -6), Target = new(-3, 0, 3);

    [Fact]
    public void SpotLightsItsAimTargetEvenlyInsteadOfItsFoot()
    {
        using var session = new D3D12FrameSession();
        var dark = session.Capture(Scene([]), W, H);
        var lit = session.Capture(Scene([Spot(LightPosition, Target, 45)]), W, H);
        D3D12OriginalFrameTests.Save("light-aim-spot", lit);
        Assert.True(LightViewportInteraction.TryProject(Target, Camera, W, H, out var target));
        int atTarget = Added(dark, lit, target), peak = Peak(dark, lit);
        // The old I / d^2 model gave the target barely half of the peak next to the light's foot.
        Assert.True(atTarget >= peak * .85, $"target={atTarget}, peak={peak}");
    }

    [Fact]
    public void NoLightReachesBeyondTheHelperRange()
    {
        using var session = new D3D12FrameSession();
        var point = Spot(new(0, 4, 0), new(1, 0, 0), 9) with { Kind = SceneLightKind.Point };
        var dark = session.Capture(Scene([]), W, H);
        var lit = session.Capture(Scene([point]), W, H);
        int inside = 0, outside = 0;
        for (float x = -14; x <= 14; x += .5f)
            for (float z = -14; z <= 14; z += .5f)
            {
                var floor = new Vector3(x, 0, z);
                float distance = Vector3.Distance(floor, point.Position);
                if (MathF.Abs(distance - point.Range) < .6f || !LightViewportInteraction.TryProject(floor, Camera, W, H, out var pixel)
                    || pixel.X < 1 || pixel.Y < 1 || pixel.X > W - 2 || pixel.Y > H - 2) continue;
                if (distance > point.Range) Assert.True(Added(dark, lit, pixel) <= 0, $"lit beyond range at {floor}");
                else if (distance < point.Range * .8f && Added(dark, lit, pixel) > 0) inside++;
                if (distance > point.Range) outside++;
            }
        Assert.True(inside > 100 && outside > 100, $"inside={inside}, outside={outside}");
        // The drawn coverage is the same Range.
        Assert.All(LightViewportInteraction.DetailSegments(point), segment =>
            Assert.InRange(Vector3.Distance(segment.Start, point.Position), point.Range * .999f, point.Range * 1.001f));
    }

    [Fact]
    public void MovingAnAimedLightKeepsItsTargetBrightness()
    {
        using var session = new D3D12FrameSession();
        var dark = session.Capture(Scene([]), W, H);
        var near = Spot(LightPosition, Target, 45);
        var far = SceneLightAim.AimAt(near with { Position = new(10, 14, -9) }, Target);
        Assert.Equal(near.Intensity, far.Intensity);
        Assert.True(Vector3.Distance(far.Target, Target) < 1e-3f);
        Assert.True(LightViewportInteraction.TryProject(Target, Camera, W, H, out var target));
        int first = Added(dark, session.Capture(Scene([near]), W, H), target);
        int second = Added(dark, session.Capture(Scene([far]), W, H), target);
        Assert.True(first > 60 && Math.Abs(first - second) <= first * .08, $"near={first}, far={second}");
    }

    [Fact]
    public void AimedShadowsLandWhereTheOccluderBlocksTheTarget()
    {
        using var session = new D3D12FrameSession();
        var plate = new Vector3(0, 3, 0);
        var light = Spot(new(2, 10, -3), plate, 60) with { OuterConeRadians = 1.2f, InnerConeRadians = .9f, CastShadows = true };
        var open = session.Capture(Scene([light with { CastShadows = false }], plate: true), W, H);
        var shadow = session.Capture(Scene([light], plate: true), W, H);
        var expected = light.Position + light.Direction * (-light.Position.Y / light.Direction.Y);
        Assert.True(LightViewportInteraction.TryProject(expected, Camera, W, H, out var pixel));
        double sum = 0, sx = 0, sy = 0;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            int d = Luma(open, x, y) - Luma(shadow, x, y);
            if (d <= 12) continue; sum += d; sx += d * x; sy += d * y;
        }
        Assert.True(sum > 0);
        Assert.True(Vector2.Distance(pixel, new((float)(sx / sum), (float)(sy / sum))) < 3, "shadow centroid away from the projected occluder");
    }

    private static NativeSceneLight Spot(Vector3 position, Vector3 target, float range)
    {
        var travel = Vector3.Normalize(target - position);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, travel, Vector3.UnitY)));
        return new(Guid.NewGuid(), "aim", SceneLightKind.Spot, position, rotation, Vector3.One, 1, range, .25f, .45f,
            CastShadows: false, AimDistance: Vector3.Distance(position, target));
    }

    private static int Luma(RenderedFrame frame, int x, int y) { int i = (y * W + x) * 4; return frame.Rgba[i] + frame.Rgba[i + 1] + frame.Rgba[i + 2]; }
    private static int Added(RenderedFrame dark, RenderedFrame lit, Vector2 pixel) => Luma(lit, (int)pixel.X, (int)pixel.Y) - Luma(dark, (int)pixel.X, (int)pixel.Y);
    private static int Peak(RenderedFrame dark, RenderedFrame lit)
    {
        int peak = 0;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) peak = Math.Max(peak, Luma(lit, x, y) - Luma(dark, x, y));
        return peak;
    }

    // A floor and an optional single-sided up-facing plate; front faces satisfy cross(v1-v0, v2-v0) = outward normal.
    private static OriginalFrameScene Scene(IEnumerable<NativeSceneLight> lights, bool plate = false)
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>();
        void Quad(Vector3 c, Vector3 u, Vector3 v)
        {
            uint b = (uint)vertices.Count;
            vertices.AddRange([new(c - u - v, Vector3.UnitY, Vector2.Zero), new(c - u + v, Vector3.UnitY, Vector2.Zero),
                new(c + u + v, Vector3.UnitY, Vector2.Zero), new(c + u - v, Vector3.UnitY, Vector2.Zero)]);
            indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        Quad(Vector3.Zero, new(15, 0, 0), new(0, 0, 15));
        if (plate) Quad(new(0, 3, 0), new(1.2f, 0, 0), new(0, 0, 1.2f));
        var mesh = new SceneSnapshot(vertices.ToArray(), indices.ToArray(),
            [new("floor", 0, 6, new(.8f, .8f, .8f, 1), Vector3.Zero, Vector3.Zero, 1),
             new("plate", 6, plate ? 6 : 0, new(.8f, .8f, .8f, 1), Vector3.Zero, Vector3.Zero, 1)],
            [], camera: Camera, lightDirection: -Vector3.UnitY);
        var surface = new OriginalStyleScene(mesh, [new(new(.2f)), new(new(.2f))], new(new(.1f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp));
        return new(surface, options: new(OriginalAlphaBlendMode.Normal, true, false),
            materials: [new(false, true, false) { UseSceneLighting = true }, new(true, true, false) { UseSceneLighting = true }], lighting: new(lights));
    }
}
