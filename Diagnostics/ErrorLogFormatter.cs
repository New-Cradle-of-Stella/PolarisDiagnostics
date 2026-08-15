using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace Polaris.Diagnostics
{
    /// <summary>把一条 <see cref="ErrorIncident"/> 写成控制台里的几行结论；完整堆栈不往控制台倒，留给报告文件。</summary>
    internal static class ErrorLogFormatter
    {
        /// <summary>嫌疑人最多念几个，多了控制台就没法看了。</summary>
        const int MaxSuspectLines = 4;

        internal static void Log(ErrorIncident incident)
        {
            ErrorVerdict verdict = incident.Verdict;
            ManualLogSource logger = DiagnosticsRuntime.Logger;

            string head = incident.Context == null
                ? $"[Polaris] Caught an error: {Short(incident.ExceptionType)}"
                : $"[Polaris] Error during {incident.Context}: {Short(incident.ExceptionType)}";

            if (!string.IsNullOrEmpty(incident.Message))
            {
                head += $" -- {Clip(incident.Message, 160)}";
            }

            logger.LogError(head);
            logger.LogError($"[Polaris] {verdict.Headline()} (confidence: {verdict.ConfidenceLabel}) {verdict.Reason}");

            if (verdict.Diagnosis != null)
            {
                logger.LogError($"[Polaris] Diagnosis: {verdict.Diagnosis}");
            }

            // 已点名主责时不复述嫌疑人，仅在还有其他嫌疑人时列出。
            var others = verdict.Suspects.Where(suspect => suspect.Owner != verdict.Culprit).ToList();
            if (others.Count > 0)
            {
                logger.LogError($"[Polaris] Other suspects: {Join(others.Take(MaxSuspectLines))}"
                                + (others.Count > MaxSuspectLines ? $" and {others.Count} more" : string.Empty));
            }

            string report = ErrorReportWriter.LastWrittenPath;
            logger.LogError(report != null
                ? $"[Polaris] Full report (with stack and installed mod list): {report}"
                : "[Polaris] Failed to write the report file; the full stack can only be found in this log and Unity's Player.log.");
        }

        static string Join(IEnumerable<ErrorSuspect> suspects)
            => string.Join(", ", suspects.Select(s => s.Owner.Describe()).ToArray());

        /// <summary>只留类名，控制台一行放不下 <c>System.NullReferenceException</c> 那种全名。</summary>
        static string Short(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return "unknown exception";
            }

            int dot = typeName.LastIndexOf('.');
            return dot >= 0 && dot < typeName.Length - 1 ? typeName.Substring(dot + 1) : typeName;
        }

        static string Clip(string text, int max)
        {
            string flat = text.Replace('\r', ' ').Replace('\n', ' ');
            return flat.Length <= max ? flat : flat.Substring(0, max) + "...";
        }
    }
}
