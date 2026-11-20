using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Mmd.Modern.App;

/// <summary>Product identity first, with the installation's complete license texts available locally.</summary>
internal sealed partial class EditorAboutWindow : Window
{
    private readonly EditorAboutInfo info;
    private readonly string applicationDirectory;
    private readonly Action<string> openOriginalDocument;
    private const string VisualCppLicense = "tools/upscaling/licenses/vc-runtime/Visual-C-V14-Runtime-2026-License.docx";
    internal const string SpecialThanksGitHubUrl = "https://github.com/LyaQanYi";
    private bool showingLicenses;
    private bool licensesLoaded;

    internal EditorAboutWindow() : this(EditorAboutInfo.Current, AppContext.BaseDirectory) { }

    internal EditorAboutWindow(EditorAboutInfo info, string applicationDirectory, Action<string>? openOriginalDocument = null)
    {
        this.info = info;
        this.applicationDirectory = Path.GetFullPath(applicationDirectory);
        this.openOriginalDocument = openOriginalDocument ?? OpenLocalDocument;
        InitializeComponent();
        RefreshText();
        EditorLocalization.Observe(this, static window => window.RefreshText());
        Opened += (_, _) =>
        {
            if (Owner is { } owner)
            {
                RequestedThemeVariant = owner.ActualThemeVariant;
                FontFamily = owner.FontFamily;
                Resources["AboutFont"] = owner.FontFamily;
            }
            this.FindControl<Button>("AboutOkButton")!.Focus();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Close(); args.Handled = true;
        };
    }

    private void RefreshText()
    {
        this.FindControl<TextBlock>("AboutVersion")!.Text = EditorLocalization.Format("Mda.Text.About.Version", info.Version);
        var build = this.FindControl<TextBlock>("AboutBuild")!;
        build.IsVisible = info.Build is not null;
        build.Text = info.Build is null ? null : EditorLocalization.Format("Mda.Text.About.Build", info.Build);
        this.FindControl<TextBlock>("AboutRuntime")!.Text = info.Runtime;
        this.FindControl<TextBlock>("AboutAuthor")!.Text = EditorLocalization.Format("Mda.Text.About.Author", "luochen Yukitsune");
        this.FindControl<Button>("AboutLicensesButton")!.Content = EditorLocalization.Text(
            showingLicenses ? "Mda.Text.About.Overview" : "Mda.Text.About.Licenses");
        if (SelectedNotice?.OriginalPath is not null) ShowSelectedLicense();
    }

    private void ToggleLicenses(object? sender, RoutedEventArgs args)
    {
        showingLicenses = !showingLicenses;
        this.FindControl<ScrollViewer>("AboutOverview")!.IsVisible = !showingLicenses;
        this.FindControl<Grid>("AboutLicensesPane")!.IsVisible = showingLicenses;
        if (showingLicenses)
        {
            LoadLicenses();
            this.FindControl<ComboBox>("AboutLicenseFiles")!.Focus();
        }
        RefreshText();
    }

    private void LoadLicenses()
    {
        if (licensesLoaded) return;
        var files = this.FindControl<ComboBox>("AboutLicenseFiles")!;
        var notices = new List<LocalNotice>();
        try
        {
            AddDirectory(applicationDirectory, recursive: false);
            AddDirectory(Path.Combine(applicationDirectory, "licenses"), recursive: true);
            AddDirectory(Path.Combine(applicationDirectory, "tools"), recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            this.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text =
                EditorLocalization.Format("Mda.Text.About.LicenseReadFailed", error.Message);
        }
        AddEmbedded("HarmonyOS Sans / LICENSE_Fonts", "Assets/Fonts/LICENSE_Fonts");
        AddEmbedded("Phosphor / LICENSE-Phosphor.txt", "Assets/Icons/LICENSE-Phosphor.txt");
        AddEmbedded("SukiUI / LICENSE-SukiUI.txt", "Assets/Icons/LICENSE-SukiUI.txt");
        foreach (var notice in notices.OrderBy(notice => notice.Name.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(notice => notice.Name, StringComparer.OrdinalIgnoreCase))
        {
            files.Items.Add(new ComboBoxItem
            {
                Theme = new ControlTheme(typeof(ComboBoxItem)),
                Content = new TextBlock { Text = notice.Name, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                Tag = notice
            });
        }
        licensesLoaded = true;
        if (files.ItemCount > 0) files.SelectedIndex = 0;

        void AddDirectory(string directory, bool recursive)
        {
            if (!Directory.Exists(directory)) return;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (string file in Directory.EnumerateFiles(directory, "*", options))
            {
                string path = file;
                string name = Path.GetRelativePath(applicationDirectory, path).Replace('\\', '/');
                if (name.Equals(VisualCppLicense, StringComparison.OrdinalIgnoreCase))
                    notices.Add(new(name, () => File.OpenRead(path), path));
                else if (IsLicenseText(path))
                    notices.Add(new(name, () => File.OpenRead(path)));
            }
        }

        void AddEmbedded(string name, string asset)
        {
            string filename = Path.GetFileName(asset);
            if (notices.Any(notice => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(notice.Name), filename))) return;
            notices.Add(new(name, () => AssetLoader.Open(new Uri("avares://Mmd.Modern.App/" + asset))));
        }
    }

    private static bool IsLicenseText(string path)
    {
        string filename = Path.GetFileName(path);
        string extension = Path.GetExtension(filename).ToLowerInvariant();
        // DOCX is a ZIP container, including when a different package names it LICENSE*.docx.
        // The shipped VC runtime original is handled explicitly above, never by StreamReader.
        return extension != ".docx" && (filename.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
            || filename.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
            || filename.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)
            || filename.Contains("NOTICE", StringComparison.OrdinalIgnoreCase) && extension is ".txt" or ".md" or "");
    }

    private LocalNotice? SelectedNotice =>
        (this.FindControl<ComboBox>("AboutLicenseFiles")!.SelectedItem as ComboBoxItem)?.Tag as LocalNotice;

    private void SelectLicense(object? sender, SelectionChangedEventArgs args) => ShowSelectedLicense();

    private void ShowSelectedLicense()
    {
        var notice = SelectedNotice;
        this.FindControl<Button>("AboutOpenOriginalButton")!.IsVisible = notice?.OriginalPath is not null;
        if (notice is null) return;
        try
        {
            using var source = notice.Open();
            if (notice.OriginalPath is { } path)
                this.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text =
                    EditorLocalization.Format("Mda.Text.About.OriginalDocument", path);
            else
            {
                using var reader = new StreamReader(source);
                this.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text = reader.ReadToEnd();
            }
            this.FindControl<ScrollViewer>("AboutLicenseScroll")!.Offset = default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            this.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text =
                EditorLocalization.Format("Mda.Text.About.LicenseReadFailed", error.Message);
        }
    }

    private void OpenOriginalLicense(object? sender, RoutedEventArgs args)
    {
        if (SelectedNotice is not { OriginalPath: { } path } notice) return;
        try
        {
            // Check the indexed original again: it may have been removed since selection.
            using (notice.Open()) { }
            openOriginalDocument(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            this.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text =
                EditorLocalization.Format("Mda.Text.About.LicenseReadFailed", error.Message);
        }
    }

    private static void OpenLocalDocument(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenSpecialThanksGitHub(object? sender, RoutedEventArgs args)
    {
        try { openOriginalDocument(SpecialThanksGitHubUrl); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { }
    }

    private void CloseAbout(object? sender, RoutedEventArgs args) => Close();
    private sealed record LocalNotice(string Name, Func<Stream> Open, string? OriginalPath = null);
}
