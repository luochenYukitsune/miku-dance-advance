using System.Collections;
using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightingEditorTests
{
    private static NativeSceneLight Light(SceneLightKind kind = SceneLightKind.Point) =>
        new(Guid.NewGuid(), "天花灯", kind, new(1, 2, 3), Quaternion.Identity, Vector3.One);

    [Fact]
    public void SnapshotCopiesInputAndExposesOnlyReadOnlyLights()
    {
        var light = Light();
        var input = new[] { light };
        var snapshot = new SceneLightingSnapshot(input);
        input[0] = light with { Position = Vector3.Zero };
        Assert.Equal(new Vector3(1, 2, 3), snapshot.Lights[0].Position);
        Assert.Throws<NotSupportedException>(() => ((IList)snapshot.Lights)[0] = Light());
        Assert.Empty(SceneLightingSnapshot.Empty.Lights);
    }

    [Fact]
    public void RotationDefinesLightTravelDirectionAlongLocalNegativeZ()
    {
        var light = Light(SceneLightKind.Spot) with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) };
        var snapshot = new SceneLightingSnapshot([light]);
        Assert.True(Vector3.Distance(-Vector3.UnitX, snapshot.Lights[0].Direction) < 1e-6f);
        Assert.Equal(-Vector3.UnitZ, Light(SceneLightKind.Directional).Direction);
    }

    [Fact]
    public void SnapshotRejectsDuplicateEmptyAndTooManyIdentities()
    {
        var light = Light();
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([light, light]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([light with { Id = Guid.Empty }]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([null!]));
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot(Enumerable.Range(0, SceneLightingSnapshot.MaxLights + 1).Select(_ => Light() with { Enabled = false })));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("kind")]
    [InlineData("position")]
    [InlineData("rotation-nan")]
    [InlineData("rotation-zero")]
    [InlineData("rotation-not-unit")]
    [InlineData("color-negative")]
    [InlineData("color-infinite")]
    [InlineData("intensity-negative")]
    [InlineData("intensity-nan")]
    [InlineData("range-zero")]
    [InlineData("range-infinite")]
    [InlineData("inner-negative")]
    [InlineData("inner-nan")]
    [InlineData("cone-equal")]
    [InlineData("outer-wide")]
    public void SnapshotRejectsInvalidParametersInsteadOfClamping(string field)
    {
        var light = Light();
        light = field switch
        {
            "name" => light with { Name = " " },
            "kind" => light with { Kind = (SceneLightKind)99 },
            "position" => light with { Position = new(float.NaN, 0, 0) },
            "rotation-nan" => light with { Rotation = new(float.NaN, 0, 0, 1) },
            "rotation-zero" => light with { Rotation = default },
            "rotation-not-unit" => light with { Rotation = new(0, 0, 0, 2) },
            "color-negative" => light with { Color = new(-1, 0, 0) },
            "color-infinite" => light with { Color = new(0, float.PositiveInfinity, 0) },
            "intensity-negative" => light with { Intensity = -1 },
            "intensity-nan" => light with { Intensity = float.NaN },
            "range-zero" => light with { Range = 0 },
            "range-infinite" => light with { Range = float.PositiveInfinity },
            "inner-negative" => light with { InnerConeRadians = -.1f },
            "inner-nan" => light with { InnerConeRadians = float.NaN },
            "cone-equal" => light with { InnerConeRadians = .7f, OuterConeRadians = .7f },
            "outer-wide" => light with { OuterConeRadians = MathF.PI },
            _ => throw new ArgumentException(field),
        };
        Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([light]));
    }

    [Fact]
    public void ZeroIntensityHdrColorAndNinetyDegreeOuterConeAreValid()
    {
        var light = Light(SceneLightKind.Spot) with { Intensity = 0, Color = new(3, 2, 0), InnerConeRadians = 0, OuterConeRadians = MathF.PI / 2 };
        Assert.Equal(light, Assert.Single(new SceneLightingSnapshot([light]).Lights));
    }

    [Fact]
    public void AddUpdateRemoveSelectAndUndoRedoKeepStableIdentities()
    {
        var editor = new SceneLightingEditor();
        var light = Light();
        editor.Add(light);
        Assert.Equal(light.Id, editor.SelectedId);
        editor.Update(light with { Name = "门窗补光", Position = new(5, 6, 7) });
        Assert.True(editor.Remove(light.Id));
        Assert.Null(editor.SelectedId);
        Assert.True(editor.Undo());
        Assert.Equal(light.Id, editor.SelectedId);
        Assert.Equal("门窗补光", Assert.Single(editor.Lights).Name);
        Assert.True(editor.Undo());
        Assert.Equal(light, Assert.Single(editor.Lights));
        Assert.True(editor.Undo());
        Assert.Empty(editor.Lights);
        Assert.False(editor.Undo());
        Assert.True(editor.Redo());
        editor.Select(null);
        Assert.Null(editor.SelectedId);
        Assert.True(editor.Redo());
        Assert.True(editor.Redo());
        Assert.False(editor.Redo());
    }

    [Fact]
    public void RejectedOperationsDoNotChangeStateOrHistory()
    {
        var editor = new SceneLightingEditor();
        var light = Light();
        editor.Add(light);
        Assert.Throws<ArgumentException>(() => editor.Add(light));
        Assert.Throws<ArgumentException>(() => editor.Update(Light()));
        Assert.Throws<ArgumentException>(() => editor.Update(light with { Range = -1 }));
        Assert.Throws<ArgumentException>(() => editor.Select(Guid.NewGuid()));
        Assert.False(editor.Remove(Guid.NewGuid()));
        Assert.Equal(light, Assert.Single(editor.Lights));
        Assert.True(editor.Undo());
        Assert.Empty(editor.Lights);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void GesturePreviewCommitsToOneUndoEntryAndCancelRestoresRedo()
    {
        var editor = new SceneLightingEditor();
        var light = Light();
        editor.Add(light);
        editor.BeginEdit();
        for (int i = 1; i <= 20; i++) editor.Update(light with { Position = new(i, 2, 3) });
        Assert.Equal(20, editor.Lights[0].Position.X);
        editor.CommitEdit();
        Assert.True(editor.Undo());
        Assert.Equal(light, editor.Lights[0]);
        Assert.True(editor.CanRedo);
        editor.BeginEdit();
        editor.Update(light with { Intensity = 4 });
        editor.CancelEdit();
        Assert.Equal(light, editor.Lights[0]);
        Assert.True(editor.CanRedo);
        Assert.True(editor.Redo());
        Assert.Equal(20, editor.Lights[0].Position.X);
    }

    [Fact]
    public void NoOpGestureKeepsRedoAndNestedGesturesAreRejected()
    {
        var editor = new SceneLightingEditor();
        var light = Light();
        editor.Add(light);
        editor.Update(light with { Intensity = 2 });
        editor.Undo();
        editor.BeginEdit();
        Assert.Throws<InvalidOperationException>(() => editor.BeginEdit());
        Assert.Throws<InvalidOperationException>(() => editor.Undo());
        Assert.Throws<InvalidOperationException>(() => editor.Redo());
        editor.Update(light with { Intensity = 3 });
        editor.Update(light);
        editor.CommitEdit();
        Assert.True(editor.CanRedo);
        editor.Update(light);
        Assert.True(editor.CanRedo);
        Assert.Throws<InvalidOperationException>(() => editor.CommitEdit());
        Assert.Throws<InvalidOperationException>(() => editor.CancelEdit());
    }

    [Fact]
    public void PurposeIsExplicitStableKeyedUndoableAndIndoorOnlyAppliesToEnvironment()
    {
        var editor = new SceneLightingEditor();
        const string key = "pmm-model:16";
        Assert.Equal(new ModelPurpose(ModelRole.Unspecified), editor.GetModelRole(key));
        editor.SetModelRole(key, ModelRole.Environment, true);
        Assert.Equal(new ModelPurpose(ModelRole.Environment, true), editor.GetModelRole(key));
        Assert.Throws<ArgumentException>(() => editor.SetModelRole(" ", ModelRole.Prop));
        Assert.Throws<ArgumentException>(() => editor.SetModelRole(key, (ModelRole)99));
        Assert.Throws<ArgumentException>(() => editor.SetModelRole(key, ModelRole.Character, true));
        Assert.True(editor.Undo());
        Assert.Empty(editor.ModelRoles);
        Assert.True(editor.Redo());
        Assert.Equal(new ModelPurpose(ModelRole.Environment, true), editor.ModelRoles[key]);
    }

    [Fact]
    public void VersionedJsonRoundTripsEveryLightFieldSelectionAndPurposes()
    {
        var editor = new SceneLightingEditor();
        var light = Light(SceneLightKind.Spot) with { Rotation = Quaternion.CreateFromYawPitchRoll(.2f, -.3f, .4f), Color = new(.1f, .5f, 3), Intensity = 2.4f, Range = 24, InnerConeRadians = .1f, OuterConeRadians = 1.3f, Enabled = false, CastShadows = false };
        editor.Add(light);
        editor.SetModelRole("pmm-model:16", ModelRole.Environment, true);
        editor.SetModelRole("pmm-model:1", ModelRole.Character);
        var restored = new SceneLightingEditor();
        Assert.True(restored.TryLoadSidecar(editor.SerializeSidecar(), out var error), error);
        Assert.Equal(light, Assert.Single(restored.Lights));
        Assert.Equal(light.Id, restored.SelectedId);
        Assert.Equal(editor.ModelRoles.OrderBy(p => p.Key), restored.ModelRoles.OrderBy(p => p.Key));
        Assert.False(restored.CanUndo);
        Assert.False(restored.CanRedo);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("version")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("invalid-light")]
    [InlineData("unknown-selection")]
    [InlineData("duplicate-role")]
    [InlineData("invalid-role")]
    public void InvalidJsonKeepsActivePreviewHistorySelectionAndPurposes(string defect)
    {
        var editor = new SceneLightingEditor();
        var light = Light();
        editor.Add(light);
        editor.SetModelRole("model:1", ModelRole.Environment, true);
        var document = JsonNode.Parse(editor.SerializeSidecar())!.AsObject();
        switch (defect)
        {
            case "version": document["version"] = 999; break;
            case "missing": document.Remove("lights"); break;
            case "unknown": document["future"] = true; break;
            case "invalid-light": document["lights"]![0]!["range"] = -1; break;
            case "unknown-selection": document["selectedId"] = Guid.NewGuid(); break;
            case "duplicate-role": document["modelRoles"]!.AsArray().Add(document["modelRoles"]![0]!.DeepClone()); break;
            case "invalid-role": document["modelRoles"]![0]!["role"] = "GuessIndoor"; break;
        }
        editor.BeginEdit();
        editor.Update(light with { Intensity = 3 });
        var prior = editor.SerializeSidecar();
        Assert.False(editor.TryLoadSidecar(defect == "malformed" ? "{" : document.ToJsonString(), out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(prior, editor.SerializeSidecar());
        Assert.True(editor.IsEditing);
        editor.CancelEdit();
        Assert.Equal(light, Assert.Single(editor.Lights));
        Assert.True(editor.Undo());
        Assert.Empty(editor.ModelRoles);
    }

    [Fact]
    public void SidecarFileSaveLeavesProjectBytesUntouchedAndFailedFileLoadKeepsState()
    {
        string directory = Path.Combine(Path.GetTempPath(), "native-light-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "project.pmm");
        string sidecar = SceneLightingEditor.GetSidecarPath(project);
        try
        {
            File.WriteAllBytes(project, [1, 4, 9, 16]);
            var editor = new SceneLightingEditor();
            editor.Add(Light());
            editor.SaveSidecar(sidecar);
            Assert.Equal(new byte[] { 1, 4, 9, 16 }, File.ReadAllBytes(project));
            Assert.Throws<ArgumentException>(() => editor.SaveSidecar(project));
            var restored = new SceneLightingEditor();
            Assert.True(restored.TryLoadSidecarFile(sidecar, out var error), error);
            Assert.Equal(editor.SerializeSidecar(), restored.SerializeSidecar());
            File.WriteAllText(sidecar, "{");
            Assert.False(restored.TryLoadSidecarFile(sidecar, out _));
            Assert.False(restored.TryLoadSidecarFile(sidecar + ".missing", out _));
            Assert.Equal(editor.SerializeSidecar(), restored.SerializeSidecar());
        }
        finally
        {
            File.Delete(sidecar);
            File.Delete(project);
            Directory.Delete(directory);
        }
    }
}
