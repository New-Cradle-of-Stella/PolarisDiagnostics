using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// 程序集 → <see cref="AssemblyOwner"/> 的归属表。判定按路径优先、程序集名兜底，结果永久缓存。
    /// </summary>
    internal static class AssemblyOwnerIndex
    {
        static readonly Dictionary<Assembly, AssemblyOwner> byAssembly = new();

        static Dictionary<Assembly, PluginInfo> pluginByAssembly;
        static Dictionary<string, AssemblyOwner> byNamespace;

        /// <summary>判不出归属的帧共用的实例。</summary>
        static readonly AssemblyOwner UnknownOwner = new AssemblyOwner
        {
            Kind = OwnerKind.Unknown,
            DisplayName = "unknown",
        };

        // ================== 对外查询 ==================

        /// <summary>取一个程序集的归属。<paramref name="assembly"/> 为 null 时给出"未知"。</summary>
        internal static AssemblyOwner Of(Assembly assembly)
        {
            if (assembly == null)
            {
                return UnknownOwner;
            }

            if (byAssembly.TryGetValue(assembly, out AssemblyOwner cached))
            {
                return cached;
            }

            AssemblyOwner owner = Classify(assembly);
            byAssembly[assembly] = owner;
            return owner;
        }

        /// <summary>按类型全名取归属（供只有字符串、没有异常对象的堆栈用），逐级剥命名空间向上查找。</summary>
        internal static AssemblyOwner OfTypeName(string fullTypeName)
        {
            if (string.IsNullOrEmpty(fullTypeName))
            {
                return UnknownOwner;
            }

            Dictionary<string, AssemblyOwner> map = NamespaceMap();

            // "nel.title.SceneTitleTemp+STATE.foo" → "nel.title.SceneTitleTemp+STATE"
            //   → "nel.title.SceneTitleTemp" → "nel.title" 命中。
            string probe = fullTypeName;
            while (true)
            {
                int dot = probe.LastIndexOf('.');
                if (dot <= 0)
                {
                    return UnknownOwner;
                }

                probe = probe.Substring(0, dot);
                if (map.TryGetValue(probe, out AssemblyOwner owner))
                {
                    // null 表示该命名空间被多个不同归属的程序集共用，不猜，直接认输。
                    return owner ?? UnknownOwner;
                }
            }
        }

        /// <summary>按 BepInEx 插件 GUID 取归属（Harmony 的 <c>Patch.owner</c> 即插件 GUID）。</summary>
        internal static AssemblyOwner ByPluginGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return UnknownOwner;
            }

            foreach (KeyValuePair<Assembly, PluginInfo> pair in PluginMap())
            {
                if (string.Equals(pair.Value.Metadata?.GUID, guid, StringComparison.Ordinal))
                {
                    return Of(pair.Key);
                }
            }

            return UnknownOwner;
        }

        /// <summary>本次游戏加载的全部模组（含 Polaris 自己），供报告头部列清单。</summary>
        internal static IEnumerable<AssemblyOwner> LoadedMods()
            => PluginMap().Keys.Select(Of)
                          .Where(owner => owner.Kind != OwnerKind.Unknown)
                          .OrderBy(owner => owner.Kind)
                          .ThenBy(owner => owner.DisplayName, StringComparer.OrdinalIgnoreCase);

        // ================== 判定 ==================

        static AssemblyOwner Classify(Assembly assembly)
        {
            var owner = new AssemblyOwner
            {
                Assembly = assembly,
                DisplayName = SafeName(assembly),
            };

            string location = SafeLocation(assembly);

            // 1. 自己：不依赖路径推断，避免分发方式影响判断。
            if (DiagnosticsRuntime.IsPolarisAssembly(assembly))
            {
                owner.Kind = OwnerKind.Polaris;
                owner.PluginGuid = assembly == DiagnosticsRuntime.CoreAssembly
                    ? DiagnosticsRuntime.PluginGuid
                    : null;
                Locate(owner, location);
                return owner;
            }

            // 2. 没有落盘位置：动态程序集（Harmony DMD、Emit）。
            if (string.IsNullOrEmpty(location))
            {
                owner.Kind = OwnerKind.Dynamic;
                return owner;
            }

            Locate(owner, location);

            // 3. 游戏 Managed 目录：原版本体，或按名字再细分出引擎/BCL。
            if (IsUnder(location, ManagedDir))
            {
                owner.Kind = IsRuntimeName(owner.DisplayName) ? OwnerKind.Runtime : OwnerKind.Vanilla;
                return owner;
            }

            // 4. BepInEx core 目录：加载器与补丁框架。
            if (IsUnder(location, CoreDir))
            {
                owner.Kind = OwnerKind.Framework;
                return owner;
            }

            // 5. Polaris 随包分发的第三方依赖；须排在 plugins 通用判断之前，避免误判为普通模组。
            if (IsUnder(location, DiagnosticsRuntime.LibsDir))
            {
                owner.Kind = OwnerKind.ModLibrary;
                return owner;
            }

            // 6. plugins 下的其它 dll：注册为 BepInEx 插件才算模组，否则算模组依赖。
            if (IsUnder(location, DiagnosticsRuntime.PluginsRoot))
            {
                owner.Kind = PluginMap().ContainsKey(assembly) ? OwnerKind.Mod : OwnerKind.ModLibrary;
                Enrich(owner);
                return owner;
            }

            // 7. 落在约定目录之外，只能按名字猜。
            owner.Kind = ClassifyByName(owner.DisplayName);
            return owner;
        }

        /// <summary>填 <see cref="AssemblyOwner.FileName"/> / <see cref="AssemblyOwner.FullPath"/>。</summary>
        static void Locate(AssemblyOwner owner, string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                return;
            }

            owner.FullPath = location;
            owner.FileName = Path.GetFileName(location);
        }

        /// <summary>给模组归属补上 GUID 与 <see cref="PolarisModInfo"/>（作者、主页）。</summary>
        static void Enrich(AssemblyOwner owner)
        {
            if (owner.FileName != null)
            {
                owner.ModInfo = PolarisModInfoResolver.Resolve(owner.FileName);
                if (!string.IsNullOrEmpty(owner.ModInfo?.DisplayName))
                {
                    owner.DisplayName = owner.ModInfo.DisplayName;
                }
            }

            if (owner.Assembly != null && PluginMap().TryGetValue(owner.Assembly, out PluginInfo info))
            {
                owner.PluginGuid = info.Metadata?.GUID;
            }
        }

        static OwnerKind ClassifyByName(string name)
        {
            if (IsRuntimeName(name))
            {
                return OwnerKind.Runtime;
            }

            if (StartsWithAny(name, "BepInEx", "0Harmony", "HarmonyLib", "MonoMod", "Mono.Cecil", "SemanticVersioning"))
            {
                return OwnerKind.Framework;
            }

            return OwnerKind.Unknown;
        }

        static bool IsRuntimeName(string name)
            => StartsWithAny(name,
                "mscorlib", "netstandard", "System", "Microsoft", "Mono.", "I18N",
                "UnityEngine", "Unity.", "TextMeshPro", "TMPro");

        static bool StartsWithAny(string value, params string[] prefixes)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (string prefix in prefixes)
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // ================== 反查表 ==================

        /// <summary><c>Assembly → PluginInfo</c> 反向表（BepInEx 只提供 GUID → PluginInfo 正向表）。</summary>
        static Dictionary<Assembly, PluginInfo> PluginMap()
        {
            if (pluginByAssembly != null)
            {
                return pluginByAssembly;
            }

            var map = new Dictionary<Assembly, PluginInfo>();

            foreach (PluginInfo info in DiagnosticsRuntime.Plugins)
            {
                Assembly assembly = SafeAssemblyOf(info);
                if (assembly != null && !map.ContainsKey(assembly))
                {
                    map[assembly] = info;
                }
            }

            pluginByAssembly = map;
            return map;
        }

        /// <summary>
        /// 命名空间 → 归属，仅在需要解析字符串堆栈时按需构建。
        /// 同一命名空间被不同归属的程序集共用时存 null，避免误判。
        /// </summary>
        static Dictionary<string, AssemblyOwner> NamespaceMap()
        {
            if (byNamespace != null)
            {
                return byNamespace;
            }

            var map = new Dictionary<string, AssemblyOwner>(StringComparer.Ordinal);

            foreach (Assembly assembly in SafeLoadedAssemblies())
            {
                AssemblyOwner owner = Of(assembly);
                if (owner.Kind == OwnerKind.Unknown || owner.Kind == OwnerKind.Dynamic)
                {
                    continue;
                }

                foreach (Type type in DiagnosticsRuntime.TypesOf(assembly))
                {
                    string ns = type.Namespace;
                    if (string.IsNullOrEmpty(ns))
                    {
                        continue;
                    }

                    if (!map.TryGetValue(ns, out AssemblyOwner existing))
                    {
                        map[ns] = owner;
                        continue;
                    }

                    // 归属不同则作废该命名空间。
                    if (existing != null && existing.Kind != owner.Kind)
                    {
                        map[ns] = null;
                    }
                }
            }

            byNamespace = map;
            return map;
        }

        // ================== 兜底的反射访问 ==================
        // 错误分析跑在"已经出事了"的路径上，加载了一半的程序集读 Location 可能抛异常，须吞掉。

        static string SafeLocation(Assembly assembly)
        {
            try
            {
                return assembly.IsDynamic ? null : assembly.Location;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string SafeName(Assembly assembly)
        {
            try
            {
                return assembly.GetName().Name;
            }
            catch (Exception)
            {
                return assembly.FullName;
            }
        }

        static Assembly SafeAssemblyOf(PluginInfo info)
        {
            try
            {
                return info?.Instance?.GetType().Assembly;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static IEnumerable<Assembly> SafeLoadedAssemblies()
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception)
            {
                return Enumerable.Empty<Assembly>();
            }
        }

        // ================== 目录常量 ==================

        static string managedDir;
        static string coreDir;

        /// <summary>游戏自带程序集目录，例如 <c>…\AliceInCradle_Data\Managed\</c>。</summary>
        static string ManagedDir => managedDir ??= SafePath(() => Paths.ManagedPath);

        /// <summary>BepInEx 自身所在目录 <c>BepInEx\core\</c>。</summary>
        static string CoreDir => coreDir ??= SafePath(() => Paths.BepInExAssemblyDirectory);

        static string SafePath(Func<string> get)
        {
            try
            {
                return get();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>判断 <paramref name="path"/> 是否在 <paramref name="directory"/> 之下（补分隔符防止前缀误匹配，如 plugins_backup）。</summary>
        static bool IsUnder(string path, string directory)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory))
            {
                return false;
            }

            try
            {
                string full = Path.GetFullPath(directory);
                if (full[full.Length - 1] != Path.DirectorySeparatorChar)
                {
                    full += Path.DirectorySeparatorChar;
                }

                return Path.GetFullPath(path).StartsWith(full, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
