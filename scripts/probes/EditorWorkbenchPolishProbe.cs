using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>Reference-only native editor diagnostic. Its screenshots render Avalonia controls;
/// the native D3D12 child HWND is omitted. It never saves the supplied PMA or preferences.</summary>
internal static class EditorWorkbenchPolishProbe
{
    private static readonly List<string> Checks = [];
    private static readonly List<object> Cases = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Dictionary<string, string> AssemblyBefore = new(StringComparer.OrdinalIgnoreCase);
    private static string output = "", pma = "", pmaBefore = "";
    private static EditorWindow? window;
    private static EditorShellViewModel? shell;
    private static EditorViewportHost? viewport;
    private static Exception? viewportError;
    private static int presented;
    private static nint childWindow;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: probe <output-directory> <read-only-existing.pma>");
        output = Path.GetFullPath(args[0]); pma = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        try
        {
            pmaBefore = Hash(pma);
            AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().SetupWithoutStarting();
            shell = new EditorShellViewModel(); shell.Workspace.Open(pma);
            var first = shell.Workspace.Session!.Objects.FirstOrDefault()
                ?? throw new InvalidOperationException("The supplied real PMA must contain a model.");
            shell.RefreshWorkspace(first.Id);
            window = new EditorWindow(false) { DataContext = shell, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
            viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
            viewport.Error += error => viewportError = error;
            viewport.FramePresented += _ => presented++;
            window.Show(); Pump(); WaitForFrame("real read-only PMA initial presentation");
            childWindow = (nint)typeof(EditorViewportHost).GetField("childWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewport)!;
            Assert(viewport.HasChildWindow && !viewport.IsWarp && childWindow != nint.Zero,
                "probe owns a real hardware D3D12 child HWND");
            Assert(GetClientRect(childWindow, out var rect) && rect.Right > 0 && rect.Bottom > 0,
                "owned D3D12 child HWND has a measurable client area");
            Assert(!shell.Workspace.IsDirty && shell.Browser.SelectedModel?.ObjectId == first.Id,
                "real PMA opens clean and its first authored model is selected");
            foreach (var assembly in ProductAssemblies()) AssemblyBefore[assembly] = Hash(assembly);
            Assert(!shell.Browser.IsHierarchyExpanded && !Named<Control>("AnimationElements").IsEffectivelyVisible,
                "animation hierarchy and search start collapsed on the selected real model");
            var light = shell.Workspace.Session.Lighting.Lights.FirstOrDefault()
                ?? throw new InvalidOperationException("The supplied real PMA must contain an authored additional light.");
            viewport.LightController!.Select(light.Id); Pump(); WaitForFrame("authored native-light selection presentation");
            Assert(!shell.Browser.IsHierarchyExpanded && !Named<Control>("AnimationElements").IsEffectivelyVisible,
                "selecting an authored light does not expand the left animation hierarchy");
            foreach (string language in new[] { "zh-CN", "en-US", "ja-JP" })
            foreach (int theme in new[] { 0, 1 })
            foreach (var size in new[] { (Width: 1100, Height: 720), (Width: 1480, Height: 940) })
                VerifyCase(language, theme, size.Width, size.Height, light.Id);
            VerifyMainLight();
            Assert(!shell.Workspace.IsDirty, "layout, disclosure and restored detached-value checks leave the PMA document clean");
            VerifyNativeLightDelete(light.Id);
            Assert(Hash(pma) == pmaBefore, "supplied read-only PMA SHA256 is unchanged");
            Assert(AssemblyBefore.All(item => File.Exists(item.Key) && Hash(item.Key) == item.Value),
                "all initially loaded product assemblies retain their SHA256");
            WriteReport(null); Console.WriteLine("PASS " + Checks.Count); return 0;
        }
        catch (Exception error)
        {
            try { Capture("failure-controls-hwnd-omitted.png"); } catch { }
            WriteReport(error.ToString()); Console.Error.WriteLine(error); return 1;
        }
        finally
        {
            if (window is not null)
            {
                // Only this diagnostic's own window/document; never display or accept Save.
                typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(window, true);
                window.Close();
            }
        }
    }

    private static void VerifyCase(string language, int theme, int width, int height, Guid lightId)
    {
        string scope = language + "-" + (theme == 0 ? "black" : "white") + "-" + width + "x" + height;
        EditorLocalization.SetLanguage(language); shell!.SelectedThemeIndex = theme;
        window!.Width = width; window.Height = height; Pump();
        WaitForFrame(scope + " real viewport presentation after language/theme/size change");
        bool hasNativeRect = GetClientRect(childWindow, out var nativeRect);
        Assert(viewport!.HasChildWindow && !viewport.IsWarp && hasNativeRect
            && nativeRect.Right > 0 && nativeRect.Bottom > 0, scope + ": actual hardware child remains live after layout");
        Assert(!shell.Browser.IsHierarchyExpanded && !Named<Control>("AnimationElements").IsEffectivelyVisible,
            scope + ": left bone/morph hierarchy remains folded");
        var inline = Named<EditorLightInspector>("NativeLightInspector");
        Assert(inline.IsEffectivelyVisible && inline.ViewModel?.SelectedLight?.Id == lightId
            && Named<Control>("LightToolsBar").IsEffectivelyVisible && !Named<Control>("StandardSelectionInspector").IsEffectivelyVisible,
            scope + ": selected authored light owns the inline inspector and transform toolbar");
        Assert(viewport.LightController?.SelectedLightId == lightId
            && shell.PlayLabel == EditorLocalization.Text("播放"),
            scope + ": locale refresh preserves light identity and translates the current transport action");
        var advanced = inline.GetVisualDescendants().OfType<Expander>()
            .Single(control => control.Name == "InlineLightAdvanced" && control.IsEffectivelyVisible);
        VerifyCollapsed(advanced, scope + " inline advanced");
        var visibleBoxes = inline.GetVisualDescendants().OfType<CheckBox>().Where(control => control.IsEffectivelyVisible).ToArray();
        Assert(visibleBoxes.Length == 1 && visibleBoxes[0].IsEffectivelyEnabled
            && visibleBoxes[0].Content?.ToString() == EditorLocalization.Text("Mda.Text.0d6601d54d15"),
            scope + ": the only effectively visible inline checkbox is the labelled Enabled control");
        var color = VerifyCheckboxCaption(visibleBoxes.Single(), theme, scope + " enabled checkbox");
        VerifySceneObjectBounds(scope);
        VerifyToolbar(Named<Control>("LightToolsBar"), ["LightMoveMode", "LightRotateMode", "DeleteLightButton"], scope + " light transforms");
        var topBar = Named<Button>("ViewportLightingButton").GetVisualAncestors().OfType<Border>()
            .First(control => control.Classes.Contains("viewportBar"));
        VerifyToolbar(topBar, ["ViewportLightingButton", "AddViewportLightButton", "WorldGuidesToggle", "SelectionBoundsToggle"], scope + " viewport top actions");
        Capture("inline-light-" + scope + "-controls-hwnd-omitted.png");
        advanced.IsExpanded = true; Pump();
        var inner = (Control)advanced.Content!;
        var shadow = inner.GetVisualDescendants().OfType<CheckBox>().Single();
        var numbers = inner.GetVisualDescendants().OfType<NumericUpDown>().Where(control => control.IsEffectivelyVisible).ToArray();
        Assert(inner.IsEffectivelyVisible && shadow.IsEffectivelyVisible && shadow.IsEffectivelyEnabled
            && numbers.Length >= 6 && numbers.All(control => control.IsEffectivelyEnabled),
            scope + ": expanding advanced exposes enabled shadow and precise numeric controls");
        VerifyCheckboxCaption(shadow, theme, scope + " expanded shadow checkbox");
        bool oldShadow = inline.ViewModel!.SelectedLight!.CastShadows;
        shadow.IsChecked = !oldShadow; Pump();
        Assert(inline.ViewModel.SelectedLight.CastShadows == !oldShadow, scope + ": expanded shadow checkbox updates its detached bound draft");
        shadow.IsChecked = oldShadow; Pump();
        var number = numbers.First(); decimal? oldValue = number.Value;
        number.Value = (oldValue ?? 0) + .125m; Pump();
        Assert(number.Value == (oldValue ?? 0) + .125m, scope + ": expanded numeric control accepts a detached value");
        number.Value = oldValue; Pump();
        Assert(inline.ViewModel.SelectedLight.CastShadows == oldShadow && number.Value == oldValue
            && inline.ViewModel.TryCreate(out var draft) && draft is not null
            && draft.Snapshot.Lights.SequenceEqual(shell.Workspace.Session!.Lighting.Lights),
            scope + ": advanced probe restores every detached edit without committing project content");
        if (language == "zh-CN" && theme == 0 && width == 1480)
            Capture("inline-light-advanced-" + scope + "-controls-hwnd-omitted.png");
        advanced.IsExpanded = false; Pump(); VerifyCollapsed(advanced, scope + " refolded inline advanced");
        Cases.Add(new { language, theme = theme == 0 ? "black" : "white", requestedWidth = width, requestedHeight = height,
            actualWidth = window.Bounds.Width, actualHeight = window.Bounds.Height, nativeWidth = nativeRect.Right,
            nativeHeight = nativeRect.Bottom, visibleInlineCheckboxes = visibleBoxes.Length, captionForeground = color,
            advancedNumericControls = numbers.Length, framesPresentedThroughCase = presented });
    }

    private static void VerifyMainLight()
    {
        EditorLocalization.SetLanguage("zh-CN"); shell!.SelectedThemeIndex = 0;
        window!.Width = 1480; window.Height = 940;
        typeof(EditorWindow).GetMethod("CloseSceneLighting", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [null, new RoutedEventArgs()]);
        Named<TabControl>("InspectorPageTabs").SelectedIndex = (int)EditorInspectorPage.Lighting;
        Pump(); WaitForFrame("main-light inspector actual presentation");
        var inspector = window.GetVisualDescendants().OfType<EditorMainLightInspector>().Single();
        var pad = inspector.FindControl<Control>("MainLightDirectionPad")!;
        var advanced = inspector.FindControl<Expander>("MainLightAdvanced")!;
        Assert(Named<Control>("NativeLightInspectorArea").IsEffectivelyVisible
            && !Named<Control>("StandardSelectionInspector").IsEffectivelyVisible && shell.IsLightingPage && shell.IsLightInspector
            && inspector.FindControl<NumericUpDown>("LightIntensity")!.IsEffectivelyVisible
            && pad.IsEffectivelyVisible && pad.IsEffectivelyEnabled && pad.Bounds.Width > 0 && pad.Bounds.Height > 0,
            "explicitly opening the lighting page exposes the native main-light direction pad and intensity");
        VerifyCollapsed(advanced, "main-light advanced default");
        Capture("main-light-black-1480x940-controls-hwnd-omitted.png");
        advanced.IsExpanded = true; Pump();
        var fields = ((Control)advanced.Content!).GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        Assert(fields.Length == 6 && fields.All(control => control.IsEffectivelyVisible && control.IsEffectivelyEnabled),
            "main-light advanced opens all six precise color/direction fields");
        advanced.IsExpanded = false; Pump(); VerifyCollapsed(advanced, "main-light advanced refolded");
        Assert(!shell.Browser.IsHierarchyExpanded && !Named<Control>("AnimationElements").IsEffectivelyVisible,
            "main-light selection also preserves the folded left animation hierarchy");
    }

    private static void VerifyNativeLightDelete(Guid lightId)
    {
        var original = shell!.Workspace.Session!.Lighting.Lights.ToArray();
        viewport!.LightController!.Select(lightId); Pump(); WaitForFrame("light selected before native Delete route");
        typeof(EditorViewportHost).GetMethod("OnWindowMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewport, [childWindow, (uint)0x100, (nint)0x2e, nint.Zero]);
        Pump();
        Assert(shell.Workspace.Session.Lighting.Lights.All(light => light.Id != lightId)
            && shell.Workspace.CanUndo && shell.Workspace.IsDirty,
            "owned native child HWND Delete route removes its selected light through the workspace transaction");
        Assert(shell.Workspace.Undo() && shell.Workspace.Session.Lighting.Lights.SequenceEqual(original),
            "native Delete transaction is undoable without saving or changing the supplied PMA");
        Pump(); WaitForFrame("native Delete Undo presentation");
    }

    private static void VerifyCollapsed(Expander advanced, string scope)
    {
        var content = (Control)advanced.Content!;
        var inputs = content.GetVisualDescendants().OfType<Control>()
            .Where(control => control is NumericUpDown or CheckBox or TextBox).ToArray();
        var presenter = advanced.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(control => control.Name == "PART_ContentPresenter" && ReferenceEquals(control.TemplatedParent, advanced));
        // A collapsed ContentPresenter can detach its content entirely. Detached controls retain
        // local IsVisible=true, but cannot draw, receive input, or belong to this window's visual tree.
        bool VisibleHere(Control control) => control.IsEffectivelyVisible && ReferenceEquals(control.GetVisualRoot(), window);
        Assert(advanced.IsEffectivelyVisible && !advanced.IsExpanded && !presenter.IsEffectivelyVisible
            && !VisibleHere(content) && inputs.Length > 0 && inputs.All(control => !VisibleHere(control)),
            scope + ": collapsed content and every nested field/checkbox are effectively hidden");
    }

    private static string VerifyCheckboxCaption(CheckBox checkbox, int theme, string scope)
    {
        var presenter = checkbox.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(control => control.Name == "PART_ContentPresenter");
        var expectedColor = Color.Parse(theme == 0 ? "#F2F2F2" : "#171717");
        Assert(presenter.IsEffectivelyVisible && presenter.Bounds.Width > 0 && presenter.Bounds.Height > 0
            && !string.IsNullOrWhiteSpace(presenter.Content?.ToString())
            && presenter.Foreground is ISolidColorBrush actual && actual.Color == expectedColor
            && window!.Foreground is ISolidColorBrush expected && expected.Color == expectedColor,
            scope + ": actual rendered ContentPresenter caption is labelled and resolves the active EditorText color");
        return ((ISolidColorBrush)presenter.Foreground!).Color.ToString();
    }

    private static void VerifySceneObjectBounds(string scope)
    {
        var list = Named<ListBox>("SceneObjects"); var limit = BoundsIn(list, window!);
        var children = list.GetVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && control is ListBoxItem or TextBlock).ToArray();
        Assert(list.IsEffectivelyVisible && list.Bounds.Width > 0 && children.Length >= 9
            && children.All(control => { var rect = BoundsIn(control, window!); return rect.Left >= limit.Left - .75 && rect.Right <= limit.Right + .75; }),
            scope + ": scene-object rows and actual label rectangles stay within both horizontal edges of the left list");
    }

    private static void VerifyToolbar(Control bar, string[] names, string scope)
    {
        var controls = names.Select(Named<Control>).ToArray();
        var rects = controls.Select(control => BoundsIn(control, bar)).ToArray();
        const double tolerance = .75;
        Assert(controls.All(control => control.IsEffectivelyVisible) && rects.All(rect => rect.Width > 0 && rect.Height > 0
            && rect.Left >= -tolerance && rect.Top >= -tolerance && rect.Right <= bar.Bounds.Width + tolerance && rect.Bottom <= bar.Bounds.Height + tolerance),
            scope + ": every action rectangle fits its actual toolbar after TranslatePoint");
        Assert(Enumerable.Range(0, rects.Length).All(first => Enumerable.Range(first + 1, rects.Length - first - 1).All(second =>
            Math.Min(rects[first].Right, rects[second].Right) - Math.Max(rects[first].Left, rects[second].Left) <= tolerance
            || Math.Min(rects[first].Bottom, rects[second].Bottom) - Math.Max(rects[first].Top, rects[second].Top) <= tolerance)),
            scope + ": actual toolbar action rectangles do not overlap");
        Assert(controls.All(control => { var rect = BoundsIn(control, window!); var clipped = ClippedBounds(control);
            return Math.Abs(rect.Width - clipped.Width) <= tolerance && Math.Abs(rect.Height - clipped.Height) <= tolerance; }),
            scope + ": action rectangles are not cut by ancestor ClipToBounds");
    }

    private static Rect ClippedBounds(Control control)
    {
        var result = BoundsIn(control, window!);
        foreach (var ancestor in control.GetVisualAncestors().OfType<Control>().Where(item => item.ClipToBounds))
            result = result.Intersect(BoundsIn(ancestor, window!));
        return result.Intersect(new Rect(window!.Bounds.Size));
    }
    private static Rect BoundsIn(Control control, Visual relative) => new(
        control.TranslatePoint(new Point(), relative) ?? throw new InvalidOperationException("Cannot translate " + control.Name), control.Bounds.Size);
    private static T Named<T>(string name) where T : Control => window!.GetVisualDescendants().OfType<T>()
        .Single(control => control.Name == name);
    private static void Capture(string name)
    {
        Pump(); if (window is not { IsVisible: true }) return;
        double scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
            (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, name));
        File.WriteAllText(Path.Combine(output, name + ".layout.json"), JsonSerializer.Serialize(new
        {
            screenshotScope = "Avalonia control render; native D3D12 child HWND omitted.",
            controls = window.GetVisualDescendants().OfType<Control>().Where(control => !string.IsNullOrEmpty(control.Name))
                .Select(control => new { control.Name, type = control.GetType().Name, bounds = BoundsIn(control, window).ToString(),
                    clippedBounds = ClippedBounds(control).ToString(), control.IsEffectivelyVisible, control.IsEffectivelyEnabled,
                    foreground = control is ContentPresenter presenter ? presenter.Foreground?.ToString() : null })
        }, Json));
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); window?.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void WaitForFrame(string operation)
    {
        int before = presented; viewport!.RequestRender();
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew(); Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { if (presented > before || viewportError is not null) frame.Continue = false;
            else if (elapsed.Elapsed.TotalSeconds >= 40) { failure = new TimeoutException(operation); frame.Continue = false; } };
        timer.Start(); try { Dispatcher.UIThread.PushFrame(frame); } finally { timer.Stop(); }
        Pump(); if (failure is not null) throw failure;
        if (viewportError is not null) throw new InvalidOperationException(operation, viewportError);
        Assert(presented > before && viewport.Statistics is not null, operation + ": actual FramePresented");
    }
    private static IEnumerable<string> ProductAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(assembly => !assembly.IsDynamic && assembly.GetName().Name?.StartsWith("Mmd.Modern", StringComparison.Ordinal) == true)
        .Select(assembly => assembly.Location).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Order();
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static void Assert(bool condition, string check) { if (!condition) throw new InvalidOperationException("FAIL: " + check); Checks.Add(check); }
    private static void WriteReport(string? failure) => File.WriteAllText(Path.Combine(output, "workbench-polish-checks.json"), JsonSerializer.Serialize(new
    {
        success = failure is null, passed = Checks.Count, checks = Checks, failure, cases = Cases,
        pmaPath = pma, originalPmaSha256 = pmaBefore, finalPmaSha256 = File.Exists(pma) ? Hash(pma) : null,
        loadedAssemblyPath = typeof(App).Assembly.Location, loadedAssemblySha256 = Hash(typeof(App).Assembly.Location),
        productAssemblies = ProductAssemblies().Select(path => new { path, sha256 = Hash(path),
            originalSha256 = AssemblyBefore.GetValueOrDefault(path) }),
        adapter = viewport?.AdapterName, isWarp = viewport?.IsWarp, nativeFramesPresented = presented,
        inputScope = "Only in-process control property changes and this probe's own window handlers; no OS input, no external window interaction.",
        screenshotScope = "Avalonia control render; native D3D12 child HWND omitted. No full-window or final user visual acceptance is claimed.",
        verificationScope = "Twelve real PMA language/theme/size layout cases plus native hardware FramePresented. No GPU utilization, FPS, codec, install lifecycle or broad playback claims. PMA and product files are read only; no preferences are saved."
    }, Json));
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint handle, out NativeRectangle rectangle);
}
