using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.FFmpeg;
using Mmd.Modern.ViewOutput.Images;
using SkiaSharp;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>
/// A native Avalonia/Win32/D3D12 integration probe. It never uses Avalonia.Headless,
/// fake model bytes or a second clock for the live project. UI captures deliberately
/// omit the native child HWND; separate GPU readback is named and reported as such.
/// </summary>
internal static class EditorWorkflowProbe
{
    private static readonly List<string> Checks = [];
    private static readonly List<string> Skipped = [];
    private static readonly Dictionary<string, string> OriginalHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    private static string output = "";
    private static EditorWindow? window;
    private static bool allowWarp;
    private static string? adapter;
    private static int presented;
    private static string inputKind = "generated PmxWriter fixture";
    private static string? suppliedPmd;
    private static string loadedAssemblyPath = "";
    private static string loadedAssemblySha256 = "";
    private static string? expectedAssemblyPath;
    private static string? expectedAssemblySha256;
    private static string? expectedProductDirectory;
    private static readonly Dictionary<string, string> ExpectedProductHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<object> LoadedProductAssemblies = [];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Usage: probe <output-directory> [--pmx=<absolute path>] [--pmd=<absolute path>] [--vmd=<absolute path>] [--ffmpeg=<absolute path> --ffprobe=<absolute path>] [--allow-warp] [--visible-editor [--keep-open]] [--expected-app=<absolute path> --expected-app-sha256=<SHA256> --expected-product-manifest=<absolute path>]");
        output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        allowWarp = args.Contains("--allow-warp", StringComparer.Ordinal);
        var options = args.Skip(1).Where(value => value.StartsWith("--", StringComparison.Ordinal) && value.Contains('='))
            .Select(value => value[2..].Split('=', 2)).ToDictionary(value => value[0], value => value[1], StringComparer.Ordinal);
        try
        {
            bool keepOpen = args.Contains("--keep-open", StringComparer.Ordinal);
            if (keepOpen && !args.Contains("--visible-editor", StringComparer.Ordinal))
                throw new ArgumentException("--keep-open requires --visible-editor.");
            foreach (string key in new[] { "pmx", "pmd", "vmd", "ffmpeg", "ffprobe" })
                if (options.TryGetValue(key, out var path)) options[key] = RequireInput(path);
            suppliedPmd = options.GetValueOrDefault("pmd");
            ConfigureProductExpectations(options);
            VerifyLoadedProducts();
            if (args.Contains("--visible-editor"))
                typeof(App).Assembly.GetType("Mmd.Modern.App.WindowsAppIdentity")?.GetMethod("Initialize")?.Invoke(null, null);
            AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().SetupWithoutStarting();
            if (args.Contains("--dialogs-only"))
            {
                inputKind = "dialog layout diagnostic only; no full workflow";
                var shell = new EditorShellViewModel();
                window = new EditorWindow(false) { DataContext = shell, ShowInTaskbar = args.Contains("--visible-editor"),
                    WindowStartupLocation = args.Contains("--visible-editor") ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual };
                if (!args.Contains("--visible-editor")) window.Position = new PixelPoint(-20000, -20000);
                window.Show(); PumpLayout(); VerifyPreferences(shell);
            }
            else if (options.TryGetValue("portable-open", out var portableProject))
            {
                inputKind = "existing portable PMA reopen diagnostic only; no full workflow";
                string projectHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(portableProject)));
                var shell = new EditorShellViewModel();
                shell.Workspace.Open(portableProject);
                shell.RefreshWorkspace(shell.Workspace.Session!.Objects.First().Id);
                window = new EditorWindow(false) { DataContext = shell, ShowInTaskbar = args.Contains("--visible-editor"),
                    WindowStartupLocation = args.Contains("--visible-editor") ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual };
                if (!args.Contains("--visible-editor")) window.Position = new PixelPoint(-20000, -20000);
                var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
                Exception? error = null; viewport.Error += value => error = value;
                viewport.FramePresented += _ => presented++;
                window.Show(); PumpLayout();
                WaitForFrame(viewport, () => error, "existing portable PMA installed-renderer viewport");
                Assert(viewport.HasChildWindow && !viewport.IsWarp, "portable reopen creates an actual installed hardware D3D12 child viewport");
                adapter = viewport.AdapterName;
                Assert(shell.Browser.Models.Count == shell.Workspace.Session.Objects.Count && shell.Browser.Models.Count > 0
                    && shell.Workspace.Session.Objects.All(item => File.Exists(item.SourcePath)), "existing PMA resolves its packaged models in the installed editor");
                Assert(!shell.Workspace.IsDirty, "portable reopen preserves a clean project");
                shell.CurrentFrame = 9; PumpLayout(); WaitForFrame(viewport, () => error, "portable PMA initialized target pose");
                CaptureGpuReadback(shell, viewport, "portable-open-d3d12-readback.png");
                CaptureControls("portable-open-controls-hwnd-omitted.png");
                Assert(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(portableProject))) == projectHash,
                    "installed portable reopen and seek preserve the original PMA bytes");
            }
            else Run(options);
            VerifyOriginals();
            VerifyLoadedProducts();
            WriteReport(null);
            Console.WriteLine($"PASS {Checks.Count} native editor workflow checks; {Skipped.Count} explicitly skipped. Evidence: {output}");
            if (args.Contains("--visible-editor") && window is { IsVisible: true })
            {
                // Keep the actual formal editor available for human review. This is a probe
                // of installed assemblies, not a different product window or a delivered EXE.
                window.Activate();
                var reviewStartedAtUtc = DateTimeOffset.UtcNow;
                File.WriteAllText(Path.Combine(output, "review-session.json"), JsonSerializer.Serialize(new
                {
                    status = keepOpen ? "awaiting-manual-close" : "awaiting-manual-close-or-timeout",
                    startedAtUtc = reviewStartedAtUtc,
                    automaticCloseAfterSeconds = keepOpen ? (int?)null : 600,
                    automaticCloseAtUtc = keepOpen ? (DateTimeOffset?)null : reviewStartedAtUtc.AddMinutes(10),
                    workflowReportScope = "Completed probe checks only; human visual review remains pending.",
                    humanVisualApprovalRecorded = false,
                    finalDeliveryApproved = false
                }, Json));
                var review = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
                timer.Tick += (_, _) => review.Continue = false;
                window.Closed += (_, _) => review.Continue = false;
                if (!keepOpen) timer.Start();
                try { Dispatcher.UIThread.PushFrame(review); }
                finally { timer.Stop(); }
            }
            return 0;
        }
        catch (Exception error)
        {
            try { if (window is { IsVisible: true }) CaptureControls("workflow-failure-controls.png"); } catch { }
            WriteReport(error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            // Detach before close so a diagnostic probe never opens the unsaved-document prompt.
            if (window is not null)
            {
                var shell = window.DataContext as EditorShellViewModel;
                window.DataContext = null;
                window.Close();
                shell?.Dispose();
            }
        }
    }

    private static void Run(IReadOnlyDictionary<string, string> options)
    {
        bool hasFfmpeg = options.TryGetValue("ffmpeg", out var ffmpeg), hasFfprobe = options.TryGetValue("ffprobe", out var ffprobe);
        if (hasFfmpeg != hasFfprobe) throw new ArgumentException("Provide FFmpeg and FFprobe together.");
        string inputDirectory = Path.Combine(output, "copied-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputDirectory);
        var pmxPath = options.TryGetValue("pmx", out var realPmx) ? CopyModel(realPmx, inputDirectory) : WriteFixture(inputDirectory);
        if (realPmx is not null) inputKind = "copied real PMX; original files read-only";
        var asset = EditorProjectWorkspace.LoadModelAsset(pmxPath);
        int boneIndex = Enumerable.Range(0, asset.Model.Bones.Count).FirstOrDefault(index =>
            asset.Model.Bones[index].Flags.HasFlag(PmxBoneFlags.AllowTranslate), -1);
        Assert(boneIndex >= 0, "input PMX supplies an explicitly translatable authored bone");
        var bone = asset.Model.Bones[boneIndex];
        string motionPath;
        if (options.TryGetValue("vmd", out var realVmd))
        {
            Remember(realVmd); motionPath = Path.Combine(inputDirectory, "copied-motion.vmd"); File.Copy(realVmd, motionPath);
        }
        else
        {
            motionPath = Path.Combine(inputDirectory, "generated-motion.vmd");
            File.WriteAllBytes(motionPath, VmdWriter.ToBytes(new VmdMotion { ModelName = "Workflow fixture",
                BoneKeyframes = [new() { BoneName = bone.Name, Frame = 0 }, new() { BoneName = bone.Name, Frame = 12, Translation = new(.25f, 0, 0) }] }));
        }

        var shell = new EditorShellViewModel();
        // Loading/writing the user's local preferences is intentionally disabled for a verification run.
        window = new EditorWindow(false) { DataContext = shell, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        Exception? viewportError = null;
        viewport.Error += error => viewportError = error;
        viewport.FramePresented += _ => presented++;
        window.Show(); PumpLayout();
        WaitForFrame(viewport, () => viewportError, "empty native D3D12 viewport");
        Assert(viewport.HasChildWindow, "formal editor creates a real Win32 child viewport");
        Assert(allowWarp || !viewport.IsWarp, "native viewport uses hardware D3D12 (WARP requires explicit --allow-warp)");
        adapter = viewport.AdapterName;

        Guid id = shell.Workspace.AddModel(pmxPath, asset, ModelRole.Character);
        shell.RefreshWorkspace(id); PumpLayout();
        Assert(shell.Browser.Models.Count == 1 && shell.Browser.SelectedModel?.ObjectId == id, "explicit-role import refreshes the formal model browser and selects the actual actor");
        Assert(shell.Workspace.Session!.Objects.Single().Role == ModelRole.Character, "role is authored explicitly; never inferred from model name");
        VerifyGroups(shell.Browser, asset.Model);
        VerifyCompactModelBrowser(shell, boneIndex);
        shell.Browser.SelectedBoneIndex = boneIndex; PumpLayout();
        Assert(shell.Animation.ObjectId == id && shell.Animation.SelectedBoneIndex == boneIndex && shell.Animation.CanTranslate,
            "browser bone selection drives the editable animation inspector");
        Assert(shell.ImportMotionCommand.CanExecute(null), "motion import is enabled for the selected real model");
        shell.Workspace.BindMotion(id, EditorProjectInterchange.PrepareVmd(motionPath, asset.Model)); PumpLayout();
        string importedSha = shell.Workspace.Motions.Single().Asset!.Sha256;
        var preservedSceneSections = SceneSections(shell.Workspace.Motions.Single().Motion);
        // Supplied VMDs can already have a frame-6 key. Baseline registration is accepted
        // explicitly here; cancellation/confirmation behavior is exercised separately below.
        shell.Animation.ConfirmOverwriteAsync = _ => Task.FromResult(true);
        int editFrame = 6;
        shell.CurrentFrame = editFrame; PumpLayout();
        var inspector = window.GetVisualDescendants().OfType<EditorAnimationInspector>().Single();
        var position = inspector.FindControl<NumericUpDown>("BonePositionX")!;
        Assert(position.IsEnabled && ReferenceEquals(inspector.DataContext, shell.Animation), "native inspector is bound to the active animation model");
        VerifyNativeInvalidInput(shell, inspector, position);
        TypeNumber(position, "1.250");
        Assert(position is EditorNumericUpDown { IsInputValid: true } && position.Value == 1.25m
            && shell.Animation.PositionX == 1.25m && shell.Animation.HasDraft && shell.Animation.CanRegister,
            "correcting native numeric text clears validation and restores authored bone registration through the binding");
        Click(inspector.FindControl<Button>("RegisterAnimationKeyframeButton")!);
        PumpUntil(() => !shell.Animation.IsBusy, "register keyframe command", 30);
        Assert(GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 1.25f, "native register button commits the edited pose to the project motion");
        Assert(shell.Workspace.Motions.Single().Asset!.Sha256 != importedSha, "editing replaces the immutable VMD asset bytes");
        Assert(SceneSections(shell.Workspace.Motions.Single().Motion) == preservedSceneSections, "bone registration retains imported camera, light, shadow and IK sections");

        var historyBeforeConflict = History(shell.Workspace.Session!);
        int asked = 0;
        shell.Animation.ConfirmOverwriteAsync = plan => { asked++; Assert(plan.Conflicts.Count != 0, "overwrite confirmation receives actual conflict addresses"); return Task.FromResult(false); };
        TypeNumber(position, "2.500"); Await(shell.Animation.RegisterKeyframeAsync(), "cancel conflicting registration");
        Assert(asked == 1 && GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 1.25f,
            "cancelling a real keyframe collision retains the authored value");
        Assert(History(shell.Workspace.Session!) == historyBeforeConflict, "cancelled keyframe overwrite creates no project history entry");
        shell.Animation.ConfirmOverwriteAsync = _ => Task.FromResult(true);
        Await(shell.Animation.RegisterKeyframeAsync(), "confirm conflicting registration");
        Assert(GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 2.5f, "confirmed collision atomically replaces the keyframe");
        Assert(History(shell.Workspace.Session!).Undo == historyBeforeConflict.Undo + 1, "confirmed collision creates exactly one project Undo entry");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 1.25f && shell.Animation.PositionX == 1.25m,
            "shell Undo refreshes both authored project motion and native inspector");
        var undone = shell.Workspace.Session!.CaptureAuthoredContent();
        var beforeReads = History(shell.Workspace.Session!);
        for (int index = 0; index < 10; index++) _ = shell.Workspace.Motions;
        Assert(undone.Motions.Single().Motion.BoneKeyframes.Single(key => key.BoneName == bone.Name && key.Frame == editFrame).Translation.X == 1.25f,
            "new authored-content snapshot reflects Undo rather than a stale motion cache");
        Assert(History(shell.Workspace.Session!) == beforeReads, "authored-content snapshots are read-only and create no Undo history");
        shell.RedoCommand.Execute(null); PumpLayout();
        Assert(GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 2.5f && shell.Animation.PositionX == 2.5m,
            "shell Redo restores the replacement key and inspector value");

        SendEditorKey(Key.Z, KeyModifiers.Control);
        Assert(GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 1.25f, "native routed Ctrl+Z undoes the actual authored key");
        SendEditorKey(Key.Y, KeyModifiers.Control);
        Assert(GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 2.5f, "native routed Ctrl+Y restores the actual authored key");
        SendEditorKey(Key.Z, KeyModifiers.Control);
        SendEditorKey(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
        Assert(GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 2.5f && shell.Animation.PositionX == 2.5m,
            "native routed Ctrl+Shift+Z restores the authored key and inspector");

        VerifyNativeMutationGuards(shell, position, id, bone.Name, editFrame);
        VerifyDraftRetention(shell, id, asset, pmxPath, boneIndex, editFrame);
        VerifyPreferences(shell);
        VerifyModelProperties(shell, id);
        VerifyInterpolation(shell, id, bone.Name, editFrame);
        VerifyInterchange(shell, id, asset, pmxPath, boneIndex, editFrame);
        VerifyRigSettings(shell, asset, pmxPath, boneIndex);
        VerifyPreviewQuality(shell, viewport, () => viewportError);
        VerifyLightingAndBoneGuides(shell, viewport, () => viewportError, id, boneIndex);
        shell.Browser.HierarchyTab = 0; shell.Browser.SelectedBoneIndex = boneIndex;
        shell.CurrentFrame = editFrame; PumpLayout();

        // Ensure a bounded >=3-frame export interval even if the supplied VMD is shorter.
        if (shell.Workspace.Session!.DurationFrames < 12)
        {
            shell.CurrentFrame = 12; shell.Animation.PositionX = .25m;
            Await(shell.Animation.RegisterKeyframeAsync(), "register terminal key");
        }
        VerifyNativeTimeline(shell, bone.Name, editFrame);
        WaitForFrame(viewport, () => viewportError, "imported model native D3D12 viewport");
        CaptureControls("workflow-black-controls-hwnd-omitted.png");
        shell.SelectedThemeIndex = 1; PumpLayout();
        Assert(window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light, "loaded editor switches to white theme");
        CaptureControls("workflow-white-controls-hwnd-omitted.png");
        shell.SelectedThemeIndex = 0; window.Width = 1100; window.Height = 720; PumpLayout();
        Assert(window.Bounds.Width >= window.MinWidth && window.Bounds.Height >= window.MinHeight, "formal workflow respects minimum native window dimensions");
        Assert(position.Bounds.Width > 30 && position.Bounds.Height > 15, "selected bone inspector remains measurable at minimum editor size");
        CaptureControls("workflow-black-minimum-controls-hwnd-omitted.png");
        window.Width = 1480; window.Height = 920; PumpLayout();

        string expectedMotionSha = shell.Workspace.Motions.Single().Asset!.Sha256;
        string projectPath = Path.Combine(output, "workflow-portable.pma");
        shell.Workspace.Save(projectPath);
        Assert(!shell.Workspace.IsDirty && shell.Workspace.ProjectPath == projectPath, "PMA save updates the formal document path and clears dirty state");
        Assert(PackageContainsHash(projectPath, expectedMotionSha), "saved PMA contains the exact edited VMD resource bytes");
        var historyBeforeSaveShortcut = History(shell.Workspace.Session!);
        SendEditorKey(Key.S, KeyModifiers.Control);
        PumpUntil(() => !shell.IsBusy && shell.OperationStatus == EditorLocalization.Text("工程已保存。"), "native routed Ctrl+S completes the existing-path PMA save", 60);
        Assert(PackageContainsHash(projectPath, expectedMotionSha) && History(shell.Workspace.Session!) == historyBeforeSaveShortcut,
            "native routed Ctrl+S writes the existing PMA path without changing authored motion or history");
        VerifyInlineLightSaveAndClose(shell, projectPath);
        string expectedNativeLights = JsonSerializer.Serialize(shell.Workspace.Session!.Lighting.Lights, Json);
        string expectedSceneMotion = JsonSerializer.Serialize(shell.Workspace.Session.CaptureAuthoredContent().SceneMotion?.Motion, Json);
        DeleteOwnedInput(inputDirectory);
        Assert(!File.Exists(pmxPath) && !File.Exists(motionPath), "only task-owned copied model/motion inputs are removed before portable reopen");
        shell.Workspace.Open(projectPath); PumpLayout();
        Assert(shell.Workspace.Motions.Single().Asset?.Sha256 == expectedMotionSha, "portable PMA reopen retains the exact edited immutable VMD bytes");
        Assert(GetBoneKey(shell, id, bone.Name, editFrame).Translation.X == 2.5f, "PMA reopens edited keys after copied original resources have been deleted");
        Assert(shell.Workspace.Session!.Objects.All(item => File.Exists(item.SourcePath)), "PMA reopen resolves packaged model resources independently");
        Assert(JsonSerializer.Serialize(shell.Workspace.Session.Lighting.Lights, Json) == expectedNativeLights,
            "portable PMA reopen preserves inline-authored native light identity, name, color, intensity, position and orientation");
        Assert(JsonSerializer.Serialize(shell.Workspace.Session.CaptureAuthoredContent().SceneMotion?.Motion, Json) == expectedSceneMotion,
            "portable PMA reopen preserves the exact registered main-light direction and intensity-combined RGB scene track");
        Assert(!shell.Workspace.IsDirty && !shell.Workspace.Session.CanUndo && !shell.Workspace.Session.CanRedo,
            "portable reopen starts a clean document with empty transient history");
        shell.Browser.SelectedBoneIndex = boneIndex; shell.CurrentFrame = editFrame; PumpLayout();
        WaitForFrame(viewport, () => viewportError, "reopened portable model native viewport");
        VerifyNativePlayback(shell, viewport, () => viewportError);
        CaptureGpuReadback(shell, viewport, "workflow-project-d3d12-readback.png");
        VerifyExports(shell, ffmpeg, ffprobe);
        VerifyOriginals();
    }

    private static void VerifyGroups(EditorModelBrowserViewModel browser, PmxModel model)
    {
        for (int index = 0; index < model.DisplayFrames.Count; index++)
        {
            var frame = model.DisplayFrames[index];
            int[] bones = frame.Targets.Where(value => value.Type == PmxDisplayTargetType.Bone && value.Index >= 0 && value.Index < model.Bones.Count)
                .Select(value => value.Index).Distinct().ToArray();
            if (bones.Length == 0) continue;
            var actual = browser.BoneGroups.Single(value => value.Key == "display:" + index.ToString(CultureInfo.InvariantCulture));
            Assert(actual.Children.Select(value => value.Index!.Value).SequenceEqual(bones), "browser preserves authored display-frame bone order: " + actual.DisplayName);
        }
        Assert(browser.BoneGroups.SelectMany(value => value.Children).Select(value => value.Index!.Value).Distinct().Count() == model.Bones.Count,
            "authored groups plus ungrouped remainder expose every actual PMX bone");
        if (model.DisplayFrames.Count == 0) Skipped.Add("Input PMX has no authored display frames; ungrouped fallback checked.");
    }

    private static void VerifyCompactModelBrowser(EditorShellViewModel shell, int boneIndex)
    {
        var browser = window!.GetVisualDescendants().OfType<EditorModelBrowser>().Single();
        var disclosure = browser.FindControl<ToggleButton>("AnimationElementsExpander")!;
        var hierarchy = browser.FindControl<TabControl>("AnimationElements")!;
        var search = browser.FindControl<TextBox>("BoneSearchBox")!;
        var modelList = browser.FindControl<ListBox>("ModelList")!;
        Assert(disclosure.IsEffectivelyVisible && !shell.Browser.IsHierarchyExpanded && disclosure.IsChecked == false
            && !hierarchy.IsEffectivelyVisible && modelList.IsEffectivelyVisible
            && browser.FindControl<Button>("LoadModelButton")!.IsEffectivelyEnabled,
            "formal model browser starts with its optional bone/morph lookup folded and model loading directly available");
        shell.Browser.SelectedBoneIndex = boneIndex; PumpLayout();
        Assert(!shell.Browser.IsHierarchyExpanded && !hierarchy.IsEffectivelyVisible
            && shell.Animation.SelectedBoneIndex == boneIndex,
            "authored bone selection reaches the animation inspector without automatically opening the optional hierarchy");
        ActivateDialogButton(disclosure);
        Assert(shell.Browser.IsHierarchyExpanded && hierarchy.IsEffectivelyVisible && search.IsEffectivelyVisible && search.IsEffectivelyEnabled,
            "the actual disclosure opens the searchable authored bone browser on demand");
        string label = shell.Browser.BoneGroups.SelectMany(group => group.Children).First(node => node.Index == boneIndex).DisplayName;
        search.Focus(); search.Text = label; PumpLayout();
        Assert(shell.Browser.BoneSearchText == label && shell.Browser.BoneGroups.SelectMany(group => group.Children).Any(node => node.Index == boneIndex)
            && browser.FindControl<TreeView>("BoneTree")!.IsEffectivelyVisible,
            "native search text filters authored groups while keeping the requested real PMX bone reachable");
        search.Text = ""; PumpLayout();
        ActivateDialogButton(disclosure);
        shell.RefreshWorkspace(shell.Browser.SelectedModel!.ObjectId); PumpLayout();
        Assert(!shell.Browser.IsHierarchyExpanded && !hierarchy.IsEffectivelyVisible
            && shell.Browser.SelectedBoneIndex == boneIndex && shell.Browser.BoneSearchText.Length == 0,
            "a workspace refresh preserves the explicitly folded lookup and active authored bone selection");
        Assert(browser.FindControl<Button>("RemoveModelButton") is null && modelList.GetVisualDescendants().OfType<Control>()
            .Any(control => control.ContextMenu?.Items.OfType<MenuItem>().Any(item => item.InputGesture?.Key == Key.Delete) == true),
            "model removal remains reachable through the model-row context menu and Delete without a permanent left-panel button");
    }

    private static void VerifyNativeInvalidInput(EditorShellViewModel shell, EditorAnimationInspector inspector, NumericUpDown position)
    {
        var numeric = position as EditorNumericUpDown
            ?? throw new InvalidOperationException("The formal animation inspector must use EditorNumericUpDown.");
        var textBox = numeric.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "PART_TextBox");
        var other = inspector.FindControl<NumericUpDown>("BonePositionY")!.GetVisualDescendants().OfType<TextBox>()
            .Single(item => item.Name == "PART_TextBox");
        var register = inspector.FindControl<Button>("RegisterAnimationKeyframeButton")!;
        var session = shell.Workspace.Session!;
        var workspaceHistory = WorkspaceHistory(shell.Workspace);
        var sessionHistory = History(session);
        string motionBefore = JsonSerializer.Serialize(shell.Workspace.Motions.Single().Motion, Json);
        string draftsBefore = JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json);
        decimal? valueBefore = position.Value;
        foreach (string invalid in new[] { "not-a-number", "999999999999999999999999999999999999" })
        {
            textBox.Focus(); textBox.Text = invalid; PumpLayout();
            Assert(!numeric.IsInputValid && DataValidationErrors.GetHasErrors(numeric)
                && numeric.Text == invalid && textBox.Text == invalid && numeric.Value == valueBefore,
                "actual native PART_TextBox rejects invalid numeric text while retaining its last committed value: " + invalid);
            other.Focus(); PumpLayout();
            Assert(!textBox.IsFocused && numeric.Text == invalid && textBox.Text == invalid && !numeric.IsInputValid,
                "native focus loss preserves invalid numeric text and its visible validation error: " + invalid);
            Assert(!shell.Animation.CanRegister && !register.IsEffectivelyEnabled
                && !shell.Animation.RegisterKeyframeCommand.CanExecute(null),
                "invalid visible numeric text disables the actual registration button and command");
            Await(shell.Animation.RegisterKeyframeAsync(), "guard direct registration of invalid native input");
            Assert(JsonSerializer.Serialize(shell.Workspace.Motions.Single().Motion, Json) == motionBefore
                && JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json) == draftsBefore
                && WorkspaceHistory(shell.Workspace) == workspaceHistory && History(session) == sessionHistory,
                "invalid numeric registration changes no authored key, pose draft or workspace/session history");
        }
        var guard = typeof(EditorWindow).GetMethod("CheckPoseDraftsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var guardTask = (Task<bool>)guard.Invoke(window, null)!;
        PumpUntil(() => window!.OwnedWindows.Any(dialog => dialog.IsVisible), "native invalid-input save guard error dialog", 30);
        var errorDialog = window!.OwnedWindows.Single(dialog => dialog.IsVisible);
        Assert(!guardTask.IsCompleted && errorDialog.Title == EditorLocalization.Text("操作失败"),
            "formal save/continue guard presents a real modal error for invalid visible numeric text");
        CaptureDialog(errorDialog, "workflow-invalid-numeric-input.png");
        // Error presentation also offers copy/technical details; dismiss through its
        // explicit cancel action rather than assuming the dialog has only one button.
        ActivateDialogButton(errorDialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel));
        Assert(!Await(guardTask, "close native invalid-input guard dialog")
            && numeric.Text == "999999999999999999999999999999999999" && !numeric.IsInputValid
            && JsonSerializer.Serialize(shell.Workspace.Motions.Single().Motion, Json) == motionBefore
            && WorkspaceHistory(shell.Workspace) == workspaceHistory && History(session) == sessionHistory,
            "closing the native error returns false and preserves invalid text, authored motion and both histories");
    }

    private static void VerifyNativeMutationGuards(EditorShellViewModel shell, NumericUpDown position,
        Guid id, string boneName, int editFrame)
    {
        var editorWindow = window!;
        var selectedBefore = shell.Animation.SelectedKeyframes.ToArray();
        var clipboardField = typeof(EditorAnimationViewModel).GetField("clipboard", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? clipboardBefore = clipboardField.GetValue(shell.Animation);
        var confirmBefore = shell.Animation.ConfirmOverwriteAsync;
        decimal? positionBefore = shell.Animation.PositionX;
        string liveBefore = LiveState(shell);
        var workspaceHistoryBefore = WorkspaceHistory(shell.Workspace);
        shell.Animation.SelectedKeyframes = [KeyframeAddress.Bone(boneName, (uint)editFrame)];
        shell.Animation.CopyKeyframes();
        TypeNumber(position, "3.125");
        Assert(shell.Animation.HasDraft && shell.Animation.CanRegister && shell.Animation.CanPaste,
            "busy-guard regression starts with an actual editable native pose and nonempty keyframe clipboard");

        string PendingState() => JsonSerializer.Serialize(new
        {
            live = LiveState(shell), history = WorkspaceHistory(shell.Workspace),
            drafts = shell.Animation.SnapshotDrafts(), position = shell.Animation.PositionX,
            objects = shell.Workspace.Session!.Objects.Select(item => new { item.Id, item.Name, item.Role, item.Position, item.Rotation, item.Scale }),
            motions = shell.Workspace.Motions.Select(actor => new { actor.ObjectId, actor.Motion }),
            shell.SceneInspector.HasCameraDraft, shell.SceneInspector.HasLightDraft
        }, Json);
        string pendingBefore = PendingState();
        var menu = editorWindow.GetVisualDescendants().OfType<Menu>().Single();
        var register = editorWindow.GetVisualDescendants().OfType<EditorAnimationInspector>().Single()
            .FindControl<Button>("RegisterAnimationKeyframeButton")!;
        Assert(menu.IsEffectivelyEnabled && register.IsEffectivelyEnabled,
            "formal native menu and registration control are enabled before the workspace operation");
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = shell.RunOperationAsync("native busy mutation guard", () => release.Task);
        try
        {
            PumpLayout();
            Assert(shell.IsBusy && !operation.IsCompleted && !menu.IsEffectivelyEnabled && !register.IsEffectivelyEnabled,
                "a genuinely pending workspace operation disables the complete native menu and animation register control");
            Assert(!shell.Animation.CanRegister && !shell.Animation.CanPaste
                && !shell.Animation.RegisterKeyframeCommand.CanExecute(null) && !shell.Animation.PasteKeyframesCommand.CanExecute(null),
                "workspace busy disables animation registration and paste properties and commands");
            Await(shell.Animation.RegisterKeyframeAsync(), "direct registration during workspace busy");
            Await(shell.Animation.PasteKeyframesAsync(), "direct paste during workspace busy");
            Assert(PendingState() == pendingBefore,
                "direct register and paste during workspace busy preserve motion, drafts, live selection, objects and both Undo/Redo histories");
        }
        finally { release.TrySetResult(true); Await(operation, "release native busy guard operation"); }
        PumpLayout();
        Assert(!shell.IsBusy && menu.IsEffectivelyEnabled && register.IsEffectivelyEnabled
            && shell.Animation.CanRegister && shell.Animation.CanPaste,
            "completing the pending operation restores the native menu and animation mutation controls");

        // A confirmation may yield before a separate workspace operation starts. Its
        // continuation must recheck the same guard before committing the staged edit.
        var overwrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int confirmations = 0;
        shell.Animation.ConfirmOverwriteAsync = plan => { confirmations++; return overwrite.Task; };
        var registration = shell.Animation.RegisterKeyframeAsync();
        Assert(confirmations == 1 && !registration.IsCompleted && shell.Animation.IsBusy,
            "registration reaches an actual existing-key conflict and waits for overwrite confirmation");
        release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        operation = shell.RunOperationAsync("native confirmation continuation guard", () => release.Task);
        try
        {
            overwrite.SetResult(true);
            Await(registration, "resume registration after workspace became busy");
            Assert(shell.IsBusy && PendingState() == pendingBefore,
                "accepted overwrite rechecks workspace busy after its await and commits no motion, draft or history changes");
        }
        finally
        {
            release.TrySetResult(true); Await(operation, "release confirmation continuation operation");
            shell.Animation.ConfirmOverwriteAsync = confirmBefore;
        }

        // Opening light tools is a selection change, not a destructive pose operation.
        // Native-light add/edit/delete and their history are covered with retained drafts by
        // the dedicated light-draft regression; they must not enter the discard/register guard.
        var previousPage = shell.InspectorPage;
        typeof(EditorWindow).GetMethod("OpenSceneLighting", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editorWindow, new object?[] { null, new RoutedEventArgs() });
        PumpLayout();
        Assert(shell.InspectorPage == EditorInspectorPage.Lighting
            && !editorWindow.OwnedWindows.Any(dialog => dialog.IsVisible) && PendingState() == pendingBefore,
            "opening actual light tools preserves pending pose inputs and histories without a pose modal");
        editorWindow.FindControl<TabControl>("InspectorPageTabs")!.SelectedIndex = (int)previousPage;
        PumpLayout();
        Assert(shell.InspectorPage == previousPage && PendingState() == pendingBefore,
            "returning from light tools retains the original pending pose and authored workflow");

        foreach (string handler in new[] { "OpenModelProperties", "RemoveModelDelete", "RemoveModelContext" })
        {
            object? result = null;
            if (handler.StartsWith("RemoveModel", StringComparison.Ordinal))
            {
                var modelList = editorWindow.GetVisualDescendants().OfType<EditorModelBrowser>().Single().FindControl<ListBox>("ModelList")!;
                if (handler == "RemoveModelDelete")
                {
                    modelList.Focus();
                    var remove = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete };
                    modelList.RaiseEvent(remove);
                    Assert(remove.Handled, "the actual model list handles Delete through its guarded removal command");
                }
                else
                {
                    var row = modelList.GetVisualDescendants().OfType<Control>()
                        .Single(control => control.ContextMenu?.Items.OfType<MenuItem>().Any(item => item.InputGesture?.Key == Key.Delete) == true);
                    var rowMenu = row.ContextMenu!;
                    rowMenu.Open(row); PumpLayout();
                    try
                    {
                        var remove = rowMenu.Items.OfType<MenuItem>().Single(item => item.InputGesture?.Key == Key.Delete);
                        Assert(rowMenu.IsOpen && remove.CommandParameter is EditorModelItem item && item.ObjectId == id,
                            "the real opened model-row context action retains the clicked actor identity");
                        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    }
                    finally { rowMenu.Close(); PumpLayout(); }
                }
            }
            else
            {
                var method = typeof(EditorWindow).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
                result = method.Invoke(editorWindow, new object?[] { null, new RoutedEventArgs() });
            }
            PumpUntil(() => editorWindow.OwnedWindows.Any(dialog => dialog.IsVisible), "native pose-draft guard before " + handler, 30);
            var dialog = editorWindow.OwnedWindows.Single(item => item.IsVisible);
            var actions = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            Assert(actions.Length == 3 && actions[0].IsCancel,
                "actual " + handler + " handler presents the three-choice unregistered-pose guard before its destructive refresh");
            ActivateDialogButton(actions[0]);
            if (result is Task task) Await(task, "cancel pending drafts before " + handler);
            PumpLayout();
            Assert(!editorWindow.OwnedWindows.Any(item => item.IsVisible) && PendingState() == pendingBefore,
                "cancelling " + handler + " preserves native pose input, drafts, motion, objects, selection and both histories");
        }

        var guard = typeof(EditorWindow).GetMethod("CheckPoseDraftsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var discard = (Task<bool>)guard.Invoke(editorWindow, null)!;
        PumpUntil(() => editorWindow.OwnedWindows.Any(dialog => dialog.IsVisible), "native explicit discard after cancelled guarded handlers", 30);
        var discardActions = editorWindow.OwnedWindows.Single(item => item.IsVisible).GetVisualDescendants().OfType<Button>().ToArray();
        Assert(discardActions.Length == 3, "explicit draft cleanup uses the same actual three-choice native guard");
        ActivateDialogButton(discardActions[1]);
        Assert(Await(discard, "discard guard-regression-only pose draft") && !shell.Animation.HasAnyDraft
            && shell.Animation.PositionX == positionBefore && position.Value == positionBefore
            && position is EditorNumericUpDown { IsInputValid: true },
            "explicit discard clears only the guard-regression draft and restores the authored native numeric value");
        shell.Animation.SelectedKeyframes = selectedBefore;
        clipboardField.SetValue(shell.Animation, clipboardBefore);
        shell.Animation.RefreshInputValidation(); PumpLayout();
        Assert(LiveState(shell) == liveBefore && WorkspaceHistory(shell.Workspace) == workspaceHistoryBefore
            && shell.Workspace.Motions.Single().ObjectId == id,
            "native mutation-guard verification restores the complete original authored workflow and both histories");
    }

    private static void VerifyNativeTimeline(EditorShellViewModel shell, string boneName, int editFrame)
    {
        var timeline = window!.FindControl<EditorTimeline>("FrameTimeline")!;
        var address = KeyframeAddress.Bone(boneName, (uint)editFrame);
        VerifyNativeGroupedTimeline(shell, timeline, address);
        try
        {
        // The product's default remains the current model's collapsed categories. The legacy
        // control path is exercised explicitly here so its leaf click/box/drag/Undo contracts
        // retain their original coverage without interpreting category indices as leaf rows.
        timeline.SetCurrentValue(EditorTimeline.GroupsProperty, Array.Empty<EditorTimelineGroup>());
        shell.Animation.SelectedKeyframes = [];
        shell.FirstVisibleFrame = 0; shell.PixelsPerFrame = 8; PumpLayout();
        int row = timeline.Tracks.Select((value, index) => (value, index)).Single(value => value.value.Kind == KeyframeTrackKind.Bone && value.value.Name == boneName).index;
        timeline.FirstVisibleTrack = row; PumpLayout();
        var location = new Point(timeline.TrackHeaderWidth + editFrame * timeline.PixelsPerFrame,
            EditorTimeline.RulerHeight + EditorTimeline.TrackRowHeight / 2);
        Assert(new Rect(timeline.Bounds.Size).Contains(location), "real edited key diamond is inside the native timeline viewport");
        var rootPoint = timeline.TranslatePoint(location, window!)!.Value;
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        int events = 0; timeline.KeyframeSelectionChanged += (_, _) => events++;
        timeline.RaiseEvent(new PointerPressedEventArgs(timeline, pointer, window!, rootPoint, 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        timeline.RaiseEvent(new PointerReleasedEventArgs(timeline, pointer, window!, rootPoint, 2,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        PumpLayout();
        Assert(events > 0 && timeline.SelectedKeyframes.Contains(address) && shell.Animation.SelectedKeyframes.Contains(address),
            "native timeline diamond hit testing raises selection and updates the authored selection binding");
        Assert(shell.CurrentFrame == editFrame && shell.Browser.SelectedBoneIndex == shell.Animation.SelectedBoneIndex,
            "actual key selection synchronizes timeline cursor, browser bone and animation inspector");

        // Start above the key's hit area and cross its center: this must exercise box
        // selection, rather than accidentally proving another diamond click.
        var boxStart = new Point(location.X - 2, EditorTimeline.RulerHeight + 1);
        var boxEnd = new Point(location.X + 2, EditorTimeline.RulerHeight + EditorTimeline.TrackRowHeight - 1);
        shell.Animation.SelectedKeyframes = []; PumpLayout();
        PointerPress(timeline, pointer, boxStart, 3);
        PointerMove(timeline, pointer, boxEnd, 4);
        PointerRelease(timeline, pointer, boxEnd, 5); PumpLayout();
        Assert(timeline.SelectedKeyframes.SequenceEqual(new[] { address }) && shell.Animation.SelectedKeyframes.SequenceEqual(new[] { address }),
            "native timeline box gesture selects the enclosed authored key through the formal selection binding");

        var workspaceHistory = WorkspaceHistory(shell.Workspace);
        var sessionHistory = History(shell.Workspace.Session!);
        string motionSha = shell.Workspace.Motions.Single().Asset!.Sha256;
        TimelineKeysMoveRequestedEventArgs? move = null;
        int moveEvents = 0;
        timeline.KeysMoveRequested += (_, args) => { move = args; moveEvents++; };
        var destination = location + new Vector(2 * timeline.PixelsPerFrame, 0);
        PointerPress(timeline, pointer, location, 6);
        PointerMove(timeline, pointer, destination, 7); PumpLayout();
        Assert(timeline.PreviewFrameDelta == 2 && shell.Workspace.Motions.Single().Asset!.Sha256 == motionSha
            && WorkspaceHistory(shell.Workspace) == workspaceHistory,
            "dragging an actual timeline key previews two frames without mutating authored motion or history");
        PointerRelease(timeline, pointer, destination, 8);
        PumpUntil(() => !shell.Animation.IsBusy, "formal timeline drag commit", 30);
        Assert(moveEvents == 1 && move is { FrameDelta: 2 } && move.Selection.SequenceEqual(new[] { address }),
            "releasing native key drag emits one move request with the original selection and exact frame delta");
        Assert(!shell.Workspace.Motions.Single().Motion.BoneKeyframes.Any(key => key.BoneName == boneName && key.Frame == editFrame)
            && GetBoneKey(shell, shell.Animation.ObjectId!.Value, boneName, editFrame + 2).Translation.X == 2.5f,
            "formal window move handler commits the dragged key to the project motion");
        Assert(WorkspaceHistory(shell.Workspace).Undo == workspaceHistory.Undo + 1
            && History(shell.Workspace.Session!).Undo == sessionHistory.Undo + 1,
            "one completed timeline drag creates one unified workspace and session Undo entry");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(shell.Workspace.Motions.Single().Asset!.Sha256 == motionSha,
            "one shell Undo restores all original key addresses and exact VMD bytes after native drag");
        shell.CurrentFrame = editFrame; PumpLayout();
        }
        finally
        {
            // Keep the original binding and restore its latest authored revision: the drag and
            // Undo above may have replaced the VM's groups while the flat test was running.
            timeline.SetCurrentValue(EditorTimeline.GroupsProperty, shell.Animation.TimelineGroups);
            PumpLayout();
            Assert(shell.Animation.TimelineTrackMode == EditorTimelineTrackMode.AllTracks
                && timeline.Groups.Count != 0 && ReferenceEquals(timeline.Groups, shell.Animation.TimelineGroups),
                "native legacy-flat timeline verification restores current-model category binding and the default AllTracks mode");
        }
    }

    private static void VerifyNativeGroupedTimeline(EditorShellViewModel shell, EditorTimeline timeline, KeyframeAddress edited)
    {
        Assert(shell.Animation.TimelineTrackMode == EditorTimelineTrackMode.AllTracks
            && shell.Animation.IsTimelineAllTracks,
            "formal workflow enters the timeline with default current-model AllTracks mode");
        Assert(shell.Animation.ObjectId == shell.Browser.SelectedModel?.ObjectId && shell.Animation.ObjectId.HasValue
            && timeline.Groups.Count != 0 && ReferenceEquals(timeline.Groups, shell.Animation.TimelineGroups),
            "native timeline categories bind to the currently selected model's actual VM groups");
        string modelPrefix = shell.Animation.ObjectId!.Value.ToString("D") + ":";
        Assert(timeline.Groups.All(group => group.Key.StartsWith(modelPrefix, StringComparison.Ordinal)),
            "all default category identities belong to the selected model rather than another instance");
        Assert(timeline.Groups.All(group => !timeline.IsGroupExpanded(group.Key))
            && timeline.DisplayRows.Count == timeline.Groups.Count && timeline.DisplayRows.All(row => row.IsGroup),
            "native current-model categories start collapsed with one summary row per category");
        var authored = shell.Animation.Tracks.SelectMany(track => track.Frames.Select(frame => new KeyframeAddress(track.Kind, track.Name, frame))).ToHashSet();
        var grouped = timeline.Groups.SelectMany(group => group.Tracks.SelectMany(track =>
            track.Frames.Select(frame => new KeyframeAddress(track.Kind, track.Name, frame)))).ToArray();
        Assert(grouped.Length == authored.Count && authored.SetEquals(grouped),
            "collapsed model categories retain every typed authored bone, morph and visibility key exactly once");
        Assert(timeline.DisplayRows.All(row => row.Frames.All(frame =>
            row.AddressesAt(frame).ToHashSet().SetEquals(row.Group!.Tracks.Where(track => track.Frames.Contains(frame))
                .Select(track => new KeyframeAddress(track.Kind, track.Name, frame))))),
            "every summary diamond resolves all actual member channel addresses at that frame without synthetic category keys");

        var group = timeline.Groups.Single(value => value.Tracks.Any(track => track.Kind == edited.Kind && track.Name == edited.Name));
        var expected = group.Tracks.Where(track => track.Frames.Contains(edited.Frame))
            .Select(track => new KeyframeAddress(track.Kind, track.Name, edited.Frame)).ToHashSet();
        Assert(expected.Contains(edited), "the edited real bone key is represented by its collapsed category summary");
        var history = WorkspaceHistory(shell.Workspace);
        var sessionHistory = History(shell.Workspace.Session!);
        string motionSha = shell.Workspace.Motions.Single().Asset!.Sha256;
        shell.Animation.SelectedKeyframes = [];
        shell.FirstVisibleFrame = 0; shell.PixelsPerFrame = 8; PumpLayout();
        int groupRow = timeline.DisplayRows.Select((row, index) => (row, index)).Single(value => value.row.Group?.Key == group.Key).index;
        timeline.FirstVisibleTrack = groupRow; PumpLayout();
        var location = new Point(timeline.TrackHeaderWidth + edited.Frame * timeline.PixelsPerFrame,
            EditorTimeline.RulerHeight + EditorTimeline.TrackRowHeight / 2);
        Assert(new Rect(timeline.Bounds.Size).Contains(location), "the actual collapsed category diamond is inside the native timeline viewport");
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        PointerPress(timeline, pointer, location, 101);
        PointerRelease(timeline, pointer, location, 102); PumpLayout();
        Assert(timeline.SelectedKeyframes.ToHashSet().SetEquals(expected)
            && shell.Animation.SelectedKeyframes.ToHashSet().SetEquals(expected),
            "native collapsed summary hit selects every actual typed key at that frame through the formal selection binding");
        Assert(shell.CurrentFrame == edited.Frame && shell.Animation.ObjectId == shell.Browser.SelectedModel?.ObjectId,
            "native category summary selection keeps the current model and synchronizes the exact authored frame");

        int collapsedRows = timeline.DisplayRows.Count;
        var header = new Point(Math.Max(1, timeline.TrackHeaderWidth / 2), location.Y);
        PointerPress(timeline, pointer, header, 103);
        PointerRelease(timeline, pointer, header, 104); PumpLayout();
        Assert(timeline.IsGroupExpanded(group.Key) && timeline.DisplayRows.Count == collapsedRows + group.Tracks.Count
            && timeline.DisplayRows.Count(row => row.IsChild) == group.Tracks.Count,
            "native category header expands into all actual member leaf channels");
        PointerPress(timeline, pointer, header, 105);
        PointerRelease(timeline, pointer, header, 106); PumpLayout();
        Assert(!timeline.IsGroupExpanded(group.Key) && timeline.DisplayRows.Count == collapsedRows
            && timeline.SelectedKeyframes.ToHashSet().SetEquals(expected),
            "native category collapse retains all selected actual keys while returning to summary rows");
        Assert(shell.Workspace.Motions.Single().Asset!.Sha256 == motionSha
            && WorkspaceHistory(shell.Workspace) == history && History(shell.Workspace.Session!) == sessionHistory,
            "native category summary selection and expansion leave authored VMD bytes and both Undo histories unchanged");
        // Legacy leaf assertions below target this exact original bone. Restore only selection,
        // never a product mode or authored key, after a mixed category chose its last member.
        int bone = shell.Browser.SelectedModel!.PmxModel.Bones.Select((value, index) => (value, index))
            .First(value => value.value.Name == edited.Name).index;
        shell.Browser.HierarchyTab = 0; shell.Browser.SelectedBoneIndex = bone;
        shell.CurrentFrame = (int)edited.Frame; PumpLayout();
    }

    private static void VerifyDraftRetention(EditorShellViewModel shell, Guid id, SceneAnimationModelAsset asset,
        string modelPath, int boneIndex, int editFrame)
    {
        string originalMotion = shell.Workspace.Motions.Single().Asset!.Sha256;
        Guid secondId = shell.Workspace.AddModel(modelPath, asset, ModelRole.Character);
        void Select(Guid actorId)
        {
            shell.Browser.SelectedModel = shell.Browser.Models.Single(item => item.ObjectId == actorId);
            shell.Browser.HierarchyTab = 0; shell.Browser.SelectedBoneIndex = boneIndex; PumpLayout();
        }
        Select(id); shell.CurrentFrame = editFrame;
        shell.Animation.PositionX = 1.625m;
        bool hasMorph = asset.Model.Morphs.Count != 0;
        if (hasMorph)
        {
            shell.Browser.HierarchyTab = 1; shell.Browser.SelectedMorphIndex = 0;
            shell.Animation.MorphWeight = .375m;
            Assert(shell.Animation.SnapshotDrafts().Any(item => item.ObjectId == id && item.BoneIndex == boneIndex && item.Translation.X == 1.625f),
                "changing from bone to morph retains the unregistered bone pose at the same global frame");
        }
        else Skipped.Add("Input PMX has no morphs; cross-model draft retention is checked, bone-to-morph retention requires an authored morph.");
        Select(secondId); shell.Animation.PositionX = 3.75m;
        Select(id);
        Assert(shell.Animation.PositionX == 1.625m && shell.Animation.HasDraft && shell.Animation.HasAnyDraft,
            "returning from another real model restores the selected bone draft");
        Assert(shell.Animation.SnapshotDrafts().Any(item => item.ObjectId == secondId && item.Translation.X == 3.75f)
            && shell.Workspace.Motions.Single(item => item.ObjectId == id).Asset!.Sha256 == originalMotion,
            "unregistered poses span real actors without changing the imported motion bytes");
        if (hasMorph)
        {
            shell.Browser.HierarchyTab = 1; shell.Browser.SelectedMorphIndex = 0;
            Assert(shell.Animation.MorphWeight == .375m && shell.Animation.HasDraft,
                "returning to a morph restores its independent unregistered weight");
            shell.Browser.HierarchyTab = 0;
        }
        var preflightHistory = WorkspaceHistory(shell.Workspace);
        var preflightSessionHistory = History(shell.Workspace.Session!);
        string draftsBeforePreflight = JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json);
        var draftPlans = shell.Animation.PrepareDraftMotions(shell.Workspace.Motions);
        var detachedCandidates = draftPlans.Select(plan => (plan.ObjectId, Motion: plan.CreateMotion(confirmOverwrite: true))).ToArray();
        Assert(detachedCandidates.Length == 2 && detachedCandidates.Any(candidate => candidate.ObjectId == id
            && candidate.Motion.BoneKeyframes.Any(key => key.BoneName == asset.Model.Bones[boneIndex].Name && key.Frame == editFrame && key.Translation.X == 1.625f))
            && detachedCandidates.Any(candidate => candidate.ObjectId == secondId
                && candidate.Motion.BoneKeyframes.Any(key => key.Frame == editFrame && key.Translation.X == 3.75f)),
            "save preflight materializes complete detached motions for two real actors at the one global frame");
        Assert(JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json) == draftsBeforePreflight
            && WorkspaceHistory(shell.Workspace) == preflightHistory && History(shell.Workspace.Session!) == preflightSessionHistory
            && shell.Workspace.Motions.Single(actor => actor.ObjectId == id).Asset!.Sha256 == originalMotion,
            "multi-model save preparation and candidate creation have no live draft, motion or Undo side effects");
        Await(shell.Animation.RegisterKeyframeAsync(), "register only the selected draft channel");
        var remaining = shell.Animation.SnapshotDrafts();
        Assert(!shell.Animation.HasDraft && remaining.Any(item => item.ObjectId == secondId && item.Translation.X == 3.75f)
            && (!hasMorph || remaining.Any(item => item.ObjectId == id && item.MorphIndex == 0 && item.MorphWeight == .375f)),
            "registering the selected bone clears only that channel and retains other morph/model drafts");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(!shell.Animation.HasAnyDraft && shell.Animation.SnapshotDrafts().Count == 0
            && shell.Workspace.Motions.Single(item => item.ObjectId == id).Asset!.Sha256 == originalMotion,
            "external shell Undo invalidates every unregistered draft and restores the original motion");
        Select(id); shell.Animation.PositionX = 4.25m;
        shell.RefreshWorkspace(id); PumpLayout();
        Assert(!shell.Animation.HasAnyDraft && !shell.Animation.HasDraft,
            "explicit workspace refresh invalidates pending drafts even when motion identity is unchanged");
        shell.Browser.SelectedBoneIndex = boneIndex; shell.Animation.PositionX = 4.5m;
        shell.CurrentFrame = editFrame + 1; PumpLayout();
        Assert(!shell.Animation.HasAnyDraft, "changing the global frame clears all frame-local pose drafts");
        shell.UndoCommand.Execute(null); PumpLayout(); // Undo only the temporary second actor.
        Assert(shell.Workspace.Motions.Count == 1 && shell.Workspace.Motions.Single().ObjectId == id
            && shell.Workspace.Motions.Single().Asset!.Sha256 == originalMotion,
            "draft-retention verification restores the exact original single-model project");
        shell.RefreshWorkspace(id); shell.Browser.HierarchyTab = 0;
        shell.Browser.SelectedBoneIndex = boneIndex; shell.CurrentFrame = editFrame; PumpLayout();
    }

    private static void VerifyPreferences(EditorShellViewModel shell)
    {
        string languageBefore = EditorLocalization.CurrentLanguage;
        var workspacePreferences = shell.Workspace.Preferences;
        var source = new EditorPreferences { PmxMaxBytes = 10 * EditorPreferences.Gibibyte + 123,
            PmaPackageMaxBytes = 50 * EditorPreferences.Gibibyte + 321,
            PmaExpandedMaxBytes = 50 * EditorPreferences.Gibibyte + 987,
            PmaEntryMaxBytes = 10 * EditorPreferences.Gibibyte + 789 };
        string path = Path.Combine(output, "probe-local-editor-preferences.json");
        source.Save(path);
        try
        {
            string[] languages = ["zh-CN", "en", "ja"];
            for (int index = 0; index < languages.Length; index++)
            {
                string language = languages[index]; EditorLocalization.SetLanguage(language); PumpLayout();
                var dialog = new EditorPreferencesWindow(source);
                var resultTask = ShowDialog<EditorPreferences?>(dialog);
                dialog.FindControl<ComboBox>("PreferencesLanguageBox")!.SelectedIndex = index;
                dialog.FindControl<TabControl>("PreferencesTabs")!.SelectedIndex = 2; PumpDialog(dialog);
                TypeNumberThenFocus(dialog.FindControl<NumericUpDown>("PreferencesPmxBudgetBox")!, "12.500", dialog.FindControl<Button>("ApplyPreferencesButton")!);
                var apply = dialog.FindControl<Button>("ApplyPreferencesButton")!;
                Assert(apply.Content?.ToString() == EditorLocalization.Text("应用"),
                    "native preferences action follows the selected UI language: " + language);
                CaptureDialog(dialog, "workflow-preferences-" + language + ".png");
                ActivateDialogButton(apply);
                var applied = Await(resultTask, "native preferences Apply in " + language);
                Assert(applied is not null && applied.Language == language
                    && applied.PmxMaxBytes == 12 * EditorPreferences.Gibibyte + EditorPreferences.Gibibyte / 2
                    && applied.PmaPackageMaxBytes == source.PmaPackageMaxBytes
                    && applied.PmaExpandedMaxBytes == source.PmaExpandedMaxBytes && applied.PmaEntryMaxBytes == source.PmaEntryMaxBytes,
                    "native preferences Apply returns selected language and preserves unedited exact capacity bytes: " + language);
                applied!.Save(path);
                var loaded = EditorPreferences.Load(out var diagnostic, path);
                Assert(diagnostic is null && loaded == applied, "machine-local preferences roundtrip exactly through isolated real storage: " + language);
                source = loaded;
            }
            byte[] beforeCancel = File.ReadAllBytes(path);
            var cancel = new EditorPreferencesWindow(source);
            var cancelled = ShowDialog<EditorPreferences?>(cancel);
            cancel.FindControl<ComboBox>("PreferencesLanguageBox")!.SelectedIndex = 0;
            cancel.FindControl<CheckBox>("PreferencesWorldGuidesBox")!.IsChecked = !source.ShowWorldGuides;
            cancel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert(Await(cancelled, "native preferences cancellation") is null && File.ReadAllBytes(path).SequenceEqual(beforeCancel)
                && shell.Workspace.Preferences == workspacePreferences,
                "cancelling native preferences returns no draft and leaves stored and active workspace settings unchanged");
        }
        finally { EditorLocalization.SetLanguage(languageBefore); PumpLayout(); }
    }

    private static void VerifyModelProperties(EditorShellViewModel shell, Guid id)
    {
        string before = ModelPropertiesState(shell.Workspace, id);
        var workspaceHistory = WorkspaceHistory(shell.Workspace);
        var sessionHistory = History(shell.Workspace.Session!);
        var cancelledDialog = new EditorModelPropertiesWindow(shell.Workspace, id);
        var cancellation = ShowDialog<EditorModelPropertiesDraft?>(cancelledDialog);
        cancelledDialog.ViewModel.Name += " [cancelled]";
        cancelledDialog.ViewModel.PositionX += 2;
        cancelledDialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert(Await(cancellation, "cancel detached model properties") is null && ModelPropertiesState(shell.Workspace, id) == before
            && WorkspaceHistory(shell.Workspace) == workspaceHistory,
            "native model-property cancellation preserves metadata, transforms, material mapping and history");
        var dialog = new EditorModelPropertiesWindow(shell.Workspace, id);
        var task = ShowDialog<EditorModelPropertiesDraft?>(dialog);
        var model = dialog.ViewModel;
        Assert(model.Materials.Count != 0, "actual imported model exposes at least one editable material mapping row");
        model.Name += " [probe edit]"; model.RoleIndex = 2; model.PositionX += .125m;
        model.PhysicsEnabled = !model.PhysicsEnabled; model.StyleIndex = 1;
        model.Materials[0].RoleIndex = 1; model.Materials[0].ModeIndex = 1;
        PumpDialog(dialog);
        Assert(ModelPropertiesState(shell.Workspace, id) == before, "model and material controls remain detached before native Apply");
        CaptureDialog(dialog, "workflow-model-properties.png");
        ActivateDialogButton(dialog.FindControl<Button>("ApplyModelPropertiesButton")!);
        var draft = Await(task, "native model properties Apply");
        Assert(draft is not null && draft.MaterialProfile is not null, "native model Apply returns one coherent metadata and explicit-mapping draft");
        shell.Workspace.ApplyModelProperties(draft!); PumpLayout();
        string after = ModelPropertiesState(shell.Workspace, id);
        Assert(after != before && shell.Workspace.Session!.Objects.Single(item => item.Id == id).Role == ModelRole.Prop
            && shell.Workspace.MaterialBindings.ContainsKey(id), "formal workspace applies authored object metadata and material mapping together");
        Assert(WorkspaceHistory(shell.Workspace).Undo == workspaceHistory.Undo + 1
            && History(shell.Workspace.Session!).Undo == sessionHistory.Undo + 1,
            "combined metadata and material mapping creates exactly one unified Undo transaction");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(ModelPropertiesState(shell.Workspace, id) == before, "one shell Undo restores all metadata, transform, flags, style and material mapping");
        shell.RedoCommand.Execute(null); PumpLayout();
        Assert(ModelPropertiesState(shell.Workspace, id) == after, "one shell Redo restores the entire coherent model-property transaction");
        shell.UndoCommand.Execute(null); PumpLayout();
    }

    private static void VerifyInterpolation(EditorShellViewModel shell, Guid id, string boneName, int editFrame)
    {
        var source = shell.Workspace.Motions.Single().Motion;
        var selection = new[] { KeyframeAddress.Bone(boneName, (uint)editFrame) };
        byte[] original = GetBoneKey(shell, id, boneName, editFrame).Interpolation.ToArray();
        string motionSha = shell.Workspace.Motions.Single().Asset!.Sha256;
        var workspaceHistory = WorkspaceHistory(shell.Workspace);
        var sessionHistory = History(shell.Workspace.Session!);
        var unchanged = new EditorInterpolationWindow(new EditorInterpolationViewModel(source, selection));
        var noChangeTask = ShowDialog<EditorInterpolationDraft?>(unchanged);
        foreach (var channel in unchanged.ViewModel.Channels) unchanged.ViewModel.SelectedChannel = channel;
        ActivateDialogButton(unchanged.FindControl<Button>("ApplyInterpolationButton")!);
        Assert(Await(noChangeTask, "apply unchanged interpolation") is null
            && shell.Workspace.Motions.Single().Asset!.Sha256 == motionSha && WorkspaceHistory(shell.Workspace) == workspaceHistory,
            "opening every native interpolation channel and applying without edits preserves exact VMD bytes and Undo history");
        var cancelled = new EditorInterpolationWindow(new EditorInterpolationViewModel(source, selection));
        var cancelTask = ShowDialog<EditorInterpolationDraft?>(cancelled);
        cancelled.ViewModel.SelectedChannel.SetHandle(1, 13, 29);
        cancelled.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert(Await(cancelTask, "cancel edited interpolation") is null && shell.Workspace.Motions.Single().Asset!.Sha256 == motionSha,
            "cancelling a native interpolation edit preserves the raw authored curve bytes");
        var dialog = new EditorInterpolationWindow(new EditorInterpolationViewModel(source, selection));
        var task = ShowDialog<EditorInterpolationDraft?>(dialog);
        var selected = dialog.ViewModel.SelectedChannel;
        decimal nextX = selected.X1 == 13 ? 14 : 13;
        TypeNumberThenFocus(dialog.FindControl<NumericUpDown>("CurveX1")!, nextX.ToString(CultureInfo.InvariantCulture), dialog.FindControl<Button>("ApplyInterpolationButton")!);
        Assert(selected.X1 == nextX && dialog.ViewModel.HasChanges, "native interpolation input updates only its detached selected curve channel");
        CaptureDialog(dialog, "workflow-interpolation.png");
        ActivateDialogButton(dialog.FindControl<Button>("ApplyInterpolationButton")!);
        var draft = Await(task, "native interpolation Apply");
        Assert(draft is not null && draft.BoneChannels.Count == 1 && draft.CameraChannels.Count == 0,
            "native interpolation Apply returns only the changed bone channel");
        shell.Workspace.CommitMotion(id, draft!.ApplyTo(source)); PumpLayout();
        var changed = GetBoneKey(shell, id, boneName, editFrame).Interpolation;
        int[] channelStorage = [0, 4, 8, 12, 19, 23, 27, 34, 38, 42, 49, 53, 57];
        Assert(changed[0] == (byte)nextX && Enumerable.Range(0, 64).Except(channelStorage).All(index => changed[index] == original[index]),
            "one curve edit preserves all unrelated raw interpolation bytes, including overlapping physics markers and padding");
        Assert(SceneSections(shell.Workspace.Motions.Single().Motion) == SceneSections(source)
            && WorkspaceHistory(shell.Workspace).Undo == workspaceHistory.Undo + 1
            && History(shell.Workspace.Session!).Undo == sessionHistory.Undo + 1,
            "curve draft commit retains scene tracks and produces one workspace/session Undo entry");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(shell.Workspace.Motions.Single().Asset!.Sha256 == motionSha
            && GetBoneKey(shell, id, boneName, editFrame).Interpolation.SequenceEqual(original),
            "one shell Undo restores every original raw curve byte and the exact immutable motion asset");
    }

    private static void VerifyInterchange(EditorShellViewModel shell, Guid id, SceneAnimationModelAsset asset,
        string modelPath, int boneIndex, int editFrame)
    {
        var actor = shell.Workspace.Motions.Single(value => value.ObjectId == id);
        string originalSha = actor.Asset!.Sha256;
        var history = WorkspaceHistory(shell.Workspace);
        var sessionHistory = History(shell.Workspace.Session!);
        var exactRotation = new Quaternion(.125f, .25f, -.375f, .75f);
        var translation = new Vector3(3.125f, .25f, -.5f);
        var saved = new VmdMotion { BoneKeyframes = [new() { BoneName = asset.Model.Bones[boneIndex].Name,
            Frame = (uint)editFrame, Translation = translation, Rotation = exactRotation }] };
        shell.Browser.HierarchyTab = 0; shell.Browser.SelectedBoneIndex = boneIndex; shell.CurrentFrame = editFrame;
        shell.Animation.RestoreSavedPoses(shell.Workspace.Motions, new Dictionary<Guid, VmdMotion> { [id] = saved }, editFrame);
        Assert(shell.Animation.HasDraft && shell.Animation.SnapshotDrafts().Single().Rotation == exactRotation,
            "PMM saved pose restores an unregistered local draft while retaining the exact stored quaternion");
        Assert(shell.Workspace.Motions.Single().Asset!.Sha256 == originalSha && WorkspaceHistory(shell.Workspace) == history
            && History(shell.Workspace.Session!) == sessionHistory,
            "restoring saved PMM editor values changes neither registered keys nor workspace/session history");
        var pose = shell.Animation.CaptureLocalPose(actor, editFrame);
        Assert(pose.Bones[boneIndex].Translation == translation && pose.Bones[boneIndex].Rotation == exactRotation,
            "local pose export overlays the restored raw animation delta without normalizing its stored quaternion");
        var unanimated = Enumerable.Range(0, asset.Model.Bones.Count).Where(index => index != boneIndex
            && !actor.Motion.BoneKeyframes.Any(key => key.BoneName == asset.Model.Bones[index].Name)).ToArray();
        Assert(unanimated.All(index => pose.Bones[index].Translation == Vector3.Zero && pose.Bones[index].Rotation == Quaternion.Identity),
            "local pose capture leaves unanimated channels at identity instead of baking rest position, IK or physics");
        var plans = shell.Animation.PrepareDraftMotions(shell.Workspace.Motions);
        Assert(plans.Count == 1 && plans[0].ObjectId == id && plans[0].Plan.Conflicts.Count > 0,
            "save preflight prepares the cached channel and reports its actual registered-key conflict");
        var candidateMotion = plans[0].CreateMotion(confirmOverwrite: true);
        Assert(candidateMotion.BoneKeyframes.Single(key => key.BoneName == asset.Model.Bones[boneIndex].Name && key.Frame == editFrame).Rotation == exactRotation
            && shell.Animation.HasDraft && shell.Workspace.Motions.Single().Asset!.Sha256 == originalSha
            && WorkspaceHistory(shell.Workspace) == history && History(shell.Workspace.Session!) == sessionHistory,
            "materializing confirmed save plans preserves exact rotation and has no live motion, draft or Undo side effect");
        string vpd = Path.Combine(output, "workflow-local-pose.vpd");
        EditorProjectInterchange.ExportVpd(vpd, asset.Model, pose.Bones, pose.MorphWeights, encoding: VpdTextEncoding.Utf8);
        var importedPose = EditorProjectInterchange.PrepareVpd(vpd, asset.Model, (uint)(editFrame + 3));
        var importedKey = importedPose.BoneKeyframes.Single(key => key.BoneName == asset.Model.Bones[boneIndex].Name);
        Assert(importedKey.Frame == editFrame + 3 && importedKey.Translation == translation
            && MathF.Abs(Quaternion.Dot(Quaternion.Normalize(importedKey.Rotation), Quaternion.Normalize(exactRotation))) > .999999f,
            "actual UTF-8 VPD export/import retains local translation and orientation with the existing normalized VPD contract");
        string vmd = Path.Combine(output, "workflow-authored-motion.vmd");
        EditorProjectInterchange.ExportVmd(vmd, actor.Motion, actor.Asset!.AnsiCodePage);
        var roundtripMotion = EditorProjectInterchange.PrepareVmd(vmd, asset.Model, actor.Asset!.AnsiCodePage);
        Assert(roundtripMotion.Sha256 == originalSha,
            "formal VMD interchange roundtrips every authored section and exact immutable bytes");
        Await(shell.Animation.RegisterKeyframeAsync(), "register restored PMM saved pose");
        Assert(GetBoneKey(shell, id, asset.Model.Bones[boneIndex].Name, editFrame).Rotation == exactRotation
            && WorkspaceHistory(shell.Workspace).Undo == history.Undo + 1,
            "explicit registration of a restored saved pose preserves its exact quaternion in one Undo transaction");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(shell.Workspace.Motions.Single().Asset!.Sha256 == originalSha && !shell.Animation.HasAnyDraft,
            "one shell Undo removes registered saved-pose edits and restores exact original motion bytes");

        string pmd = suppliedPmd ?? Path.GetFullPath(Path.Combine("fixtures", "humanoid.pmd"));
        if (suppliedPmd is null && !File.Exists(pmd)) pmd = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", "humanoid.pmd"));
        RequireInput(pmd); Remember(pmd);
        var pmdAsset = EditorProjectWorkspace.LoadModelAsset(pmd);
        Assert(pmdAsset.Model.Bones.Count > 0 && pmdAsset.Model.Vertices.Count > 0,
            "formal model loader reads the real humanoid PMD through the existing PMD-to-PMX adapter");
        var pmmOptions = new PmmSessionExportOptions { AnsiCodePage = 936 };
        var compatibility = EditorProjectInterchange.InspectPmmExport(shell.Workspace.Session!, pmmOptions);
        if (!compatibility.CanExport && compatibility.Issues.Any(issue => issue.Code == "pmm-name-encoding"))
        {
            Assert(compatibility.RecommendPma, "actual model names outside PMM legacy encoding limits explicitly recommend lossless PMA");
            string incompatible = Path.Combine(output, "workflow-encoding-refused.pmm");
            bool refusedEncoding = false;
            try { EditorProjectInterchange.ExportPmm(incompatible, shell.Workspace.Session!, pmmOptions); }
            catch (PmaPmmExportException) { refusedEncoding = true; }
            Assert(refusedEncoding && !File.Exists(incompatible) && shell.Workspace.Motions.Single().Asset!.Sha256 == originalSha,
                "actual PMM name encoding rejection publishes no file and preserves the active motion");
            Skipped.Add("This actual model has names outside the explicitly selected PMM legacy encoding; lossless PMA is verified, ordinary PMM role/adoption roundtrip is covered by the fixture run.");
            return;
        }
        Assert(compatibility.CanExport,
            "PMM compatibility accepts the original MMD white world background without losing its setting: "
            + string.Join("; ", compatibility.Issues.Select(issue => issue.Message)));

        using var interchange = new EditorProjectWorkspace();
        Guid interchangeId = interchange.AddModel(modelPath, asset, ModelRole.Character);
        interchange.BindMotion(interchangeId, SceneMotionAsset.FromMotion(new VmdMotion { ModelName = "Probe actor",
            BoneKeyframes = actor.Motion.BoneKeyframes, MorphKeyframes = actor.Motion.MorphKeyframes,
            IkKeyframes = actor.Motion.IkKeyframes }, "probe-actor.vmd"));
        interchange.Edit(edit => edit.Settings = edit.Settings with { BackgroundRed = .1f, BackgroundGreen = .2f, BackgroundBlue = .3f });
        var unsupportedBackground = EditorProjectInterchange.InspectPmmExport(interchange.Session!, pmmOptions);
        string refused = Path.Combine(output, "workflow-refused.pmm");
        bool rejected = false;
        try { EditorProjectInterchange.ExportPmm(refused, interchange.Session!, pmmOptions); }
        catch (PmaPmmExportException) { rejected = true; }
        Assert(!unsupportedBackground.CanExport && unsupportedBackground.RecommendPma
            && unsupportedBackground.Issues.Any(issue => issue.Code == "background")
            && rejected && !File.Exists(refused) && shell.Workspace.Motions.Single().Asset!.Sha256 == originalSha,
            "unrepresentable custom PMM background refuses publication and recommends PMA without changing the live workflow");
        interchange.Edit(edit => edit.Settings = edit.Settings with { BackgroundRed = 0, BackgroundGreen = 0, BackgroundBlue = 0 });
        string pmm = Path.Combine(output, "workflow-compatible.pmm");
        var compatible = EditorProjectInterchange.InspectPmmExport(interchange.Session!, pmmOptions);
        Assert(compatible.CanExport, "explicit PMM-compatible project passes the complete compatibility gate: "
            + string.Join("; ", compatible.Issues.Select(issue => issue.Message)));
        EditorProjectInterchange.ExportPmm(pmm, interchange.Session!, pmmOptions);
        var plan = EditorProjectInterchange.PreparePmm(pmm);
        Assert(plan.Document.Settings.BlackBackgroundEnabled,
            "formal PMM export writes the actual black background flag for a black project");
        Assert(plan.CanCommit && plan.HasUnconfirmedRoles && plan.Models.Count == 1,
            "saved PMM prepares all actual model resources and requires fresh explicit per-instance role confirmation");
        var cancelledDialog = new EditorPmmImportWindow(plan);
        var cancelled = ShowDialog<PmmProjectImportPlan?>(cancelledDialog);
        cancelledDialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert(Await(cancelled, "cancel native PMM role selection") is null && plan.HasUnconfirmedRoles
            && ReferenceEquals(shell.Workspace.Session, viewportProject()),
            "cancelling native PMM import leaves its detached plan and the active project unchanged");
        var roleDialog = new EditorPmmImportWindow(plan);
        var confirmedTask = ShowDialog<PmmProjectImportPlan?>(roleDialog);
        Assert(!roleDialog.FindControl<Button>("PmmConfirmImportButton")!.IsEnabled,
            "native PMM confirmation stays disabled until every model purpose is explicitly selected");
        foreach (var box in roleDialog.GetVisualDescendants().OfType<ComboBox>().Where(box => box.Name?.StartsWith("PmmRole_", StringComparison.Ordinal) == true))
            box.SelectedIndex = 0;
        PumpDialog(roleDialog); CaptureDialog(roleDialog, "workflow-pmm-roles.png");
        ActivateDialogButton(roleDialog.FindControl<Button>("PmmConfirmImportButton")!);
        var confirmed = Await(confirmedTask, "confirm native PMM role selections")!;
        Assert(!confirmed.HasUnconfirmedRoles && confirmed.Models.All(model => model.Purpose.Role == ModelRole.Character),
            "native PMM Apply returns the explicit character roles without filename heuristics");
        using var candidate = EditorProjectInterchange.CreatePmmCandidate(confirmed,
            (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token),
            EditorProjectWorkspace.CreatePhysicsProvider());
        var importedActor = candidate.Session.CaptureAuthoredContent().Motions.Single();
        Assert(importedActor.Motion.BoneKeyframes.Any(key => key.BoneName == asset.Model.Bones[boneIndex].Name
            && key.Frame == editFrame && key.Translation.X == 2.5f) && candidate.SavedPoses.Count == 1,
            "formal PMM candidate retains registered motion and exposes saved local editor poses separately");
        using var adopted = new EditorProjectWorkspace();
        var importedSession = candidate.Session;
        adopted.Adopt(candidate);
        Assert(ReferenceEquals(adopted.Session, importedSession) && adopted.IsDirty && adopted.ProjectPath is null && !adopted.CanUndo,
            "formal PMM adoption transfers the candidate into an unsaved native document with an empty Undo history");
        var importedAnimation = new EditorAnimationViewModel();
        importedAnimation.SetActor(importedActor.ObjectId, importedActor.Model, importedActor.Motion);
        importedAnimation.SelectBone(boneIndex);
        string importedMotionBefore = JsonSerializer.Serialize(importedActor.Motion, Json);
        string? importedMotionSha = importedActor.Asset?.Sha256;
        var adoptedSession = adopted.Session!;
        importedAnimation.RestoreSavedPoses(adopted.Motions, candidate.SavedPoses, confirmed.CurrentFrame);
        Assert(importedAnimation.HasAnyDraft && JsonSerializer.Serialize(adopted.Motions.Single().Motion, Json) == importedMotionBefore
            && adopted.Motions.Single().Asset?.Sha256 == importedMotionSha && !adopted.CanUndo && !adoptedSession.CanUndo,
            "adopted PMM restores saved editor poses separately from its registered motion and project Undo");
        Assert(asset.Model.Morphs.All(morph => importedActor.Motion.MorphKeyframes.Any(key => key.MorphName == morph.Name)),
            "PMM adoption retains every complete model morph name, including names longer than VMD fields");
        var persistenceErrors = adopted.GetMotionPersistenceErrors();
        if (inputKind == "generated PmxWriter fixture")
            Assert(asset.Model.Morphs.Single().Name == "fixture expression" && "fixture expression".Length == 18
                && importedActor.Motion.MorphKeyframes.Any(key => key.MorphName == "fixture expression")
                && persistenceErrors.Count != 0,
                "generated PMM fixture keeps its authored 18-byte ASCII morph name and exercises the long-track save refusal");
        if (persistenceErrors.Count != 0)
        {
            Assert(importedActor.Asset is null && candidate.Diagnostics.Any(message => message.Contains("VMD", StringComparison.Ordinal)),
                "PMM tracks outside VMD name limits retain their raw authored motion and explicit compatibility diagnostics");
            string refusedPma = Path.Combine(output, "workflow-long-track-refused.pma");
            var adoptedHistory = WorkspaceHistory(adopted);
            var adoptedSessionHistory = History(adoptedSession);
            var filesBefore = Directory.GetFileSystemEntries(output).ToHashSet(StringComparer.OrdinalIgnoreCase);
            NotSupportedException? persistenceFailure = null;
            try { adopted.Save(refusedPma); }
            catch (NotSupportedException error) { persistenceFailure = error; }
            Assert(persistenceFailure is not null && persistenceFailure.Message.Contains("VMD", StringComparison.Ordinal)
                && persistenceFailure.Message.Contains("PMA", StringComparison.Ordinal),
                "PMA save explicitly reports that long PMM track names cannot be persisted losslessly in VMD storage");
            Assert(!File.Exists(refusedPma) && filesBefore.SetEquals(Directory.GetFileSystemEntries(output))
                && JsonSerializer.Serialize(adopted.Motions.Single().Motion, Json) == importedMotionBefore
                && adopted.Motions.Single().Asset?.Sha256 == importedMotionSha && adopted.ProjectPath is null && adopted.IsDirty
                && ReferenceEquals(adopted.Session, adoptedSession)
                && WorkspaceHistory(adopted) == adoptedHistory && History(adoptedSession) == adoptedSessionHistory,
                "refused long-track PMA save publishes no file or partial and preserves complete raw motion, document state and histories");
        }
        string pmmRoundtrip = Path.Combine(output, "workflow-adopted-roundtrip.pmm");
        adoptedSession.SavePmm(pmmRoundtrip);
        var preparedRoundtrip = EditorProjectInterchange.PreparePmm(pmmRoundtrip);
        string MorphState(VmdMotion motion) => JsonSerializer.Serialize(motion.MorphKeyframes
            .OrderBy(key => key.MorphName, StringComparer.Ordinal).ThenBy(key => key.Frame)
            .Select(key => new { key.MorphName, key.Frame, key.Weight }), Json);
        Assert(preparedRoundtrip.CanCommit && preparedRoundtrip.Models.Count == 1
            && MorphState(preparedRoundtrip.Models.Single().Motion) == MorphState(importedActor.Motion),
            "adopted PMM SavePmm roundtrip preserves every full morph name, frame and weight without VMD truncation");
        Assert(JsonSerializer.Serialize(adopted.Motions.Single().Motion, Json) == importedMotionBefore
            && adopted.Motions.Single().Asset?.Sha256 == importedMotionSha && adopted.IsDirty && adopted.ProjectPath is null,
            "lossless PMM roundtrip leaves the adopted raw motion and native document identity unchanged");
        Assert(shell.Workspace.Motions.Single().Asset!.Sha256 == originalSha && shell.Workspace.Motions.Count == 1,
            "complete PMM/PMD/VMD/VPD interchange verification preserves the original single-model workflow");

        SceneProjectSession? viewportProject() => window!.FindControl<EditorViewportHost>("SceneViewport")!.Project;
    }

    private static void VerifyPreviewQuality(EditorShellViewModel shell, EditorViewportHost viewport, Func<Exception?> getError)
    {
        string languageBefore = EditorLocalization.CurrentLanguage;
        string liveBefore = LiveState(shell);
        var source = viewport.Quality;
        var capabilities = Await(Task.Run(() => EditorPreviewCapabilities.QueryInstalled(640, 480)), "query installed preview SDK/device capabilities", 90);
        Assert(capabilities.QueryWidth == 640 && capabilities.QueryHeight == 480
            && (!capabilities.DlssAvailable || capabilities.DlssQueries!.Any(query => query.Available && query.Initialized && query.Failure is null)),
            "preview capability choices come from actual device initialization at the requested display dimensions");
        File.WriteAllText(Path.Combine(output, "workflow-preview-capabilities.json"), JsonSerializer.Serialize(capabilities, Json));
        string localPath = Path.Combine(output, "probe-local-preview-quality.json");
        string missingPreviewPath = Path.Combine(output, "missing-preview-quality.json");
        var firstRun = EditorPreferences.LoadPreviewQuality(missingPreviewPath);
        Assert(!File.Exists(missingPreviewPath) && firstRun.Antialiasing == FrameAntialiasing.Smaa1X
            && firstRun.SmaaQuality == SmaaQuality.High && firstRun.SpatialSamples == 1
            && !firstRun.EffectiveGi.Enabled && firstRun.EffectiveTemporalUpscaling.Mode == ViewTemporalUpscalingMode.Off,
            "first-run editor preview defaults to SMAA High and one spatial sample without writing storage or enabling costly GI/reconstruction");
        var explicitOff = firstRun with { Antialiasing = FrameAntialiasing.Off };
        string explicitOffPath = Path.Combine(output, "probe-preview-explicit-off.json");
        EditorPreferences.SavePreviewQuality(explicitOff, explicitOffPath);
        Assert(EditorPreferences.LoadPreviewQuality(explicitOffPath) == explicitOff,
            "saved explicit antialiasing-off choice remains exact instead of being overwritten by the editor first-run preset");
        var runtime = new EditorPreviewRuntimeInfo(viewport.AdapterName, viewport.IsWarp,
            viewport.Statistics!.Width, viewport.Statistics.Height, viewport.Statistics);
        try
        {
            foreach (string language in new[] { "zh-CN", "en", "ja" })
            {
                EditorLocalization.SetLanguage(language); PumpLayout();
                int qualityOpened = 0;
                var preferences = new EditorPreferencesWindow(new EditorPreferences())
                {
                    GetPreviewRuntimeInfo = () => runtime,
                    OpenPreviewQualityAsync = _ => { qualityOpened++; return Task.CompletedTask; }
                };
                var preferencesTask = ShowDialog<EditorPreferences?>(preferences);
                preferences.FindControl<TabControl>("PreferencesTabs")!.SelectedIndex = 3;
                PumpDialog(preferences);
                Assert(preferences.FindControl<TextBlock>("PreferencesDeviceSummary")!.Text!.Contains(viewport.AdapterName!, StringComparison.Ordinal)
                    && preferences.FindControl<Button>("PreferencesQualityButton")!.IsEffectivelyEnabled,
                    "Settings exposes preview quality and the actual viewport adapter: " + language);
                ActivateDialogButton(preferences.FindControl<Button>("PreferencesQualityButton")!);
                Assert(qualityOpened == 1, "Settings quality button invokes the shared preview-quality flow: " + language);
                CaptureDialog(preferences, "workflow-settings-quality-" + language + ".png");
                preferences.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert(Await(preferencesTask, "cancel quality-entry settings") is null, "quality-entry inspection preserves preference drafts: " + language);
                var dialog = new EditorPreviewSettingsWindow(source, capabilities, runtime);
                var appliedTask = ShowDialog<ViewQualityOptions?>(dialog);
                Assert(dialog.ViewModel.HasRuntimeInfo && dialog.ViewModel.DeviceSummary.Contains(viewport.AdapterName!, StringComparison.Ordinal)
                    && dialog.FindControl<Expander>("PreviewAdvancedSampling")!.IsExpanded == false,
                    "preview quality opens with actual runtime information and advanced sampling folded: " + language);
                CaptureDialog(dialog, "workflow-preview-sampling-" + language + ".png");
                dialog.FindControl<TabControl>("PreviewSettingsTabs")!.SelectedIndex = 2; PumpDialog(dialog);
                dialog.FindControl<CheckBox>("PreviewDiffusionEnabledBox")!.IsChecked = true;
                TypeNumberThenFocus(dialog.FindControl<NumericUpDown>("PreviewDiffusionStrengthBox")!, "0.450", dialog.FindControl<Button>("ApplyPreviewSettingsButton")!);
                var apply = dialog.FindControl<Button>("ApplyPreviewSettingsButton")!;
                Assert(apply.Content?.ToString() == dialog.ViewModel.ApplyLabel,
                    "native preview-quality Apply follows the selected UI language: " + language);
                CaptureDialog(dialog, "workflow-preview-quality-" + language + ".png");
                ActivateDialogButton(apply);
                var quality = Await(appliedTask, "native preview quality Apply in " + language)!;
                Assert(quality.EffectiveDiffusion.Enabled && quality.EffectiveDiffusion.Strength == .45f
                    && (quality with { Diffusion = source.Diffusion }) == source,
                    "filter-only native quality edit preserves all untouched AA/GI/upscaling overrides: " + language);
                EditorPreferences.SavePreviewQuality(quality, localPath);
                Assert(EditorPreferences.LoadPreviewQuality(localPath) == quality,
                    "preview quality persists exactly in isolated machine-local storage: " + language);
            }
            byte[] stored = File.ReadAllBytes(localPath);
            var cancelDialog = new EditorPreviewSettingsWindow(source, capabilities);
            var cancelled = ShowDialog<ViewQualityOptions?>(cancelDialog);
            cancelDialog.ViewModel.SpatialIndex = 3;
            cancelDialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert(Await(cancelled, "cancel preview quality draft") is null && File.ReadAllBytes(localPath).SequenceEqual(stored)
                && viewport.Quality == source, "cancelling preview quality preserves active quality and the stored local preset");
            WaitForFrame(viewport, getError, "pre-filter native viewport");
            var rendererField = typeof(EditorViewportHost).GetField("renderer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var historyField = typeof(EditorViewportHost).GetField("historyGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var applyQuality = typeof(EditorWindow).GetMethod("ApplyPreviewQuality", BindingFlags.NonPublic | BindingFlags.Instance)!;
            object renderer = rendererField.GetValue(viewport)!;
            object generation = historyField.GetValue(viewport)!;
            applyQuality.Invoke(window, [viewport, source with { Diffusion = new(true, .45f, .002f) }]);
            WaitForFrame(viewport, getError, "native viewport after final-filter-only change");
            Assert(ReferenceEquals(renderer, rendererField.GetValue(viewport)) && Equals(generation, historyField.GetValue(viewport)),
                "final-filter-only quality change presents on the same real D3D12 renderer and retains its temporal history generation");
            applyQuality.Invoke(window, [viewport, source]);
            WaitForFrame(viewport, getError, "native viewport after restoring preview quality");

            // Only latch the host's failure gates after real hardware rendering. This
            // exercises retry wiring without injecting a driver fault or device removal.
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var failedField = typeof(EditorViewportHost).GetField("failed", flags)!;
            var gate = typeof(EditorViewportHost).GetField("workerGate", flags)!.GetValue(viewport)!;
            var worker = typeof(EditorViewportHost).GetField("workerTask", flags)!;
            var rendererError = typeof(EditorViewportHost).GetProperty(nameof(EditorViewportHost.HasRendererError))!;
            var revision = typeof(EditorViewportHost).GetField("revision", flags)!;
            viewport.IsSuspended = true;
            PumpUntil(() => { lock (gate) return ((Task)worker.GetValue(viewport)!).IsCompleted && !shell.PosePreview.IsPreparingPreview; },
                "settle native worker before controlled failure gate", 30);
            renderer = rendererField.GetValue(viewport)!;
            long failedRevision = (long)revision.GetValue(viewport)!;
            lock (gate) failedField.SetValue(viewport, true);
            rendererError.SetValue(viewport, true);
            typeof(EditorViewportHost).GetMethod("SetPreparingFrame", flags)!.Invoke(viewport, [false]);
            typeof(EditorShellViewModel).GetMethod("ReportActivityFailure", flags)!.Invoke(shell,
                [new InvalidOperationException("Probe-controlled renderer failure gate; no driver/device fault injected."), true]);
            viewport.IsSuspended = false;
            int gatedFrames = presented;
            viewport.RequestRender(); PumpLayout();
            Assert(viewport.HasRendererError && (bool)failedField.GetValue(viewport)! && presented == gatedFrames
                && shell.ActivityState == EditorActivityState.Failed && shell.HasActivityDetails,
                "controlled failed host blocks native presentation and retains the preview error before retry");
            applyQuality.Invoke(window, [viewport, source]);
            Assert(!viewport.HasRendererError && !(bool)failedField.GetValue(viewport)! && (long)revision.GetValue(viewport)! == failedRevision + 1
                && viewport.IsPreparingFrame && !shell.HasActivityDetails,
                "applying unchanged quality after a failed host clears its gates and starts a real renderer retry");
            WaitForFrame(viewport, getError, "native viewport after controlled failure-gate quality retry");
            Assert(!ReferenceEquals(renderer, rendererField.GetValue(viewport)) && viewport.HasChildWindow
                && (allowWarp || !viewport.IsWarp) && !viewport.HasRendererError && !(bool)failedField.GetValue(viewport)!
                && shell.ActivityState != EditorActivityState.Failed && !shell.HasActivityDetails,
                "quality retry rebuilds the actual D3D12 renderer and leaves host and shell failure gates clear");
            Assert(LiveState(shell) == liveBefore, "native preview settings and SDK queries leave project motion, selection, frame and Undo unchanged");
        }
        finally { viewport.IsSuspended = false; viewport.Quality = source; EditorLocalization.SetLanguage(languageBefore); PumpLayout(); }
    }

    private static void VerifyMainLightDirection(EditorShellViewModel shell)
    {
        var scene = shell.SceneInspector;
        var history = WorkspaceHistory(shell.Workspace);
        string originalSceneMotion = JsonSerializer.Serialize(shell.Workspace.Session!.CaptureAuthoredContent().SceneMotion?.Motion, Json);
        var confirmBefore = scene.ConfirmSceneOverwriteAsync;
        var pageTabs = window!.FindControl<TabControl>("InspectorPageTabs")!;
        pageTabs.SelectedIndex = (int)EditorInspectorPage.Lighting; PumpLayout();
        var inspector = window.GetVisualDescendants().OfType<EditorMainLightInspector>().Single();
        var advanced = inspector.FindControl<Expander>("MainLightAdvanced")!;
        var directionPad = inspector.FindControl<EditorLightDirectionPad>("MainLightDirectionPad")!;
        Assert(shell.IsLightingPage && shell.IsLightInspector
            && window.FindControl<Control>("NativeLightInspectorArea")!.IsEffectivelyVisible
            && !window.FindControl<Control>("StandardSelectionInspector")!.IsEffectivelyVisible
            && directionPad.IsEffectivelyVisible && directionPad.IsEffectivelyEnabled && !advanced.IsExpanded
            && inspector.FindControl<NumericUpDown>("LightIntensity")!.IsEffectivelyVisible,
            "the explicit lighting page exposes main-light direction and intensity with precise RGB/XYZ inputs folded");
        advanced.IsExpanded = true; PumpLayout();
        TypeNumber(inspector.FindControl<NumericUpDown>("LightColorR")!, "0.800");
        TypeNumber(inspector.FindControl<NumericUpDown>("LightColorG")!, "0.700");
        TypeNumber(inspector.FindControl<NumericUpDown>("LightColorB")!, "0.600");
        TypeNumber(inspector.FindControl<NumericUpDown>("LightIntensity")!, "0.750");
        var pose = scene.LightPose;
        var rgb = (scene.LightColorR, scene.LightColorG, scene.LightColorB);
        decimal? intensity = scene.LightIntensity;
        advanced.IsExpanded = false; PumpLayout();
        directionPad.Focus();
        var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right };
        directionPad.RaiseEvent(key); PumpLayout();
        var changed = scene.LightPose;
        Assert(key.Handled && scene.HasLightDraft && changed.Direction != pose.Direction
            && changed.Color == pose.Color && (scene.LightColorR, scene.LightColorG, scene.LightColorB) == rgb
            && scene.LightIntensity == intensity && WorkspaceHistory(shell.Workspace) == history,
            "the native main-light direction pad changes a real pose draft while preserving independent RGB, intensity and project history");
        try
        {
            scene.ConfirmSceneOverwriteAsync = _ => Task.FromResult(true);
            Click(inspector.FindControl<Button>("RegisterLightKeyframeButton")!);
            PumpUntil(() => !scene.IsBusy, "register native main-light direction", 30);
            var registered = shell.Workspace.Session.CaptureAuthoredContent().SceneMotion!.Motion.LightKeyframes
                .Single(value => value.Frame == (uint)shell.CurrentFrame);
            Assert(!scene.HasLightDraft && registered.Direction == changed.Direction && registered.Color == changed.Color
                && WorkspaceHistory(shell.Workspace).Undo == history.Undo + 1,
                "the native main-light register button persists its direction and intensity-combined RGB in one scene-motion transaction");
            shell.UndoCommand.Execute(null); PumpLayout();
            Assert(JsonSerializer.Serialize(shell.Workspace.Session.CaptureAuthoredContent().SceneMotion?.Motion, Json) == originalSceneMotion,
                "shell Undo restores the exact scene motion before main-light registration");
            shell.RedoCommand.Execute(null); PumpLayout();
            var redone = shell.Workspace.Session.CaptureAuthoredContent().SceneMotion!.Motion.LightKeyframes
                .Single(value => value.Frame == (uint)shell.CurrentFrame);
            Assert(redone.Direction == changed.Direction && redone.Color == changed.Color && !scene.HasLightDraft,
                "shell Redo restores the registered main-light direction and RGB for portable-save verification");
        }
        finally
        {
            scene.ConfirmSceneOverwriteAsync = confirmBefore;
            pageTabs.SelectedIndex = (int)EditorInspectorPage.Animation; PumpLayout();
        }
    }

    private static void AddNativeLight(SceneLightKind kind)
    {
        var add = window!.FindControl<Button>("AddViewportLightButton")!;
        var flyout = add.Flyout as MenuFlyout ?? throw new InvalidOperationException("Formal Add Light menu is missing.");
        ActivateDialogButton(add);
        var choices = flyout.Items.OfType<MenuItem>().ToArray();
        Assert(flyout.IsOpen && choices.Length == 3, "native Add Light opens the directional/point/spot choices in its viewport flyout");
        choices[(int)kind].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        flyout.Hide(); PumpLayout();
    }

    private static void VerifyInlineLightSaveAndClose(EditorShellViewModel shell, string projectPath)
    {
        var controller = window!.FindControl<EditorViewportHost>("SceneViewport")!.LightController!;
        var light = shell.Workspace.Session!.Lighting.Lights.Last();
        controller.Select(light.Id); PumpLayout();
        var inline = window!.FindControl<EditorLightInspector>("NativeLightInspector")!;
        TextBox NameInput() => inline.GetVisualDescendants().OfType<TextBox>()
            .Single(item => item.Name == "LightNameInput" && item.IsEffectivelyVisible);
        string packageBeforeClose = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(projectPath)));
        var history = WorkspaceHistory(shell.Workspace);
        var name = NameInput(); name.Focus(); name.Text = light.Name + " close-check"; PumpLayout();
        string closingName = name.Text!;
        Assert(inline.ViewModel?.SelectedLight?.Name == closingName
            && shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id).Name == light.Name
            && !shell.Workspace.IsDirty && WorkspaceHistory(shell.Workspace) == history,
            "focused inline light-name text remains a pending edit before the clean document's close/save guard");
        window!.Close();
        PumpUntil(() => window.OwnedWindows.Any(dialog => dialog.IsVisible), "native close guard with pending inline light input", 30);
        var dialog = window.OwnedWindows.Single(item => item.IsVisible);
        var actions = dialog.GetVisualDescendants().OfType<Button>().ToArray();
        Assert(window.IsVisible && actions.Length == 3 && actions[0].IsCancel
            && shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id).Name == closingName
            && shell.Workspace.IsDirty && WorkspaceHistory(shell.Workspace).Undo == history.Undo + 1,
            "closing commits valid pending inline light input before asking to save the newly dirty project");
        ActivateDialogButton(actions[0]); PumpLayout();
        Assert(window.IsVisible && !window.OwnedWindows.Any(item => item.IsVisible)
            && shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id).Name == closingName
            && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(projectPath))) == packageBeforeClose,
            "cancelling the real close prompt keeps the edited native light in the open document and preserves the saved PMA bytes");
        var hex = inline.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "LightHexColor" && item.IsEffectivelyVisible);
        var colorBefore = shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id).Color;
        hex.Focus(); hex.Text = "#GGGGGG"; PumpLayout();
        SendEditorKey(Key.S, KeyModifiers.Control);
        Assert(window.IsVisible && shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id).Color == colorBefore
            && WorkspaceHistory(shell.Workspace).Undo == history.Undo + 1 && DataValidationErrors.GetHasErrors(hex)
            && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(projectPath))) == packageBeforeClose,
            "native Ctrl+S rejects invalid focused hex color without modifying the authored light, history or saved PMA");
        hex.Text = "#82B5FF"; PumpLayout();
        Assert(shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id).Color == colorBefore,
            "the corrected focused hex-color text remains pending before the real save command");
        SendEditorKey(Key.S, KeyModifiers.Control);
        PumpUntil(() => !shell.IsBusy && !shell.Workspace.IsDirty
            && shell.OperationStatus == EditorLocalization.Text("工程已保存。"), "save pending inline light through native Ctrl+S", 60);
        var saved = shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id);
        Assert(saved.Name == closingName && saved.Color == new Vector3(130 / 255f, 181 / 255f, 1)
            && !DataValidationErrors.GetHasErrors(hex)
            && WorkspaceHistory(shell.Workspace).Undo == history.Undo + 2,
            "native Ctrl+S commits corrected focused hex color once and saves the inline light's real PMA document");
        typeof(EditorWindow).GetMethod("CloseSceneLighting", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, new object?[] { null, new RoutedEventArgs() }); PumpLayout();
    }

    private static void VerifyPendingLightBeforeSameBonePick(EditorShellViewModel shell, EditorViewportHost viewport,
        Func<Exception?> getError, Guid objectId, int boneIndex)
    {
        var editorWindow = window ?? throw new InvalidOperationException("The formal editor window is missing.");
        var boneController = viewport.BoneController!;
        var light = shell.Workspace.Session!.Lighting.Lights.Last();
        var pageTabs = editorWindow.FindControl<TabControl>("InspectorPageTabs")!;
        pageTabs.SelectedIndex = (int)EditorInspectorPage.Lighting; PumpLayout();
        viewport.LightController!.Select(light.Id); PumpLayout();
        var inline = editorWindow.FindControl<EditorLightInspector>("NativeLightInspector")!;
        WaitForFrame(viewport, getError, "same selected bone while native light context is open");
        var snapshot = shell.Workspace.Session.CaptureRenderSnapshot(boneController.SnapshotRequests([]));
        int width = viewport.Statistics!.Width, height = viewport.Statistics.Height;
        var camera = viewport.CurrentCamera;
        Assert(shell.Browser.SelectedBoneIndex == boneIndex && shell.Animation.SelectedBoneIndex == boneIndex
            && LightViewportInteraction.TryProject(snapshot.SceneInputs[objectId].BoneWorldMatrices[boneIndex].Translation,
                camera, width, height, out _),
            "the native light context retains the same selected authored bone as a presented viewport target");
        LightViewportInteraction.TryProject(snapshot.SceneInputs[objectId].BoneWorldMatrices[boneIndex].Translation,
            camera, width, height, out var center);
        var history = WorkspaceHistory(shell.Workspace);
        string motionSha = shell.Workspace.Motions.Single(item => item.ObjectId == objectId).Asset!.Sha256;
        string drafts = JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json);
        var name = inline.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "LightNameInput" && item.IsEffectivelyVisible);
        name.Focus(); name.Text = light.Name + " bone-check"; PumpLayout();
        string pendingName = name.Text!;
        Assert(name.IsFocused && shell.IsLightingPage && !boneController.IsManipulationEnabled
            && pageTabs.SelectedIndex == (int)EditorInspectorPage.Lighting
            && inline.ViewModel?.SelectedLight?.Name == pendingName
            && shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id).Name == light.Name
            && WorkspaceHistory(shell.Workspace) == history,
            "same-bone selection regression starts with valid focused light-name text that has not blurred or entered project history");
        int x = (int)MathF.Round(center.X), y = (int)MathF.Round(center.Y);
        Assert(boneController.TryBeginDrag(x, y, width, height, camera) && !boneController.IsDragging,
            "a presented same-bone hit is consumed safely while its pending light input is resolved");
        PumpLayout();
        Assert(shell.Workspace.Session.Lighting.Lights.Single(item => item.Id == light.Id).Name == pendingName
            && WorkspaceHistory(shell.Workspace).Undo == history.Undo + 1
            && shell.IsLightingPage && shell.IsLightInspector && pageTabs.SelectedIndex == (int)EditorInspectorPage.Lighting
            && inline.IsEffectivelyVisible && editorWindow.FindControl<Control>("NativeLightInspectorArea")!.IsEffectivelyVisible
            && !editorWindow.FindControl<Control>("StandardSelectionInspector")!.IsEffectivelyVisible
            && viewport.LightController!.SelectedLightId is null && inline.ViewModel?.SelectedLight is null
            && shell.Browser.SelectedBoneIndex == boneIndex && shell.Animation.SelectedBoneIndex == boneIndex
            && JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json) == drafts
            && shell.Workspace.Motions.Single(item => item.ObjectId == objectId).Asset!.Sha256 == motionSha
            && !editorWindow.OwnedWindows.Any(item => item.IsVisible),
            "selecting the same bone commits the pending light once and releases its handle while preserving the chosen lighting page, inspector, pose and motion state");
        WaitForFrame(viewport, getError, "same-bone target after pending light commit on the preserved lighting page");
        Assert(boneController.TryBeginDrag(x, y, width, height, viewport.CurrentCamera) && !boneController.IsDragging
            && shell.Browser.SelectedBoneIndex == boneIndex && WorkspaceHistory(shell.Workspace).Undo == history.Undo + 1
            && shell.IsLightingPage && pageTabs.SelectedIndex == (int)EditorInspectorPage.Lighting
            && inline.IsEffectivelyVisible && !editorWindow.FindControl<Control>("StandardSelectionInspector")!.IsEffectivelyVisible,
            "the repainted same-bone target remains selectable on the chosen lighting page without another lighting or animation edit");
    }

    private static void VerifyLightingAndBoneGuides(EditorShellViewModel shell, EditorViewportHost viewport,
        Func<Exception?> getError, Guid id, int boneIndex)
    {
        VerifyMainLightDirection(shell);
        var workspace = shell.Workspace;
        string lightBefore = JsonSerializer.Serialize(workspace.Session!.Lighting.Lights, Json);
        string motionSha = workspace.Motions.Single().Asset!.Sha256;
        var history = WorkspaceHistory(workspace);
        var sessionHistory = History(workspace.Session!);
        var cancelDialog = new EditorLightingWindow(workspace.Session.Lighting);
        var cancelled = ShowDialog<EditorLightingDraft?>(cancelDialog);
        cancelDialog.ViewModel.AddLight(SceneLightKind.Point);
        cancelDialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert(Await(cancelled, "cancel native lighting draft") is null
            && JsonSerializer.Serialize(workspace.Session.Lighting.Lights, Json) == lightBefore && WorkspaceHistory(workspace) == history,
            "cancelling native light editing preserves the real lighting snapshot and history");
        var dialog = new EditorLightingWindow(workspace.Session.Lighting);
        var applied = ShowDialog<EditorLightingDraft?>(dialog);
        dialog.ViewModel.AddLight(SceneLightKind.Point);
        var draftLight = dialog.ViewModel.SelectedLight!;
        draftLight.PositionX = 0; draftLight.PositionY = 10; draftLight.PositionZ = 0;
        draftLight.Intensity = .5m;
        PumpDialog(dialog); CaptureDialog(dialog, "workflow-lighting-draft-compatibility.png");
        ActivateDialogButton(dialog.FindControl<Button>("ApplyLightingButton")!);
        var draft = Await(applied, "apply native lighting draft")!;
        Assert(JsonSerializer.Serialize(workspace.Session.Lighting.Lights, Json) == lightBefore,
            "native lighting Apply returns a detached draft before its one host transaction");
        AddNativeLight(SceneLightKind.Spot);
        Assert(WorkspaceHistory(workspace).Undo == history.Undo + 1 && History(workspace.Session!).Undo == sessionHistory.Undo + 1
            && workspace.Session.Objects.Single(item => item.Id == id).Role == ModelRole.Character,
            "the actual viewport Add Light menu commits exactly one workspace/session Undo transaction");
        var lightController = viewport.LightController ?? throw new InvalidOperationException("Formal viewport light controller is missing.");
        Assert(viewport.BoneController is not null && viewport.SnapshotProvider is not null && viewport.FrameDecorator is not null,
            "formal host connects native light/bone controllers, source requests and final editor decoration");
        var inline = window!.FindControl<EditorLightInspector>("NativeLightInspector")!;
        Guid lightId = lightController.SelectedLightId ?? throw new InvalidOperationException("New native light is not selected.");
        Assert(lightController.IsEnabled && inline.IsEffectivelyVisible
            && !window!.FindControl<Control>("StandardSelectionInspector")!.IsEffectivelyVisible
            && window!.FindControl<Control>("LightToolsBar")!.IsEffectivelyVisible
            && inline.ViewModel?.SelectedLight?.Id == lightId && !window!.OwnedWindows.Any(item => item.IsVisible),
            "adding a light selects its inline inspector and transform context directly without a modal lighting window");
        var advanced = inline.GetVisualDescendants().OfType<Expander>().Single(item => item.Name == "InlineLightAdvanced" && item.IsEffectivelyVisible);
        var intensity = inline.GetVisualDescendants().OfType<NumericUpDown>().Single(item => item.Name == "InlineLightIntensity" && item.IsEffectivelyVisible);
        Assert(!advanced.IsExpanded && intensity.IsEffectivelyEnabled
            && inline.GetVisualDescendants().OfType<TextBox>().Any(item => item.Name == "LightHexColor" && item.IsEffectivelyVisible),
            "the selected native light exposes color and intensity while precise transform/cone/RGB settings start folded");
        TypeNumber(intensity, "0.500");
        Assert(workspace.Session.Lighting.Lights.Single(value => value.Id == lightId).Intensity == .5f
            && WorkspaceHistory(workspace).Undo == history.Undo + 2 && !window!.OwnedWindows.Any(item => item.IsVisible),
            "valid native inline intensity input commits one immediate project-light transaction");
        var colorButton = inline.GetVisualDescendants().OfType<Button>().Single(item => item.Tag is "#82B5FF" && item.IsEffectivelyVisible);
        ActivateDialogButton(colorButton);
        Assert(workspace.Session.Lighting.Lights.Single(value => value.Id == lightId).Color == new Vector3(130 / 255f, 181 / 255f, 1)
            && WorkspaceHistory(workspace).Undo == history.Undo + 3,
            "the actual native light color swatch commits its selected RGB rather than restoring stale hex text");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(workspace.Session.Lighting.Lights.Single(value => value.Id == lightId).Color == Vector3.One,
            "one shell Undo restores native-light color independently of its valid intensity edit");
        CaptureControls("workflow-inline-lighting-controls-hwnd-omitted.png");
        WaitForFrame(viewport, getError, "native Add Light and inline intensity presentation");
        var light = workspace.Session.Lighting.Lights.Single(value => value.Id == lightId);
        var camera = viewport.CurrentCamera;
        var lightSnapshot = workspace.Session.CaptureRenderSnapshot();
        var lightFrame = new PreviewFrame(640, 480, shell.CurrentFrame, lightSnapshot.Scene, lightSnapshot.Camera, lightSnapshot.Light, lightSnapshot.SelfShadow)
        { ResolvedCamera = camera };
        Assert(lightController.DecorateFrame(lightFrame).Scene.EditorOverlay?.Lines.Count > (lightFrame.Scene.EditorOverlay?.Lines.Count ?? 0),
            "actual native-light controller adds visible editor light handles to the final scene overlay");
        int width = viewport.Statistics!.Width, height = viewport.Statistics.Height;
        Assert(LightViewportInteraction.TryProject(light.Position, camera, width, height, out var center),
            "authored native spotlight projects inside the actual editor camera");
        int x = (int)MathF.Round(center.X), y = (int)MathF.Round(center.Y);
        var dragHistory = WorkspaceHistory(workspace);
        Assert(lightController.TryBeginDrag(x, y, width, height, camera) && lightController.MoveDrag(x + 14, y + 7)
            && lightController.IsDragging && WorkspaceHistory(workspace) == dragHistory,
            "picking and dragging a real light previews its pose without mutating project Undo");
        lightController.CancelDrag();
        Assert(!lightController.IsDragging && lightController.PreviewLighting.Lights.Single(value => value.Id == light.Id).Position == light.Position
            && WorkspaceHistory(workspace) == dragHistory,
            "cancelled light gesture restores the exact native pose and creates no history");
        WaitForFrame(viewport, getError, "native light icon after cancelling its preview");
        Assert(lightController.TryBeginDrag(x, y, width, height, camera) && lightController.MoveDrag(x + 20, y)
            && lightController.EndDrag() && WorkspaceHistory(workspace).Undo == dragHistory.Undo + 1,
            "releasing a changed native light gesture publishes exactly one project edit");
        var movedLight = workspace.Session.Lighting.Lights.Single(value => value.Id == lightId);
        Assert(movedLight.Position != light.Position && movedLight.Rotation == light.Rotation,
            "dragging the displayed light icon changes position while preserving its authored direction");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(workspace.Session.Lighting.Lights.Single(value => value.Id == light.Id).Position == light.Position,
            "one shell Undo restores the light pose before its completed viewport gesture");
        shell.RedoCommand.Execute(null); PumpLayout();
        Assert(workspace.Session.Lighting.Lights.Single(value => value.Id == lightId) == movedLight,
            "one shell Redo restores the exact completed native light movement");
        shell.UndoCommand.Execute(null); PumpLayout();
        ActivateDialogButton(window!.FindControl<ToggleButton>("LightRotateMode")!);
        WaitForFrame(viewport, getError, "selected native spotlight rotation rings");
        var rotationHistory = WorkspaceHistory(workspace);
        Assert(lightController.RotateHandle && window!.FindControl<ToggleButton>("LightRotateMode")!.IsChecked == true,
            "the native Rotate tool selects the controller mode used by displayed light rings");
        var rotationFrame = lightFrame with { Width = width, Height = height, ResolvedCamera = camera };
        var emittedRings = lightController.DecorateFrame(rotationFrame).Scene.EditorOverlay!.Lines;
        var ring = emittedRings.Where(line => line.Color == new Vector4(.32f, .55f, 1, 1))
            .Where(line => Vector2.Distance((line.Start + line.End) * new Vector2(width, height) * .5f, center) > 20)
            .OrderByDescending(line => Vector2.DistanceSquared(line.Start * new Vector2(width, height), line.End * new Vector2(width, height))).First();
        var ringPoint = (ring.Start + ring.End) * new Vector2(width, height) * .5f;
        var ringDelta = Vector2.Normalize((ring.End - ring.Start) * new Vector2(width, height)) * 18;
        int ringX = (int)MathF.Round(ringPoint.X), ringY = (int)MathF.Round(ringPoint.Y);
        Assert(lightController.TryBeginDrag(ringX, ringY, width, height, camera)
            && lightController.MoveDrag((int)MathF.Round(ringPoint.X + ringDelta.X), (int)MathF.Round(ringPoint.Y + ringDelta.Y))
            && lightController.IsDragging && WorkspaceHistory(workspace) == rotationHistory,
            "a displayed spotlight ring previews a direction edit without adding project history");
        Assert(lightController.EndDrag(), "releasing the displayed native light ring commits a changed direction");
        var rotatedLight = workspace.Session.Lighting.Lights.Single(value => value.Id == lightId);
        Assert(rotatedLight.Position == light.Position && rotatedLight.Rotation != light.Rotation
            && WorkspaceHistory(workspace).Undo == rotationHistory.Undo + 1,
            "the rotation ring changes only orientation and creates exactly one project-light edit");
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(workspace.Session.Lighting.Lights.Single(value => value.Id == lightId) == light,
            "shell Undo restores the exact native spotlight before its ring rotation");
        shell.RedoCommand.Execute(null); PumpLayout();
        Assert(workspace.Session.Lighting.Lights.Single(value => value.Id == lightId) == rotatedLight,
            "shell Redo restores the exact completed native spotlight rotation");
        shell.UndoCommand.Execute(null); PumpLayout();
        shell.UndoCommand.Execute(null); PumpLayout();
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(JsonSerializer.Serialize(workspace.Session.Lighting.Lights, Json) == lightBefore
            && workspace.Motions.Single().Asset!.Sha256 == motionSha,
            "undoing native light rotation, inline intensity and Add restores original lighting and the exact actor motion");
        shell.RedoCommand.Execute(null); PumpLayout();
        shell.RedoCommand.Execute(null); PumpLayout();
        shell.RedoCommand.Execute(null); PumpLayout();
        Assert(workspace.Session.Lighting.Lights.Single(value => value.Id == lightId) == rotatedLight,
            "the same native light transactions can be restored for subsequent portable PMA validation");
        var closeLightContext = typeof(EditorWindow).GetMethod("CloseSceneLighting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        closeLightContext.Invoke(window, new object?[] { null, new RoutedEventArgs() }); PumpLayout();
        Assert(!inline.IsEffectivelyVisible && window!.FindControl<Control>("StandardSelectionInspector")!.IsEffectivelyVisible,
            "leaving light context restores the model/bone inspector without changing authored lights");

        shell.Browser.HierarchyTab = 0; shell.Browser.SelectedBoneIndex = boneIndex; PumpLayout();
        var boneController = viewport.BoneController!;
        Assert(!boneController.IsManipulationEnabled && window!.FindControl<ToggleButton>("BoneSelectMode")!.IsChecked == true,
            "formal bone tools start in selection mode before an explicit Move or Rotate action");
        bool showBones = boneController.ShowBones, showBounds = boneController.ShowSelectionBounds;
        boneController.ShowBones = true; boneController.ShowSelectionBounds = true;
        VerifyPendingLightBeforeSameBonePick(shell, viewport, getError, id, boneIndex);
        WaitForFrame(viewport, getError, "native solved-bone overlays before selection and explicit tools");
        int boneWidth = viewport.Statistics!.Width, boneHeight = viewport.Statistics.Height;
        var requests = boneController.SnapshotRequests([new(id, localTextureIndices: [0], optionalLocalTextureIndices: [1])]);
        var merged = requests.Single(value => value.ObjectId == id);
        Assert(merged.BoneIndices.Count == shell.Browser.SelectedModel!.PmxModel.Bones.Count && merged.IncludeSourceGeometry
            && merged.LocalTextureIndices.SequenceEqual(new[] { 0 }) && merged.OptionalLocalTextureIndices.SequenceEqual(new[] { 1 }),
            "bone guides merge every authored bone and selection geometry while preserving required/optional renderer texture requests");
        var snapshot = workspace.Session.CaptureRenderSnapshot(boneController.SnapshotRequests([]));
        var frame = new PreviewFrame(boneWidth, boneHeight, shell.CurrentFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        { ResolvedCamera = viewport.CurrentCamera, SceneInputs = snapshot.SceneInputs, RenderingStyles = snapshot.RenderingStyles, MorphChannels = snapshot.MorphChannels };
        var decorated = boneController.DecorateFrame(frame, publishPicking: false);
        Assert(decorated.Scene.EditorOverlay?.Lines.Count > (frame.Scene.EditorOverlay?.Lines.Count ?? 0)
            && snapshot.SceneInputs[id].BoneWorldMatrices.Count == shell.Browser.SelectedModel.PmxModel.Bones.Count,
            "actual evaluated model produces solved-bone/selection overlay lines after scene rendering");
        var selectedWorld = snapshot.SceneInputs[id].BoneWorldMatrices[boneIndex].Translation;
        if (LightViewportInteraction.TryProject(selectedWorld, viewport.CurrentCamera, boneWidth, boneHeight, out var boneCenter))
        {
            var beforePick = WorkspaceHistory(workspace);
            var draftBeforePick = JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json);
            bool hadDraftBeforePick = shell.Animation.HasAnyDraft;
            int boneX = (int)MathF.Round(boneCenter.X), boneY = (int)MathF.Round(boneCenter.Y);
            Assert(boneController.TryBeginDrag(boneX, boneY, boneWidth, boneHeight, viewport.CurrentCamera)
                && shell.Browser.SelectedBoneIndex == shell.Animation.SelectedBoneIndex && !boneController.IsDragging,
                "selection-mode bone-center picking synchronizes browser and inspector without starting a gesture");
            Assert(!boneController.MoveDrag(boneX + 14, boneY + 7) && !boneController.EndDrag()
                && JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json) == draftBeforePick
                && shell.Animation.HasAnyDraft == hadDraftBeforePick
                && WorkspaceHistory(workspace) == beforePick && workspace.Motions.Single().Asset!.Sha256 == motionSha,
                "moving after a selection-only pick changes neither pose drafts nor registered motion and history");

            int pickedBone = shell.Animation.SelectedBoneIndex!.Value;
            var pickedWorld = snapshot.SceneInputs[id].BoneWorldMatrices[pickedBone].Translation;
            Assert(LightViewportInteraction.TryProject(pickedWorld, viewport.CurrentCamera, boneWidth, boneHeight, out var pickedCenter),
                "the actually picked authored bone remains projectable for explicit transform verification");
            boneX = (int)MathF.Round(pickedCenter.X); boneY = (int)MathF.Round(pickedCenter.Y);
            bool canMove = shell.Animation.CanTranslate, canRotate = shell.Animation.CanRotate;
            Assert(canMove || canRotate, "the picked authored bone supports at least one explicit transform tool");
            if (canMove) VerifyExplicitBoneGesture("BoneMoveMode", rotate: false);
            if (canRotate) VerifyExplicitBoneGesture("BoneRotateMode", rotate: true);
            ActivateBoneMode("BoneSelectMode");
            Assert(!boneController.IsManipulationEnabled && window!.FindControl<ToggleButton>("BoneSelectMode")!.IsChecked == true,
                "the formal Select button exits explicit manipulation mode after the cancelled gestures");

            void VerifyExplicitBoneGesture(string buttonName, bool rotate)
            {
                ActivateBoneMode(buttonName);
                Assert(boneController.IsManipulationEnabled && boneController.RotateHandle == rotate
                    && window!.FindControl<ToggleButton>(buttonName)!.IsChecked == true,
                    "the actual bone tool button enables its matching explicit gesture: " + buttonName);
                WaitForFrame(viewport, getError, "native presented bone tool geometry: " + buttonName);
                Assert(boneController.TryBeginDrag(boneX, boneY, boneWidth, boneHeight, viewport.CurrentCamera)
                    && boneController.IsDragging && shell.Animation.SelectedBoneIndex == pickedBone,
                    "explicit tool starts a real gesture on the picked authored bone: " + buttonName);
                Assert(boneController.MoveDrag(boneX + 14, boneY + 7) && boneController.IsDragging
                    && shell.Animation.HasAnyDraft
                    && JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json) != draftBeforePick
                    && WorkspaceHistory(workspace) == beforePick && workspace.Motions.Single().Asset!.Sha256 == motionSha,
                    "explicit bone gesture changes an unregistered pose while preserving project history: " + buttonName);
                boneController.CancelDrag();
                Assert(!boneController.IsDragging && JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json) == draftBeforePick
                    && shell.Animation.HasAnyDraft == hadDraftBeforePick
                    && WorkspaceHistory(workspace) == beforePick && workspace.Motions.Single().Asset!.Sha256 == motionSha,
                    "cancelling the explicit bone gesture restores the exact prior draft without registering: " + buttonName);
            }
        }
        else throw new InvalidOperationException("Selected authored bone must be visible for native bone picking verification.");
        WaitForFrame(viewport, getError, "native GPU presentation with formal bone/selection overlays");
        boneController.ShowBones = showBones; boneController.ShowSelectionBounds = showBounds;

        void ActivateBoneMode(string buttonName)
        {
            var button = window!.FindControl<ToggleButton>(buttonName)
                ?? throw new InvalidOperationException("Formal bone tool button is missing: " + buttonName);
            Assert(button.IsEffectivelyEnabled, "formal bone tool button is enabled: " + buttonName);
            button.Focus();
            button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
            PumpLayout();
        }
    }

    private static void VerifyRigSettings(EditorShellViewModel shell, SceneAnimationModelAsset asset, string modelPath, int boneIndex)
    {
        string mainBefore = LiveState(shell);
        using var workspace = new EditorProjectWorkspace();
        Guid child = workspace.AddModel(modelPath, asset, ModelRole.Character);
        Guid parent = workspace.AddModel(modelPath, asset, ModelRole.Character);
        workspace.BindMotion(child, shell.Workspace.Motions.Single().Asset!);
        workspace.Edit(edit =>
        {
            for (int index = 0; index < edit.Motions.Count; index++) edit.Motions[index] = edit.Motions[index] with { PhysicsEnabled = false };
            edit.Composition.SetTransform(parent, new(2, 3, 1), Quaternion.CreateFromYawPitchRoll(.2f, 0, 0), 1);
        });
        var controllers = asset.Model.Bones.Where(bone => bone.Flags.HasFlag(PmxBoneFlags.Ik)).ToArray();
        bool hasIk = controllers.Length != 0 && controllers.Select(bone => bone.Name).Distinct(StringComparer.Ordinal).Count() == controllers.Length;
        if (hasIk)
        {
            var staged = new MotionEditor(workspace.Motions.Single(actor => actor.ObjectId == child).Motion);
            var records = new VmdMotion { IkKeyframes = [new() { Frame = 0, Visible = true,
                Switches = controllers.Select(bone => new VmdIkSwitch(bone.Name, true)).Append(new("probe-extra", false)).ToArray() }] };
            staged.Apply(staged.PrepareRegister(records), confirmOverwrite: true);
            workspace.CommitMotion(child, staged.ExportMotion());
        }
        else Skipped.Add("Input model has no unambiguous IK controllers; native outside-parent keep-world/Undo is checked, real-model IK switches require authored controllers.");
        string baselineMotion = workspace.Motions.Single(actor => actor.ObjectId == child).Asset!.Sha256;
        var baselineHistory = WorkspaceHistory(workspace);
        var baselineSessionHistory = History(workspace.Session!);
        var noop = new EditorRigSettingsWindow(workspace, child, boneIndex);
        var noopTask = ShowDialog<EditorRigSettingsDraft?>(noop);
        ActivateDialogButton(noop.FindControl<Button>("ApplyRigSettingsButton")!);
        var empty = Await(noopTask, "native unchanged rig Apply")!;
        workspace.ApplyRigSettings(empty);
        Assert(empty.OutsideParents.Count == 0 && empty.Motion is null && WorkspaceHistory(workspace) == baselineHistory,
            "native rig Apply without changes creates no project transaction or Undo entry");
        var cancel = new EditorRigSettingsWindow(workspace, child, boneIndex);
        var cancelTask = ShowDialog<EditorRigSettingsDraft?>(cancel);
        cancel.ViewModel.SelectedTargetModel = cancel.ViewModel.TargetModels.Single(item => item.ObjectId == parent);
        cancel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert(Await(cancelTask, "native rig cancellation") is null && workspace.Session!.OutsideParents.Count == 0
            && WorkspaceHistory(workspace) == baselineHistory && workspace.Motions.Single(actor => actor.ObjectId == child).Asset!.Sha256 == baselineMotion,
            "cancelling an incomplete native outside-parent draft preserves attachments, authored IK and Undo");
        var request = new SceneObjectRenderRequest(child, Enumerable.Range(0, asset.Model.Bones.Count));
        var rigSession = workspace.Session!;
        var beforeWorlds = rigSession.CaptureRenderSnapshot([request]).SceneInputs[child].BoneWorldMatrices;
        var dialog = new EditorRigSettingsWindow(workspace, child, boneIndex);
        var task = ShowDialog<EditorRigSettingsDraft?>(dialog);
        dialog.FindControl<ComboBox>("RigSubjectBox")!.SelectedItem = dialog.ViewModel.Subjects.Single(item => item.Index == -1);
        dialog.FindControl<ComboBox>("RigTargetModelBox")!.SelectedItem = dialog.ViewModel.TargetModels.Single(item => item.ObjectId == parent);
        dialog.FindControl<ComboBox>("RigTargetBoneBox")!.SelectedItem = dialog.ViewModel.TargetBones.Single(item => item.Index == boneIndex);
        Assert(dialog.ViewModel.KeepWorld && dialog.FindControl<CheckBox>("RigKeepWorldBox")!.IsChecked == true,
            "native outside-parent binding defaults to preserving the current world pose");
        if (hasIk)
        {
            dialog.ViewModel.IkControllers[0].Enabled = false;
            int confirmations = 0;
            dialog.ConfirmOverwriteAsync = plan => { confirmations++; return Task.FromResult(false); };
            ActivateDialogButton(dialog.FindControl<Button>("ApplyRigSettingsButton")!);
            PumpUntil(() => !dialog.ViewModel.IsBusy, "cancel native IK conflict", 30);
            Assert(confirmations == 1 && !task.IsCompleted && dialog.ViewModel.HasValidationMessage
                && rigSession.OutsideParents.Count == 0 && WorkspaceHistory(workspace) == baselineHistory,
                "cancelling a native IK key conflict retains the combined outside-parent/IK draft without a partial project edit");
            // Commit keep-world and the deliberately pose-changing IK switch separately:
            // preserving every solved bone cannot also require a changed IK solution.
            dialog.ViewModel.IkControllers[0].Enabled = true;
        }
        PumpDialog(dialog); CaptureDialog(dialog, "workflow-rig-settings.png");
        ActivateDialogButton(dialog.FindControl<Button>("ApplyRigSettingsButton")!);
        var draft = Await(task, "native outside-parent and IK Apply")!;
        Assert(draft.OutsideParents.Count == 1 && draft.OutsideParents[0].BoneIndex == -1 && draft.OutsideParents[0].KeepWorld
            && draft.OutsideParents[0].Binding?.ParentObjectId == parent && rigSession.OutsideParents.Count == 0,
            "native rig Apply returns one detached explicit whole-model keep-world binding");
        workspace.ApplyRigSettings(draft);
        var afterWorlds = rigSession.CaptureRenderSnapshot([request]).SceneInputs[child].BoneWorldMatrices;
        Assert(beforeWorlds.All(pair => MatrixNear(pair.Value, afterWorlds[pair.Key]))
            && WorkspaceHistory(workspace).Undo == baselineHistory.Undo + 1 && History(rigSession).Undo == baselineSessionHistory.Undo + 1,
            "formal rig host commits one Undo transaction and preserves every solved child bone world transform");
        workspace.Undo();
        Assert(rigSession.OutsideParents.Count == 0 && workspace.Motions.Single(actor => actor.ObjectId == child).Asset!.Sha256 == baselineMotion,
            "one rig Undo restores both original attachment graph and exact authored IK motion bytes");
        if (hasIk)
        {
            var ikDialog = new EditorRigSettingsWindow(workspace, child, boneIndex) { ConfirmOverwriteAsync = _ => Task.FromResult(true) };
            var ikTask = ShowDialog<EditorRigSettingsDraft?>(ikDialog);
            ikDialog.ViewModel.IkControllers[0].Enabled = false;
            ActivateDialogButton(ikDialog.FindControl<Button>("ApplyRigSettingsButton")!);
            var ikDraft = Await(ikTask, "confirm native authored IK switch")!;
            workspace.ApplyRigSettings(ikDraft);
            var switches = workspace.Motions.Single(actor => actor.ObjectId == child).Motion.IkKeyframes.Single(key => key.Frame == 0).Switches;
            Assert(!switches.Single(item => item.BoneName == controllers[0].Name).Enabled
                && switches.Any(item => item.BoneName == "probe-extra" && !item.Enabled)
                && WorkspaceHistory(workspace).Undo == baselineHistory.Undo + 1,
                "confirmed native IK edit updates its real controller, retains unknown authored switches and creates one Undo");
            workspace.Undo();
            Assert(workspace.Motions.Single(actor => actor.ObjectId == child).Asset!.Sha256 == baselineMotion,
                "one IK Undo restores the exact original immutable motion including authored switches");
        }
        Assert(LiveState(shell) == mainBefore, "independently verified native rig editing leaves the active user workflow unchanged");
    }

    private static bool MatrixNear(Matrix4x4 a, Matrix4x4 b)
    {
        const float tolerance = 2e-4f;
        return MathF.Abs(a.M11 - b.M11) <= tolerance && MathF.Abs(a.M12 - b.M12) <= tolerance && MathF.Abs(a.M13 - b.M13) <= tolerance
            && MathF.Abs(a.M21 - b.M21) <= tolerance && MathF.Abs(a.M22 - b.M22) <= tolerance && MathF.Abs(a.M23 - b.M23) <= tolerance
            && MathF.Abs(a.M31 - b.M31) <= tolerance && MathF.Abs(a.M32 - b.M32) <= tolerance && MathF.Abs(a.M33 - b.M33) <= tolerance
            && MathF.Abs(a.M41 - b.M41) <= tolerance && MathF.Abs(a.M42 - b.M42) <= tolerance && MathF.Abs(a.M43 - b.M43) <= tolerance;
    }

    private static string ModelPropertiesState(EditorProjectWorkspace workspace, Guid id)
    {
        var item = workspace.Session!.Objects.Single(value => value.Id == id);
        var actor = workspace.Motions.Single(value => value.ObjectId == id);
        workspace.MaterialBindings.TryGetValue(id, out var mapping);
        return JsonSerializer.Serialize(new { item.Name, item.Role, item.Position, item.Rotation, item.Scale,
            actor.IkEnabled, actor.MorphsEnabled, actor.PhysicsEnabled,
            style = workspace.Session.RenderingStyles.ModelOverrides.GetValueOrDefault(id), mapping }, Json);
    }

    private static Task<T> ShowDialog<T>(Window dialog)
    {
        dialog.ShowInTaskbar = false; dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(-20000, -20000);
        var task = dialog.ShowDialog<T>(window!); PumpDialog(dialog); return task;
    }
    private static void PumpDialog(Window dialog) { PumpLayout(); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void ActivateDialogButton(Button button)
    {
        Assert(button.IsEffectivelyEnabled, "native dialog action is enabled: " + button.Name);
        button.Focus();
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
        PumpLayout();
    }
    private static void CaptureDialog(Window dialog, string name)
    {
        Assert(dialog.IsVisible, "native dialog is still open when its visual evidence is captured: " + name);
        // Run the native dispatcher long enough for theme/template invalidation and opening
        // transitions to complete. RunJobs alone can capture an all-black first composition.
        var settled = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => settled.Continue = false;
        timer.Start();
        try { Dispatcher.UIThread.PushFrame(settled); }
        finally { timer.Stop(); }
        PumpDialog(dialog); var scale = dialog.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(dialog.Bounds.Width * scale),
            (int)Math.Ceiling(dialog.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(dialog); string path = Path.Combine(output, name); bitmap.Save(path);
        File.WriteAllText(path + ".layout.json", JsonSerializer.Serialize(dialog.GetVisualDescendants().Prepend(dialog).Select(visual => new
        { type = visual.GetType().Name, name = (visual as Control)?.Name, bounds = visual.Bounds.ToString(), visual.IsVisible, visual.IsEffectivelyVisible,
            visual.Opacity, transform = visual.RenderTransform?.ToString(), clip = visual.Clip?.ToString() }), Json));
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException("Dialog capture failed: " + name);
        var first = decoded.GetPixel(0, 0);
        Assert(decoded.Pixels.Count(pixel => Math.Abs(pixel.Red - first.Red) + Math.Abs(pixel.Green - first.Green) + Math.Abs(pixel.Blue - first.Blue) > 40) > 1000,
            "native dialog capture contains visible controls rather than an empty background: " + name);
    }

    private static void SendEditorKey(Key key, KeyModifiers modifiers)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        window!.RaiseEvent(args); PumpLayout();
        Assert(args.Handled, "formal native window handles routed shortcut: " + modifiers + "+" + key);
    }
    private static void PointerPress(EditorTimeline timeline, IPointer pointer, Point location, ulong timestamp) =>
        timeline.RaiseEvent(new PointerPressedEventArgs(timeline, pointer, window!, timeline.TranslatePoint(location, window!)!.Value, timestamp,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
    private static void PointerMove(EditorTimeline timeline, IPointer pointer, Point location, ulong timestamp) =>
        timeline.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, timeline, pointer, window!, timeline.TranslatePoint(location, window!)!.Value, timestamp,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
    private static void PointerRelease(EditorTimeline timeline, IPointer pointer, Point location, ulong timestamp) =>
        timeline.RaiseEvent(new PointerReleasedEventArgs(timeline, pointer, window!, timeline.TranslatePoint(location, window!)!.Value, timestamp,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));

    private static HistoryCounts WorkspaceHistory(EditorProjectWorkspace workspace)
    {
        int Count(string name) => ((ICollection)typeof(EditorProjectWorkspace).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workspace)!).Count;
        return new(Count("undo"), Count("redo"));
    }

    private static void VerifyExports(EditorShellViewModel shell, string? ffmpeg, string? ffprobe)
    {
        var workspace = shell.Workspace;
        // Keep a real undoable edit active so export must preserve meaningful history and dirty state.
        string beforeEditSha = workspace.Motions.Single().Asset!.Sha256;
        shell.CurrentFrame = 9; shell.Animation.PositionX = 1.75m;
        Await(shell.Animation.RegisterKeyframeAsync(), "unsaved pre-export key edit");
        var before = LiveState(shell);
        var temporaryBefore = ExportTemporaries();
        var still = Draft(OutputMediaKind.PngImage, Path.Combine(output, "workflow-transparent.png"), 6, 6, true);
        var stillResult = Await(EditorExportService.ExportAsync(workspace, still, null, null), "actual transparent PNG export", 120);
        Assert(stillResult.CompletedFrames == 1 && (allowWarp || !stillResult.BackendId.Contains("warp", StringComparison.OrdinalIgnoreCase)),
            "formal export service captures one actual D3D12 PNG frame");
        VerifyPng(still.Destination, true);
        Assert(LiveState(shell) == before, "PNG export leaves live frame, dirty state, document path, selection, motion and Undo/Redo counts unchanged");
        var sequence = Draft(OutputMediaKind.PngSequence, Path.Combine(output, "workflow-sequence"), 6, 8, true);
        var sequenceResult = Await(EditorExportService.ExportAsync(workspace, sequence, null, null), "actual PNG sequence export", 120);
        Assert(sequenceResult.CompletedFrames == 3 && Directory.GetFiles(sequence.Destination, "*.png").Length == 3,
            "formal service publishes exactly three planned PNG sequence frames");
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(sequence.Destination, "manifest.json"))))
            Assert(manifest.RootElement.GetProperty("complete").GetBoolean() && manifest.RootElement.GetProperty("frameCount").GetInt64() == 3,
                "published PNG sequence manifest declares all planned frames complete");
        foreach (var path in Directory.GetFiles(sequence.Destination, "*.png")) VerifyPng(path, true);
        Assert(LiveState(shell) == before, "sequence export preserves the full live document state");

        if (ffmpeg is not null && ffprobe is not null)
        {
          foreach (var encoder in new[] { VideoEncoder.HevcSoftware, VideoEncoder.H264Software, VideoEncoder.Av1AomSoftware, VideoEncoder.RawVideo })
          {
            var options = new FfmpegOutputOptions("unused", encoder);
            string slug = options.CodecName;
            var video = Draft(OutputMediaKind.Video, Path.Combine(output, "workflow-" + slug + "." + options.Container), 6, 8, false);
            video = video with { VideoOptions = video.VideoOptions! with { Encoder = encoder } };
            var result = Await(EditorExportService.ExportAsync(workspace, video, ffmpeg, ffprobe), "actual short " + slug + " export", 120);
            Assert(result.CompletedFrames == 3 && new FileInfo(video.Destination).Length > 100, "formal service publishes a verified three-frame " + slug + " video");
            var info = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,width,height,nb_read_frames", "-of", "json", video.Destination }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            Await(process.WaitForExitAsync(), "FFprobe verification", 30);
            string json = Await(stdout, "FFprobe JSON", 30), error = Await(stderr, "FFprobe diagnostics", 30);
            Assert(process.ExitCode == 0, "FFprobe succeeds: " + error);
            File.WriteAllText(Path.Combine(output, "workflow-" + slug + "-ffprobe.json"), json);
            using var document = JsonDocument.Parse(json);
            var stream = document.RootElement.GetProperty("streams")[0];
            Assert(stream.GetProperty("codec_name").GetString() == options.CodecName && stream.GetProperty("nb_read_frames").GetString() == "3"
                && stream.GetProperty("width").GetInt32() == 320 && stream.GetProperty("height").GetInt32() == 240,
                "independent FFprobe confirms " + slug + " codec, three decodable frames and requested dimensions");
            Assert(LiveState(shell) == before, slug + " export preserves the full live document state");
          }
        }
        else Skipped.Add("Optional HEVC integration was not executed because explicit absolute FFmpeg/FFprobe paths were not supplied.");

        string cancelledPath = Path.Combine(output, "workflow-cancelled-sequence");
        using var cancellation = new CancellationTokenSource();
        bool cancelledAfterFrame = false;
        var progress = new InlineProgress<ExportTaskProgress>(value =>
        { if (value.State == ExportTaskState.Running && value.CompletedFrames >= 1) { cancelledAfterFrame = true; cancellation.Cancel(); } });
        try
        {
            Await(EditorExportService.ExportAsync(workspace, Draft(OutputMediaKind.PngSequence, cancelledPath, 0, 12, true), null, null, progress, cancellation.Token),
                "cancel export after a real completed frame", 120);
            throw new InvalidOperationException("The cancellation probe unexpectedly completed export.");
        }
        catch (OperationCanceledException) { }
        Assert(cancelledAfterFrame, "export cancellation occurs after at least one actual GPU-captured frame");
        Assert(!Directory.Exists(cancelledPath) && !Directory.GetFileSystemEntries(output, ".workflow-cancelled-sequence.*.partial").Any(),
            "cancelled export publishes no destination and removes its partial sequence");
        Assert(ExportTemporaries().SetEquals(temporaryBefore), "successful and cancelled exports remove their private temporary PMA snapshot directories");
        Assert(LiveState(shell) == before, "cancelled export preserves the full live document state");
        string editedSha = workspace.Motions.Single().Asset!.Sha256;
        var editedHistory = History(workspace.Session!);
        shell.UndoCommand.Execute(null); PumpLayout();
        Assert(workspace.Motions.Single().Asset!.Sha256 == beforeEditSha,
            "the original pre-export edit remains the immediate Undo target after every export");
        shell.RedoCommand.Execute(null); PumpLayout();
        Assert(workspace.Motions.Single().Asset!.Sha256 == editedSha && History(workspace.Session!) == editedHistory,
            "Undo/Redo after export restores the original authored edit and exact history counts");
    }

    private static OutputSettingsDraft Draft(OutputMediaKind kind, string destination, uint start, uint end, bool transparent)
    {
        var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1);
        var export = new ViewExportPreset(320, 240, new(30), quality);
        var preset = new ViewOutputPresetDefinition("probe.workflow-output", "Workflow verification", new(320, 240, quality), export);
        var video = kind == OutputMediaKind.Video ? new FfmpegOutputOptions(destination) { Speed = VideoSpeed.Fast, Threads = 2,
            BackgroundRed = 255, BackgroundGreen = 255, BackgroundBlue = 255, Timeout = TimeSpan.FromSeconds(60) } : null;
        return new(preset, export.CreateRequest(start, end), kind, destination, transparent, transparent ? Vector4.Zero : Vector4.One, video);
    }

    private static void VerifyNativePlayback(EditorShellViewModel shell, EditorViewportHost viewport, Func<Exception?> getError)
    {
        var session = shell.Workspace.Session!;
        int frameBefore = shell.CurrentFrame;
        var selectedBefore = shell.Animation.SelectedKeyframes.ToArray();
        var cameraBefore = viewport.CurrentCamera;
        string liveBefore = LiveState(shell);
        string draftsBefore = JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json);
        Assert(!shell.IsPlaying && !shell.Animation.HasAnyDraft && !shell.SceneInspector.HasCameraDraft
            && !shell.SceneInspector.HasLightDraft && session.DurationFrames >= (long)frameBefore + 3,
            "native playback verification starts paused with no pending pose drafts and at least three authored frames remaining");
        string AuthoredState() => JsonSerializer.Serialize(new
        {
            shell.Workspace.IsDirty, shell.Workspace.ProjectPath,
            workspaceHistory = WorkspaceHistory(shell.Workspace), sessionHistory = History(session),
            motions = shell.Workspace.Motions.Select(actor => new { actor.ObjectId, actor.Asset?.Sha256, actor.Motion }),
            sceneMotion = session.CaptureAuthoredContent().SceneMotion?.Motion,
            selected = shell.Animation.SelectedKeyframes, model = shell.Browser.SelectedModel?.ObjectId,
            shell.Browser.HierarchyTab, shell.Browser.SelectedBoneIndex, shell.Browser.SelectedMorphIndex,
            drafts = shell.Animation.SnapshotDrafts(), shell.SceneInspector.HasCameraDraft, shell.SceneInspector.HasLightDraft
        }, Json);
        string authoredBefore = AuthoredState();
        int presentedBefore = presented;
        long nativeCountBefore = viewport.Statistics?.PresentCount ?? 0;
        double lastViewportFrame = double.NaN;
        var originalProvider = viewport.SnapshotProvider
            ?? throw new InvalidOperationException("Formal playback requires the connected native viewport snapshot provider.");
        viewport.SnapshotProvider = requests =>
        {
            var snapshot = originalProvider(requests);
            lastViewportFrame = snapshot.SourceFrame;
            return snapshot;
        };
        var elapsed = Stopwatch.StartNew();
        try
        {
            var play = window!.FindControl<Button>("PlayAnimationButton")!;
            Assert(ReferenceEquals(play.Command, shell.PlayCommand) && shell.PlayCommand.CanExecute(null),
                "actual native playback button is bound to the formal shell playback command");
            Click(play);
            Assert(shell.IsPlaying, "actual native play button starts the shell DispatcherTimer on the live project");
            try
            {
                PumpUntil(() => getError() is not null || (shell.CurrentFrame >= frameBefore + 3
                    && lastViewportFrame >= frameBefore + 3 && presented > presentedBefore
                    && viewport.Statistics?.PresentCount > nativeCountBefore), "native timer playback advances and presents three frames", 5);
                if (getError() is { } error) throw new InvalidOperationException("Native timer playback viewport failed.", error);
                Assert(shell.CurrentFrame >= frameBefore + 3 && session.CurrentFrame >= frameBefore + 3
                    && lastViewportFrame >= frameBefore + 3 && presented > presentedBefore
                    && viewport.Statistics?.PresentCount > nativeCountBefore,
                    "real shell timer advances at least three frames and the connected native HWND viewport captures and presents advancing project content");
            }
            finally
            {
                if (shell.IsPlaying) shell.PlayCommand.Execute(null);
                shell.StopPlayback();
            }
            elapsed.Stop();
            Assert(!shell.IsPlaying && shell.CurrentFrame == (int)session.CurrentFrame
                && shell.Animation.CurrentFrame == shell.CurrentFrame && shell.SceneInspector.CurrentFrame == shell.CurrentFrame
                && ReferenceEquals(viewport.Project, session)
                && session.Actors.All(actor => actor.LocalFrame == Math.Min(session.CurrentFrame, actor.DurationFrames)),
                "stopping formal playback leaves session, animation inspector, scene inspector and viewport on the single live clock");

            var requests = shell.Workspace.Motions.Select(actor => new SceneObjectRenderRequest(actor.ObjectId,
                Enumerable.Range(0, actor.Model.Bones.Count))).ToArray();
            var sampled = session.CaptureRenderSnapshot(requests);
            bool finiteVertices = true;
            foreach (var vertex in sampled.Scene.Surface.Snapshot.Vertices)
                finiteVertices &= Finite(vertex.Position) && Finite(vertex.Normal) && float.IsFinite(vertex.Uv.X) && float.IsFinite(vertex.Uv.Y);
            var matrices = sampled.SceneInputs.Values.SelectMany(inputs => inputs.BoneWorldMatrices.Values).ToArray();
            Assert(sampled.SourceFrame == session.CurrentFrame && sampled.Scene.Surface.Snapshot.Vertices.Length > 0
                && finiteVertices && matrices.Length > 0 && matrices.All(FiniteMatrix),
                "actual playback publishes finite evaluated vertices, normals, UVs and solved bone matrices without relying on fixed physics pixels");
            Assert(AuthoredState() == authoredBefore,
                "timer playback changes no authored motion, scene keys, dirty state, document path, selection, pose drafts or either Undo/Redo history");
            WaitForFrame(viewport, getError, "stopped playback native presentation");
            var playbackCamera = viewport.CurrentCamera;
            CaptureGpuReadback(shell, viewport, "workflow-playback-d3d12-readback.png");
            Assert(viewport.CurrentCamera == playbackCamera && AuthoredState() == authoredBefore,
                "independent playback GPU readback uses the stopped native viewport camera and preserves authored project state");
            File.WriteAllText(Path.Combine(output, "playback-evidence.json"), JsonSerializer.Serialize(new
            {
                initialFrame = frameBefore, playedShellFrame = shell.CurrentFrame, playedSessionFrame = session.CurrentFrame,
                lastCapturedViewportFrame = lastViewportFrame, nativeFramesBefore = presentedBefore, nativeFramesAfter = presented,
                nativePresentCountBefore = nativeCountBefore, nativePresentCountAfter = viewport.Statistics?.PresentCount,
                timerPlaybackMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                actors = session.Actors.Select(actor => new { actor.ObjectId, actor.LocalFrame, actor.PhysicsEnabled, actor.PhysicsBackend }),
                solvedBoneMatrixCount = matrices.Length, playbackCamera,
                capture = "independent authoritative D3D12 renderer readback at the stopped playback pose and native CurrentCamera; not an HWND swap-chain or full-window capture"
            }, Json));
        }
        finally
        {
            shell.StopPlayback();
            viewport.SnapshotProvider = originalProvider;
            shell.CurrentFrame = frameBefore;
            shell.Animation.SelectedKeyframes = selectedBefore;
            PumpLayout();
        }
        WaitForFrame(viewport, getError, "restore pre-playback project frame and native camera");
        Assert(LiveState(shell) == liveBefore && AuthoredState() == authoredBefore
            && JsonSerializer.Serialize(shell.Animation.SnapshotDrafts(), Json) == draftsBefore
            && viewport.CurrentCamera == cameraBefore,
            "native playback verification restores the original frame, selections, draft state and camera for the remaining workflow");

        static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        static bool FiniteMatrix(Matrix4x4 value) => new[]
        {
            value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34, value.M41, value.M42, value.M43, value.M44
        }.All(float.IsFinite);
    }

    private static void CaptureGpuReadback(EditorShellViewModel shell, EditorViewportHost viewport, string name)
    {
        var session = shell.Workspace.Session!;
        var request = new ViewRenderRequest(640, 480, new(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1));
        var renderer = new D3D12ViewRenderer(request);
        try
        {
            Assert(allowWarp || !renderer.IsWarp, "independent project readback uses actual hardware D3D12");
            var snapshot = session.CaptureRenderSnapshot(renderer.SceneInputRequests);
            uint frame = (uint)session.CurrentFrame;
            var sample = new OfflineFramePlan(request.Width, request.Height, new(30), frame, frame).GetSample(0);
            var readback = new OfflineFrame(sample, request.Width, request.Height, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
            { ResolvedCamera = viewport.CurrentCamera, MorphChannels = snapshot.MorphChannels, RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs };
            using var pixels = Await(renderer.CaptureAsync(readback).AsTask(), "separate authoritative D3D12 project readback", 30);
            var sink = new PngFrameSink(Path.Combine(output, name));
            try
            {
                Await(sink.BeginAsync(new(new(request.Width, request.Height, new(30), frame, frame))).AsTask(), "readback PNG begin");
                Await(sink.WriteAsync(sample, pixels).AsTask(), "readback PNG write");
                Await(sink.CompleteAsync().AsTask(), "readback PNG complete");
            }
            finally { Await(sink.DisposeAsync().AsTask(), "readback PNG disposal"); }
            var rgba = pixels.Pixels.ToArray();
            uint first = BitConverter.ToUInt32(rgba, 0);
            Assert(Enumerable.Range(1, pixels.Width * pixels.Height - 1).Any(index => BitConverter.ToUInt32(rgba, index * 4) != first),
                "separate GPU readback contains rendered project content");
            File.WriteAllText(Path.Combine(output, "viewport-evidence.json"), JsonSerializer.Serialize(new
            { renderer.AdapterName, renderer.IsWarp, viewportAdapter = viewport.AdapterName, nativeFramesPresented = presented,
                capture = "independent authoritative D3D12 renderer readback of reopened project; not a capture of the HWND swap chain",
                uiScreenshots = "Avalonia RenderTargetBitmap of native control tree; child HWND viewport intentionally omitted" }, Json));
        }
        finally { Await(renderer.DisposeAsync().AsTask(), "readback renderer disposal", 30); }
    }

    private static void VerifyPng(string path, bool transparent)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException("PNG decode failed: " + path);
        Assert(bitmap.Width == 320 && bitmap.Height == 240, "actual PNG has requested 320x240 dimensions: " + Path.GetFileName(path));
        var colors = bitmap.Pixels;
        Assert(colors.Any(value => value.Alpha > 0), "actual PNG contains rendered model coverage: " + Path.GetFileName(path));
        if (transparent)
        {
            Assert(colors.Any(value => value.Alpha == 0), "actual transparent PNG retains uncovered alpha: " + Path.GetFileName(path));
            Assert(colors.Where(value => value.Alpha == 0).All(value => value.Red == 0 && value.Green == 0 && value.Blue == 0),
                "fully transparent PNG pixels have zero external RGB: " + Path.GetFileName(path));
        }
    }

    private static void WaitForFrame(EditorViewportHost viewport, Func<Exception?> getError, string label)
    {
        int before = presented; viewport.RequestRender();
        PumpUntil(() => presented > before || getError() is not null, label, 30);
        if (getError() is { } error) throw new InvalidOperationException(label, error);
        Assert(presented > before && viewport.Statistics is not null, label + " completes an actual FramePresented event");
    }

    private static void TypeNumber(NumericUpDown control, string value)
    {
        var textBox = control.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "PART_TextBox");
        textBox.Focus(); textBox.Text = value;
        textBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        PumpLayout();
    }
    private static void TypeNumberThenFocus(NumericUpDown control, string value, Button next)
    {
        // Enter invokes the dialog's real default Apply action. Change focus instead so
        // the numeric edit commits while the modal remains open for the separate capture/Apply.
        var textBox = control.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "PART_TextBox");
        textBox.Focus(); textBox.Text = value; next.Focus(); PumpLayout();
        Assert(!textBox.IsFocused && control.Value == decimal.Parse(value, CultureInfo.InvariantCulture),
            "numeric focus loss commits the intended dialog draft without invoking default Apply: " + control.Name);
    }
    private static void Click(Button button)
    {
        Assert(button.IsEnabled && button.Command?.CanExecute(button.CommandParameter) == true, "native action button is enabled: " + button.Name);
        button.Focus();
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
        PumpLayout();
    }
    private static VmdBoneKeyframe GetBoneKey(EditorShellViewModel shell, Guid id, string name, int frame) => shell.Workspace.Motions.Single(value => value.ObjectId == id).Motion.BoneKeyframes.Single(value => value.BoneName == name && value.Frame == frame);
    private static string SceneSections(VmdMotion motion) => JsonSerializer.Serialize(new { motion.CameraKeyframes, motion.LightKeyframes, motion.SelfShadowKeyframes, motion.IkKeyframes }, Json);
    private static HistoryCounts History(SceneProjectSession session)
    {
        int Count(string name) => ((ICollection)typeof(SceneProjectSession).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!).Count;
        return new(Count("undo"), Count("redo"));
    }
    private static string LiveState(EditorShellViewModel shell) => JsonSerializer.Serialize(new { shell.CurrentFrame,
        sessionFrame = shell.Workspace.Session!.CurrentFrame, shell.Workspace.IsDirty, shell.Workspace.ProjectPath,
        history = History(shell.Workspace.Session), selected = shell.Animation.SelectedKeyframes,
        bone = shell.Browser.SelectedBoneIndex, morph = shell.Browser.SelectedMorphIndex,
        motions = shell.Workspace.Motions.Select(value => new { value.ObjectId, value.Asset?.Sha256 }) });
    private static HashSet<string> ExportTemporaries() => Directory.GetDirectories(Path.GetTempPath(), "MDA-export-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static bool PackageContainsHash(string path, string sha)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries.Where(value => value.Length > 0 && value.Length <= SceneMotionAsset.MaxBytes))
        { using var stream = entry.Open(); if (Convert.ToHexStringLower(SHA256.HashData(stream)) == sha) return true; }
        return false;
    }
    private static string RequireInput(string path)
    { if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new ArgumentException("Input must name an existing absolute file: " + path); return Path.GetFullPath(path); }
    private static void Remember(string path)
    { if (!OriginalHashes.ContainsKey(path)) { using var input = File.OpenRead(path); OriginalHashes.Add(path, Convert.ToHexStringLower(SHA256.HashData(input))); } }
    private static void VerifyOriginals()
    { foreach (var pair in OriginalHashes) { using var input = File.OpenRead(pair.Key); Assert(Convert.ToHexStringLower(SHA256.HashData(input)) == pair.Value, "original user resource remains byte-identical: " + Path.GetFileName(pair.Key)); } }
    private static string CopyModel(string source, string destination)
    {
        Remember(source);
        PmxModel model; using (var input = File.OpenRead(source)) model = PmxReader.Read(input);
        string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(source)!) + Path.DirectorySeparatorChar;
        string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        foreach (var relative in model.Textures.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized) || normalized.Contains(':')) throw new InvalidDataException("Texture must remain inside the model directory.");
            string from = Path.GetFullPath(Path.Combine(sourceRoot, normalized)), to = Path.GetFullPath(Path.Combine(targetRoot, normalized));
            if (!from.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) || !to.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Texture path leaves copied input directory.");
            if (!File.Exists(from)) continue;
            Remember(from); Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to, overwrite: true);
        }
        string result = Path.Combine(destination, "copied-model.pmx"); File.Copy(source, result, overwrite: true); return result;
    }
    private static void DeleteOwnedInput(string directory)
    {
        string root = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(output), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("copied-input-", StringComparison.Ordinal)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe copied-input deletion target.");
        if (Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Unexpected reparse point inside task-owned copied input.");
        Directory.Delete(root, recursive: true);
    }
    private static string WriteFixture(string directory)
    {
        // Geometry/header follow EditedMotionProjectTests.Model; the native probe adds a real
        // display group, editable bone flags and a visible two-sided triangle, serialized by PmxWriter.
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "Workflow fixture", EnglishName = "Generated test fixture", Comment = "Generated native integration fixture", EnglishComment = "Not user content",
            Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "fixture teal", EnglishName = "", Diffuse = new(.2f, .7f, .65f, 1), Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = new(.15f), DrawMode = PmxDrawModeFlags.BothFaces, EdgeColor = Vector4.Zero, EdgeSize = 0,
                TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "Root", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0,
                Flags = PmxBoneFlags.AllowTranslate | PmxBoneFlags.AllowRotate | PmxBoneFlags.Visible | PmxBoneFlags.AllowControl }],
            Morphs = [new() { Name = "fixture expression", EnglishName = "Fixture expression", ControlPanel = 0, Type = PmxMorphType.Position,
                PositionOffsets = [new(2, new(.25f, 0, 0))] }],
            DisplayFrames = [new() { Name = "Fixture controls", EnglishName = "Fixture controls", Type = PmxDisplayFrameType.Default,
                Targets = [new(PmxDisplayTargetType.Bone, 0), new(PmxDisplayTargetType.Morph, 0)] }], Joints = [], RigidBodies = [], SoftBodies = [],
            Vertices = new[] { new Vector3(-4, 6, 0), new Vector3(4, 6, 0), new Vector3(0, 14, 0) }.Select(position => new PmxVertex
            { Position = position, Normal = -Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
        };
        string result = Path.Combine(directory, "generated-workflow-fixture.pmx");
        using (var stream = File.Create(result)) PmxWriter.Write(stream, model);
        File.WriteAllText(Path.Combine(output, "fixture-provenance.txt"), "No PMX was supplied. This run uses a generated valid PMX serialized by PmxWriter, based on tests/Mmd.Modern.Application.Tests/EditedMotionProjectTests.cs. It verifies native integration mechanics; it is not user-model visual acceptance.");
        return result;
    }
    private static void CaptureControls(string name)
    {
        PumpLayout(); var scale = window!.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, name));
    }
    private static void PumpLayout() { Dispatcher.UIThread.RunJobs(); window?.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void PumpUntil(Func<bool> predicate, string operation, int timeoutSeconds)
    {
        if (predicate()) { PumpLayout(); return; }
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        Exception? failure = null;
        timer.Tick += (_, _) =>
        {
            try { if (predicate()) frame.Continue = false; else if (clock.Elapsed.TotalSeconds >= timeoutSeconds) { failure = new TimeoutException(operation + " exceeded " + timeoutSeconds + " seconds."); frame.Continue = false; } }
            catch (Exception error) { failure = error; frame.Continue = false; }
        };
        timer.Start();
        try { Dispatcher.UIThread.PushFrame(frame); }
        finally { timer.Stop(); }
        PumpLayout();
        if (failure is not null) throw failure;
    }
    private static void Await(Task task, string operation, int timeoutSeconds = 30) { PumpUntil(() => task.IsCompleted, operation, timeoutSeconds); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task, string operation, int timeoutSeconds = 30) { Await((Task)task, operation, timeoutSeconds); return task.GetAwaiter().GetResult(); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); Checks.Add(message); }
    private static void ConfigureProductExpectations(IReadOnlyDictionary<string, string> options)
    {
        loadedAssemblyPath = Path.GetFullPath(typeof(App).Assembly.Location);
        loadedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loadedAssemblyPath))).ToLowerInvariant();
        bool hasPath = options.TryGetValue("expected-app", out expectedAssemblyPath);
        bool hasHash = options.TryGetValue("expected-app-sha256", out expectedAssemblySha256);
        bool hasManifest = options.TryGetValue("expected-product-manifest", out var manifestPath);
        if (hasPath != hasHash || hasPath != hasManifest) throw new ArgumentException("Published verification requires the expected app path, SHA256 and product manifest together.");
        if (!hasPath) return;
        expectedAssemblyPath = RequireInput(expectedAssemblyPath!);
        expectedProductDirectory = Path.GetDirectoryName(expectedAssemblyPath)!;
        if (expectedAssemblySha256!.Length != 64 || !expectedAssemblySha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected app SHA256 must contain exactly 64 hexadecimal digits.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(RequireInput(manifestPath!)));
        foreach (var file in manifest.RootElement.EnumerateArray())
        {
            string relative = file.GetProperty("path").GetString()!;
            string expected = Path.GetFullPath(Path.Combine(expectedProductDirectory, relative));
            if (!expected.StartsWith(expectedProductDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Product manifest contains a path outside the published copy.");
            ExpectedProductHashes.Add(expected, file.GetProperty("sha256").GetString()!);
        }
    }
    private static void VerifyLoadedProducts()
    {
        LoadedProductAssemblies.Clear();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name?.StartsWith("Mmd.Modern.", StringComparison.Ordinal) == true).OrderBy(assembly => assembly.FullName))
        {
            string path = Path.GetFullPath(assembly.Location);
            string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            string? expectedPath = expectedProductDirectory is null ? null : Path.Combine(expectedProductDirectory, Path.GetFileName(path));
            string? expectedHash = null;
            if (expectedPath is not null) ExpectedProductHashes.TryGetValue(expectedPath, out expectedHash);
            LoadedProductAssemblies.Add(new { name = assembly.GetName().Name, loadedAssemblyPath = path, loadedAssemblySha256 = sha256,
                expectedAssemblyPath = expectedPath, expectedAssemblySha256 = expectedHash });
            if (expectedPath is not null)
                Assert(path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) && expectedHash is not null
                    && sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase), "published product assembly loads from the isolated payload with the original SHA256: " + assembly.GetName().Name);
        }
        if (expectedAssemblyPath is not null)
            Assert(loadedAssemblyPath.Equals(expectedAssemblyPath, StringComparison.OrdinalIgnoreCase)
                && loadedAssemblySha256.Equals(expectedAssemblySha256, StringComparison.OrdinalIgnoreCase),
                "formal editor assembly path and SHA256 match the supplied published payload");
    }
    private static void WriteReport(string? failure) => File.WriteAllText(Path.Combine(output, "workflow-checks.json"), JsonSerializer.Serialize(new
    { success = failure is null, passed = Checks.Count, checks = Checks, skipped = Skipped, inputKind, adapter, nativeFramesPresented = presented,
        loadedAssemblyPath, loadedAssemblySha256, expectedAssemblyPath, expectedAssemblySha256, loadedProductAssemblies = LoadedProductAssemblies,
        suppliedPmd, publishedReferenceVerification = expectedAssemblyPath is not null,
        hardwareRequired = !allowWarp, screenshotScope = "Native Avalonia control render; child HWND omitted. Project D3D12 readback is a separate artifact.", failure }, Json));
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private readonly record struct HistoryCounts(int Undo, int Redo);
}
