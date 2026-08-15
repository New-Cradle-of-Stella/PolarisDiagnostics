using Polaris.Components;

namespace Polaris.Diagnostics
{
    /// <summary>高级诊断模块入口：在普通组件 Awake 前接管 Core 捕获结果，并安装报告、哨兵与看门狗。</summary>
    public sealed class PolarisDiagnosticsComponent : PolarisComponent
    {
        public override string Id => "PolarisDiagnostics";
        public override int Order => int.MinValue;

        public override void Bootstrap()
        {
            DiagnosticsRuntime.Configure(
                Plugin.Logger,
                typeof(Plugin).Assembly,
                MyPluginInfo.PLUGIN_GUID,
                MyPluginInfo.PLUGIN_NAME,
                MyPluginInfo.PLUGIN_VERSION,
                PolarisMeta.ReportTarget,
                () => PolarisAPI.Game.Localization.CurrentLocale,
                () => UserModToggleManager.Scan()
                    .FindAll(record => !record.Enabled)
                    .ConvertAll(record => record.DisplayName));

            DiagnosticsHost.Register(new DiagnosticsBackend());

            MainThreadBeat.Install();
            DiagnosticsConfig.Resolve();
            ErrorReportWriter.PrimeEnvironment();
            PolarisAPI.Errors.Guard(SessionSentinel.Install, "registering this session's sentinel");
            PolarisAPI.Errors.Guard(AppendPreviousSession, "archiving how the previous session ended");
            Watchdog.Install();
        }

        static void AppendPreviousSession()
        {
            if (SessionSentinel.LastSession != null)
            {
                ErrorReportWriter.AppendPreviousSession(SessionSentinel.LastSession);
            }
        }
    }
}
