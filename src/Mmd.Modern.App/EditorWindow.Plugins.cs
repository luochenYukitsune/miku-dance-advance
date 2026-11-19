using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    internal EditorPluginService? Plugins { get; private set; }

    internal void AttachPlugins(EditorPluginService plugins)
    {
        if (Plugins is not null) throw new InvalidOperationException("A plugin host is already attached to this editor.");
        Plugins = plugins;
        plugins.PluginsChanged += OnPluginsChanged;
        _shell?.AttachPluginService(plugins);
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
            await new EditorPluginsWindow(this, host, _shell?.Workspace).ShowDialog(this);
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private void OnPluginsChanged(object? sender, EventArgs args)
    {
        void Apply() => _shell?.OnPluginsChanged();
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    internal async ValueTask ReleasePluginsAsync()
    {
        if (Plugins is not { } service) return;
        service.PluginsChanged -= OnPluginsChanged;
        _shell?.AttachPluginService(null);
        Plugins = null;
        await service.DisposeAsync();
    }
}
