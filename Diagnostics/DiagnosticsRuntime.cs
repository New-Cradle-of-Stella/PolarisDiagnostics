using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono.Bootstrap;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// Core 在最早期注入的宿主信息。诊断模块直接依赖 BepInEx，绝不反向引用 PolarisCore。
    /// </summary>
    internal static class DiagnosticsRuntime
    {
        static readonly Dictionary<Assembly, Type[]> typeCache = new();
        static Func<string> currentLocale;
        static Func<IReadOnlyList<string>> disabledMods;

        internal static ManualLogSource Logger { get; private set; }
        internal static Assembly CoreAssembly { get; private set; }
        internal static string PluginGuid { get; private set; }
        internal static string PluginName { get; private set; }
        internal static string PluginVersion { get; private set; }
        internal static string ReportTarget { get; private set; }

        internal static string PluginsRoot => Paths.PluginPath;
        internal static string PolarisRoot => Path.Combine(PluginsRoot, "Polaris");
        internal static string LibsDir => Path.Combine(PolarisRoot, "libs");
        internal static string ConfigDir => Path.Combine(Paths.ConfigPath, "Polaris");
        internal static string StateDir => Path.Combine(Paths.BepInExRootPath, "Polaris");
        internal static string ReportsDir => Path.Combine(StateDir, "reports");

        internal static IEnumerable<PluginInfo> Plugins
            => UnityChainloader.Instance?.Plugins.Values ?? Enumerable.Empty<PluginInfo>();

        internal static string CurrentLocale
        {
            get
            {
                try
                {
                    return currentLocale?.Invoke();
                }
                catch
                {
                    return null;
                }
            }
        }

        internal static IReadOnlyList<string> DisabledMods
            => disabledMods?.Invoke() ?? Array.Empty<string>();

        internal static void Configure(
            ManualLogSource logger,
            Assembly coreAssembly,
            string pluginGuid,
            string pluginName,
            string pluginVersion,
            string reportTarget,
            Func<string> localeProvider,
            Func<IReadOnlyList<string>> disabledModsProvider)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            CoreAssembly = coreAssembly ?? throw new ArgumentNullException(nameof(coreAssembly));
            PluginGuid = pluginGuid ?? throw new ArgumentNullException(nameof(pluginGuid));
            PluginName = pluginName ?? throw new ArgumentNullException(nameof(pluginName));
            PluginVersion = pluginVersion ?? throw new ArgumentNullException(nameof(pluginVersion));
            ReportTarget = reportTarget ?? throw new ArgumentNullException(nameof(reportTarget));
            currentLocale = localeProvider;
            disabledMods = disabledModsProvider;
        }

        internal static bool IsPolarisAssembly(Assembly assembly)
            => assembly != null
               && assembly.GetName().Name.StartsWith("Polaris", StringComparison.Ordinal);

        internal static IReadOnlyList<Type> TypesOf(Assembly assembly)
        {
            if (assembly == null)
            {
                return Array.Empty<Type>();
            }

            if (typeCache.TryGetValue(assembly, out Type[] cached))
            {
                return cached;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
            }
            catch (Exception exception)
            {
                Logger?.LogWarning(
                    $"[Polaris.Diagnostics] Failed to read the types of {assembly.GetName().Name}: {exception.Message}");
                types = Array.Empty<Type>();
            }

            typeCache[assembly] = types;
            return types;
        }
    }
}
