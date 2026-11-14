using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>
/// Published startup controls and embedded catalogs only. Does not start the editor workspace,
/// alter startup timing, request D3D12, save preferences, or read/write the OS clipboard.
/// EditorStartupAnimationProbe separately verifies ordinary App startup, first GPU frame and cancel.
/// </summary>
internal static class EditorStartupPresentationProbe
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string AssetRoot = "avares://Mmd.Modern.App/Assets/Localization/";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly List<string> Checks = [];
    private static readonly List<string> Captures = [];
    private static readonly List<object> Scenarios = [];
    private static readonly List<object> CatalogEvidence = [];
    private static readonly List<object> LoadedAssemblies = [];
    private static readonly Dictionary<string, string> ProductHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FileState> PreferencesBefore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FileState> PreferencesAfter = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new(StringComparer.Ordinal);
    // Reflect only after platform setup: LoadingDots' static brush must not initialize an unconfigured dispatcher.
    private static StyledProperty<double>? progressProperty;
    private static string output = "", expectedApp = "", expectedAppSha = "", productRoot = "";
    private static string originalLanguage = "zh-CN";
    private static EditorStartupWindow? window;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Usage: startup-presentation-probe <output-directory> --expected-app=<absolute published App DLL> --expected-app-sha256=<SHA256> [--expected-product-manifest=<payload JSON>]");
        output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        Exception? failure = null;
        bool platformReady = false;
        try
        {
            var options = args.Skip(1).Select(ParseOption).ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
            ConfigureProduct(options); VerifyProduct("before");
            foreach (string path in new[] { EditorPreferences.DefaultPath, EditorPreferences.DefaultOutputPath,
                Path.Combine(EditorPreferences.DirectoryPath, "editor-preview.json") })
                PreferencesBefore.Add(path, ReadState(path));
            AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().SetupWithoutStarting();
            platformReady = true; originalLanguage = EditorLocalization.CurrentLanguage;
            progressProperty = (StyledProperty<double>)(typeof(LoadingDots).GetField("AnimationProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                ?? throw new MissingFieldException(typeof(LoadingDots).FullName, "AnimationProgressProperty"));
            VerifyCatalogs();
            foreach (string language in new[] { "zh-CN", "en", "ja" })
                foreach (int theme in new[] { 0, 1 }) VerifyPresentation(language, theme);
            VerifyActiveClose();
        }
        catch (Exception error)
        {
            failure = Unwrap(error);
            try { if (window?.IsVisible == true) Capture("startup-presentation-unexpected-failure.png"); } catch { }
        }
        finally
        {
            try { if (window?.IsVisible == true) window.Close(); if (platformReady) Dispatcher.UIThread.RunJobs(); }
            catch (Exception error) { failure ??= error; }
            if (platformReady)
                try { EditorLocalization.SetLanguage(originalLanguage); }
                catch (Exception error) { failure ??= error; }
            // Unexpected changes are evidence: never repair, replace or restore machine-local files.
            try { VerifyPreferences(); VerifyProduct("after"); }
            catch (Exception error) { failure ??= error; }
            WriteReport(failure);
        }
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine($"PASS {Checks.Count} published startup-presentation checks. Evidence: {output}");
        return 0;
    }

    private static void VerifyCatalogs()
    {
        foreach (string language in new[] { "zh-CN", "en", "ja" })
        {
            using var input = AssetLoader.Open(new Uri(AssetRoot + language + ".json"));
            using var bytes = new MemoryStream(); input.CopyTo(bytes);
            byte[] payload = bytes.ToArray();
            var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(payload)
                ?? throw new InvalidDataException("Empty published catalog: " + language);
            Assert(catalog.Count > 0 && catalog.All(pair => pair.Key.StartsWith("Mda.Text.", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pair.Value)),
                language + ": the published embedded catalog contains nonempty explicit keys");
            Catalogs.Add(language, catalog);
            CatalogEvidence.Add(new { language, uri = AssetRoot + language + ".json", keys = catalog.Count,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)) });
        }
        foreach (string language in Catalogs.Keys)
        {
            var catalog = Catalogs[language];
            Assert(catalog.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Catalogs["zh-CN"].Keys), language + ": catalog key set matches Chinese");
            EditorLocalization.SetLanguage(language);
            Assert(EditorLocalization.CurrentLanguage == language && catalog.All(pair => EditorLocalization.Text(pair.Key) == pair.Value),
                language + ": the real localization loader resolves every published explicit key");
            foreach (string key in new[] { "Mda.Text.d696a35bdd18", "Mda.Text.1709305c9fa9", "Mda.Text.b23e968e4359",
                "Mda.Text.1269413e2df4", "Mda.Text.51801cae6590", "Mda.Text.a515222da88f" })
                Assert(EditorLocalization.Text(key) == catalog[key] && EditorLocalization.Text(key) != key,
                    language + ": Profile/Level/material role/schema/LightMap/Ramp label resolves by explicit key: " + key);
            foreach (string name in new[] { "Profile", "Level", "Role", "LightMap", "Ramp", "角色", "原神", "ユーザーのモデル",
                "Mda.Text.d696a35bdd18" })
            {
                string formatted = EditorLocalization.Format("Mda.Text.1269413e2df4", name, 12, 3);
                Assert(formatted.StartsWith(name + " · ", StringComparison.Ordinal), language + ": formatted user name remains verbatim: " + name);
            }
            const string unknown = "owned-startup-name-柴郡-ユーザー";
            Assert(EditorLocalization.Text(unknown) == unknown, language + ": unknown authored name remains unchanged");
        }
        var chinese = Catalogs["zh-CN"];
        Assert(chinese["Mda.Text.d696a35bdd18"] == "编码规格" && chinese["Mda.Text.1709305c9fa9"] == "编码级别"
            && chinese["Mda.Text.b23e968e4359"] == "材质部位" && chinese["Mda.Text.1269413e2df4"].Contains("格式版本", StringComparison.Ordinal)
            && !chinese["Mda.Text.1269413e2df4"].Contains("schema", StringComparison.OrdinalIgnoreCase),
            "Chinese Profile, Level, material role and schema labels use the approved readable UI terms");
        Assert(chinese["Mda.Text.51801cae6590"] == "光照控制图（LightMap）" && chinese["Mda.Text.a515222da88f"] == "明暗渐变图／图集",
            "Chinese LightMap and Ramp labels resolve through their explicit readable keys");
    }

    private static void VerifyPresentation(string language, int theme)
    {
        string label = language + "-" + (theme == 0 ? "black" : "white");
        window = NewWindow(language, theme); window.Show(); PumpLayout();
        var dots = Find<LoadingDots>("StartupDots");
        bool actualAnimationPreference = dots.AnimationsEnabled;
        WaitUntil(() => Field("logoEntrance") is null, "the brief logo entrance completes");
        Assert(window.Width == 440 && window.Height == 284 && Math.Abs(window.Bounds.Width - 440) <= .5 && Math.Abs(window.Bounds.Height - 284) <= .5,
            label + ": real undecorated startup client remains 440 by 284 DIPs");
        Assert(window.RequestedThemeVariant == (theme == 0 ? ThemeVariant.Dark : ThemeVariant.Light)
            && EditorLocalization.CurrentLanguage == language, label + ": ApplyPreferences applies the requested theme and language without saving");
        Assert(window.Background is ISolidColorBrush background && background.Color == (theme == 0 ? Colors.Black : Colors.White),
            label + ": the large surface uses the selected pure black or pure white");
        var close = Find<Button>("CloseStartup");
        var title = window.GetVisualDescendants().OfType<TextBlock>().Single(value => value.Text == "MDA · MikuDance Advance");
        var logo = Find<Border>("BrandLogo"); var brand = Find<StackPanel>("BrandContent");
        var status = Find<TextBlock>("StartupStatus"); var hint = Find<TextBlock>("EscapeHint");
        Assert(WithinWindow(title) && RootBounds(title).Bottom <= 32.5 && WithinWindow(close) && RootBounds(close).Bottom <= 32.5
            && !RootBounds(title).Intersects(RootBounds(close)), label + ": title and close button fit the 32-DIP top rail without overlap");
        Assert(WithinWindow(brand) && WithinWindow(logo) && logo.Background is ImageBrush { Source: not null }
            && logo.Bounds.Width == 300 && logo.Bounds.Height == 138 && logo.Opacity == 1,
            label + ": the actual branded image is visible at its intended size after its brief entrance");
        Assert(window.GetVisualDescendants().OfType<LoadingDots>().SequenceEqual([dots]) && dots.IsActive && WithinWindow(dots),
            label + ": StartupDots is the single truthful active five-dot control");
        Assert(Text(status) == Catalog(language, "Mda.Text.380bd4398b9e") && Text(hint) == Catalog(language, "Mda.Text.412bd6bd4b88")
            && Equals(close.Content, Catalog(language, "Mda.Text.3fd47edce45b")), label + ": real startup status, Escape hint and close action are localized");
        Assert(WithinWindow(status) && WithinWindow(hint) && !RootBounds(dots).Intersects(RootBounds(status))
            && !RootBounds(status).Intersects(RootBounds(hint)) && RootBounds(status).Top >= 253.5,
            label + ": dot, localized status and Escape hint fit the bottom rail without collision");
        CheckTextFits(title, label); CheckTextFits(status, label); CheckTextFits(hint, label); CheckButtonFits(close, label);
        var animationSamples = SampleDots(dots, 5);
        Assert(logo.Opacity == 1 && Field("logoEntrance") is null && LocalAnimatedControls().All(value => ReferenceEquals(value, dots)),
            label + ": logo stays still and only StartupDots retains a continuous local animation after entrance");
        if (actualAnimationPreference)
            Assert(ClockRunning(dots) && animationSamples.Distinct().Count() >= 2, label + ": enabled native dot progress changes across real render frames");
        else Assert(ClockStopped(dots) && animationSamples.All(value => value == 0), label + ": actual reduced-motion preference starts with steady active dots");
        Capture(label + "-startup-controls.png");
        dots.AnimationsEnabled = false; PumpFrames(3);
        var steady = SampleDots(dots, 3);
        Assert(dots.IsActive && ClockStopped(dots) && steady.All(value => value == 0), label + ": disabling animations keeps an active static indicator with no clock");
        dots.AnimationsEnabled = actualAnimationPreference; PumpFrames(3);
        Assert(dots.AnimationsEnabled == actualAnimationPreference && dots.IsActive
            && (actualAnimationPreference ? ClockRunning(dots) : ClockStopped(dots)), label + ": the control restores the actual animation preference");

        const string marker = "owned-startup-presentation-error-marker";
        var error = new InvalidOperationException(marker + Environment.NewLine + new string('W', 512) + " 日本語の技術原文",
            new IOException("owned-inner-error: 原文 remains verbatim"));
        Invoke("ShowFailure", error); PumpLayout();
        var message = Find<TextBlock>("FailureMessage"); var details = Find<SelectableTextBlock>("FailureDetails");
        var expander = Find<Expander>("FailureDetailsExpander"); var failureClose = Find<Button>("FailureClose");
        var copy = Find<Button>("CopyFailureDetails"); var failureContent = Find<StackPanel>("FailureContent");
        Assert(!Visible(brand) && !Visible(Find<Grid>("NormalRail")) && Visible(failureContent) && Visible(Find<Grid>("FailureActions")),
            label + ": ShowFailure replaces the brand and busy rail with actual failure content and actions");
        Assert(!dots.IsActive && ClockStopped(dots) && Field("logoEntrance") is null, label + ": failure releases native dot and logo animation clocks");
        Assert(Text(message) == Catalog(language, "Mda.Text.91cb43b44b53") && !Text(message).Contains(marker, StringComparison.Ordinal)
            && Equals(ToolTip.GetTip(message), Text(message)), label + ": localized failure summary is separate from technical exception text");
        Assert(details.Text == error.ToString() && details.Text?.Contains(marker, StringComparison.Ordinal) == true && !expander.IsExpanded && !Visible(details),
            label + ": complete original exception is retained in initially collapsed technical details");
        Assert(Text(Find<TextBlock>("FailureTitle")) == Catalog(language, "Mda.Text.0b8f4e683ce7")
            && Equals(expander.Header, Catalog(language, "Mda.Text.c26fb419db3c"))
            && Text(Find<TextBlock>("StartupStopped")) == Catalog(language, "Mda.Text.6ed4c4b1d712")
            && Equals(copy.Content, Catalog(language, "Mda.Text.347aa91cd870"))
            && Equals(failureClose.Content, Catalog(language, "Mda.Text.3fd47edce45b")), label + ": real failure title, details header and actions use the requested language");
        Assert(failureClose.IsFocused, label + ": the real failure handler focuses its close action");
        VerifyFailureLayout(label, expanded: false); Capture(label + "-failure-controls.png");
        var toggle = expander.GetVisualDescendants().OfType<ToggleButton>().Single(value => value.Name == "PART_toggle");
        Assert(Visible(toggle) && toggle.IsChecked == false, label + ": the expander's actual disclosure toggle is initially collapsed");
        CheckButtonFits(toggle, label);
        Activate(toggle); Assert(expander.IsExpanded && toggle.IsChecked == true && Visible(details), label + ": keyboard activation of the actual template toggle expands and shows technical details");
        Assert(details.Text == error.ToString(), label + ": opening details preserves every original technical character");
        VerifyFailureLayout(label, expanded: true); Capture(label + "-failure-details-controls.png");
        Activate(toggle); Assert(!expander.IsExpanded && toggle.IsChecked == false && !Visible(details), label + ": activating the same disclosure toggle collapses details again");
        Activate(toggle); Assert(expander.IsExpanded && Visible(details), label + ": the actual disclosure remains usable after repeated toggling");

        // Isolated resource-cleanup contract: starting this owned timer does not invoke the
        // copy handler or touch the clipboard. Clipboard success/failure is outside this probe.
        var feedbackTimer = Field("copyFeedbackTimer") as DispatcherTimer ?? throw new MissingFieldException("copyFeedbackTimer");
        Assert(!feedbackTimer.IsEnabled, label + ": failure leaves no copy-feedback timer running");
        feedbackTimer.Start(); Assert(feedbackTimer.IsEnabled, label + ": owned feedback timer is active for the separate close-cleanup contract");
        if (theme == 0)
        {
            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            toggle.RaiseEvent(escape); PumpLayout();
            Assert(escape.Handled && !window.IsVisible, label + ": routed Escape from the disclosure closes the owned failure window");
        }
        else
        {
            Activate(failureClose);
            Assert(!window.IsVisible, label + ": keyboard activation of the real Close action closes the owned failure window");
        }
        Assert(!feedbackTimer.IsEnabled && !dots.IsActive && ClockStopped(dots) && Field("logoEntrance") is null,
            label + ": Closed releases owned timer and all startup animation resources");
        Scenarios.Add(new { language, theme = theme == 0 ? "black" : "white", width = window.Bounds.Width, height = window.Bounds.Height,
            actualAnimationPreference, animationSamples, staticSamples = steady, localizedSummary = message.Text,
            fullExceptionPreserved = details.Text == error.ToString(), closeRoute = theme == 0 ? "routed Escape" : "real Close key activation",
            clipboardInvoked = false, feedbackTimerStartedOnlyForCleanupContract = true });
        window = null;
    }

    private static void VerifyFailureLayout(string label, bool expanded)
    {
        var content = Find<StackPanel>("FailureContent"); var actions = Find<Grid>("FailureActions");
        Assert(WithinWindow(content) && RootBounds(content).Top >= 31.5 && RootBounds(content).Bottom <= 254.5
            && WithinWindow(actions) && RootBounds(actions).Top >= 253.5 && !RootBounds(content).Intersects(RootBounds(actions)),
            label + $": failure content stays between rails with details {(expanded ? "expanded" : "collapsed")}");
        var title = Find<TextBlock>("FailureTitle"); var message = Find<TextBlock>("FailureMessage");
        var expander = Find<Expander>("FailureDetailsExpander");
        Assert(!RootBounds(title).Intersects(RootBounds(message)) && !RootBounds(message).Intersects(RootBounds(expander)),
            label + ": title, complete localized summary and details disclosure have separate layout rows");
        CheckTextFits(title, label); CheckTextFits(message, label); CheckTextFits(Find<TextBlock>("StartupStopped"), label);
        CheckButtonFits(Find<Button>("CopyFailureDetails"), label); CheckButtonFits(Find<Button>("FailureClose"), label);
        var stopped = Find<TextBlock>("StartupStopped"); var copy = Find<Button>("CopyFailureDetails"); var close = Find<Button>("FailureClose");
        Assert(WithinWindow(stopped) && WithinWindow(copy) && WithinWindow(close) && !RootBounds(stopped).Intersects(RootBounds(copy))
            && !RootBounds(copy).Intersects(RootBounds(close)), label + ": localized stopped, Copy and Close labels fit without collisions");
        if (expanded)
        {
            var scroll = expander.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert(Visible(scroll) && WithinWindow(scroll) && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto
                && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, label + ": long original technical text is bounded by the real scroll viewer");
        }
    }

    private static void VerifyActiveClose()
    {
        window = NewWindow("zh-CN", 0); window.Show(); PumpLayout();
        var dots = Find<LoadingDots>("StartupDots"); bool actualPreference = dots.AnimationsEnabled;
        dots.AnimationsEnabled = true; PumpFrames(3);
        Assert(dots.IsActive && ClockRunning(dots), "separate active-close contract starts with the owned startup dot clock running");
        window.Close(); PumpLayout();
        Assert(!window.IsVisible && !dots.IsActive && ClockStopped(dots) && Field("logoEntrance") is null
            && Field("copyFeedbackTimer") is DispatcherTimer { IsEnabled: false },
            "closing the owned normal startup clears active state, logo entrance and feedback timer");
        dots.AnimationsEnabled = actualPreference;
        Assert(dots.AnimationsEnabled == actualPreference && ClockStopped(dots),
            "the closed owned control restores its actual animation preference without restarting a clock");
        window = null;
    }

    private static EditorStartupWindow NewWindow(string language, int theme)
    {
        var owned = new EditorStartupWindow { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-20000, -20000) };
        (typeof(EditorStartupWindow).GetMethod("ApplyPreferences", InstanceMembers, null, [typeof(EditorPreferences)], null)
            ?? throw new MissingMethodException("ApplyPreferences(EditorPreferences)"))
            .Invoke(owned, [new EditorPreferences { Language = language, ThemeIndex = theme }]);
        return owned;
    }
    private static void Invoke(string name, Exception error) => (typeof(EditorStartupWindow).GetMethod(name, InstanceMembers, null, [typeof(Exception)], null)
        ?? throw new MissingMethodException(name + "(Exception)" )).Invoke(window, [error]);
    private static object? Field(string name) => (typeof(EditorStartupWindow).GetField(name, InstanceMembers)
        ?? throw new MissingFieldException(typeof(EditorStartupWindow).FullName, name)).GetValue(window);
    private static T Find<T>(string name) where T : Control => window!.FindControl<T>(name) ?? throw new InvalidOperationException("Missing product control: " + name);
    private static string Text(TextBlock block) => block.Text ?? "";
    private static string Catalog(string language, string key) => Catalogs[language][key];
    private static bool ClockRunning(LoadingDots dots) => dots.IsAnimating(progressProperty!) && dots.Classes.Contains(":animating");
    private static bool ClockStopped(LoadingDots dots) => !dots.IsAnimating(progressProperty!) && !dots.Classes.Contains(":animating");
    private static IEnumerable<Control> LocalAnimatedControls() => window!.GetVisualDescendants().OfType<Control>().Prepend(window!)
        .Where(control => control.Styles.OfType<Style>().Any(style => style.Animations.Count > 0));
    private static bool Visible(Control control) => ReferenceEquals(control.GetVisualRoot(), window) && control.IsVisible
        && control.GetVisualAncestors().All(parent => parent.IsVisible);
    private static Rect RootBounds(Control control) => new(control.TranslatePoint(new Point(), window!)
        ?? throw new InvalidOperationException("Cannot map " + control.Name + " into owned startup window."), control.Bounds.Size);
    private static bool WithinWindow(Control control)
    {
        if (!Visible(control) || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        var bounds = RootBounds(control);
        return bounds.X >= -.5 && bounds.Y >= -.5 && bounds.Right <= window!.Bounds.Width + .5 && bounds.Bottom <= window.Bounds.Height + .5;
    }
    private static void CheckTextFits(TextBlock actual, string label)
    {
        Assert(WithinWindow(actual), label + ": actual visible text stays inside client bounds: " + (actual.Name ?? actual.Text));
        // Measure the same text/font without trimming or line limits so ellipsis cannot hide
        // a clipped English/Japanese label. Captures always render the actual product control.
        var natural = new TextBlock { Text = actual.Text, FontFamily = actual.FontFamily, FontSize = actual.FontSize,
            FontWeight = actual.FontWeight, FontStyle = actual.FontStyle, FontStretch = actual.FontStretch,
            LetterSpacing = actual.LetterSpacing, LineHeight = actual.LineHeight, LineSpacing = actual.LineSpacing,
            Padding = actual.Padding, FlowDirection = actual.FlowDirection, TextWrapping = actual.TextWrapping,
            TextTrimming = TextTrimming.None, MaxLines = 0 };
        natural.Measure(new Size(actual.Bounds.Width, double.PositiveInfinity));
        Assert(natural.TextLayout.WidthIncludingTrailingWhitespace + actual.Padding.Left + actual.Padding.Right <= actual.Bounds.Width + 1
            && natural.TextLayout.Height + actual.Padding.Top + actual.Padding.Bottom <= actual.Bounds.Height + 1,
            label + ": localized text fits without relying on ellipsis: " + (actual.Name ?? actual.Text));
        natural.TextLayout.Dispose();
    }
    private static void CheckButtonFits(Button button, string label)
    {
        Assert(WithinWindow(button), label + ": real action fits inside client bounds: " + button.Name);
        var labels = button.GetVisualDescendants().OfType<TextBlock>().ToArray();
        Assert(labels.Length > 0, label + ": real action template contains a rendered text label: " + button.Name);
        foreach (var text in labels) CheckTextFits(text, label + "/" + button.Name);
    }
    private static void Activate(Button button)
    {
        Assert(Visible(button) && button.IsEnabled, "actual product control is usable: " + button.Name);
        button.Focus();
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
        PumpLayout();
    }
    private static double[] SampleDots(LoadingDots dots, int count)
    {
        var samples = new List<double>();
        for (int index = 0; index < count; index++) { PumpFrames(1); samples.Add(dots.GetValue(progressProperty!)); }
        return samples.ToArray();
    }
    private static void WaitUntil(Func<bool> predicate, string label)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.Elapsed.TotalSeconds > 10) throw new TimeoutException(label); PumpFrames(1); }
    }
    private static void PumpLayout() { Dispatcher.UIThread.RunJobs(); window?.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void PumpFrames(int count)
    {
        int remaining = count; var frame = new DispatcherFrame(); Exception? failure = null; bool active = true;
        void Next(TimeSpan _)
        {
            if (!active) return;
            if (--remaining <= 0) frame.Continue = false;
            else Dispatcher.UIThread.Post(() => { if (active) window!.RequestAnimationFrame(Next); }, DispatcherPriority.Background);
        }
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => { failure = new TimeoutException("Owned startup render frames did not arrive."); frame.Continue = false; };
        window!.RequestAnimationFrame(Next); timeout.Start();
        try { Dispatcher.UIThread.PushFrame(frame); } finally { active = false; timeout.Stop(); }
        PumpLayout(); if (failure is not null) throw failure;
    }
    private static void Capture(string name)
    {
        window!.UpdateLayout(); double scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
            (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, name)); Captures.Add(name);
    }
    private static KeyValuePair<string, string> ParseOption(string text)
    {
        if (!text.StartsWith("--", StringComparison.Ordinal) || !text.Contains('=')) throw new ArgumentException("Expected --name=value: " + text);
        var parts = text[2..].Split('=', 2); return new(parts[0], parts[1]);
    }
    private static string Required(IReadOnlyDictionary<string, string> options, string name) => options.TryGetValue(name, out var value)
        ? value : throw new ArgumentException("Missing --" + name + ".");
    private static string Input(string path) => Path.IsPathFullyQualified(path) && File.Exists(path) ? Path.GetFullPath(path)
        : throw new ArgumentException("Input must be an existing absolute file: " + path);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static FileState ReadState(string path) => File.Exists(path) ? new(true, new FileInfo(path).Length, Hash(path)) : new(false, 0, null);
    private static void VerifyPreferences()
    {
        foreach (var pair in PreferencesBefore)
        {
            var after = ReadState(pair.Key); PreferencesAfter[pair.Key] = after;
            Assert(after == pair.Value, "machine-local file existence/length/SHA256 remains unchanged: " + pair.Key);
        }
    }
    private static void ConfigureProduct(IReadOnlyDictionary<string, string> options)
    {
        expectedApp = Input(Required(options, "expected-app")); expectedAppSha = Required(options, "expected-app-sha256");
        productRoot = Path.GetDirectoryName(expectedApp)!;
        if (expectedAppSha.Length != 64 || !expectedAppSha.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid expected App SHA256.");
        foreach (string path in Directory.EnumerateFiles(productRoot, "Mmd.Modern.*.dll", SearchOption.TopDirectoryOnly)) ProductHashes.Add(path, Hash(path));
        if (options.TryGetValue("expected-product-manifest", out var manifestPath))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Input(manifestPath)));
            foreach (var file in manifest.RootElement.EnumerateArray())
            {
                string path = Path.GetFullPath(Path.Combine(productRoot, file.GetProperty("path").GetString()!));
                if (!path.StartsWith(productRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload manifest leaves product root.");
                ProductHashes[path] = file.GetProperty("sha256").GetString()!;
            }
        }
    }
    private static void VerifyProduct(string phase)
    {
        if (expectedApp.Length == 0) return;
        foreach (var pair in ProductHashes)
            Assert(File.Exists(pair.Key) && Hash(pair.Key).Equals(pair.Value, StringComparison.OrdinalIgnoreCase), phase + ": published file SHA256 is preserved: " + Path.GetRelativePath(productRoot, pair.Key));
        string actualApp = Path.GetFullPath(typeof(App).Assembly.Location);
        Assert(actualApp.Equals(expectedApp, StringComparison.OrdinalIgnoreCase) && Hash(actualApp).Equals(expectedAppSha, StringComparison.OrdinalIgnoreCase),
            phase + ": probe directly loads the supplied published App DLL at its expected SHA256");
        LoadedAssemblies.Clear();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(value => value.GetName().Name?.StartsWith("Mmd.Modern.", StringComparison.Ordinal) == true))
        {
            string path = Path.GetFullPath(assembly.Location), hash = Hash(path);
            Assert(Path.GetDirectoryName(path)!.Equals(productRoot, StringComparison.OrdinalIgnoreCase), phase + ": loaded product assembly comes from supplied payload: " + assembly.GetName().Name);
            Assert(ProductHashes.TryGetValue(path, out var expected) && hash.Equals(expected, StringComparison.OrdinalIgnoreCase), phase + ": loaded product assembly SHA256 is preserved: " + assembly.GetName().Name);
            LoadedAssemblies.Add(new { name = assembly.GetName().Name, path, sha256 = hash });
        }
    }
    private static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? inner : error;
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); Checks.Add(message); }
    private static void WriteReport(Exception? failure) => File.WriteAllText(Path.Combine(output, "startup-presentation-checks.json"), JsonSerializer.Serialize(new
    {
        success = failure is null, passed = Checks.Count, checks = Checks, captures = Captures, scenarios = Scenarios,
        catalogs = CatalogEvidence, expectedApp, expectedAppSha256 = expectedAppSha, loadedProductAssemblies = LoadedAssemblies,
        publishedPayloadFilesChecked = ProductHashes.Count, preferenceFilesBefore = PreferencesBefore, preferenceFilesAfter = PreferencesAfter,
        scope = "Real published EditorStartupWindow controls instantiated after SetupWithoutStarting; reflection calls only ApplyPreferences and ShowFailure plus private clock/timer observation. No preferences Save and no machine-local file restoration.",
        screenshotScope = "RenderTargetBitmap of actual 440x284 startup controls in zh-CN/en/ja and black/white, including original branded ImageBrush, collapsed failure and expanded original technical text. These are control captures, not OS composited full-window captures or human visual approval.",
        animationScope = "Native RequestAnimationFrame sampling. Only owned LoadingDots.AnimationsEnabled is temporarily changed and restored; the Windows reduced-motion setting is never changed. The owned copy-feedback timer is started only for close cleanup, without invoking clipboard code.",
        untested = new[] { "Ordinary App startup state machine, GPU first frame and cancellation: use EditorStartupAnimationProbe.",
            "OS clipboard success/failure and CopyFailureDetails async feedback path were not invoked.",
            "OS keyboard injection, title dragging, OS composited capture and final human visual approval were not exercised.",
            "User names are verified as unchanged format values; this presentation probe imports no model or project." },
        failure = failure?.ToString()
    }, Json));
    private sealed record FileState(bool Exists, long Length, string? Sha256);
}
