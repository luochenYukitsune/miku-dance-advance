using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace Mmd.Modern.App;

/// <summary>代码构建的对话框统一使用 <c>EditorDialogStyles.axaml</c>：按钮、列表、下拉框跟随深浅主题，
/// 默认操作用品牌绿，取消为扁平按钮，危险操作可加 <c>danger</c> 类。</summary>
internal static class EditorDialogTheme
{
    private static readonly Uri BaseUri = new("avares://Mmd.Modern.App/");
    private static readonly Uri Source = new("avares://Mmd.Modern.App/EditorDialogStyles.axaml");

    internal static T Apply<T>(T dialog, Window owner) where T : Window
    {
        dialog.RequestedThemeVariant = owner.ActualThemeVariant;
        dialog.Styles.Add(new StyleInclude(BaseUri) { Source = Source });
        dialog.ClearValue(Window.BackgroundProperty);
        dialog.ClearValue(Window.ForegroundProperty);
        return dialog;
    }
}
