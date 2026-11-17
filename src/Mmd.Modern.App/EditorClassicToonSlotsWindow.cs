using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>对应原版 MMD Toon01–10 并列设置的十槽界面。只改当前模型在各共享槽上的图片，
/// 不改 PMX 的 ToonMode／编号；所有修改都走工作区现有的可撤销 Toon 事务。</summary>
internal sealed class EditorClassicToonSlotsWindow : Window
{
    private sealed record ModelRow(Guid Id, string Name) { public override string ToString() => Name; }
    private sealed class SlotCard
    {
        internal required int Slot { get; init; }
        internal required Border Root { get; init; }
        internal required Image Preview { get; init; }
        internal required TextBlock Source { get; init; }
        internal CommonSlotState? State { get; set; }
    }

    internal const int Columns = 5;
    private readonly EditorProjectWorkspace workspace;
    private readonly ComboBox models = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 220 };
    private readonly ListBox slots = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button replace = new() { MinWidth = 96 };
    private readonly Button restore = new() { MinWidth = 96 };
    private readonly Button close;
    private readonly SlotCard[] cards = new SlotCard[10];
    private readonly Dictionary<SceneTexture, WriteableBitmap> previews = new(ReferenceEqualityComparer.Instance);
    private bool preparing;
    private static string T(string key) => EditorLocalization.Text("Mda.Text.Mme." + key);

    internal EditorClassicToonSlotsWindow(Window owner, EditorProjectWorkspace workspace, Guid? preferredModel = null)
    {
        this.workspace = workspace;
        var session = workspace.Session ?? throw new InvalidOperationException("Open a Classic project first.");
        if (workspace.ProjectMetadata is not EditorClassicDocumentState { Toons: not null })
            throw new InvalidOperationException("Classic toon resources are not prepared.");
        Title = T("ToonSlots.Title"); Width = 760; Height = 580; MinWidth = 700; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        RequestedThemeVariant = owner.ActualThemeVariant; FontFamily = owner.FontFamily; FontSize = 14;
        UseLayoutRounding = true; EditorDialogTheme.Apply(this, owner);
        IBrush Brush(string key, string fallback) => owner.TryFindResource(key, out var value) && value is IBrush brush
            ? brush : new SolidColorBrush(Color.Parse(fallback));
        var border = Brush("EditorBorder", "#2E3B48");

        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,Auto,*,Auto,Auto,Auto"), RowSpacing = 12 };
        root.Children.Add(new TextBlock { Text = T("ToonSlots.Help"), TextWrapping = TextWrapping.Wrap });
        var modelRow = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        modelRow.Children.Add(new TextBlock { Text = T("ToonSlots.Model"), VerticalAlignment = VerticalAlignment.Center });
        modelRow.Children.Add(At(models, 1));
        root.Children.Add(modelRow); Grid.SetRow(modelRow, 1);

        // 原版是十个槽并列；这里按 5×2 排列。先点选一个槽，再用下方按钮替换或恢复。
        slots.ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = Columns, Rows = 2 });
        ScrollViewer.SetHorizontalScrollBarVisibility(slots, ScrollBarVisibility.Disabled);
        for (int index = 0; index < cards.Length; index++)
        {
            int slot = index + 1;
            var preview = new Image { Width = 40, Height = 40, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapInterpolationMode(preview, BitmapInterpolationMode.None);
            var source = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            cards[index] = new SlotCard
            {
                Slot = slot, Preview = preview, Source = source,
                Root = new Border
                {
                    Padding = new(2, 4), Child = new StackPanel
                    {
                        Spacing = 4, Children =
                        {
                            new TextBlock { Text = $"Toon{slot:00}", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                            new Border { Width = 42, Height = 42, BorderBrush = border, BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Center, Child = preview },
                            source
                        }
                    }
                }
            };
        }
        slots.ItemsSource = cards.Select(card => card.Root).ToArray();
        slots.SelectionChanged += (_, _) => RefreshActions();
        root.Children.Add(slots); Grid.SetRow(slots, 2);

        replace.Content = T("ToonSlots.Replace"); restore.Content = T("ToonSlots.Restore");
        replace.Click += async (_, _) => { if (SelectedSlot is { } slot) await ReplaceAsync(slot); };
        restore.Click += async (_, _) => { if (SelectedSlot is { } slot) await RestoreAsync(slot); };
        var actions = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 10 };
        actions.Children.Add(detail); actions.Children.Add(At(replace, 1)); actions.Children.Add(At(restore, 2));
        root.Children.Add(actions); Grid.SetRow(actions, 3);
        root.Children.Add(status); Grid.SetRow(status, 4);
        close = new Button { Content = T("Close"), IsCancel = true, MinWidth = 88 };
        close.Click += (_, _) => Close();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10, Children = { close } };
        root.Children.Add(footer); Grid.SetRow(footer, 5);
        Content = root;

        Closing += (_, args) => { if (preparing) args.Cancel = true; };
        Closed += (_, _) => { foreach (var bitmap in previews.Values) bitmap.Dispose(); previews.Clear(); };
        var rows = session.Objects.Select(value => new ModelRow(value.Id, value.Name)).ToArray();
        models.ItemsSource = rows;
        models.SelectionChanged += (_, _) => Refresh();
        models.SelectedIndex = rows.Length == 0 ? -1
            : Math.Max(0, Array.FindIndex(rows, row => row.Id == preferredModel));
        if (rows.Length == 0) { status.Text = T("ToonSlots.NoModels"); Refresh(); }
        else if (workspace.ClassicToonResources is null) status.Text = T("ToonSlots.NoOriginals");
    }

    internal Guid? SelectedModelId => (models.SelectedItem as ModelRow)?.Id;
    internal int? SelectedSlot => slots.SelectedIndex is >= 0 and < 10 ? slots.SelectedIndex + 1 : null;
    internal void SelectSlot(int slot) => slots.SelectedIndex = slot - 1;

    private static TControl At<TControl>(TControl control, int column) where TControl : Control
    { Grid.SetColumn(control, column); return control; }

    private void Refresh()
    {
        IReadOnlyList<CommonSlotState>? states = null;
        // 由选择变化和 RunAsync 的 finally 调用，异常不能逃出事件处理器；读不出的模型按“未使用”显示。
        try
        {
            if (models.SelectedItem is ModelRow row && workspace.Session is { } session
                && workspace.ProjectMetadata is EditorClassicDocumentState { Toons: { } toons })
            {
                var model = session.CaptureAuthoredContent().Motions.Single(value => value.ObjectId == row.Id).Model;
                states = toons.DescribeCommonSlots(row.Id, model);
            }
        }
        catch (Exception error) { states = null; status.Text = error.Message; }
        foreach (var card in cards)
        {
            var state = card.State = states?[card.Slot - 1];
            bool used = state?.Used == true;
            card.Preview.Source = state?.Image is { } image ? Preview(image) : null;
            card.Source.Text = !used ? T("ToonSlots.Unused") : T(state!.Replaced ? "ToonSlots.Replaced" : "ToonSlots.Default");
            // Unused slots stay legible: a muted caption and a lighter frame instead of fading the whole card.
            card.Root.Opacity = 1;
            card.Source.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(used ? "EditorText" : "EditorMuted"));
            card.Preview.Opacity = used ? 1 : 0.55;
        }
        // 默认选中本模型第一个用到的槽；切换模型时，原来选中的槽仍被使用就保持不变。
        if (SelectedSlot is not { } selected || cards[selected - 1].State?.Used != true)
            slots.SelectedIndex = Math.Max(0, Array.FindIndex(cards, card => card.State?.Used == true));
        RefreshActions();
    }

    private void RefreshActions()
    {
        var state = SelectedSlot is { } slot ? cards[slot - 1].State : null;
        bool used = state?.Used == true, originals = workspace.ClassicToonResources is not null;
        detail.Text = SelectedSlot is not { } selected ? ""
            : $"Toon{selected:00} · " + (used
                ? EditorLocalization.Format("Mda.Text.Mme.ToonSlots.Materials", state!.MaterialIndices.Count)
                : T("ToonSlots.Unused"));
        replace.IsEnabled = used && !preparing;
        restore.IsEnabled = used && state!.Replaced && originals && !preparing;
        ToolTip.SetTip(restore, originals ? null : T("ToonSlots.NoOriginals"));
        ToolTip.SetShowOnDisabled(restore, true);
        models.IsEnabled = slots.IsEnabled = close.IsEnabled = !preparing;
    }

    private async Task ReplaceAsync(int slot)
    {
        if (preparing || SelectedModelId is not { } id || cards[slot - 1].State is not { Used: true } state) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = T("ToonReplace"), AllowMultiple = false,
                FileTypeFilter = [new(T("Images")) { Patterns = ["*.bmp", "*.png", "*.jpg", "*.jpeg", "*.tga", "*.dds"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            await RunAsync(() => workspace.ApplyClassicToonAsync(id, state.MaterialIndices, path), "ToonApplied");
        }
        catch (Exception error) { status.Text = error.Message; }
    }

    internal Task RestoreAsync(int slot)
        => SelectedModelId is { } id && !preparing
            ? RunAsync(() => workspace.RestoreClassicToonAsync(id, slot), "ToonSlots.Restored") : Task.CompletedTask;

    /// <summary>执行一次工作区 Toon 事务并刷新十个槽；失败时只在状态行报告原因。</summary>
    internal async Task RunAsync(Func<Task> edit, string doneKey)
    {
        preparing = true; RefreshActions(); status.Text = T("Preparing");
        try { await edit(); status.Text = T(doneKey); }
        catch (Exception error) { status.Text = error.Message; }
        finally { preparing = false; Refresh(); }
    }

    private WriteableBitmap Preview(SceneTexture texture)
    {
        if (previews.TryGetValue(texture, out var cached)) return cached;
        // SceneTexture 是直通 alpha 的 RGBA8；逐行拷贝以适配位图的行跨度。
        var bitmap = new WriteableBitmap(new PixelSize(texture.Width, texture.Height), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var buffer = bitmap.Lock())
        {
            int row = texture.Width * 4;
            byte[] pixels = texture.Rgba.ToArray();
            for (int y = 0; y < texture.Height; y++)
                Marshal.Copy(pixels, y * row, buffer.Address + y * buffer.RowBytes, row);
        }
        previews.Add(texture, bitmap);
        return bitmap;
    }
}
