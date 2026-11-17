using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

internal static class EditorDialogs
{
    /// <param name="destructive">确认操作会删除内容时为 true，确认按钮显示为危险色。</param>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string action = "确认", bool destructive = false) =>
        await ChooseAsync(owner, title, message, ["取消", action], destructive ? -1 : 1, destructive ? 1 : -1) == 1;

    public static async Task<ModelRole?> ChooseRoleAsync(Window owner, string file)
    {
        int choice = await ChooseAsync(owner, "模型用途", System.IO.Path.GetFileName(file) + EditorLocalization.Text("\n请选择此实例的用途，用于正确应用光照与反射策略。"),
            ["取消", "角色", "场景", "道具"]);
        return choice switch { 1 => ModelRole.Character, 2 => ModelRole.Environment, 3 => ModelRole.Prop, _ => null };
    }

    /// <summary>One explicit per-instance purpose choice. No name-based classification or follow-up lighting dialog.</summary>
    public static Task<EditorModelImportOptions?> ChooseModelImportAsync(Window owner, string file, int nativeLightCount)
    {
        if (nativeLightCount is < 0 or > SceneLightingSnapshot.MaxLights)
            throw new ArgumentOutOfRangeException(nameof(nativeLightCount));
        bool lightsFull = nativeLightCount == SceneLightingSnapshot.MaxLights;
        var dialog = new Window
        {
            Title = EditorLocalization.Text("Mda.Text.Context.Room.ImportTitle"), Width = 560,
            SizeToContent = SizeToContent.Height, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant,
            FontFamily = owner.FontFamily, FontSize = 14,
            UseLayoutRounding = true
        };
        EditorDialogTheme.Apply(dialog, owner);
        var choices = new (ModelPurpose Purpose, string Label, string Help)[]
        {
            (new(ModelRole.Character), "角色", "Mda.Text.Context.Room.CharacterHelp"),
            (new(ModelRole.Environment, true), "Mda.Text.Context.Room.IndoorChoice", "Mda.Text.Context.Room.IndoorImportHelp"),
            (new(ModelRole.Environment, false), "Mda.Text.Context.Room.MmdEnvironmentChoice", "Mda.Text.Context.Room.MmdEnvironmentHelp"),
            (new(ModelRole.Prop), "道具", "Mda.Text.Context.Room.PropHelp")
        };
        int selected = -1;
        var explanation = new TextBlock
        {
            Name = "IndoorImportLightingNote", TextWrapping = TextWrapping.Wrap, LineHeight = 20,
            IsVisible = false
        };
        var apply = new Button
        {
            Name = "ImportModelPurposeButton", Content = EditorLocalization.Text("Mda.Text.Context.Room.ImportAction"),
            IsDefault = true, IsEnabled = false, MinWidth = 88, Padding = new Thickness(14, 6)
        };
        void RefreshChoice()
        {
            bool indoor = selected >= 0 && choices[selected].Purpose.EnvironmentIndoor == true;
            explanation.IsVisible = indoor;
            explanation.Text = EditorLocalization.Text(lightsFull
                ? "Mda.Text.Context.Room.LightsFullHelp" : "Mda.Text.Context.Room.BasicLightHelp");
            apply.IsEnabled = selected >= 0;
            apply.Content = EditorLocalization.Text(indoor && lightsFull
                ? "Mda.Text.Context.Room.ImportUsingLightsAction" : "Mda.Text.Context.Room.ImportAction");
        }
        var options = new StackPanel { Spacing = 12 };
        for (int index = 0; index < choices.Length; index++)
        {
            int choiceIndex = index;
            var choice = choices[index];
            var radio = new RadioButton
            {
                Name = "ModelImportPurpose" + index, GroupName = "ModelImportPurpose", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 2, Children =
                {
                    new TextBlock { Text = EditorLocalization.Text(choice.Label), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = EditorLocalization.Text(choice.Help), TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 18 }
                } }
            };
            AutomationProperties.SetName(radio, EditorLocalization.Text(choice.Label));
            radio.PropertyChanged += (_, change) =>
            {
                if (change.Property != RadioButton.IsCheckedProperty || radio.IsChecked != true) return;
                selected = choiceIndex; RefreshChoice();
            };
            options.Children.Add(radio);
        }
        var cancel = new Button { Content = EditorLocalization.Text("取消"), IsCancel = true, MinWidth = 72, Padding = new Thickness(14, 6) };
        cancel.Click += (_, _) => dialog.Close(null);
        apply.Click += (_, _) =>
        {
            if (selected < 0) return;
            var purpose = choices[selected].Purpose;
            dialog.Close(new EditorModelImportOptions(purpose, purpose.EnvironmentIndoor == true && !lightsFull));
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 20, Children =
            {
                new StackPanel { Spacing = 6, Children =
                {
                    new TextBlock { Text = System.IO.Path.GetFileName(file), TextTrimming = TextTrimming.CharacterEllipsis,
                        FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = EditorLocalization.Text("Mda.Text.Context.Room.ImportPrompt"), TextWrapping = TextWrapping.Wrap,
                        LineHeight = 20 }
                } },
                options, explanation,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, apply } }
            }
        };
        dialog.Opened += (_, _) => options.Children[0].Focus();
        return dialog.ShowDialog<EditorModelImportOptions?>(owner);
    }

    /// <param name="primary">推荐操作的序号：品牌绿、回车触发。-1 表示没有推荐项（例如几个并列的用途）。</param>
    /// <param name="danger">会删除内容的操作序号，显示为危险色；-1 表示没有。</param>
    public static Task<int> ChooseAsync(Window owner, string title, string message, IReadOnlyList<string> choices,
        int primary = -1, int danger = -1)
    {
        var dialog = new Window
        {
            Title = EditorLocalization.Text(title), Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant,
            FontFamily = owner.FontFamily, FontSize = 14, UseLayoutRounding = true
        };
        EditorDialogTheme.Apply(dialog, owner);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < choices.Count; i++)
        {
            int result = i;
            var button = new Button { Content = EditorLocalization.Text(choices[i]), MinWidth = 72, IsCancel = i == 0, IsDefault = i == primary };
            if (i == danger) button.Classes.Add("danger");
            button.Click += (_, _) => dialog.Close(result);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 24,
            Children = { new TextBlock { Text = EditorLocalization.Text(message), TextWrapping = TextWrapping.Wrap, LineHeight = 22 }, buttons }
        };
        return dialog.ShowDialog<int>(owner);
    }

    public static Task<string?> PromptAsync(Window owner, string title, string message, string initial = "")
    {
        var dialog = new Window { Title = EditorLocalization.Text(title), Width = 500, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant, FontFamily = owner.FontFamily, FontSize = 14, UseLayoutRounding = true };
        EditorDialogTheme.Apply(dialog, owner);
        var input = new TextBox { Text = initial, MaxLength = 256 };
        var cancel = new Button { Content = EditorLocalization.Text("取消"), IsCancel = true };
        var apply = new Button { Content = EditorLocalization.Text("确认"), IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(null);
        apply.Click += (_, _) => dialog.Close(input.Text ?? "");
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children =
        { new TextBlock { Text = EditorLocalization.Text(message), TextWrapping = TextWrapping.Wrap }, input,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, apply } } } };
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog<string?>(owner);
    }
}
