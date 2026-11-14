using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.Images;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vector2 = System.Numerics.Vector2;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>Reference-only native editor diagnostic. It reads the supplied PMA, changes only
/// its in-memory document, and invokes input handlers only on this probe's own child HWND.
/// Control screenshots omit that native child; they are not full-window visual acceptance.</summary>
internal static class EditorBoneEditingProbe
{
    private static readonly List<string> Checks = [];
    private static readonly List<string> ExplicitSkip = [];
    private static readonly List<object> Gestures = [];
    private static readonly List<object> PhysicsVisibility = [];
    private static readonly List<object> ContinuousNativeDrags = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    private static string output = "", pma = "", originalHash = "";
    private static EditorWindow? window;
    private static EditorShellViewModel? shell;
    private static EditorViewportHost? viewport;
    private static Exception? viewportError;
    private static int presented;
    private static nint childWindow;
    private static int width, height, filterShownBoneCount, declaredTailEligibleBoneCount;
    private static HashSet<PmxBone> physicsDriven = [];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: probe <output-directory> <read-only-existing.pma>");
        output = Path.GetFullPath(args[0]); pma = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        try
        {
            originalHash = Hash(pma);
            AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().SetupWithoutStarting();
            shell = new EditorShellViewModel(); shell.Workspace.Open(pma);
            shell.RefreshWorkspace(shell.Workspace.Session!.Objects.First().Id);
            window = new EditorWindow(false) { DataContext = shell, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
            viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
            viewport.Error += error => viewportError = error;
            viewport.FramePresented += _ => presented++;
            window.Show(); Pump(); WaitForFrame("real PMA opens in the native hardware viewport");
            Assert(viewport.HasChildWindow && !viewport.IsWarp, "probe owns a real hardware D3D12 child HWND");
            childWindow = (nint)typeof(EditorViewportHost).GetField("childWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewport)!;
            Assert(GetClientRect(childWindow, out var rect) && rect.Right > 0 && rect.Bottom > 0, "owned child HWND has a measurable client area");
            width = rect.Right; height = rect.Bottom;
            Assert(!shell.Workspace.IsDirty && shell.Browser.Models.Count > 0, "read-only existing PMA opens clean with a real selected model");
            shell.CurrentFrame = 9; Pump(); WaitForFrame("real model initializes target frame nine");
            VerifyOriginalTypeRules();
            VerifyEditing();
            VerifyContinuousNativeDragging();
            VerifyControls();
            VerifyPhysicsVisibilitySwitch();
            Assert(Hash(pma) == originalHash, "bone picking, in-memory key registration and captures preserve original PMA SHA256");
            ExplicitSkip.Add("This focused probe does not replace codec/export, installed lifecycle, full-window HWND capture, broad animation playback or user visual acceptance.");
            WriteReport(null); Console.WriteLine("PASS " + Checks.Count); return 0;
        }
        catch (Exception error)
        {
            try { Capture("failure-controls-hwnd-omitted.png"); } catch { }
            WriteReport(error.ToString()); Console.Error.WriteLine(error); return 1;
        }
        finally
        {
            // The probe owns this window and in-memory document. Avoid opening a Save dialog.
            if (window is not null)
            {
                typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(window, true);
                window.Close();
            }
        }
    }

    private static void VerifyOriginalTypeRules()
    {
        PmxBone Bone(int index, PmxBoneFlags extra = PmxBoneFlags.None, int target = -1, params int[] links) => new()
        {
            Name = "fixture-" + index, EnglishName = "fixture-" + index, Position = default,
            ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.Visible | PmxBoneFlags.FixedAxis | extra,
            PositionOffset = System.Numerics.Vector3.UnitY, IkTargetBoneIndex = target,
            IkLinks = links.Select(link => new PmxIkLink(link, false, default, default)).ToArray()
        };
        PmxBone[] bones = [Bone(0, PmxBoneFlags.AllowTranslate), Bone(1), Bone(2), Bone(3), Bone(4, PmxBoneFlags.Ik, 2, 3)];
        var markerOnly = (HashSet<int>)typeof(EditorBoneViewportController)
            .GetMethod("FindFixedAxisMarkerOnlyBones", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [bones])!;
        Assert(markerOnly.SetEquals([0, 1]),
            "original PMX fixed-axis rod classification keeps plain/translation fixed-axis bones marker-only, while IK controller, target and links receive their later rod-capable types");
    }

    private static void VerifyEditing()
    {
        var s = shell!; var v = viewport!;
        var c = v.BoneController ?? throw new InvalidOperationException("Formal bone controller missing.");
        var authored = s.Browser.SelectedModel!.PmxModel;
        physicsDriven = authored.RigidBodies.Where(body => body.Operation is PmxRigidBodyOperation.Dynamic or PmxRigidBodyOperation.DynamicAndBoneMerge)
            .Select(body => body.BoneIndex).Where(index => index >= 0 && index < authored.Bones.Count).Select(index => authored.Bones[index]).ToHashSet();
        Assert(c.ShowBones && window!.FindControl<ToggleButton>("BoneGuidesToggle")!.IsChecked == true,
            "bone guides and their plainly labelled toolbar toggle are enabled by default");
        Assert(!c.IsManipulationEnabled && window!.FindControl<ToggleButton>("BoneSelectMode")!.IsChecked == true,
            "the formal editor defaults to classic bone Select mode with manipulation disabled");
        Assert(v.SnapshotProvider is not null && v.FrameDecorator is not null, "formal host connects evaluated bone snapshot requests and final editor decoration");
        s.Browser.SelectedBoneIndex = null;
        var frame = CaptureFrame();
        var drawn = c.DecorateFrame(frame);
        var capturedField = typeof(EditorBoneViewportController).GetField("captured", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previouslyPublished = capturedField.GetValue(c);
        var awaitingPresentation = c.DecorateFrame(frame, publishPicking: false);
        Assert(ReferenceEquals(previouslyPublished, capturedField.GetValue(c)),
            "queued but unpresented bone decoration cannot replace hit geometry for the displayed frame");
        c.FramePresented(awaitingPresentation);
        Assert(!ReferenceEquals(previouslyPublished, capturedField.GetValue(c)),
            "successful presentation publishes hit geometry belonging to that exact decorated frame");
        Assert((drawn.Scene.EditorOverlay?.Lines.Count ?? 0) > (frame.Scene.EditorOverlay?.Lines.Count ?? 0),
            "default bone guides add visible lines for the actual evaluated user model");
        var model = s.Browser.SelectedModel!;
        filterShownBoneCount = model.PmxModel.Bones.Count(Shown);
        Assert(frame.SceneInputs[model.ObjectId].BoneWorldMatrices.Count == model.PmxModel.Bones.Count,
            "formal source requests capture all actual model bone worlds");
        var segments = Segments(c);
        Assert(segments.Count > 0 && segments.All(item => Shown(model.PmxModel.Bones[item.Index])),
            "unselected drawn skeleton segments follow authored PMX Visible and current physics state independently of editing permission");
        bool DeclaredTail(int index) => model.PmxModel.Bones[index].Flags.HasFlag(PmxBoneFlags.TargetShowMode)
            ? model.PmxModel.Bones[index].LinkBoneIndex > 0 && frame.SceneInputs[model.ObjectId].BoneWorldMatrices.ContainsKey(model.PmxModel.Bones[index].LinkBoneIndex)
            : model.PmxModel.Bones[index].PositionOffset != System.Numerics.Vector3.Zero;
        declaredTailEligibleBoneCount = Enumerable.Range(0, model.PmxModel.Bones.Count)
            .Count(index => Shown(model.PmxModel.Bones[index]) && DeclaredTail(index));
        Assert(segments.All(segment => DeclaredTail(segment.Index)),
            "every actual captured rod has a declared PMX linked display tail or nonzero offset; zero-offset bones never invent a parent fallback");
        var fixedAxisOnly = (HashSet<int>)typeof(EditorBoneViewportController)
            .GetField("fixedAxisMarkerOnlyBones", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(c)!;
        Assert(segments.All(segment => !fixedAxisOnly.Contains(segment.Index)),
            "actual emitted rods exclude original fixed-axis marker-only types without suppressing their joint markers");
        var centers = Centers().ToArray();
        var visibleFixedAxis = fixedAxisOnly.Where(index => Shown(model.PmxModel.Bones[index])).ToArray();
        Assert(visibleFixedAxis.Length > 0 && visibleFixedAxis.All(index => centers.Any(center => center.Index == index)),
            "actual visible nonphysical fixed-axis bones retain their selectable joint markers");
        Assert(centers.All(center => !physicsDriven.Contains(model.PmxModel.Bones[center.Index]))
            && segments.All(segment => !physicsDriven.Contains(model.PmxModel.Bones[segment.Index])),
            "normal bone guides exclude PMX mode-one/mode-two driven nodes and rods, using rigid-body BoneIndex rather than names or display group guesses");
        File.WriteAllText(Path.Combine(output, "authored-bones.json"), JsonSerializer.Serialize(new
        {
            model.PmxModel.Name, boneCount = authored.Bones.Count, manualGuideCount = filterShownBoneCount,
            rigidBodyCount = authored.RigidBodies.Count, dynamicBoundBoneCount = physicsDriven.Count,
            bones = authored.Bones.Select((bone, index) => new { index, bone.Name, bone.ParentIndex, bone.Position,
                bone.LinkBoneIndex, bone.PositionOffset, flags = bone.Flags.ToString(), physical = physicsDriven.Contains(bone), guide = Shown(bone) }),
            displayFrames = authored.DisplayFrames.Select(group => new { group.Name, group.Targets }),
            rigidBodies = authored.RigidBodies.Select(body => new { body.Name, body.BoneIndex, operation = body.Operation.ToString() })
        }, Json));
        var markers = (drawn.Scene.EditorOverlay?.Lines ?? []).Skip(frame.Scene.EditorOverlay?.Lines.Count ?? 0)
            .Where(line => line.Shape is ViewportOverlayShape.Circle or ViewportOverlayShape.Square).ToArray();
        var normalBlue = new System.Numerics.Vector4(.12f, .30f, .95f, 1);
        Assert(markers.Length == centers.Length && markers.Length > 0 && markers.All(marker => marker.Color == normalBlue)
            && centers.All(center => markers.Any(marker => marker.Start == new Vector2(center.Point.X / width, center.Point.Y / height)
                && marker.Shape == (model.PmxModel.Bones[center.Index].Flags.HasFlag(PmxBoneFlags.AllowTranslate)
                    ? ViewportOverlayShape.Square : ViewportOverlayShape.Circle))),
            "unselected actual node primitives are normal blue, with squares for translation controls and circles for rotation-only controls");
        var candidate = PickSegment(segments, frame, item =>
            model.PmxModel.Bones[item.Index].Flags.HasFlag(PmxBoneFlags.AllowRotate)
            && !model.PmxModel.Bones[item.Index].Flags.HasFlag(PmxBoneFlags.AllowTranslate));
        Assert(candidate is not null, "real model offers an isolated visible rotation-only bone rod away from bone centers");
        var selected = candidate!.Value;
        var selectCenter = PickCenter(frame, index => Shown(model.PmxModel.Bones[index]))
            ?? throw new InvalidOperationException("No independently pickable real bone center for default Select mode.");
        VerifySelectOnly(selectCenter, "center");
        s.Browser.SelectedBoneIndex = null; c.DecorateFrame(frame);
        VerifyNativeSelectOnly(selectCenter);
        s.Browser.SelectedBoneIndex = null; c.DecorateFrame(frame);
        VerifySelectOnly(selected, "rod midpoint");
        s.Browser.SelectedBoneIndex = null;
        c.IsManipulationEnabled = true;
        VerifyBrowserSelectedMode(selected, rotating: true, "rotation-only rod");
        int editFrame = Enumerable.Range(9, 120).First(value => !Actor().Motion.BoneKeyframes.Any(key => key.BoneName == model.PmxModel.Bones[selected.Index].Name && key.Frame == (uint)value));
        s.CurrentFrame = editFrame; Pump(); WaitForFrame("unoccupied registration frame initializes");
        s.Browser.SelectedBoneIndex = null; c.RotateHandle = false;
        frame = CaptureFrame(); c.DecorateFrame(frame);
        // Recapture the same real rod after seeking. Do not reuse a stale screen coordinate.
        selected = PickSegment(Segments(c), frame, item => item.Index == selected.Index)
            ?? throw new InvalidOperationException("Chosen rotation-only rod is no longer independently pickable at the registration frame.");
        string motionBefore = MotionHash(); var historyBefore = History();
        Assert(c.TryBeginDrag(selected.X, selected.Y, width, height, v.CurrentCamera) && c.IsDragging
            && s.Browser.SelectedBoneIndex == selected.Index && s.Animation.SelectedBoneIndex == selected.Index
            && c.RotateHandle && s.Animation.CanRotate && !s.Animation.CanTranslate,
            "clicking the actual rod midpoint selects that rotation-only bone and automatically enters rotation mode");
        var originalRotation = s.Animation.DraftRotation;
        Assert(c.MoveDrag(selected.X + 24, selected.Y + 13) && c.EndDrag() && !c.IsDragging
            && s.Animation.HasDraft && s.Animation.DraftRotation != originalRotation,
            "rod drag and release form a changed unregistered rotation draft");
        Assert(MotionHash() == motionBefore && History() == historyBefore, "releasing a bone drag leaves registered motion and Undo untouched");
        Gestures.Add(new { kind = "rotation-only rod", bone = model.PmxModel.Bones[selected.Index].Name, selected.Index, selected.X, selected.Y, frame = editFrame });

        var register = window!.FindControl<Button>("ViewportRegisterKeyframeButton") ?? throw new InvalidOperationException("Visible viewport registration button missing.");
        Assert(register.IsEffectivelyVisible && register.IsEffectivelyEnabled
            && ReferenceEquals(register.Command, s.Animation.RegisterKeyframeCommand),
            "visible viewport registration button binds the existing animation keyframe command");
        int dispatched = 0; bool handled = false;
        EventHandler<EditorViewportKeyEventArgs> observe = (_, args) => { dispatched++; handled = args.Handled; };
        v.EditorKeyPressed += observe;
        try
        {
            NativeKey(0x0d, repeat: true); NativeKey(0x4b, repeat: true);
            Assert(dispatched == 0 && MotionHash() == motionBefore, "repeated native Enter and K do not dispatch registration or mutate motion");
            NativeKey(0x0d);
            PumpUntil(() => !s.Animation.IsBusy && MotionHash() != motionBefore, "native child Enter registration", 15);
            Assert(dispatched == 1 && handled && !s.Animation.HasDraft && History().Undo == historyBefore.Undo + 1
                && Actor().Motion.BoneKeyframes.Any(key => key.BoneName == model.PmxModel.Bones[selected.Index].Name && key.Frame == (uint)editFrame),
                "native child HWND Enter registers the selected rod draft in exactly one Undo transaction");
            string registered = MotionHash();
            s.UndoCommand.Execute(null); Pump();
            Assert(MotionHash() == motionBefore && History().Undo == historyBefore.Undo, "one formal Undo restores exact motion before the registered rod gesture");
            s.RedoCommand.Execute(null); Pump();
            Assert(MotionHash() == registered, "one formal Redo restores exact registered rod motion");
            s.UndoCommand.Execute(null); Pump();
            s.Browser.SelectedBoneIndex = null; frame = CaptureFrame(); c.DecorateFrame(frame);
            var again = PickSegment(Segments(c), frame, item => item.Index == selected.Index)
                ?? throw new InvalidOperationException("Rotation rod missing after Undo.");
            Assert(c.TryBeginDrag(again.X, again.Y, width, height, v.CurrentCamera) && c.IsDragging
                && c.MoveDrag(again.X + 18, again.Y - 11) && c.EndDrag(), "same real rod accepts a second unregistered gesture after Undo");
            NativeKey(0x4b);
            PumpUntil(() => !s.Animation.IsBusy && MotionHash() != motionBefore, "native child K registration", 15);
            Assert(dispatched == 2 && handled && History().Undo == historyBefore.Undo + 1 && !s.Animation.HasDraft,
                "native child HWND K registers a bone draft through the same formal keyframe command");
            s.UndoCommand.Execute(null); Pump();
            Assert(MotionHash() == motionBefore, "Undo after K restores exact original actor motion");
        }
        finally { v.EditorKeyPressed -= observe; }

        s.Browser.SelectedBoneIndex = null; c.RotateHandle = true;
        frame = CaptureFrame(); c.DecorateFrame(frame);
        var movable = PickCenter(frame, index => Shown(model.PmxModel.Bones[index]) && model.PmxModel.Bones[index].Flags.HasFlag(PmxBoneFlags.AllowTranslate)
            && !model.PmxModel.Bones[index].Flags.HasFlag(PmxBoneFlags.AllowRotate));
        bool onlyTranslate = movable is not null;
        if (onlyTranslate)
        {
            var translationOnly = movable!.Value;
            VerifyBrowserSelectedMode(translationOnly, rotating: false, "translation-only center");
            s.Browser.SelectedBoneIndex = null; c.RotateHandle = true;
            frame = CaptureFrame(); c.DecorateFrame(frame);
            movable = PickCenter(frame, index => index == translationOnly.Index);
        }
        if (!onlyTranslate)
        {
            ExplicitSkip.Add("This real PMA model has no independently pickable translation-only control at this camera, so Browser-selected translation-only mode normalization is skipped; a real translation-capable center control is tested instead.");
            c.RotateHandle = false;
            movable = PickCenter(frame, index => Shown(model.PmxModel.Bones[index]) && model.PmxModel.Bones[index].Flags.HasFlag(PmxBoneFlags.AllowTranslate));
        }
        Assert(movable is not null, "real model has a visible independently pickable translation control center");
        var translate = movable!.Value;
        Assert(c.TryBeginDrag(translate.X, translate.Y, width, height, v.CurrentCamera) && c.IsDragging
            && s.Animation.SelectedBoneIndex == translate.Index && !c.RotateHandle,
            onlyTranslate ? "translation-only center automatically switches rotation mode to movement" : "real center bone starts a movement gesture in move mode");
        var translationBefore = s.Animation.DraftTranslation; var rotationBefore = s.Animation.DraftRotation;
        Assert(c.MoveDrag(translate.X + 20, translate.Y + 12) && s.Animation.DraftTranslation != translationBefore,
            "real center control drag changes its local translation draft");
        NativeKey(0x1b);
        Assert(!c.IsDragging && s.Animation.DraftTranslation == translationBefore && s.Animation.DraftRotation == rotationBefore
            && MotionHash() == motionBefore && History().Undo == historyBefore.Undo,
            "native child Escape cancels the active gesture and restores exact draft pose without a registered edit");
        Gestures.Add(new { kind = onlyTranslate ? "translation-only center" : "translation-capable center", bone = model.PmxModel.Bones[translate.Index].Name, translate.Index, translate.X, translate.Y });

        s.Browser.SelectedBoneIndex = null;
        frame = CaptureFrame(); c.DecorateFrame(frame);
        var budgetCenter = PickCenter(frame, index => Shown(model.PmxModel.Bones[index]))
            ?? throw new InvalidOperationException("No independently pickable drawn center for the full overlay-budget regression.");
        s.Browser.SelectedBoneIndex = budgetCenter.Index;
        var fullOverlay = new ViewportOverlaySnapshot(Enumerable.Repeat(
            new ViewportOverlayLine(new(.1f, .1f), new(.2f, .2f), new(.5f, .5f, .5f, 1)), ViewportOverlaySnapshot.MaxLines));
        var originalScene = frame.Scene;
        var fullFrame = frame with { Scene = new(originalScene.Surface, originalScene.Outline, originalScene.OutlineOptions,
            originalScene.Options, originalScene.Materials, originalScene.Lighting, fullOverlay) };
        var capped = c.DecorateFrame(fullFrame);
        Assert(capped.Scene.EditorOverlay!.Lines.Count == ViewportOverlaySnapshot.MaxLines && !Centers().Any() && Segments(c).Count == 0,
            "an already full final-overlay line budget emits and retains no pickable bone center or rod");
        Assert(!c.TryBeginDrag(budgetCenter.X, budgetCenter.Y, width, height, v.CurrentCamera) && !c.IsDragging,
            "a previously isolated selected center cannot be picked when the full overlay budget omitted its marker and handles");
        s.Browser.SelectedBoneIndex = null;
        c.DecorateFrame(frame);

        c.ShowBones = false; s.Browser.SelectedBoneIndex = null;
        frame = CaptureFrame(); var hidden = c.DecorateFrame(frame);
        Assert((hidden.Scene.EditorOverlay?.Lines.Count ?? 0) == (frame.Scene.EditorOverlay?.Lines.Count ?? 0)
            && !c.TryBeginDrag(selected.X, selected.Y, width, height, v.CurrentCamera),
            "hiding bone guides removes their overlay and disables their picking");
        c.ShowBones = true; Pump();
        var sourceOnly = s.Workspace.Session!.CaptureRenderSnapshot([]);
        Assert(sourceOnly.Scene.EditorOverlay is null || sourceOnly.Scene.EditorOverlay.Lines.Count == 0,
            "undecorated session snapshot used by independent output contains no editor bone helpers");
        Assert(MotionHash() == motionBefore, "all focused gesture and registration checks leave original actor motion restored in memory");
        WaitForFrame("restored live viewport presents after focused bone edits");
        CaptureGpuBoneGuides(selected.Index);
    }

    private static void CaptureGpuBoneGuides(int boneIndex)
    {
        var s = shell!; var v = viewport!; var c = v.BoneController!;
        int? beforeSelection = s.Browser.SelectedBoneIndex;
        bool beforeMode = c.RotateHandle;
        s.Browser.SelectedBoneIndex = boneIndex;
        var request = new ViewRenderRequest(960, 720, v.Quality);
        using var gpuSession = new D3D12FrameSession();
        try
        {
            Assert(!gpuSession.IsWarp, "independent bone-guide diagnostic uses actual hardware D3D12");
            var snapshot = s.Workspace.Session!.CaptureRenderSnapshot(c.SnapshotRequests([]));
            var scene = snapshot.Scene;
            var options = (scene.Options ?? new(OriginalAlphaBlendMode.Normal, true, false)) with { BackgroundColor = System.Numerics.Vector4.One };
            var whiteScene = new OriginalFrameScene(scene.Surface, scene.Outline, scene.OutlineOptions, options, scene.Materials, scene.Lighting, scene.EditorOverlay);
            var preview = new PreviewFrame(request.Width, request.Height, snapshot.SourceFrame, whiteScene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
            {
                ResolvedCamera = v.CurrentCamera, MorphChannels = snapshot.MorphChannels,
                RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs
            };
            var decorated = c.DecorateFrame(preview);
            Assert(c.RotateHandle && (decorated.Scene.EditorOverlay?.Lines.Count ?? 0) > (whiteScene.EditorOverlay?.Lines.Count ?? 0),
                "independent noncapture frame decorates the real Browser-selected rotation-only bone with its effective rotation guides");
            // Resolve the same sampled camera/light and explicit style metadata as the realtime
            // view adapter. Export Capture intentionally omits editor overlays and cannot prove them.
            var resolve = typeof(D3D12ViewRenderer).Assembly.GetType("Mmd.Modern.ViewOutput.D3D12.D3D12ViewScene", throwOnError: true)!
                .GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("D3D12ViewScene.Resolve");
            var resolved = (OriginalFrameScene)resolve.Invoke(null, [decorated.Scene, decorated.Camera, decorated.Light,
                decorated.SelfShadow, decorated.MorphChannels, request.EffectiveQuality, decorated.ResolvedCamera])!;
            var styled = D3D12ViewRenderingStyles.Resolve(resolved, D3D12StylePresetCatalog.BuiltIn,
                decorated.RenderingStyles, sceneInputs: decorated.SceneInputs);
            Assert(styled.Scene.Options?.CameraOverride == v.CurrentCamera && styled.Scene.EditorOverlay is { Lines.Count: > 0 },
                "noncapture diagnostic retains the actual viewport camera and decorated GPU overlay through scene/style resolution");
            using var retained = gpuSession.Render(styled.Scene, request.Width, request.Height,
                rayGiOptions: request.EffectiveQuality.EffectiveGi, surfaceStyles: styled.SurfaceStyles);
            Assert(gpuSession.EditorOverlayDrawCalls == 1 && retained.Statistics.ReadbackCount == 0,
                "noncapture D3D12FrameSession.Render draws the actual final editor overlay with zero routine readbacks");
            if (v.Quality.Antialiasing == FrameAntialiasing.Smaa1X)
                Assert(retained.Statistics.AntialiasingPassDrawCalls > 0 && retained.Statistics.SmaaQuality == SmaaQuality.High,
                    "actual editor first-run SMAA High runs GPU antialiasing passes while final bone helpers remain independently composited");
            // Keep the retained lease and session alive until the copy's own fence completes.
            // A timed-out background task must not outlive either native resource owner.
            var diagnostic = ReadRetainedGpuOutput(gpuSession, retained);
            using var pixels = new FrameBufferLease(retained.Width, retained.Height, checked(retained.Width * 4), diagnostic.Rgba);
            uint sourceFrame = (uint)snapshot.SourceFrame;
            var sample = new OfflineFramePlan(request.Width, request.Height, new(30), sourceFrame, sourceFrame).GetSample(0);
            string imagePath = Path.Combine(output, "bone-guides-d3d12-readback.png");
            var sink = new PngFrameSink(imagePath);
            try
            {
                Await(sink.BeginAsync(new(new(request.Width, request.Height, new(30), sourceFrame, sourceFrame))).AsTask(), "bone-guide PNG begin");
                Await(sink.WriteAsync(sample, pixels).AsTask(), "bone-guide PNG write");
                Await(sink.CompleteAsync().AsTask(), "bone-guide PNG complete");
            }
            finally { Await(sink.DisposeAsync().AsTask(), "bone-guide PNG disposal"); }
            var rgba = diagnostic.Rgba; uint first = BitConverter.ToUInt32(rgba, 0);
            Assert(Enumerable.Range(1, pixels.Width * pixels.Height - 1).Any(index => BitConverter.ToUInt32(rgba, index * 4) != first),
                "diagnostic 960x720 copy of the retained noncapture GPU frame contains rendered project content");
            File.WriteAllText(Path.Combine(output, "bone-guides-d3d12-readback.json"), JsonSerializer.Serialize(new
            {
                imagePath, imageSha256 = Hash(imagePath), gpuSession.AdapterName, gpuSession.IsWarp, sourceFrame, boneIndex,
                overlayLineCount = decorated.Scene.EditorOverlay?.Lines.Count, request.Width, request.Height,
                frameEditorOverlayDrawCalls = gpuSession.EditorOverlayDrawCalls,
                frameRenderStatistics = retained.Statistics,
                diagnosticReadbackCount = 1, diagnosticSubmissionCount = 1,
                diagnostic.Fence, diagnostic.BufferBytes, diagnostic.RowPitch, diagnostic.Format,
                captureScope = "Independent noncapture D3D12FrameSession.Render output with final GPU editor overlay, followed by one separately counted diagnostic GPU CopyTextureRegion/readback. Not export Capture, child HWND capture, or a full-window screenshot."
            }, Json));
        }
        finally
        {
            s.Browser.SelectedBoneIndex = beforeSelection; c.RotateHandle = beforeMode; Pump();
        }
    }

    private static GpuDiagnosticReadback ReadRetainedGpuOutput(D3D12FrameSession session, D3D12FrameOutput frame)
    {
        const BindingFlags internalInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var runtime = typeof(D3D12FrameSession).GetProperty("Runtime", internalInstance)!.GetValue(session)
            ?? throw new InvalidOperationException("Independent D3D12 session runtime missing.");
        var runtimeType = runtime.GetType();
        // These COM objects are borrowed. The independent session and retained frame own them.
        var device = (ID3D12Device)runtimeType.GetProperty("Device", internalInstance)!.GetValue(runtime)!;
        var source = (ID3D12Resource)typeof(D3D12FrameOutput).GetProperty("Resource", internalInstance)!.GetValue(frame)!;
        var description = source.Description;
        if (description.Format != Format.R8G8B8A8_UNorm || description.Width != (ulong)frame.Width || description.Height != frame.Height)
            throw new InvalidOperationException("Expected the retained final RGBA8 texture at the requested diagnostic dimensions.");
        var footprint = new PlacedSubresourceFootPrint[1]; var rowCounts = new int[1]; var rowSizes = new ulong[1];
        device.GetCopyableFootprints(description, 0, 1, 0, footprint, rowCounts, rowSizes, out ulong bytes);
        int stride = checked(frame.Width * 4), rowPitch = footprint[0].Footprint.RowPitch, offset = checked((int)footprint[0].Offset);
        if (rowCounts[0] != frame.Height || rowSizes[0] != (ulong)stride || rowPitch < stride)
            throw new InvalidOperationException("Unexpected diagnostic texture copy footprint.");
        using var readback = device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(bytes), ResourceStates.CopyDest);
        object? Invoke(string name, params object[] arguments) => (runtimeType.GetMethod(name, internalInstance)
            ?? throw new MissingMethodException(runtimeType.FullName, name)).Invoke(runtime, arguments);
        try
        {
            Invoke("Begin");
            var commands = (ID3D12GraphicsCommandList)runtimeType.GetProperty("Commands", internalInstance)!.GetValue(runtime)!;
            // D3D12FrameSession.Render ends its retained output in CopySource. Copy on the same
            // runtime queue preserves ordering after the rendered overlay, without another render.
            commands.CopyTextureRegion(new TextureCopyLocation(readback, footprint[0]), 0, 0, 0, new TextureCopyLocation(source, 0));
            ulong completion = (ulong)Invoke("Submit")!;
            Invoke("Wait", completion);
            var rgba = new byte[checked(stride * frame.Height)];
            var mapped = readback.Map<byte>(0, checked((int)bytes));
            try
            {
                for (int y = 0; y < frame.Height; y++)
                    mapped.Slice(checked(offset + y * rowPitch), stride).CopyTo(rgba.AsSpan(y * stride, stride));
            }
            finally { readback.Unmap(0); }
            return new(rgba, completion, bytes, rowPitch, description.Format.ToString());
        }
        finally { Invoke("Abort"); }
    }

    private static void VerifyControls()
    {
        var owner = window ?? throw new InvalidOperationException("Editor window missing.");
        var s = shell!; var v = viewport!; var c = v.BoneController!;
        var select = owner.FindControl<ToggleButton>("BoneSelectMode") ?? throw new InvalidOperationException("Bone selection control missing.");
        var move = owner.FindControl<ToggleButton>("BoneMoveMode") ?? throw new InvalidOperationException("Bone move control missing.");
        var rotate = owner.FindControl<ToggleButton>("BoneRotateMode") ?? throw new InvalidOperationException("Bone rotate control missing.");
        var light = owner.FindControl<ToggleButton>("HandleRotationToggle") ?? owner.FindControl<ToggleButton>("LightRotationToggle") ?? owner.FindControl<ToggleButton>("LightRotateMode")
            ?? throw new InvalidOperationException("Light rotation control missing.");
        var bar = owner.FindControl<Control>("BoneToolsBar") ?? throw new InvalidOperationException("Dedicated bone toolbar missing.");
        bool lightBefore = v.RotateLightHandle;
        ClickToggle(select, true);
        Assert(!c.IsManipulationEnabled && select.IsChecked == true && move.IsChecked == false && rotate.IsChecked == false
            && v.RotateLightHandle == lightBefore, "bone Select mode disables manipulation and operates independently from light mode");
        ClickToggle(rotate, true);
        Assert(c.IsManipulationEnabled && c.RotateHandle && select.IsChecked == false && v.RotateLightHandle == lightBefore,
            "explicit bone Rotate enables manipulation and operates independently from light rotation mode");
        ClickToggle(move, true);
        Assert(c.IsManipulationEnabled && !c.RotateHandle && select.IsChecked == false && v.RotateLightHandle == lightBefore,
            "explicit bone Move enables manipulation and operates independently from light rotation mode");
        SetLightMode(!lightBefore);
        Assert(v.RotateLightHandle != lightBefore && !c.RotateHandle, "light rotation control no longer changes bone mode");
        SetLightMode(lightBefore);
        void SetLightMode(bool rotating)
        {
            var moveLight = owner.FindControl<ToggleButton>("LightMoveMode");
            ClickToggle(rotating || moveLight is null ? light : moveLight, rotating || moveLight is not null);
        }
        foreach (string language in new[] { "zh-CN", "en", "ja" })
            foreach (int theme in new[] { 0, 1 })
            {
                EditorLocalization.SetLanguage(language); s.SelectedThemeIndex = theme;
                owner.Width = 1100; owner.Height = 720; Pump();
                Assert(bar.IsEffectivelyVisible && select.IsEffectivelyVisible && move.IsEffectivelyVisible && rotate.IsEffectivelyVisible
                    && move.Bounds.Width > 0 && rotate.Bounds.Width > 0, language + "/" + theme + " exposes dedicated bone controls at minimum window size");
                VerifyToolbarButtonBounds(owner, bar, language + "/" + theme + " minimum window");
                Capture("bone-tools-" + language + "-" + (theme == 0 ? "black" : "white") + "-minimum-controls-hwnd-omitted.png");
            }
        // Exercise the actual center column used by the splitters. Window minimum width alone
        // leaves a wider viewport and therefore does not cover the 380-DIP panel minimum.
        var columns = bar.GetVisualAncestors().OfType<Grid>().FirstOrDefault(grid => grid.ColumnDefinitions.Count == 5
            && grid.Children.OfType<GridSplitter>().Any(splitter => Grid.GetColumn(splitter) == 1)
            && grid.Children.OfType<GridSplitter>().Any(splitter => Grid.GetColumn(splitter) == 3))
            ?? throw new InvalidOperationException("Formal three-panel splitter grid missing.");
        var savedWidths = columns.ColumnDefinitions.Select(column => column.Width).ToArray();
        try
        {
            columns.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            columns.ColumnDefinitions[1].Width = new GridLength(4);
            columns.ColumnDefinitions[2].Width = new GridLength(380);
            columns.ColumnDefinitions[3].Width = new GridLength(4);
            columns.ColumnDefinitions[4].Width = new GridLength(1, GridUnitType.Star);
            foreach (string language in new[] { "en", "ja" })
                foreach (int theme in new[] { 0, 1 })
                {
                    EditorLocalization.SetLanguage(language); s.SelectedThemeIndex = theme; Pump();
                    Assert(Math.Abs(columns.ColumnDefinitions[2].ActualWidth - 380) <= 1 && bar.Bounds.Width <= 380 && bar.Bounds.Width >= 376,
                        language + "/" + theme + " uses the actual 380-DIP center splitter column");
                    VerifyToolbarButtonBounds(owner, bar, language + "/" + theme + " 380-DIP center");
                    Capture("bone-tools-" + language + "-" + (theme == 0 ? "black" : "white") + "-380-center-controls-hwnd-omitted.png");
                }
        }
        finally
        {
            for (int index = 0; index < savedWidths.Length; index++) columns.ColumnDefinitions[index].Width = savedWidths[index];
        }
        EditorLocalization.SetLanguage("zh-CN"); s.SelectedThemeIndex = 0; Pump();
    }

    private static void VerifyPhysicsVisibilitySwitch()
    {
        var s = shell!; var c = viewport!.BoneController!;
        var actor = Actor(); var model = actor.Model; Guid actorId = actor.ObjectId;
        string motionBefore = MotionHash(); var historyBefore = History();
        Assert(actor.PhysicsEnabled && physicsDriven.Count > 0,
            "real PMA physics-switch fixture starts with actor physics enabled and dynamic-bound bones");
        s.Browser.SelectedBoneIndex = null;
        Assert(c.ShowBones, "physics visibility switch tests normal unselected bone guides rather than selected-bone exceptions");
        WaitForFrame("physics-enabled baseline presents before visibility switch");
        var onFrame = CaptureFrame(); var onDrawn = c.DecorateFrame(onFrame);
        var onCenters = Centers().ToArray(); var onSegments = Segments(c);
        Assert(onCenters.Length > 0 && onCenters.All(center => !physicsDriven.Contains(model.Bones[center.Index]))
            && onSegments.All(segment => !physicsDriven.Contains(model.Bones[segment.Index])),
            "actual physics-enabled baseline centers and rods exclude dynamic-bound bones");

        s.Workspace.Edit(edit =>
        {
            int index = Enumerable.Range(0, edit.Motions.Count).Single(index => edit.Motions[index].ObjectId == actorId);
            edit.Motions[index] = edit.Motions[index] with { PhysicsEnabled = false };
        });
        s.RefreshWorkspace(actorId); Pump(); WaitForFrame("formal actor physics-disable edit presents");
        s.Browser.SelectedBoneIndex = null;
        var offFrame = CaptureFrame(); var offDrawn = c.DecorateFrame(offFrame);
        var offCenters = Centers().ToArray(); var offSegments = Segments(c);
        var offDynamicCenters = offCenters.Where(center => physicsDriven.Contains(model.Bones[center.Index])).ToArray();
        var offMarkers = (offDrawn.Scene.EditorOverlay?.Lines ?? []).Skip(offFrame.Scene.EditorOverlay?.Lines.Count ?? 0)
            .Where(line => line.Shape is ViewportOverlayShape.Circle or ViewportOverlayShape.Square).ToArray();
        Assert(!Actor().PhysicsEnabled && History().Undo == historyBefore.Undo + 1 && History().Redo == 0
            && MotionHash() == motionBefore,
            "formal physics-disable edit creates one Undo transaction while preserving exact actor motion");
        Assert(offDynamicCenters.Length > 0 && offCenters.All(center => model.Bones[center.Index].Flags.HasFlag(PmxBoneFlags.Visible)
            && model.Bones[center.Index].Flags.HasFlag(PmxBoneFlags.AllowControl)
            && (model.Bones[center.Index].Flags.HasFlag(PmxBoneFlags.AllowRotate) || model.Bones[center.Index].Flags.HasFlag(PmxBoneFlags.AllowTranslate)))
            && offDynamicCenters.All(center => offMarkers.Any(marker => marker.Start == new Vector2(center.Point.X / width, center.Point.Y / height))),
            "physics disabled reveals actual dynamic-bound manual bone centers with emitted marker primitives within the overlay budget");

        Assert(s.UndoCommand.CanExecute(null), "formal Undo is available for the physics-disable edit");
        s.UndoCommand.Execute(null); Pump(); WaitForFrame("formal physics-disable Undo presents");
        s.Browser.SelectedBoneIndex = null;
        var restoredFrame = CaptureFrame(); var restoredDrawn = c.DecorateFrame(restoredFrame);
        var restoredCenters = Centers().ToArray(); var restoredSegments = Segments(c);
        Assert(Actor().ObjectId == actorId && Actor().PhysicsEnabled && History().Undo == historyBefore.Undo
            && History().Redo == 1 && MotionHash() == motionBefore,
            "one formal Undo restores actor physics and exact original motion after the switch");
        Assert(restoredCenters.Length > 0 && restoredCenters.All(center => !physicsDriven.Contains(model.Bones[center.Index]))
            && restoredSegments.All(segment => !physicsDriven.Contains(model.Bones[segment.Index])),
            "actual frame after Undo again excludes dynamic-bound normal bone centers and rods");
        Assert(Hash(pma) == originalHash, "physics on/off/Undo visibility checks preserve the original PMA SHA256");
        PhysicsVisibility.Add(new
        {
            actorId, frame = s.CurrentFrame, dynamicBoundBoneCount = physicsDriven.Count,
            enabled = new { centerCount = onCenters.Length, rodCount = onSegments.Count, overlayLines = onDrawn.Scene.EditorOverlay?.Lines.Count },
            disabled = new { centerCount = offCenters.Length, rodCount = offSegments.Count,
                dynamicCenterIndices = offDynamicCenters.Select(center => center.Index).ToArray(), overlayLines = offDrawn.Scene.EditorOverlay?.Lines.Count },
            restored = new { centerCount = restoredCenters.Length, rodCount = restoredSegments.Count, overlayLines = restoredDrawn.Scene.EditorOverlay?.Lines.Count },
            maxOverlayLines = ViewportOverlaySnapshot.MaxLines, motionSha256 = motionBefore,
            scope = "Actual presented native frames followed by fresh evaluated snapshots and final bone decoration; counts are emitted geometry under the shared overlay budget, not all eligible model bones."
        });
    }

    private static void VerifySelectOnly(Hit hit, string kind)
    {
        var s = shell!; var v = viewport!; var c = v.BoneController!;
        string motionBefore = MotionHash(), draftsBefore = JsonSerializer.Serialize(s.Animation.SnapshotDrafts(), Json);
        var historyBefore = History();
        Assert(!c.IsManipulationEnabled && !s.Animation.HasDraft && c.TryBeginDrag(hit.X, hit.Y, width, height, v.CurrentCamera)
            && !c.IsDragging && s.Browser.SelectedBoneIndex == hit.Index && s.Animation.SelectedBoneIndex == hit.Index,
            "default Select click on the actual bone " + kind + " selects the bone without beginning a drag or draft");
        c.DecorateFrame(CaptureFrame());
        var captured = typeof(EditorBoneViewportController).GetField("captured", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(c)!;
        var handles = (IEnumerable)captured.GetType().GetProperty("BoneHandles")!.GetValue(captured)!;
        Assert(!handles.Cast<object>().Any() && !c.MoveDrag(hit.X + 15, hit.Y + 9) && !c.EndDrag()
            && !s.Animation.HasDraft && MotionHash() == motionBefore && History() == historyBefore
            && JsonSerializer.Serialize(s.Animation.SnapshotDrafts(), Json) == draftsBefore,
            "default Select mode shows no manipulation handles and preserves exact drafts, motion and Undo after a " + kind + " click/move/release");
    }

    private static void VerifyNativeSelectOnly(Hit hit)
    {
        var s = shell!; var v = viewport!; var c = v.BoneController!;
        string motionBefore = MotionHash(), draftsBefore = JsonSerializer.Serialize(s.Animation.SnapshotDrafts(), Json);
        var historyBefore = History();
        Assert(!c.IsManipulationEnabled && !c.IsDragging, "owned child mouse selection starts in default Select mode without a drag");
        try
        {
            NativeMouse(0x201, hit.X, hit.Y, 1);
            Assert(GetCapture() != childWindow && !c.IsDragging && s.Browser.SelectedBoneIndex == hit.Index
                && s.Animation.SelectedBoneIndex == hit.Index && viewportError is null,
                "owned child WM_LBUTTONDOWN selects the real isolated center without capturing the mouse or beginning a gesture");
            NativeMouse(0x202, hit.X, hit.Y, 0);
            Assert(GetCapture() != childWindow && !c.IsDragging && !s.Animation.HasDraft
                && MotionHash() == motionBefore && History() == historyBefore
                && JsonSerializer.Serialize(s.Animation.SnapshotDrafts(), Json) == draftsBefore,
                "owned child WM_LBUTTONUP preserves exact source motion, drafts and Undo with no lingering mouse capture");
        }
        finally
        {
            // Only this probe's owned HWND may be released if the capture regression fails.
            if (GetCapture() == childWindow) ReleaseCapture();
        }
    }

    private static void VerifyBrowserSelectedMode(Hit hit, bool rotating, string kind)
    {
        var s = shell!; var v = viewport ?? throw new InvalidOperationException("Editor viewport missing."); var c = v.BoneController!;
        c.RotateHandle = !rotating;
        s.Browser.SelectedBoneIndex = hit.Index;
        Assert(s.Animation.SelectedBoneIndex == hit.Index && !c.IsDragging,
            "Browser selects the real " + kind + " before any viewport hit");
        c.DecorateFrame(CaptureFrame());
        Assert(c.RotateHandle == rotating && (rotating ? s.Animation.CanRotate && !s.Animation.CanTranslate : s.Animation.CanTranslate && !s.Animation.CanRotate),
            "decorating a Browser-selected " + kind + " normalizes its effective mode before drawing or the first hit");
        var beforeTranslation = s.Animation.DraftTranslation; var beforeRotation = s.Animation.DraftRotation;
        Assert(c.TryBeginDrag(hit.X, hit.Y, width, height, v.CurrentCamera) && c.IsDragging
            && s.Animation.SelectedBoneIndex == hit.Index && c.RotateHandle == rotating,
            "the first viewport hit after Browser selection starts the already displayed " + kind + " mode");
        c.CancelDrag();
        Assert(!c.IsDragging && s.Animation.DraftTranslation == beforeTranslation && s.Animation.DraftRotation == beforeRotation,
            "canceling the Browser-selected " + kind + " first hit preserves its exact local draft");
        s.Browser.SelectedBoneIndex = null;
    }

    private static void VerifyToolbarButtonBounds(EditorWindow owner, Control bar, string scope)
    {
        var buttons = new[] { "BoneGuidesToggle", "BoneSelectMode", "BoneMoveMode", "BoneRotateMode", "ViewportRegisterKeyframeButton" }
            .Select(name => owner.FindControl<Button>(name) ?? throw new InvalidOperationException(name + " missing.")).ToArray();
        var bounds = buttons.Select(button =>
        {
            var topLeft = button.TranslatePoint(new Point(), bar) ?? throw new InvalidOperationException("Cannot map " + button.Name + " into its toolbar.");
            return new Rect(topLeft, button.Bounds.Size);
        }).ToArray();
        const double tolerance = .75;
        Assert(buttons.All(button => button.IsEffectivelyVisible) && bounds.All(rect => rect.Width > 0 && rect.Height > 0
            && rect.Left >= -tolerance && rect.Top >= -tolerance && rect.Right <= bar.Bounds.Width + tolerance && rect.Bottom <= bar.Bounds.Height + tolerance),
            scope + ": every named bone-toolbar button fits within BoneToolsBar");
        Assert(Enumerable.Range(0, bounds.Length).All(first => Enumerable.Range(first + 1, bounds.Length - first - 1)
            .All(second => Math.Min(bounds[first].Right, bounds[second].Right) - Math.Max(bounds[first].Left, bounds[second].Left) <= tolerance
                || Math.Min(bounds[first].Bottom, bounds[second].Bottom) - Math.Max(bounds[first].Top, bounds[second].Top) <= tolerance)),
            scope + ": named bone-toolbar button rectangles do not overlap");
    }

    private static void VerifyContinuousNativeDragging()
    {
        var s = shell!; var v = viewport!; var c = v.BoneController!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int? priorSelection = s.Browser.SelectedBoneIndex;
        var owner = s.PosePreview;
        var gate = typeof(EditorPosePreview).GetField("workerGate", flags)!.GetValue(owner)!;
        var running = typeof(EditorPosePreview).GetField("workerRunning", flags)!;
        var warm = typeof(EditorPosePreview).GetField("warmOwner", flags)!;
        bool Idle() { lock (gate) return !(bool)running.GetValue(owner)!; }
        PreviewFrame Presented()
        {
            var pose = typeof(EditorBoneViewportController).GetField("captured", flags)!.GetValue(c)!;
            return (PreviewFrame)pose.GetType().GetProperty("RenderFrame")!.GetValue(pose)!;
        }
        string Fingerprint(PreviewFrame frame)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(MemoryMarshal.AsBytes(frame.Scene.Surface.Snapshot.Vertices));
            foreach (var input in frame.SceneInputs.OrderBy(pair => pair.Key))
            {
                if (input.Value.SourceGeometry is { } geometry)
                    hash.AppendData(MemoryMarshal.AsBytes(geometry.Vertices));
                foreach (var bone in input.Value.BoneWorldMatrices.OrderBy(pair => pair.Key))
                    hash.AppendData(MemoryMarshal.AsBytes(new[] { bone.Value }.AsSpan()));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        void PumpFor(int milliseconds)
        {
            var loop = new DispatcherFrame(); var watch = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
            timer.Tick += (_, _) => { if (watch.ElapsedMilliseconds >= milliseconds) loop.Continue = false; };
            timer.Start(); try { Dispatcher.UIThread.PushFrame(loop); } finally { timer.Stop(); } Pump();
        }
        s.Animation.InvalidateDrafts(); owner.Clear(); s.CurrentFrame = 9;
        c.ShowBones = true; c.ShowSelectionBounds = true; c.IsManipulationEnabled = true; c.RotateHandle = true;
        WaitForFrame("continuous native drag baseline");
        var model = s.Browser.SelectedModel!.PmxModel;
        var weights = new int[model.Bones.Count];
        foreach (var vertex in model.Vertices)
            for (int slot = 0; slot < Math.Min(vertex.BoneIndices.Count, vertex.BoneWeights.Count); slot++)
                if (vertex.BoneWeights[slot] > 0 && (uint)vertex.BoneIndices[slot] < (uint)weights.Length)
                    weights[vertex.BoneIndices[slot]]++;
        int chosen = Centers().Where(center => Shown(model.Bones[center.Index])
            && model.Bones[center.Index].Flags.HasFlag(PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowControl)
            && weights[center.Index] > 0).OrderByDescending(center => weights[center.Index]).First().Index;
        s.Browser.SelectedBoneIndex = chosen; WaitForFrame("selected weighted bone preheating");
        PumpUntil(() => Idle() && warm.GetValue(owner) is not null, "formal Authoring committed prewarm", 30);
        string motion = MotionHash(); var history = History(); int heartbeatCount = 0;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) => heartbeatCount++; heartbeat.Start();
        try
        {
            for (int gesture = 0; gesture < 2; gesture++)
            {
                WaitForFrame("continuous gesture " + gesture + " current displayed geometry");
                var center = Centers().Single(item => item.Index == chosen);
                int x = (int)MathF.Round(center.Point.X), y = (int)MathF.Round(center.Point.Y);
                var poses = new HashSet<string>(); int frameCount = 0, startHeartbeats = heartbeatCount;
                void Observe(EditorViewportPresentationStatistics _) { if (c.IsDragging) { poses.Add(Fingerprint(Presented())); frameCount++; } }
                v.FramePresented += Observe;
                var watch = Stopwatch.StartNew();
                NativeMouse(0x201, x, y, 1);
                Assert(c.IsDragging && s.Animation.SelectedBoneIndex == chosen,
                    "native weighted bone center starts continuous rotation gesture " + gesture);
                try
                {
                    for (int index = 1; index <= 32; index++)
                    { NativeMouse(0x200, x + index, y + index / 2, 1); PumpFor(10); }
                    PumpUntil(Idle, "continuous physical step drain", 30); PumpFor(80);
                    Assert(c.IsDragging && poses.Count > 1 && frameCount > 1 && heartbeatCount > startHeartbeats,
                        "formal Authoring continuous gesture presents multiple changing solved poses while UI heartbeat continues " + gesture);
                }
                finally { v.FramePresented -= Observe; NativeMouse(0x202, x + 32, y + 16, 0); }
                double dragMs = watch.Elapsed.TotalMilliseconds;
                Assert(!c.IsDragging && s.Animation.HasAnyDraft && MotionHash() == motion && History() == history,
                    "native release retains an unregistered draft and unchanged motion/history " + gesture);
                PumpUntil(Idle, "release exact evaluation and next-gesture prewarm", 30);
                WaitForFrame("release exact result presentation " + gesture);
                string actual = Fingerprint(Presented());
                var requests = c.SnapshotRequests([]);
                using var exact = new EditorPosePreview();
                var snapshot = exact.Update(s.Workspace.Session, s.Animation)!.CaptureRenderSnapshot(requests);
                var reference = new PreviewFrame(width, height, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
                    { SceneInputs = snapshot.SceneInputs };
                Assert(actual == Fingerprint(reference), "presented release geometry and solved bones equal exact synchronous final pose " + gesture);
                ContinuousNativeDrags.Add(new { gesture, chosen, boneName = model.Bones[chosen].Name, positiveWeightSlots = weights[chosen],
                    dragMs, framesDuringDrag = frameCount, distinctPresentedPoses = poses.Count, heartbeats = heartbeatCount - startHeartbeats,
                    exactPresentedFingerprint = actual, scope = "Owned initially off-screen HWND; actual presented changing poses, not foreground FPS." });
            }
        }
        finally { heartbeat.Stop(); c.CancelDrag(); s.Animation.InvalidateDrafts(); owner.Clear(); c.ShowSelectionBounds = false;
            s.Browser.SelectedBoneIndex = priorSelection; }
        WaitForFrame("continuous native diagnostic restores source pose");
        Assert(MotionHash() == motion && History() == history, "two native continuous gestures preserve authored motion and Undo/Redo history");
    }

    private static PreviewFrame CaptureFrame()
    {
        var s = shell!; var v = viewport!;
        var snapshot = v.SnapshotProvider!([]);
        return new PreviewFrame(width, height, s.CurrentFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        { ResolvedCamera = v.CurrentCamera, SceneInputs = snapshot.SceneInputs, RenderingStyles = snapshot.RenderingStyles, MorphChannels = snapshot.MorphChannels };
    }

    private static List<Segment> Segments(EditorBoneViewportController controller)
    {
        var captured = typeof(EditorBoneViewportController).GetField("captured", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)
            ?? throw new InvalidOperationException("Controller did not retain its displayed solved pose.");
        var list = (IEnumerable)captured.GetType().GetProperty("BoneSegments")!.GetValue(captured)!;
        return list.Cast<object>().Select(item => new Segment((int)item.GetType().GetProperty("Index")!.GetValue(item)!,
            (Vector2)item.GetType().GetProperty("Start")!.GetValue(item)!, (Vector2)item.GetType().GetProperty("End")!.GetValue(item)!)).ToList();
    }

    private static Hit? PickSegment(IEnumerable<Segment> segments, PreviewFrame frame, Func<Segment, bool> predicate)
    {
        var shownCenters = Centers().ToArray();
        foreach (var segment in segments.Where(predicate).OrderByDescending(item => Vector2.DistanceSquared(item.Start, item.End)))
            foreach (float t in new[] { .5f, .4f, .6f })
            {
                var point = Vector2.Lerp(segment.Start, segment.End, t);
                int x = (int)MathF.Round(point.X), y = (int)MathF.Round(point.Y);
                if (x < 2 || x >= width - 2 || y < 2 || y >= height - 2 || shownCenters.Any(item => Vector2.DistanceSquared(new(x, y), item.Point) <= 121)) continue;
                // Demand an unambiguous actual drawn rod. Avoid another rod's 6px hit corridor.
                if (segments.Any(other => other.Index != segment.Index && DistanceSquared(new(x, y), other.Start, other.End) <= 36)) continue;
                return new(segment.Index, x, y);
            }
        return null;
    }

    private static Hit? PickCenter(PreviewFrame frame, Func<int, bool> predicate)
    {
        var centers = Centers().ToArray(); var model = shell!.Browser.SelectedModel!.PmxModel;
        foreach (var center in centers.Where(item => predicate(item.Index)).OrderByDescending(item => model.Bones[item.Index].Name.Contains("センター") || model.Bones[item.Index].Name.Contains("中心")))
        {
            int x = (int)MathF.Round(center.Point.X), y = (int)MathF.Round(center.Point.Y);
            if (x < 2 || x >= width - 2 || y < 2 || y >= height - 2) continue;
            if (centers.Any(other => other.Index != center.Index && Shown(model.Bones[other.Index]) && Vector2.DistanceSquared(new(x, y), other.Point) < 4)) continue;
            return new(center.Index, x, y);
        }
        return null;
    }

    private static IEnumerable<(int Index, Vector2 Point)> Centers()
    {
        var captured = typeof(EditorBoneViewportController).GetField("captured", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewport!.BoneController)
            ?? throw new InvalidOperationException("Controller did not retain its displayed solved pose.");
        var list = (IEnumerable)captured.GetType().GetProperty("BoneCenters")!.GetValue(captured)!;
        foreach (var item in list.Cast<object>())
            yield return ((int)item.GetType().GetProperty("Index")!.GetValue(item)!, (Vector2)item.GetType().GetProperty("Point")!.GetValue(item)!);
    }
    private static bool Shown(PmxBone bone) => !physicsDriven.Contains(bone) && bone.Flags.HasFlag(PmxBoneFlags.Visible);
    private static float DistanceSquared(Vector2 point, Vector2 start, Vector2 end)
    { var d = end - start; float length = d.LengthSquared(); return Vector2.DistanceSquared(point, start + d * (length < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(point - start, d) / length, 0, 1))); }
    private static SceneActorMotion Actor() => shell!.Workspace.Motions.Single(item => item.ObjectId == shell.Browser.SelectedModel!.ObjectId);
    private static string MotionHash() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Actor().Motion, Json)));
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static (int Undo, int Redo) History()
    { int Count(string name) => ((ICollection)typeof(EditorProjectWorkspace).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell!.Workspace)!).Count; return (Count("undo"), Count("redo")); }
    private static void NativeKey(int key, bool repeat = false)
    {
        Assert(childWindow != nint.Zero && viewport!.HasChildWindow, "native key injection targets only this probe's owned editor child HWND");
        typeof(EditorViewportHost).GetMethod("OnWindowMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewport, [childWindow, (uint)0x100, (nint)key, (nint)(repeat ? 1L << 30 : 0)]);
        Pump();
    }
    private static void NativeMouse(uint message, int x, int y, int buttons)
    {
        Assert(childWindow != nint.Zero && viewport!.HasChildWindow && message is 0x200 or 0x201 or 0x202,
            "native mouse-handler invocation targets only this probe's owned child HWND left-button messages");
        nint position = unchecked((nint)((uint)(ushort)x | ((uint)(ushort)y << 16)));
        typeof(EditorViewportHost).GetMethod("OnWindowMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewport, [childWindow, message, (nint)buttons, position]);
        Pump();
    }
    private static void ClickToggle(ToggleButton button, bool value)
    { button.IsChecked = value; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Capture(string name)
    {
        Pump(); if (window is null || !window.IsVisible) return;
        var bar = window.FindControl<Control>("BoneToolsBar");
        double scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, name));
        File.WriteAllText(Path.Combine(output, name + ".layout.json"), JsonSerializer.Serialize(window.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Name is "BoneToolsBar" or "BoneGuidesToggle" or "BoneSelectMode" or "BoneMoveMode" or "BoneRotateMode" or "ViewportRegisterKeyframeButton")
            .Select(control => new { control.Name, bounds = control.Bounds.ToString(),
                toolbarOrigin = bar is null ? null : control.TranslatePoint(new Point(), bar)?.ToString(),
                control.IsEffectivelyVisible, control.IsEffectivelyEnabled }), Json));
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); window?.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Await(Task task, string operation, int seconds = 30) { PumpUntil(() => task.IsCompleted, operation, seconds); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task, string operation, int seconds = 30) { Await((Task)task, operation, seconds); return task.GetAwaiter().GetResult(); }
    private static void WaitForFrame(string operation)
    {
        int before = presented; viewport!.RequestRender(); PumpUntil(() => presented > before || viewportError is not null, operation, 30);
        if (viewportError is not null) throw new InvalidOperationException(operation, viewportError);
        Assert(presented > before && viewport.Statistics is not null, operation + ": actual FramePresented");
    }
    private static void PumpUntil(Func<bool> condition, string operation, int seconds)
    {
        if (condition()) { Pump(); return; }
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { try { if (condition()) frame.Continue = false; else if (clock.Elapsed.TotalSeconds >= seconds) { failure = new TimeoutException(operation); frame.Continue = false; } } catch (Exception error) { failure = error; frame.Continue = false; } };
        timer.Start(); try { Dispatcher.UIThread.PushFrame(frame); } finally { timer.Stop(); }
        Pump(); if (failure is not null) throw failure;
    }
    private static void Assert(bool condition, string check) { if (!condition) throw new InvalidOperationException("FAIL: " + check); Checks.Add(check); }
    private static void WriteReport(string? failure) => File.WriteAllText(Path.Combine(output, "bone-editing-checks.json"), JsonSerializer.Serialize(new
    {
        success = failure is null, passed = Checks.Count, checks = Checks, explicitSkip = ExplicitSkip, failure, gestures = Gestures,
        physicsVisibility = PhysicsVisibility, continuousNativeDrags = ContinuousNativeDrags,
        pmaPath = pma, originalPmaSha256 = originalHash, finalPmaSha256 = File.Exists(pma) ? Hash(pma) : null,
        loadedAssemblyPath = typeof(App).Assembly.Location, loadedAssemblySha256 = Hash(typeof(App).Assembly.Location),
        adapter = viewport?.AdapterName, nativeFramesPresented = presented, viewportWidth = width, viewportHeight = height,
        filterShownBoneCount, declaredTailEligibleBoneCount,
        inputScope = "Reflection invokes this probe's own child HWND WM_KEYDOWN/WM_LBUTTONDOWN/WM_LBUTTONUP handlers; no OS SendInput or external window input.",
        screenshotScope = "Native Avalonia control render; the D3D12 child HWND is omitted. No full-window visual acceptance is claimed."
    }, Json));
    private sealed record GpuDiagnosticReadback(byte[] Rgba, ulong Fence, ulong BufferBytes, int RowPitch, string Format);
    private readonly record struct Segment(int Index, Vector2 Start, Vector2 End);
    private readonly record struct Hit(int Index, int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
}
