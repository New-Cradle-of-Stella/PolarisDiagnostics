using System;
using System.Collections.Generic;
using System.Reflection;

namespace Polaris.Diagnostics
{
    /// <summary>PolarisDiagnostics 对 Core 诊断契约的完整实现。</summary>
    internal sealed class DiagnosticsBackend : IDiagnosticsBackend
    {
        public bool IsFatal => FatalRegistry.Any;
        public IReadOnlyList<ErrorIncident> Incidents => ErrorRegistry.Incidents;
        public LastSessionInfo LastSession => SessionSentinel.LastSession;
        public SessionEndKind LastSessionEnd => SessionSentinel.LastEnd;
        public FatalError FirstFatal => FatalRegistry.First;
        public int OtherFatalCount => FatalRegistry.OtherCount;
        public string FatalReportPath => FatalRegistry.ReportPath;
        public string LastWrittenReportPath => ErrorReportWriter.LastWrittenPath;
        public double SecondsSinceLastFrame => MainThreadBeat.SecondsSinceBeat;
        public int HangCount => Watchdog.HangCount;

        public event Action<ErrorIncident> IncidentRecorded
        {
            add => ErrorRegistry.Recorded += value;
            remove => ErrorRegistry.Recorded -= value;
        }

        public event Action<HangReport> HangSuspected
        {
            add => Watchdog.HangSuspected += value;
            remove => Watchdog.HangSuspected -= value;
        }

        public void Report(Exception exception, string context, Assembly culprit)
            => ErrorRegistry.Submit(exception, context, culprit);

        public void ReportLog(string condition, string stackTrace, string context)
            => ErrorRegistry.Submit(condition, stackTrace, context);

        public void RecordLoggedErrors(long count)
            => ErrorRegistry.CountLoggedErrors(count);

        public void RaiseFatal(FatalError fatal) => FatalRegistry.Raise(fatal);

        public IDisposable ExpectStall(string reason, double seconds)
            => Watchdog.ExpectStall(reason, seconds);

        public IDisposable Activity(string what, Assembly owner)
            => MainThreadBeat.Enter(what, owner);

        public void Beat(int frameCount) => MainThreadBeat.Beat(frameCount);
        public void SetPaused(bool paused) => Watchdog.SetPaused(paused);

        public void RecordCallbackInvocation(string ownerGuid, string context, double millis)
            => CallbackDiagnostics.RecordInvocation(ownerGuid, context, millis);

        public void RecordCallbackException(string ownerGuid, string context)
            => CallbackDiagnostics.RecordException(ownerGuid, context);

        public void Stop() => Watchdog.Uninstall();
        public string Summary() => ErrorRegistry.Summary();
        public void CloseSession() => SessionSentinel.Close();
    }
}
