using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics.Bullet;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Actual VMD bone evaluation and Bullet feedback feed the same persistent GI session.
/// Fresh references consume those already evaluated poses, never a second physics clock.</summary>
[Trait("Category", "Gpu")]
public sealed class D3D12AnimatedPhysicsGiTests
{
    private const int Width = 95, Height = 71;

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void ContinuousBulletAndVmdDeformationMatchesFreshGiAndResetReplay(LightingMode mode)
    {
        using var controller = Controller();
        controller.EnablePhysics(new BulletCharacterPhysicsBackend(new(0, -120, 0), null), 0);
        var initial = Frame(controller.Sample(0).Surface);
        var initialVertices = initial.Surface.Snapshot.Vertices.ToArray();
        var initialWorlds = controller.BoneWorldMatrices.ToArray();
        var gi = new RayGiOptions(mode, Strength: 8, SamplesPerPixel: 64, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, DirectionalIrradiance: new(3));
        using var session = new D3D12FrameSession();
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? capabilities.HardwareAvailable : capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? "Required GPU GI backend is unavailable.");

        var initialOff = session.Capture(initial, Width, Height);
        var initialGi = session.Capture(initial, Width, Height, rayGiOptions: gi);
        long initialContribution = CenterRedContribution(initialOff, initialGi);
        Assert.True(initialContribution > 0, "The finite red bounce wall must contribute GI before moving.");
        var lastBuildVertices = initialVertices;
        int expectedRebuilds = 0, expectedRefits = 0;
        var firstGeometry = RayGiScene.FromOriginal(initial).GeometryIdentity;
        Guid previousGeometry = firstGeometry;
        int? cacheBaseline = null;
        OriginalFrameScene? final = null;
        SceneVertex[]? firstAdvancedVertices = null;
        byte[]? firstAdvancedImage = null;

        for (int i = 1; i <= 12; i++)
        {
            final = Frame(controller.Advance(i, 1d / 30).Surface);
            Assert.Equal(4, controller.PhysicsLastStepCount); // Existing 120 Hz solver, not render-rate integration.
            var transport = RayGiScene.FromOriginal(final);
            Assert.NotEqual(previousGeometry, transport.GeometryIdentity);
            previousGeometry = transport.GeometryIdentity;
            Assert.Equal(initial.Surface.Snapshot.Materials, final.Surface.Snapshot.Materials);
            Assert.Equal(initial.Surface.Snapshot.Textures, final.Surface.Snapshot.Textures);
            using (var submitted = session.Render(final, Width, Height, rayGiOptions: gi))
            {
                Assert.Equal(0, submitted.Statistics.ReadbackCount);
                Assert.Equal(1, submitted.Statistics.SubmissionCount);
                Assert.InRange(submitted.Statistics.PendingFrames, 0, 3);
                bool hardware = mode == LightingMode.HardwareRayTracing;
                var vertices = final.Surface.Snapshot.Vertices;
                bool needsBuild = hardware && ExceedsRefitDistance(lastBuildVertices, vertices);
                Assert.Equal(needsBuild ? 1 : 0, session.RayGiStatistics!.AccelerationBuilds);
                Assert.Equal(hardware && !needsBuild ? 1 : 0, session.RayGiStatistics.AccelerationUpdates);
                if (needsBuild)
                {
                    lastBuildVertices = vertices.ToArray();
                    expectedRebuilds++;
                }
                else if (hardware) expectedRefits++;
                cacheBaseline ??= submitted.Statistics.CachedResources;
                Assert.InRange(submitted.Statistics.CachedResources, 0, cacheBaseline.Value + 4);
            }

            if (i is 1 or 6 or 12)
            {
                // The preceding Render built this exact geometry. Re-capture must reuse it.
                var actual = session.Capture(final, Width, Height, rayGiOptions: gi);
                Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
                Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
                using var fresh = new D3D12FrameSession();
                var reference = fresh.Capture(final, Width, Height, rayGiOptions: gi);
                Assert.Equal(reference.Rgba, actual.Rgba);
                if (i == 1)
                {
                    firstAdvancedVertices = final.Surface.Snapshot.Vertices.ToArray();
                    firstAdvancedImage = actual.Rgba;
                }
            }
        }

        if (mode == LightingMode.HardwareRayTracing)
        {
            Assert.True(expectedRefits > 0, "Small changes to the receiver and wall must refit.");
            Assert.True(expectedRebuilds > 0, "Gravity must move the wall far enough to require a fresh build.");
        }
        Assert.NotNull(final);
        var finalVertices = final.Surface.Snapshot.Vertices.ToArray();
        Assert.True(controller.BoneWorldMatrices[0].Translation.X > initialWorlds[0].Translation.X + .01f,
            "The receiver must really follow its VMD bone track.");
        Assert.True(controller.BoneWorldMatrices[1].Translation.Y < initialWorlds[1].Translation.Y - 5,
            "The red bounce wall must really follow Bullet gravity feedback.");
        Assert.True(finalVertices[0].Position.X > initialVertices[0].Position.X + .01f);
        Assert.True(finalVertices[4].Position.Y < initialVertices[4].Position.Y - 5);
        var finalOff = session.Capture(final, Width, Height);
        var finalGi = session.Capture(final, Width, Height, rayGiOptions: gi);
        Assert.NotEqual(initialContribution, CenterRedContribution(finalOff, finalGi));
        Assert.NotEqual(initialGi.Rgba, finalGi.Rgba);

