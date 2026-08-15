using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// 把错误写成纯文本报告（落在 <see cref="DiagnosticsRuntime.ReportsDir"/>），玩家可直接整段贴出去。
    /// 一局一个文件、追加写，不按事件分文件。
    /// </summary>
    internal static class ErrorReportWriter
    {
        /// <summary>报告目录里最多留几份，超出的按修改时间从旧到新删。</summary>
        const int KeepReports = 20;

        /// <summary>串行化所有写入：主线程与看门狗线程（写卡死报告）都会写这份共享状态。</summary>
        static readonly object Gate = new();

        /// <summary>进程启动时间，固定取一次；报告文件名与文件头都用它，避免中途重算出两个文件名。</summary>
        static readonly DateTime ProcessStart = DateTime.Now;

        static string path;
        static bool headerWritten;

        /// <summary>写盘失败过一次就不再尝试（目录只读/磁盘满这类问题不会自己好）。</summary>
        static bool disabled;

        /// <summary>最近一次写入成功的报告路径；从没写成功过为 null。</summary>
        internal static string LastWrittenPath { get; private set; }

        /// <summary>本进程的报告文件路径（文件不一定已经存在）。</summary>
        static string ReportPath => path ??= Path.Combine(
            DiagnosticsRuntime.ReportsDir, $"polaris-report_{ProcessStart:yyyyMMdd_HHmmss}.txt");

        // ================== 写入 ==================

        internal static void Append(ErrorIncident incident)
            => Write(() => BuildIncident(incident));

        /// <summary>写一条致命错误（见 <see cref="FatalError"/>），排版与普通事件不同，突出显示。</summary>
        internal static void AppendFatal(FatalError fatal)
        {
            if (fatal == null)
            {
                return;
            }

            Write(() => BuildFatal(fatal));
        }

        /// <summary>写一次疑似卡死（见 <see cref="HangReport"/>），由看门狗线程调用。</summary>
        internal static void AppendHang(HangReport report)
        {
            if (report == null)
            {
                return;
            }

            Write(() => BuildHang(report));
        }

        /// <summary>写一条"这类错误正在持续反复发生"的加注（见 <see cref="ErrorIncident.IsStorm"/>）。</summary>
        internal static void AppendStorm(ErrorIncident incident)
        {
            if (incident == null)
            {
                return;
            }

            Write(() => BuildStorm(incident));
        }

        /// <summary>
        /// 写一段"关于上一局"，本局启动时调用一次；写进本局报告而非追溯上一局文件（后者可能不存在）。
        /// </summary>
        internal static void AppendPreviousSession(LastSessionInfo info)
        {
            if (info == null)
            {
                return;
            }

            Write(() => BuildPreviousSession(info));
        }

        /// <summary>所有写入的唯一出口：补文件头、追加正文、记住路径，全程持锁；<paramref name="body"/> 延迟求值。</summary>
        static void Write(Func<string> body)
        {
            if (disabled)
            {
                return;
            }

            lock (Gate)
            {
                if (disabled)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(DiagnosticsRuntime.ReportsDir);

                    if (!headerWritten)
                    {
                        Cleanup();
                        File.AppendAllText(ReportPath, BuildHeader(), Encoding.UTF8);
                        headerWritten = true;
                    }

                    File.AppendAllText(ReportPath, body(), Encoding.UTF8);
                    LastWrittenPath = ReportPath;
                }
                catch (Exception)
                {
                    // 不能记日志：会被自己的监听器抓到，绕回来再写一次报告、再失败、再记日志。
                    disabled = true;
                    LastWrittenPath = null;
                }
            }
        }

        /// <summary>报告目录只留最近 <see cref="KeepReports"/> 份，免得跑几百局之后堆成一片。</summary>
        static void Cleanup()
        {
            try
            {
                var files = new DirectoryInfo(DiagnosticsRuntime.ReportsDir)
                    .GetFiles("polaris-report_*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(KeepReports - 1)
                    .ToList();

                foreach (FileInfo file in files)
                {
                    file.Delete();
                }
            }
            catch (Exception)
            {
                // 清不掉旧报告不影响写新报告。
            }
        }

        // ================== 环境信息 ==================

        static string gameVersion;
        static string unityVersion;
        static string operatingSystem;
        static string playerLog;

        /// <summary>
        /// 在主线程预取文件头要用的 Unity 属性，由 <c>Plugin.Awake</c> 调一次
        /// （<see cref="AppendHang"/> 跑在看门狗线程，无法直接读取 Unity API）。
        /// </summary>
        internal static void PrimeEnvironment()
        {
            gameVersion = Safe(() => Application.version);
            unityVersion = Safe(() => Application.unityVersion);
            operatingSystem = Safe(() => SystemInfo.operatingSystem);
            playerLog = Safe(PlayerLogPath);
        }

        static string Env(string cached, Func<string> get) => cached ?? Safe(get);

        // ================== 文件头 ==================

        static string BuildHeader()
        {
            var b = new StringBuilder();

            b.AppendLine("==================================================================");
            b.AppendLine(" Polaris Error Report");
            b.AppendLine("==================================================================");
            b.AppendLine();
            b.AppendLine("This file is generated by Polaris to help you tell whether the problem");
            b.AppendLine("comes from a mod, from Polaris, or from the base game.");
            b.AppendLine("Who to report it to is spelled out under \"What to do\" at the end of each event.");
            b.AppendLine();
            b.AppendLine($"Generated       : {ProcessStart:yyyy-MM-dd HH:mm:ss}");
            b.AppendLine($"Polaris version : {DiagnosticsRuntime.PluginVersion}");
            b.AppendLine($"Game version    : {Env(gameVersion, () => Application.version)}");
            b.AppendLine($"Unity version   : {Env(unityVersion, () => Application.unityVersion)}");
            b.AppendLine($"OS              : {Env(operatingSystem, () => SystemInfo.operatingSystem)}");
            b.AppendLine($"Game language   : {Safe(() => DiagnosticsRuntime.CurrentLocale)}");
            b.AppendLine();
            b.AppendLine("Other logs:");
            b.AppendLine($"  Unity player log : {Env(playerLog, PlayerLogPath)}");
            b.AppendLine("  BepInEx log      : BepInEx/LogOutput.log");
            b.AppendLine();
            b.AppendLine("------------------------------------------------------------------");
            b.AppendLine(" Loaded mods");
            b.AppendLine("------------------------------------------------------------------");

            foreach (string line in ModLines())
            {
                b.AppendLine(line);
            }

            b.AppendLine();
            b.AppendLine("Disabled (not loaded this session):");
            foreach (string line in DisabledLines())
            {
                b.AppendLine(line);
            }

            b.AppendLine();
            return b.ToString();
        }

        static IEnumerable<string> ModLines()
        {
            // yield return 不能出现在 catch 内，先取好结果，出 try/catch 后再 yield。
            List<AssemblyOwner> mods;
            try
            {
                mods = AssemblyOwnerIndex.LoadedMods().ToList();
            }
            catch (Exception)
            {
                mods = null;
            }

            if (mods == null)
            {
                yield return "  (failed to read the mod list)";
                yield break;
            }

            if (mods.Count == 0)
            {
                yield return "  (no loaded plugins detected)";
                yield break;
            }

            foreach (AssemblyOwner mod in mods)
            {
                var line = new StringBuilder($"  [{mod.KindLabel}] {mod.FileName ?? mod.DisplayName}");

                if (mod.ModInfo?.Version != null)
                {
                    line.Append($"  v{mod.ModInfo.Version}");
                }

                if (mod.PluginGuid != null)
                {
                    line.Append($"  GUID={mod.PluginGuid}");
                }

                if (mod.ModInfo?.Author != null)
                {
                    line.Append($"  author={mod.ModInfo.Author}");
                }

                if (mod.ModInfo?.Url != null)
                {
                    line.Append($"  url={mod.ModInfo.Url}");
                }

                yield return line.ToString();

                // 完整路径单独一行，区分同名但不同目录的 dll。
                if (mod.FullPath != null)
                {
                    yield return $"      path={mod.FullPath}";
                }
            }
        }

        /// <summary>列出被改名成 <c>.dll.disabled</c> 的模组，方便玩家回忆上次关了哪个。</summary>
        static IEnumerable<string> DisabledLines()
        {
            IReadOnlyList<string> disabledMods;
            try
            {
                disabledMods = DiagnosticsRuntime.DisabledMods;
            }
            catch (Exception)
            {
                disabledMods = null;
            }

            if (disabledMods == null)
            {
                yield return "  (failed to read)";
                yield break;
            }

            if (disabledMods.Count == 0)
            {
                yield return "  (none)";
                yield break;
            }

            foreach (string displayName in disabledMods)
            {
                yield return $"  {displayName}";
            }
        }

        // ================== 单条事件 ==================

        static string BuildIncident(ErrorIncident incident)
        {
            ErrorVerdict verdict = incident.Verdict;
            var b = new StringBuilder();

            b.AppendLine("==================================================================");
            b.AppendLine($" Event #{incident.Index}    {incident.FirstSeen:HH:mm:ss}");
            b.AppendLine("==================================================================");

            if (incident.Context != null)
            {
                b.AppendLine($"Occurred in : {incident.Context}");
            }

            b.AppendLine($"Verdict     : {verdict.Headline()}");
            b.AppendLine($"Confidence  : {verdict.ConfidenceLabel}");
            b.AppendLine($"Reason      : {verdict.Reason}");

            if (verdict.Diagnosis != null)
            {
                b.AppendLine($"Diagnosis   : {verdict.Diagnosis}");
            }

            if (verdict.Suspects.Count > 0)
            {
                b.AppendLine("Suspects:");
                foreach (ErrorSuspect suspect in verdict.Suspects)
                {
                    b.AppendLine($"  * {suspect.Describe()}");
                }
            }

            b.AppendLine();
            b.AppendLine("Exception:");
            b.AppendLine(Indent(incident.ExceptionChain, "  "));

            if (incident.Frames.Count > 0)
            {
                b.AppendLine();
                b.AppendLine("Stack (bracketed owner is Polaris attribution, not original text):");
                foreach (ErrorFrame frame in incident.Frames)
                {
                    b.AppendLine("  " + frame.Describe());
                }
            }

            if (!string.IsNullOrEmpty(incident.RawStackTrace))
            {
                b.AppendLine();
                b.AppendLine("Raw stack:");
                b.AppendLine(Indent(incident.RawStackTrace, "  "));
            }

            b.AppendLine();
            b.AppendLine("--- What to do ---");
            b.AppendLine(Advice(verdict));
            b.AppendLine();

            return b.ToString();
        }

        /// <summary>按主责类别分叉的行动建议，是整份报告最有价值的部分。</summary>
        static string Advice(ErrorVerdict verdict)
        {
            var b = new StringBuilder();

            if (verdict.SuggestedAction != null)
            {
                b.AppendLine($"* {verdict.SuggestedAction}");
            }

            switch (verdict.Kind)
            {
                case OwnerKind.Mod:
                    b.AppendLine("* Verify it yourself first: disable this mod on the Polaris page of the title screen,");
                    b.AppendLine("  restart the game, and see whether the problem goes away.");
                    b.AppendLine($"* Once confirmed, send this report to its author{Contact(verdict.Culprit)}.");
                    b.AppendLine("* Do not report this to the game's original author or official channels -- it is not a vanilla issue.");
                    break;

                case OwnerKind.Polaris:
                    b.AppendLine($"* This is a Polaris problem. Please submit this report to {DiagnosticsRuntime.ReportTarget}.");
                    b.AppendLine("* Again, do not report it to the game's original author.");
                    break;

                case OwnerKind.Vanilla:
                    b.AppendLine("* There is no mod code in the stack, so this one may not be caused by a mod.");
                    b.AppendLine("* But do not report it to the game's author until you have reproduced it on a clean");
                    b.AppendLine("  install with no mods -- knock-on effects from mods do not always show up in the stack.");
                    break;

                case OwnerKind.Framework:
                    b.AppendLine("* Most likely a mod's Harmony patch failed to apply. Check the BepInEx log for patch");
                    b.AppendLine("  failures logged around the same time.");
                    break;

                default:
                    b.AppendLine("* Polaris could not determine who is responsible. Bisect it: disable half of your mods,");
                    b.AppendLine("  restart, see whether the problem is still there, and narrow it down round by round.");
                    b.AppendLine("* If it still reproduces with every mod disabled, confirm once on a clean install.");
                    break;
            }

            if (verdict.Suspects.Count > 1)
            {
                b.AppendLine("* Several suspects are listed above. Disable them one at a time, not all at once.");
            }

            return b.ToString().TrimEnd();
        }

        static string Contact(AssemblyOwner culprit)
        {
            string author = culprit?.ModInfo?.Author;
            string url = culprit?.ModInfo?.Url;

            if (author == null && url == null)
            {
                return " (this mod declares no author info -- look wherever you downloaded it from)";
            }

            var parts = new List<string>();
            if (author != null)
            {
                parts.Add(author);
            }

            if (url != null)
            {
                parts.Add(url);
            }

            return $" ({string.Join(" / ", parts.ToArray())})";
        }

        // ================== 致命错误 ==================

        static string BuildFatal(FatalError fatal)
        {
            var b = new StringBuilder();

            b.AppendLine("##################################################################");
            b.AppendLine($" FATAL ERROR    {DateTime.Now:HH:mm:ss}    reported by {fatal.Source}");
            b.AppendLine("##################################################################");
            b.AppendLine();
            b.AppendLine("This session was judged unable to continue: the mod environment itself is broken,");
            b.AppendLine("and playing on would only produce wrong results or harder-to-diagnose failures.");
            b.AppendLine("Polaris has blocked the title-screen menu and asked the player to quit the game.");
            b.AppendLine();
            b.AppendLine($"Reason : {fatal.Reason?.ForReport}");

            if (fatal.Details.Count > 0)
            {
                b.AppendLine();
                b.AppendLine("Details:");
                foreach (string detail in fatal.Details)
                {
                    b.AppendLine($"  * {detail}");
                }
            }

            b.AppendLine();
            b.AppendLine("Mods involved:");
            foreach (string line in CulpritLines(fatal))
            {
                b.AppendLine(line);
            }

            b.AppendLine();
            b.AppendLine("--- What to do ---");
            b.AppendLine(fatal.Action?.ForReport ?? DefaultFatalAction);
            b.AppendLine("* This is not a vanilla game problem. Do not report it to the game's original author or official channels.");
            b.AppendLine();

            return b.ToString();
        }

        const string DefaultFatalAction =
            "* Disable the mods listed above (Polaris page on the title screen) and restart, leaving only\n"
            + "  a combination that plays correctly.\n"
            + "* Then send this report to their authors -- this kind of conflict is usually only fixable by them.";

        /// <summary>致命错误的"涉及的模组"段；可能有多个责任方，每个都带联系方式。</summary>
        static IEnumerable<string> CulpritLines(FatalError fatal)
        {
            if (fatal.Culprits.Count == 0)
            {
                yield return "  (the caller did not name specific mods -- judge from the details above)";
                yield break;
            }

            foreach (Assembly culprit in fatal.Culprits)
            {
                if (culprit == null)
                {
                    continue;
                }

                foreach (string line in DescribeCulprit(culprit))
                {
                    yield return line;
                }
            }
        }

        // ================== 卡死 ==================

        static string BuildHang(HangReport report)
        {
            var b = new StringBuilder();

            b.AppendLine("##################################################################");
            b.AppendLine($" SUSPECTED HANG #{report.Index}    {report.DetectedAt:HH:mm:ss}");
            b.AppendLine("##################################################################");
            b.AppendLine();
            b.AppendLine($"The main thread has not advanced a single frame for {report.StallSeconds:0} seconds.");
            b.AppendLine("This is not an exception -- nothing was thrown, so it does not appear among the");
            b.AppendLine("\"events\" above, and it does not appear in Unity's Player.log either.");
            b.AppendLine("Typical causes are an infinite loop, a deadlock, or a wait that never completes.");
            b.AppendLine();
            b.AppendLine($"Stalled at   : {(report.DuringBoot ? "game startup (main loop not entered yet)" : $"frame {report.LastFrame}")}");

            if (!string.IsNullOrEmpty(report.Scene))
            {
                b.AppendLine($"Scene        : {report.Scene}");
            }

            b.AppendLine($"Detected at  : {report.DetectedAt:yyyy-MM-dd HH:mm:ss}");
            b.AppendLine();

            if (report.Activity != null)
            {
                b.AppendLine("Main thread was executing when it stopped responding:");
                b.AppendLine($"  {report.Activity}");
                b.AppendLine();
                b.AppendLine("Mods involved:");
                foreach (string line in HangCulpritLines(report))
                {
                    b.AppendLine(line);
                }
            }
            else
            {
                b.AppendLine("The main thread was not inside any Polaris instrumentation point when it stopped.");
                b.AppendLine("That is itself a clue: the stuck code was not mod code dispatched through Polaris.");
                b.AppendLine("More likely vanilla logic, a Harmony patch a mod installed itself, or its own");
                b.AppendLine("MonoBehaviour/coroutine.");
            }

            b.AppendLine();
            b.AppendLine("--- What to do ---");

            if (report.Activity != null)
            {
                b.AppendLine("* The mod named above is the prime suspect: disable it on the Polaris page of the");
                b.AppendLine("  title screen, restart, and see whether it still hangs.");
                b.AppendLine("* Once confirmed, send this report to its author.");
            }
            else
            {
                b.AppendLine("* Bisect it: disable half your mods, restart, and see whether it still hangs in the");
                b.AppendLine("  same place. Narrow it down round by round.");
                b.AppendLine("* The \"Stalled at\" line above is key information -- if the same scene or the same");
                b.AppendLine("  action reproduces it reliably, include that in your report.");
            }

            b.AppendLine("* The hang detection threshold can be tuned in");
            b.AppendLine("  BepInEx/config/Polaris/_polaris_diagnostics.cfg.");
            b.AppendLine("  If the game was really just loading a very large save rather than hanging, raise ReportSeconds.");
            b.AppendLine();

            return b.ToString();
        }

        static IEnumerable<string> HangCulpritLines(HangReport report)
        {
            if (report.Culprit == null)
            {
                yield return "  (the instrumentation point did not name a specific mod -- judge from the line above)";
                yield break;
            }

            foreach (string line in DescribeCulprit(report.Culprit))
            {
                yield return line;
            }
        }

        /// <summary>把一个程序集描述成"责任人 + 联系方式 + 路径"，供致命错误与卡死报告共用。</summary>
        static IEnumerable<string> DescribeCulprit(Assembly culprit)
        {
            AssemblyOwner owner = null;
            try
            {
                owner = AssemblyOwnerIndex.Of(culprit);
            }
            catch (Exception)
            {
                // 查不出归属退回程序集名。
            }

            if (owner == null)
            {
                yield return $"  * {SafeAssemblyName(culprit)}";
                yield break;
            }

            yield return $"  * {owner.Describe()}{Contact(owner)}";

            if (owner.FullPath != null)
            {
                yield return $"      path={owner.FullPath}";
            }
        }

        static string SafeAssemblyName(Assembly assembly)
        {
            try
            {
                return assembly.GetName().Name;
            }
            catch (Exception)
            {
                return "(unknown assembly)";
            }
        }

        // ================== 持续性故障 ==================

        static string BuildStorm(ErrorIncident incident)
        {
            var b = new StringBuilder();

            b.AppendLine("------------------------------------------------------------------");
            b.AppendLine($" NOTE: event #{incident.Index} is happening repeatedly    {incident.StormDetectedAt:HH:mm:ss}");
            b.AppendLine("------------------------------------------------------------------");
            b.AppendLine($"This class of error occurred {incident.StormBurst} times within "
                         + $"{DiagnosticsConfig.StormWindowSeconds:0.#} seconds ({incident.Count} times in total so far).");
            b.AppendLine("That rate means it lives in code that runs every frame (Update, OnGUI, a per-frame coroutine).");
            b.AppendLine("The corresponding feature is not \"occasionally failing\" this session -- it is fully broken.");
            b.AppendLine("For the full verdict and stack, see \"Event #" + incident.Index + "\" above.");
            b.AppendLine();

            return b.ToString();
        }

        // ================== 上一局 ==================

        static string BuildPreviousSession(LastSessionInfo info)
        {
            var b = new StringBuilder();

            b.AppendLine("##################################################################");
            b.AppendLine(info.Kind == SessionEndKind.Hung
                ? " ABOUT THE PREVIOUS SESSION: suspected hang"
                : " ABOUT THE PREVIOUS SESSION: did not exit cleanly");
            b.AppendLine("##################################################################");
            b.AppendLine();
            b.AppendLine("The following describes not this session but the previous run -- it never completed the");
            b.AppendLine("normal shutdown path, so this session has to report on its behalf.");
            b.AppendLine();

            if (info.Kind == SessionEndKind.Hung)
            {
                b.AppendLine($"Conclusion   : the main thread stopped advancing for about {info.StallSeconds:0} seconds and was judged hung.");
            }
            else
            {
                b.AppendLine("Conclusion   : the process vanished without going through OnApplicationQuit.");
                b.AppendLine("               A native crash, a stack overflow, running out of memory, being killed");
                b.AppendLine("               from Task Manager, or being force-killed by Steam all look identical at");
                b.AppendLine("               the Polaris layer -- they cannot be told apart any further.");
            }

            if (info.StartedAt != DateTime.MinValue)
            {
                b.AppendLine($"Started at   : {info.StartedAt:yyyy-MM-dd HH:mm:ss}");
            }

            if (info.LastAliveAt != DateTime.MinValue)
            {
                b.AppendLine($"Last alive   : {info.LastAliveAt:yyyy-MM-dd HH:mm:ss}");
            }

            b.AppendLine($"Stalled at   : {info.Where()}");

            if (info.PolarisVersion != null)
            {
                b.AppendLine($"Polaris ver. : {info.PolarisVersion}");
            }

            if (info.ReportPath != null)
            {
                b.AppendLine($"Report       : {info.ReportPath}");
            }

            if (info.ErrorKinds > 0)
            {
                b.AppendLine();
                b.AppendLine($"The previous session archived {info.ErrorKinds} classes of error"
                             + (info.StormKinds > 0 ? $" ({info.StormKinds} of them happening repeatedly)" : "")
                             + ":");
                foreach (string line in info.ErrorLines)
                {
                    b.AppendLine($"  * {line}");
                }

                if (info.MoreErrorKinds > 0)
                {
                    b.AppendLine($"  ... and {info.MoreErrorKinds} more classes; see the previous session's report file.");
                }
            }

            b.AppendLine();
            b.AppendLine("--- What to do ---");

            if (info.Kind == SessionEndKind.Hung && !string.IsNullOrEmpty(info.Activity))
            {
                b.AppendLine("* The mod named on the \"Stalled at\" line above is the prime suspect; disable it and try once.");
            }
            else
            {
                b.AppendLine("* There is no stack to read for this kind of problem, so reproduction is the only way to");
                b.AppendLine("  locate it: think about what you were doing at the end of the previous session.");
                b.AppendLine("* If you can reproduce it reliably, bisect your mods to narrow it down; if it still happens");
                b.AppendLine("  with all of them off, confirm once on a clean install.");
            }

            b.AppendLine("* If errors are listed above, start with them: an exception thrown before a crash is often");
            b.AppendLine("  an early symptom of the same root cause.");
            b.AppendLine($"* Unity's own crash dumps and logs live next to: {Env(playerLog, PlayerLogPath)}");
            b.AppendLine();

            return b.ToString();
        }

        // ================== 杂项 ==================

        static string PlayerLogPath()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppData\LocalLow", Application.companyName, Application.productName, "Player.log");

        static string Safe(Func<string> get)
        {
            try
            {
                return get() ?? "(unknown)";
            }
            catch (Exception)
            {
                return "(failed to read)";
            }
        }

        static string Indent(string text, string prefix)
        {
            if (string.IsNullOrEmpty(text))
            {
                return prefix + "(none)";
            }

            return string.Join("\n", text.Replace("\r\n", "\n").Split('\n')
                .Select(line => prefix + line).ToArray());
        }
    }
}
