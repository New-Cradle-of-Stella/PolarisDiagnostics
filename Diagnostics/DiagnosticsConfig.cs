using System;
using System.IO;
using BepInEx.Configuration;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// 崩溃/卡死检测的阈值与开关，存在 <c>BepInEx/config/Polaris/_polaris_diagnostics.cfg</c>。
    /// 刻意不走 <see cref="Settings.PolarisSettingAttribute"/>（不进游戏设置界面，且需要在
    /// <c>Awake</c> 就绑定完成，早于特性轨扫描）。绑定失败则整体退回默认值。
    /// </summary>
    internal static class DiagnosticsConfig
    {
        const string FileName = "_polaris_diagnostics.cfg";

        const string WatchdogSection = "Watchdog";
        const string StormSection = "Storm";

        // ================== 默认值 ==================
        // 偏保守：宁可漏报真卡顿，也不要把正常长加载误判为卡死。

        const bool DefaultEnabled = true;

        /// <summary>只在控制台记一行警告的阈值。到这一步不写报告、不惊扰玩家。</summary>
        const float DefaultWarnSeconds = 10f;

        /// <summary>写报告、给下一局的告知页上膛的阈值。</summary>
        const float DefaultReportSeconds = 30f;

        /// <summary>首个 <c>Update</c> 之前专用的阈值，启动期本身就会长时间不进 <c>Update</c>。</summary>
        const float DefaultBootReportSeconds = 90f;

        const bool DefaultKillOnHang = false;

        const float DefaultStormWindowSeconds = 5f;
        const int DefaultStormThreshold = 200;

        // ================== 状态 ==================

        static ConfigFile file;
        static bool resolved;

        static ConfigEntry<bool> enabled;
        static ConfigEntry<float> warnSeconds;
        static ConfigEntry<float> reportSeconds;
        static ConfigEntry<float> bootReportSeconds;
        static ConfigEntry<bool> killOnHang;
        static ConfigEntry<float> stormWindowSeconds;
        static ConfigEntry<int> stormThreshold;

        /// <summary>由 <c>Plugin.Awake</c> 在装看门狗之前调用一次；失败不抛，退回默认值。</summary>
        internal static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;

            try
            {
                Directory.CreateDirectory(DiagnosticsRuntime.ConfigDir);
                file = new ConfigFile(Path.Combine(DiagnosticsRuntime.ConfigDir, FileName), saveOnInit: true);

                enabled = file.Bind(WatchdogSection, "Enabled", DefaultEnabled,
                    "Enable hang detection (a background thread watches whether the main thread is still advancing frames). Crash detection stays active when this is off.");

                warnSeconds = file.Bind(WatchdogSection, "WarnSeconds", DefaultWarnSeconds,
                    "How many seconds of no main-thread progress before a warning line is written to the BepInEx log. Log only -- no report file, no player-facing notice.");

                reportSeconds = file.Bind(WatchdogSection, "ReportSeconds", DefaultReportSeconds,
                    "How many seconds of no main-thread progress before it is judged a suspected hang: a report file is written"
                    + " and the next session's title screen tells the player about it."
                    + " Lowering it is more sensitive, but normal long operations such as loading a save or switching scenes are then more easily misjudged.");

                bootReportSeconds = file.Bind(WatchdogSection, "BootReportSeconds", DefaultBootReportSeconds,
                    "Separate threshold used during game startup (before the first Update). In that stretch every plugin's"
                    + " Awake, the first scene load, and the game's own asset init have not finished, so long gaps before Update are normal.");

                killOnHang = file.Bind(WatchdogSection, "KillOnHang", DefaultKillOnHang,
                    "Whether to kill the game process outright once a hang is judged. Off by default: one false positive"
                    + " costs the player this session's progress, which is worse than hanging; a hung player can close the"
                    + " window themselves, and the report was already written the moment it was judged.");

                stormWindowSeconds = file.Bind(StormSection, "WindowSeconds", DefaultStormWindowSeconds,
                    "Detection window for an exception storm, in seconds. The same class of error occurring more than Threshold times inside this window counts as a persistent failure.");

                stormThreshold = file.Bind(StormSection, "Threshold", DefaultStormThreshold,
                    "Occurrence threshold for an exception storm. Throwing once per frame is roughly 60 times per second, so the default is about three seconds of throwing every frame.");
            }
            catch (Exception e)
            {
                DiagnosticsRuntime.Logger.LogWarning(
                    $"[Polaris] Failed to open {FileName}; diagnostics thresholds fall back to defaults for this session: {e.Message}");
                file = null;
            }
        }

        // ================== 读取 ==================
        // 一律 ?? 默认值，兼容 Resolve 失败时 entry 为 null 的情况。

        internal static bool WatchdogEnabled => enabled?.Value ?? DefaultEnabled;

        internal static float WarnSeconds => Sane(warnSeconds?.Value ?? DefaultWarnSeconds, 2f, DefaultWarnSeconds);

        internal static float ReportSeconds
            => Sane(reportSeconds?.Value ?? DefaultReportSeconds, 5f, DefaultReportSeconds);

        internal static float BootReportSeconds
            => Sane(bootReportSeconds?.Value ?? DefaultBootReportSeconds, 15f, DefaultBootReportSeconds);

        internal static bool KillOnHang => killOnHang?.Value ?? DefaultKillOnHang;

        internal static float StormWindowSeconds
            => Sane(stormWindowSeconds?.Value ?? DefaultStormWindowSeconds, 0.5f, DefaultStormWindowSeconds);

        internal static int StormThreshold
            => Sane(stormThreshold?.Value ?? DefaultStormThreshold, 10, DefaultStormThreshold);

        /// <summary>把手改 cfg 填出的 0 或负数当作"没填"，退回默认值。</summary>
        static float Sane(float value, float minimum, float fallback)
            => value >= minimum ? value : fallback;

        static int Sane(int value, int minimum, int fallback)
            => value >= minimum ? value : fallback;
    }
}
