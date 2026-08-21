using System;
using System.Collections.Generic;
using System.Reflection;

namespace Polaris.Diagnostics
{
    /// <summary>PolarisDiagnostics 的统一公开入口；底层状态仍由 Core 的诊断契约承载。</summary>
    public static class DiagnosticsAPI
    {
        public static bool IsFatal => PolarisAPI.Errors.IsFatal;

        public static IReadOnlyList<ErrorIncident> Incidents => PolarisAPI.Errors.Session;

        public static LastSessionInfo LastSession => PolarisAPI.Health.LastSession;

        public static SessionEndKind LastSessionEnd => PolarisAPI.Health.LastSessionEnd;

        public static double SecondsSinceLastFrame => PolarisAPI.Health.SecondsSinceLastFrame;

        public static int HangCount => PolarisAPI.Health.HangCount;

        public static void Report(Exception exception, string context = null, Assembly culprit = null) =>
            PolarisAPI.Errors.Report(exception, context, culprit);

        public static IDisposable ExpectStall(string reason, double seconds = 60d) =>
            PolarisAPI.Health.ExpectStall(reason, seconds);

        public static IDisposable Activity(string what, Assembly owner = null) =>
            PolarisAPI.Health.Activity(what, owner);

        public static event Action<ErrorIncident> IncidentRecorded
        {
            add => PolarisAPI.Errors.IncidentRecorded += value;
            remove => PolarisAPI.Errors.IncidentRecorded -= value;
        }

        public static event Action<HangReport> HangSuspected
        {
            add => PolarisAPI.Health.HangSuspected += value;
            remove => PolarisAPI.Health.HangSuspected -= value;
        }
    }
}
