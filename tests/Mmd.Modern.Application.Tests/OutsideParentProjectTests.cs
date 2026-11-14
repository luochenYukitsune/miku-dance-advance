using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OutsideParentProjectTests : IDisposable
{
    private static readonly SceneProjectSaveOptions ExternalAssets = new() { CopyModelAssets = false };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "outside-project-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("No bodies.");
    public OutsideParentProjectTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void EditedBindingOffsetAndCurrentPoseSurviveSaveReopen(int bone)
    {
        var model = EditedMotionProjectTests.Model(); var rest = EditedMotionProjectTests.Rest(model);
        string modelPath = Path.Combine(directory, "model.pmx"); File.WriteAllText(modelPath, "injected fixture");
        var layout = new SceneCompositionEditor(); var lighting = new SceneLightingEditor();
        var child = layout.Add("child", modelPath, ModelRole.Character, rest); var parent = layout.Add("parent", modelPath, ModelRole.Character, rest);
        layout.SetTransform(child.Id, new(5, 2, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f), 2);
        layout.SetTransform(parent.Id, new(-2, 0, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.4f), .5f);
        var asset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = Vector3.UnitY }] });
        using var session = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset), new(parent.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset)], NoPhysics, lighting.Snapshot, Token);
        session.Seek(10, Token); session.SetOutsideParent(child.Id, bone, parent.Id, 0, token: Token);
        session.Seek(20, Token); var expected = session.Frame.Surface.Snapshot.Vertices.ToArray();
        string path = Path.Combine(directory, "outside.mmdscene");
        SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        string json = File.ReadAllText(path); using var document = JsonDocument.Parse(json);
        Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
        Assert.Single(document.RootElement.GetProperty("outsideParents").EnumerateArray());
        using var reopened = SceneAnimationProjectStore.Load(path, (_, _) => new(model, rest), NoPhysics, Token);
        Assert.Equal(expected, reopened.Animation.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(session.OutsideParents, reopened.Animation.OutsideParents);
        session.RemoveOutsideParent(child.Id, bone, Token); Assert.True(session.UndoOutsideParent(Token));
        Assert.Equal(expected, session.Frame.Surface.Snapshot.Vertices);
    }

    [Theory]
    [InlineData("oldVersion")]
    [InlineData("unknownActor")]
    [InlineData("cycle")]
    [InlineData("duplicate")]
    [InlineData("singular")]
    [InlineData("badBone")]
    public void InvalidBindingDocumentRejectsBeforeAnyHostLoader(string kind)
    {
        var model = EditedMotionProjectTests.Model(); var rest = EditedMotionProjectTests.Rest(model);
        string modelPath = Path.Combine(directory, "model.pmx"); File.WriteAllText(modelPath, "fixture");
        var layout = new SceneCompositionEditor(); var lighting = new SceneLightingEditor();
        var a = layout.Add("a", modelPath, ModelRole.Character, rest); var b = layout.Add("b", modelPath, ModelRole.Character, rest);
        var asset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }] });
        using var session = new SceneAnimationSession(layout.Objects,
            [new(a.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset), new(b.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset)], NoPhysics, lighting.Snapshot, Token);
        session.SetOutsideParent(a.Id, 0, b.Id, 0, keepWorld: false, token: Token);
        string path = Path.Combine(directory, "test.mmdscene"); SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        var root = JsonNode.Parse(File.ReadAllText(path))!; var entries = root["outsideParents"]!.AsArray(); var first = entries[0]!;
        switch (kind)
        {
            case "oldVersion": root["version"] = 4; break;
            case "unknownActor": first["parentObjectId"] = Guid.NewGuid(); break;
            case "cycle": var reverse = first.DeepClone(); reverse["objectId"] = b.Id; reverse["parentObjectId"] = a.Id; entries.Add(reverse); break;
            case "duplicate": entries.Add(first.DeepClone()); break;
            case "singular": first["offset"] = new JsonArray(Enumerable.Repeat(0f, 16).Select(value => JsonValue.Create(value) as JsonNode).ToArray()); break;
            default: first["boneIndex"] = -2; break;
        }
        Assert.Throws<JsonException>(() => SceneAnimationProjectStore.Prepare(root.ToJsonString(), path,
            (_, _) => throw new InvalidOperationException("Invalid bindings must fail before model loading."), NoPhysics, Token));
        Assert.False(session.IsFaulted); Assert.Single(session.OutsideParents);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(directory), temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid temp scope.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
