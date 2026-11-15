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

    private bool openingPlugins;

    private async void OpenPlugins(object? sender, RoutedEventArgs args)
    {
        if (_shell?.IsBusy == true || openingPlugins) return;
        if (Plugins is null && !_persistPreferences) return;
        _shell?.StopPlayback();
        // InitializeAsync yields the UI thread while the menu stays clickable. Without this latch a second click
        // created another plugin host that ran the enabled plugins again and was never disposed.
        openingPlugins = true;
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
        finally { openingPlugins = false; }
    }

    internal async ValueTask ReleasePluginsAsync()
    {
        if (Plugins is not { } service) return;
        await service.DisposeAsync();
        Plugins = null;
    }
}