        // Absolute seek restarts Bullet at the requested animated pose, then reset/replay restores frame 1.
        var sought = Frame(controller.Sample(8).Surface);
        Assert.Equal(0, controller.PhysicsLastStepCount);
        Assert.Equal(initialVertices[4].Position.Y, sought.Surface.Snapshot.Vertices[4].Position.Y);
        controller.ResetPhysics(0);
        var replay = Frame(controller.Advance(1, 1d / 30).Surface);
        Assert.Equal(firstAdvancedVertices, replay.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(firstAdvancedImage, session.Capture(replay, Width, Height, rayGiOptions: gi).Rgba);
        session.WaitForIdle();
    }

    private static bool ExceedsRefitDistance(ReadOnlySpan<SceneVertex> built, ReadOnlySpan<SceneVertex> current)
    {
        // The fixture has two translating quads in one DXR sample block. Their corners span
        // the sampled bounds, so we can check the quarter-diagonal policy using the original mesh.
        var min = built[0].Position;
        var max = min;
        float maxDistance = 0;
        for (int i = 0; i < built.Length; i++)
        {
            min = Vector3.Min(min, built[i].Position);
            max = Vector3.Max(max, built[i].Position);
            maxDistance = Math.Max(maxDistance, Vector3.Distance(built[i].Position, current[i].Position));
        }
        return maxDistance > Vector3.Distance(min, max) / 4;
    }

    private static long CenterRedContribution(RenderedFrame off, RenderedFrame on)
    {
        long total = 0;
        // Stay well inside the receiver despite its small VMD translation; exclude silhouette movement.
        for (int y = Height / 2 - 3; y <= Height / 2 + 3; y++)
            for (int x = Width / 2 - 3; x <= Width / 2 + 3; x++)
                total += on.Rgba[(y * Width + x) * 4] - off.Rgba[(y * Width + x) * 4];
        return total;
    }

    private static OriginalFrameScene Frame(OriginalStyleScene surface) => new(surface,
        options: new(OriginalAlphaBlendMode.Normal, true, false, null, null),
        materials: [new(false, false, false), new(false, false, false)]);

    private static CharacterAnimationController Controller()
    {
        var template = D3D12RayGiTests.Bounce().Surface;
        var mesh = template.Snapshot;
        var vertices = mesh.Vertices.ToArray().Select((v, i) => new PmxVertex
        {
            Position = i < 4 ? v.Position : new(v.Position.X * 4 / 15, v.Position.Y * 4 / 15, v.Position.Z),
            Normal = v.Normal, Uv = v.Uv, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [i < 4 ? 0 : 1, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 0,
        }).ToArray();
        var indices = mesh.Indices.ToArray();
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8,
                AdditionalUvCount = 0, VertexIndexSize = 4, TextureIndexSize = 4, MaterialIndexSize = 4,
                BoneIndexSize = 4, MorphIndexSize = 4, RigidBodyIndexSize = 4 },
            Name = "animated GI acceptance", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = vertices,
            Faces = Enumerable.Range(0, indices.Length / 3).Select(i => new PmxTriangle((int)indices[i * 3],
                (int)indices[i * 3 + 1], (int)indices[i * 3 + 2])).ToArray(),
            Textures = [],
            Materials = mesh.Materials.Select((m, i) => new PmxMaterial
            {
                Name = m.Name, EnglishName = "", Diffuse = i == 0 ? new(.35f, .35f, .35f, 1) : m.Diffuse,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero,
                DrawMode = i == 0 ? PmxDrawModeFlags.None : PmxDrawModeFlags.BothFaces,
                EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1,
                SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1,
                Memo = "", FaceVertexCount = m.IndexCount,
            }).ToArray(),
            Bones = [Bone("receiver", Vector3.Zero), Bone("falling wall", new(0, 0, -8))],
            Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [],
            RigidBodies = [new() { Name = "falling bounce wall", EnglishName = "", BoneIndex = 1,
                Group = 0, CollisionGroup = 0, Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f),
                Translation = new(0, 0, -8), Rotation = Vector3.Zero, Mass = 1, TranslationDamping = 0,
                RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }],
        };
        var motion = new VmdMotion { BoneKeyframes = [
            new() { BoneName = "receiver", Frame = 0 },
            new() { BoneName = "receiver", Frame = 60, Translation = Vector3.UnitX },
        ] };
        var adapted = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        var surface = new OriginalStyleScene(adapted.Snapshot.WithCamera(mesh.Camera).WithLighting(mesh.LightDirection),
            adapted.Materials, template.Profile);
        return new(model, motion, surface, PmxOriginalOutlineAdapter.FromRestPose(model));
    }

    private static PmxBone Bone(string name, Vector3 position) => new()
    {
        Name = name, EnglishName = "", Position = position, ParentIndex = -1, DeformDepth = 0,
        Flags = PmxBoneFlags.AllowTranslate | PmxBoneFlags.AllowRotate,
    };
}
