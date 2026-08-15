using System;
using System.Collections.Generic;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// 本局致命错误的登记处：落日志、写报告、给标题画面的 <see cref="PolarisFatalNotice"/> 上膛。
    /// 独立于 <see cref="ErrorRegistry"/>，因为致命错误一条都不能被去重/限流过滤掉。
    /// </summary>
    internal static class FatalRegistry
    {
        /// <summary>最多留几条完整记录，超出仍计入 <see cref="Count"/>，防止内存被吃光。</summary>
        const int MaxRetained = 8;

        static readonly List<FatalError> retained = new(MaxRetained);

        /// <summary>并发闸：API 公开，可能有多个线程同时报致命错误，防止 <see cref="retained"/> 被撕坏。</summary>
        static readonly object Gate = new();

        /// <summary>防重入（同 <see cref="ErrorRegistry"/>）：写报告失败的日志不该绕回来再报一条。</summary>
        [ThreadStatic]
        static bool inside;

        /// <summary>本局报出过的致命错误总数（含超出 <see cref="MaxRetained"/> 未留存的）。</summary>
        internal static int Count { get; private set; }

        /// <summary>本局是否已经被判死刑。</summary>
        internal static bool Any => Count > 0;

        /// <summary>第一条致命错误（告知页展示的那一条），后续往往是它的连锁反应。</summary>
        internal static FatalError First => retained.Count > 0 ? retained[0] : null;

        /// <summary>除 <see cref="First"/> 之外还有几条；告知页据此显示"另有 N 条"。</summary>
        internal static int OtherCount => Math.Max(0, Count - 1);

        /// <summary>报告文件路径；一次都没写成功过为 null。</summary>
        internal static string ReportPath { get; private set; }

        internal static void Raise(FatalError fatal)
        {
            if (fatal == null || inside)
            {
                return;
            }

            inside = true;
            try
            {
                lock (Gate)
                {
                    Count++;
                    if (retained.Count < MaxRetained)
                    {
                        retained.Add(fatal);
                    }

                    // 先落盘再打日志：日志要报出报告文件位置，写失败不能撒谎说"已写入"。
                    ErrorReportWriter.AppendFatal(fatal);
                    ReportPath = ErrorReportWriter.LastWrittenPath;

                    Log(fatal);
                }
            }
            catch (Exception)
            {
                // 登记本身炸了：不能再往外抛或记日志，静默放弃，但 Count 已加过，告知页仍会拦住玩家。
            }
            finally
            {
                inside = false;
            }
        }

        static void Log(FatalError fatal)
        {
            // 用 LogError 而非 LogFatal：后者会被 ErrorCapture 的监听器再建一条重复事件档。
            DiagnosticsRuntime.Logger.LogError(
                $"[Polaris] Fatal error (reported by {fatal.Source}): {fatal.Reason?.ForReport}");

            foreach (string detail in fatal.Details)
            {
                DiagnosticsRuntime.Logger.LogError($"[Polaris]   * {detail}");
            }

            DiagnosticsRuntime.Logger.LogError(
                "[Polaris] This session will not continue: the title screen will block the menu and ask the player to quit. "
                + (ReportPath != null ? $"Report: {ReportPath}" : "(failed to write the report file; see the lines above for details)"));
        }
    }
}
