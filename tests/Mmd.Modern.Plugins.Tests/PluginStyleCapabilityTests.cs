using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Plugins.Contracts;
using Xunit;

namespace Mmd.Modern.Plugins.Tests;

public sealed class PluginStyleCapabilityTests
{
    private const string DynamicEntryType = "Mmd.Modern.TestStylePlugin.DynamicStylePlugin";

    [Fact(Timeout = 15_000)]
    public async Task Minimal_manifest_remains_backward_compatible_with_null_optional_metadata()
    {
        var workspace = new StylePluginWorkspace();
        workspace.AddFixturePlugin("minimal-style", writeOptionalMetadata: false);
        await using var manager = CreateManager();

        var refresh = await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken);
        Assert.True(refresh.Succeeded, refresh.Diagnostic);

        var plugin = Assert.Single(manager.Plugins);
        Assert.Equal(PluginState.Disabled, plugin.State);
        Assert.Null(plugin.Description);
        Assert.Null(plugin.Author);
        Assert.Null(plugin.ProvidedStyles);
        Assert.Null(plugin.LocalizedNames);
        Assert.Null(plugin.LocalizedDescriptions);
        Assert.Equal("Minimal Plugin", plugin.GetDisplayName("zh-CN"));
        Assert.Null(plugin.GetDescription("zh-CN"));
        Assert.Equal([PluginCapabilities.RenderingStyles], plugin.Capabilities);
    }

    [Fact(Timeout = 15_000)]
    public async Task Optional_manifest_metadata_populates_plugin_info_and_localization_helpers()
    {
        var workspace = new StylePluginWorkspace();
        workspace.AddFixturePlugin("rich-style", writeOptionalMetadata: true);
        await using var manager = CreateManager();

        var refresh = await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken);
        Assert.True(refresh.Succeeded, refresh.Diagnostic);

        var plugin = Assert.Single(manager.Plugins);
        Assert.Equal(PluginState.Disabled, plugin.State);
        Assert.Equal("Default description", plugin.Description);
        Assert.Equal("Plugin Author", plugin.Author);
        Assert.Equal(["cel-v1", "ink_2"], plugin.ProvidedStyles);
        Assert.Equal([PluginCapabilities.RenderingStyles], plugin.Capabilities);
        Assert.NotNull(plugin.LocalizedNames);
        Assert.NotNull(plugin.LocalizedDescriptions);

        Assert.Equal("风格插件", plugin.GetDisplayName("zh-CN"));
        Assert.Equal("风格插件", plugin.GetDisplayName("zh"));
        Assert.Equal("Style Plugin EN", plugin.GetDisplayName("en-US"));
        Assert.Equal("スタイルプラグイン", plugin.GetDisplayName("ja-JP"));
        Assert.Equal("Rich Style", plugin.GetDisplayName("fr-FR"));

        Assert.Equal("中文说明", plugin.GetDescription("zh-CN"));
        Assert.Equal("中文说明", plugin.GetDescription("zh"));
        Assert.Equal("English description", plugin.GetDescription("en-US"));
        Assert.Equal("日本語説明", plugin.GetDescription("ja-JP"));
        Assert.Equal("Default description", plugin.GetDescription("fr-FR"));
    }

    [Theory(Timeout = 15_000)]
    [InlineData("description-number")]
    [InlineData("description-null")]
    [InlineData("author-array")]
    [InlineData("localizedNames-array")]
    [InlineData("localizedNames-empty-value")]
    [InlineData("localizedNames-number-value")]
    [InlineData("localizedDescriptions-string")]
    [InlineData("localizedDescriptions-number-value")]
    [InlineData("providesStyles-string")]
    [InlineData("providesStyles-empty-slug")]
    [InlineData("providesStyles-non-ascii")]
    [InlineData("providesStyles-slash")]
    [InlineData("providesStyles-duplicate")]
    public async Task Malformed_optional_manifest_fields_are_rejected_as_incompatible(string caseId)
    {
        var workspace = new StylePluginWorkspace();
        var directory = workspace.AddRawManifestPlugin("invalid-meta", caseId);
        await using var manager = CreateManager();

        var refresh = await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken);
        Assert.True(refresh.Succeeded, refresh.Diagnostic);

        var plugin = Assert.Single(manager.Plugins);
        Assert.Equal(PluginState.Incompatible, plugin.State);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Diagnostic));
        Assert.False((await manager.EnableAsync("invalid-meta", TestContext.Current.CancellationToken)).Succeeded);
        Assert.False(File.Exists(Path.Combine(directory, "constructed.txt")));
    }

    [Fact(Timeout = 15_000)]
    public async Task Rendering_style_provider_registers_unregisters_cleanly_and_plugins_changed_fires_outside_gate()
    {
        var workspace = new StylePluginWorkspace();
        workspace.AddDynamicStylePlugin("style.cel");
        await using var manager = CreateManager();

        var events = new List<(string Phase, PluginState State, int StyleProviderCount)>();
        var probeGateOnNextEvent = false;
        var reentrantRefreshSucceeded = false;

        manager.PluginsChanged += (_, _) =>
        {
            var plugin = Assert.Single(manager.Plugins);
            var providers = manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles);
            events.Add(("event", plugin.State, providers.Count));

            if (probeGateOnNextEvent)
            {
                probeGateOnNextEvent = false;
                // Must not deadlock: _gate must already be released before PluginsChanged fires.
                var task = Task.Run(() => manager.RefreshAsync([workspace.Root], preserveActive: true, TestContext.Current.CancellationToken));
                Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "PluginsChanged executed while _gate was still held.");
                reentrantRefreshSucceeded = task.Result.Succeeded;
            }
        };

        probeGateOnNextEvent = true;
        var refreshResult = await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken);
        Assert.True(refreshResult.Succeeded, refreshResult.Diagnostic);
        Assert.True(reentrantRefreshSucceeded);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(PluginState.Disabled, e.State));
        Assert.Empty(manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles));

        events.Clear();
        probeGateOnNextEvent = true;
        reentrantRefreshSucceeded = false;
        var enableResult = await manager.EnableAsync("style.cel", TestContext.Current.CancellationToken);
        Assert.True(enableResult.Succeeded, enableResult.Diagnostic);
        Assert.True(reentrantRefreshSucceeded);
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(PluginState.Active, e.State);
            Assert.Equal(1, e.StyleProviderCount);
        });

        var registration = Assert.Single(manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles));
        Assert.Equal("style.cel", registration.PluginId);
        Assert.Equal("dynamic-cel", registration.RegistrationId);
        var style = Assert.Single(registration.Value.GetStyles());
        Assert.Equal("dynamic-cel", style.StyleId);
        Assert.Equal("Dynamic Cel", style.DisplayName.Resolve("en"));

        // Enabling an already-active plugin performs no state change and should not emit spurious PluginsChanged.
        events.Clear();
        Assert.True((await manager.EnableAsync("style.cel", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Empty(events);

        // Disabling unregisters the style provider and raises PluginsChanged outside _gate.
        probeGateOnNextEvent = true;
        reentrantRefreshSucceeded = false;
        var disableResult = await manager.DisableAsync("style.cel", TestContext.Current.CancellationToken);
        Assert.True(disableResult.Succeeded, disableResult.Diagnostic);
        Assert.True(reentrantRefreshSucceeded);
        Assert.Empty(manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles));
        Assert.Equal(2, events.Count);
        Assert.Equal(PluginState.Disabled, events[0].State);
        Assert.Equal(0, events[0].StyleProviderCount);

        // Disabling an already-disabled plugin should not emit PluginsChanged.
        events.Clear();
        Assert.True((await manager.DisableAsync("style.cel", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Empty(events);

        // DisposeAsync cleans up safely without raising PluginsChanged or throwing.
        await manager.DisposeAsync();
        Assert.Empty(manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles));
        Assert.False((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);
        Assert.Empty(events);
    }

    private static PluginManager CreateManager() => new(
        new PluginHostInfo(new Version(0, 1, 22), 1, "zh-CN"),
        [
            new PluginCapability(PluginCapabilities.EditorCommands, typeof(IPluginCommand)),
            new PluginCapability(PluginCapabilities.RenderingStyles, typeof(IRenderingStyleProvider)),
        ]);

    private sealed class StylePluginWorkspace
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public StylePluginWorkspace([CallerMemberName] string testName = "")
        {
            Root = Path.Combine(Path.GetTempPath(), "MDA", "Tests", "Plugins", $"{testName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string AddFixturePlugin(string id, bool writeOptionalMetadata)
        {
            var directory = CopyFixtureFiles(id);
            if (!writeOptionalMetadata)
            {
                File.WriteAllText(Path.Combine(directory, "mda-plugin.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    id,
                    name = "Minimal Plugin",
                    version = "1.0.0",
                    entryAssembly = "Mmd.Modern.PluginFixture.dll",
                    entryType = "Mmd.Modern.PluginFixture.CommandPlugin",
                    apiVersion = 1,
                    minimumHostVersion = "0.1.0",
                    dependencies = Array.Empty<object>(),
                    capabilities = new[] { PluginCapabilities.RenderingStyles },
                }, JsonOptions));
            }
            else
            {
                File.WriteAllText(Path.Combine(directory, "mda-plugin.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    id,
                    name = "Rich Style",
                    version = "1.2.0",
                    entryAssembly = "Mmd.Modern.PluginFixture.dll",
                    entryType = "Mmd.Modern.PluginFixture.CommandPlugin",
                    apiVersion = 1,
                    minimumHostVersion = "0.1.0",
                    dependencies = Array.Empty<object>(),
                    capabilities = new[] { PluginCapabilities.RenderingStyles },
                    description = "Default description",
                    author = "Plugin Author",
                    localizedNames = new Dictionary<string, string>
                    {
                        ["zh-CN"] = "风格插件",
                        ["en"] = "Style Plugin EN",
                        ["ja"] = "スタイルプラグイン",
                    },
                    localizedDescriptions = new Dictionary<string, string>
                    {
                        ["zh-CN"] = "中文说明",
                        ["en"] = "English description",
                        ["ja"] = "日本語説明",
                    },
                    providesStyles = new[] { "cel-v1", "ink_2" },
                }, JsonOptions));
            }
            return directory;
        }

        public string AddRawManifestPlugin(string id, string invalidCase)
        {
            var directory = CopyFixtureFiles(id);
            var payload = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["id"] = id,
                ["name"] = "Invalid Optional",
                ["version"] = "1.0.0",
                ["entryAssembly"] = "Mmd.Modern.PluginFixture.dll",
                ["entryType"] = "Mmd.Modern.PluginFixture.CommandPlugin",
                ["apiVersion"] = 1,
                ["minimumHostVersion"] = "0.1.0",
                ["dependencies"] = Array.Empty<object>(),
                ["capabilities"] = new[] { PluginCapabilities.RenderingStyles },
            };

            switch (invalidCase)
            {
                case "description-number": payload["description"] = 123; break;
                case "description-null": payload["description"] = null; break;
                case "author-array": payload["author"] = new[] { "a" }; break;
                case "localizedNames-array": payload["localizedNames"] = new[] { "zh-CN" }; break;
                case "localizedNames-empty-value": payload["localizedNames"] = new Dictionary<string, string> { ["zh-CN"] = "   " }; break;
                case "localizedNames-number-value": payload["localizedNames"] = new Dictionary<string, int> { ["zh-CN"] = 42 }; break;
                case "localizedDescriptions-string": payload["localizedDescriptions"] = "not-an-object"; break;
                case "localizedDescriptions-number-value": payload["localizedDescriptions"] = new Dictionary<string, object> { ["en"] = 7 }; break;
                case "providesStyles-string": payload["providesStyles"] = "cel-v1"; break;
                case "providesStyles-empty-slug": payload["providesStyles"] = new[] { "" }; break;
                case "providesStyles-non-ascii": payload["providesStyles"] = new[] { "风格" }; break;
                case "providesStyles-slash": payload["providesStyles"] = new[] { "bad/slug" }; break;
                case "providesStyles-duplicate": payload["providesStyles"] = new[] { "same", "same" }; break;
                default: throw new ArgumentOutOfRangeException(nameof(invalidCase));
            }

            File.WriteAllText(Path.Combine(directory, "mda-plugin.json"), JsonSerializer.Serialize(payload));
            return directory;
        }

        public string AddDynamicStylePlugin(string id)
        {
            var directory = Path.Combine(Root, id);
            Directory.CreateDirectory(directory);
            var dllName = "Mmd.Modern.TestStylePlugin.dll";
            EmitDynamicStylePluginAssembly(Path.Combine(directory, dllName));
            File.WriteAllText(Path.Combine(directory, "mda-plugin.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id,
                name = "Dynamic Style Plugin",
                version = "1.0.0",
                entryAssembly = dllName,
                entryType = DynamicEntryType,
                apiVersion = 1,
                minimumHostVersion = "0.1.0",
                dependencies = Array.Empty<object>(),
                capabilities = new[] { PluginCapabilities.RenderingStyles },
                providesStyles = new[] { "dynamic-cel" },
            }, JsonOptions));
            return directory;
        }

        private string CopyFixtureFiles(string id)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "PluginFixtures");
            Assert.True(File.Exists(Path.Combine(source, "Mmd.Modern.PluginFixture.dll")), "The actual compiled plugin fixture is missing.");
            var directory = Path.Combine(Root, id);
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            return directory;
        }

        private static void EmitDynamicStylePluginAssembly(string outputPath)
        {
            var assemblyBuilder = new PersistedAssemblyBuilder(
                new AssemblyName("Mmd.Modern.TestStylePlugin"),
                typeof(object).Assembly);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule("Mmd.Modern.TestStylePlugin.dll");
            var typeBuilder = moduleBuilder.DefineType(
                DynamicEntryType,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                typeof(RenderingStyleDefinitionBase),
                [typeof(IMdaPlugin), typeof(IRenderingStyleProvider)]);

            var baseCtor = typeof(RenderingStyleDefinitionBase).GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null)!;
            var ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var ctorIl = ctor.GetILGenerator();
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Call, baseCtor);
            ctorIl.Emit(OpCodes.Ret);

            var styleIdGetter = typeBuilder.DefineMethod(
                "get_StyleId",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                typeof(string), Type.EmptyTypes);
            var styleIdIl = styleIdGetter.GetILGenerator();
            styleIdIl.Emit(OpCodes.Ldstr, "dynamic-cel");
            styleIdIl.Emit(OpCodes.Ret);
            typeBuilder.DefineProperty("StyleId", PropertyAttributes.None, typeof(string), null).SetGetMethod(styleIdGetter);

            var localizedTextCtor = typeof(LocalizedText).GetConstructor([typeof(string), typeof(IReadOnlyDictionary<string, string>)])!;
            var displayNameGetter = typeBuilder.DefineMethod(
                "get_DisplayName",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                typeof(LocalizedText), Type.EmptyTypes);
            var displayNameIl = displayNameGetter.GetILGenerator();
            displayNameIl.Emit(OpCodes.Ldstr, "Dynamic Cel");
            displayNameIl.Emit(OpCodes.Ldnull);
            displayNameIl.Emit(OpCodes.Newobj, localizedTextCtor);
            displayNameIl.Emit(OpCodes.Ret);
            typeBuilder.DefineProperty("DisplayName", PropertyAttributes.None, typeof(LocalizedText), null).SetGetMethod(displayNameGetter);

            var getStyles = typeBuilder.DefineMethod(
                nameof(IRenderingStyleProvider.GetStyles),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
                typeof(IReadOnlyList<IRenderingStyleDefinition>), Type.EmptyTypes);
            var getStylesIl = getStyles.GetILGenerator();
            getStylesIl.Emit(OpCodes.Ldc_I4_1);
            getStylesIl.Emit(OpCodes.Newarr, typeof(IRenderingStyleDefinition));
            getStylesIl.Emit(OpCodes.Dup);
            getStylesIl.Emit(OpCodes.Ldc_I4_0);
            getStylesIl.Emit(OpCodes.Ldarg_0);
            getStylesIl.Emit(OpCodes.Stelem_Ref);
            getStylesIl.Emit(OpCodes.Ret);

            var completedTaskGetter = typeof(ValueTask).GetProperty(nameof(ValueTask.CompletedTask))!.GetMethod!;
            var registerMethod = typeof(IPluginContext).GetMethod(nameof(IPluginContext.RegisterExtension))!;

            var startMethod = typeBuilder.DefineMethod(
                nameof(IMdaPlugin.StartAsync),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
                typeof(ValueTask), [typeof(IPluginContext), typeof(CancellationToken)]);
            var startIl = startMethod.GetILGenerator();
            startIl.Emit(OpCodes.Ldarg_1);
            startIl.Emit(OpCodes.Ldstr, PluginCapabilities.RenderingStyles);
            startIl.Emit(OpCodes.Ldstr, "dynamic-cel");
            startIl.Emit(OpCodes.Ldarg_0);
            startIl.Emit(OpCodes.Callvirt, registerMethod);
            startIl.Emit(OpCodes.Call, completedTaskGetter);
            startIl.Emit(OpCodes.Ret);

            var stopMethod = typeBuilder.DefineMethod(
                nameof(IMdaPlugin.StopAsync),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
                typeof(ValueTask), [typeof(CancellationToken)]);
            var stopIl = stopMethod.GetILGenerator();
            stopIl.Emit(OpCodes.Call, completedTaskGetter);
            stopIl.Emit(OpCodes.Ret);

            var disposeMethod = typeBuilder.DefineMethod(
                nameof(IAsyncDisposable.DisposeAsync),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
                typeof(ValueTask), Type.EmptyTypes);
            var disposeIl = disposeMethod.GetILGenerator();
            disposeIl.Emit(OpCodes.Call, completedTaskGetter);
            disposeIl.Emit(OpCodes.Ret);

            typeBuilder.CreateType();
            assemblyBuilder.Save(outputPath);
        }
    }
}
