using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    internal EditorPluginService? Plugins { get; private set; }

    internal void AttachPlugins(EditorPluginService plugins)
    {
        if (Plugins is not null) throw new InvalidOperationException("A plugin host is already attached to this editor.");
        Plugins = plugins;
        this.FindControl<Avalonia.Controls.MenuItem>("PluginsMenuItem")!.IsEnabled = true;
    }

    private async void OpenPlugins(object? sender, RoutedEventArgs args)
    {
        if (_shell?.IsBusy == true) return;
        if (Plugins is null && !_persistPreferences) return;
        _shell?.StopPlayback();
        try
        {
            var host = Plugins;
            if (host is null)
            {
                host = EditorPluginService.CreateDefault();
                try { await host.InitializeAsync(); }
                catch { await host.DisposeAsync(); throw; }
                AttachPlugins(host);
            }
            await new EditorPluginsWindow(this, host).ShowDialog(this);
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    internal async ValueTask ReleasePluginsAsync()
    {
        if (Plugins is not { } service) return;
        await service.DisposeAsync();
        Plugins = null;
    }
}
