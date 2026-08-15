using System;
using System.Collections.Generic;
using System.Reflection;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// 本局所有错误的登记处：去重、限流、存档，推给日志/报告文件/下游订阅者。
    /// 有防重入闸、并发锁、硬上限与限流，宁可少记一条也不拖累游戏本体。
    /// </summary>
    internal static class ErrorRegistry
    {
        /// <summary>本局最多归档多少种错误，超出后只计数，防止无上限吃掉内存和日志。</summary>
        const int MaxDistinctIncidents = 64;

        /// <summary>每秒最多分析多少<b>新</b>指纹。挡的是启动期成片失败造成的雪崩。</summary>
        const int NewIncidentsPerSecond = 8;

        static readonly object Gate = new();
        static readonly Dictionary<string, ErrorIncident> byFingerprint = new(StringComparer.Ordinal);
        static readonly List<ErrorIncident> incidents = new();

        /// <summary>防重入：写报告失败会被自己的日志监听器再次抓到。用 ThreadStatic 避免跨线程互相挡住。</summary>
        [ThreadStatic]
        static bool inside;

        static DateTime windowStart;
        static int windowCount;

        /// <summary>被上限挡掉、没有归档的错误种类数。</summary>
        internal static int Suppressed { get; private set; }

        /// <summary>已经被判定为持续性故障（异常风暴）的错误种类数。见 <see cref="NoteRepeat"/>。</summary>
        internal static int Storms { get; private set; }

        /// <summary>纯原版错误的累计次数。只计数不建档，退出时汇总成一行。</summary>
        internal static long VanillaOnly { get; private set; }

        /// <summary>Debug.LogError / 插件 LogError 这类无异常错误的次数，只计数不建档。</summary>
        internal static long LoggedErrors { get; private set; }

        /// <summary>记若干次非异常的错误日志。</summary>
        internal static void CountLoggedErrors(long count)
        {
            if (count <= 0)
            {
                return;
            }

            lock (Gate)
            {
                LoggedErrors += count;
            }
        }

        /// <summary>本局已归档的错误（按指纹去重），按首次出现顺序。</summary>
        internal static IReadOnlyList<ErrorIncident> Incidents => incidents;

        /// <summary>有新错误归档时触发。订阅者抛异常会被吞掉，不影响其它订阅者与后续流程。</summary>
        internal static event Action<ErrorIncident> Recorded;

        // ================== 提交 ==================

        /// <summary>提交一个异常对象。<paramref name="culprit"/> 可空，非空表示调用方直接点名。</summary>
        internal static void Submit(Exception exception, string context, Assembly culprit)
        {
            if (exception != null)
            {
                Guarded(() => ErrorAnalyzer.Analyze(exception, context, culprit));
            }
        }

        /// <summary>提交一条只有文本的错误（Unity 日志回调那一路）。</summary>
        internal static void Submit(string condition, string stackTrace, string context)
        {
            if (!string.IsNullOrEmpty(condition))
            {
                Guarded(() => ErrorAnalyzer.Analyze(condition, stackTrace, context));
            }
        }

        /// <summary>两条提交路径共用的外壳：挡重入、持锁归档、吞掉分析自身的异常。</summary>
        static void Guarded(Func<ErrorIncident> analyze)
        {
            if (inside)
            {
                return;
            }

            inside = true;
            try
            {
                lock (Gate)
                {
                    Record(analyze);
                }
            }
            catch (Exception)
            {
                // 分析本身炸了：不能再往外抛或记日志（会绕回监听器），静默放弃这一条。
            }
            finally
            {
                inside = false;
            }
        }

        // ================== 归档 ==================

        static void Record(Func<ErrorIncident> analyze)
        {
            ErrorIncident incident = analyze();
            if (incident == null)
            {
                return;
            }

            // 已见过这一类：只累加，不重复写日志/报告。
            if (byFingerprint.TryGetValue(incident.Fingerprint, out ErrorIncident existing))
            {
                existing.Count++;
                existing.LastSeen = incident.LastSeen;
                NoteRepeat(existing);
                return;
            }

            // 和模组无关的错误只计数，不归档给玩家看。
            if (!incident.Verdict.IsModRelated)
            {
                VanillaOnly++;
                return;
            }

            if (incidents.Count >= MaxDistinctIncidents || !WithinRateLimit())
            {
                Suppressed++;
                return;
            }

            incident.Index = incidents.Count + 1;
            byFingerprint[incident.Fingerprint] = incident;
            incidents.Add(incident);

            // 先落盘再打日志：日志要报告文件路径，写失败也不能撒谎说"已写入"。
            ErrorReportWriter.Append(incident);
            ErrorLogFormatter.Log(incident);
            Raise(incident);
        }

        /// <summary>
        /// 判断已归档错误是否升级为持续性故障：同一类错误在
        /// <see cref="DiagnosticsConfig.StormWindowSeconds"/> 窗口内超过 <see cref="DiagnosticsConfig.StormThreshold"/> 次。
        /// 每类错误只判一次，<see cref="ErrorIncident.IsStorm"/> 一旦为 true 就不再重判。
        /// </summary>
        static void NoteRepeat(ErrorIncident existing)
        {
            if (existing.IsStorm)
            {
                return;
            }

            DateTime now = existing.LastSeen;

            if (existing.StormWindowCount == 0
                || (now - existing.StormWindowStart).TotalSeconds > DiagnosticsConfig.StormWindowSeconds)
            {
                existing.StormWindowStart = now;
                existing.StormWindowCount = 1;
                return;
            }

            if (++existing.StormWindowCount < DiagnosticsConfig.StormThreshold)
            {
                return;
            }

            existing.IsStorm = true;
            existing.StormDetectedAt = now;
            existing.StormBurst = existing.StormWindowCount;
            Storms++;

            ErrorReportWriter.AppendStorm(existing);

            DiagnosticsRuntime.Logger.LogError(
                $"[Polaris] Event #{existing.Index} is happening repeatedly"
                + $" ({existing.StormBurst} times within {DiagnosticsConfig.StormWindowSeconds:0.#}s, {existing.Count} in total): "
                + $"{existing.OneLine()}");
            DiagnosticsRuntime.Logger.LogError(
                "[Polaris] This class of error most likely lives in code that runs every frame; the matching feature is now completely broken for this session.");
        }

        static bool WithinRateLimit()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - windowStart).TotalSeconds >= 1d)
            {
                windowStart = now;
                windowCount = 0;
            }

            return ++windowCount <= NewIncidentsPerSecond;
        }

        static void Raise(ErrorIncident incident)
        {
            Action<ErrorIncident> handlers = Recorded;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<ErrorIncident>)handler)(incident);
                }
                catch (Exception)
                {
                    // 订阅者写坏了不该连累其它订阅者。
                }
            }
        }

        // ================== 快照 ==================

        /// <summary>不可变快照，供看门狗线程读取，避免直接遍历主线程正在改动的 <c>incidents</c> 列表。</summary>
        internal sealed class Snapshot
        {
            internal int Kinds;
            internal int More;
            internal int Storms;
            internal List<string> Lines = new();
        }

        /// <summary>取一份快照，最多带 <paramref name="maxLines"/> 条一行式摘要。可从任意线程调用。</summary>
        internal static Snapshot Take(int maxLines)
        {
            var snapshot = new Snapshot();

            lock (Gate)
            {
                snapshot.Kinds = incidents.Count;
                snapshot.Storms = Storms;
                snapshot.More = Math.Max(0, incidents.Count - maxLines);

                int take = Math.Min(maxLines, incidents.Count);
                for (int i = 0; i < take; i++)
                {
                    snapshot.Lines.Add(incidents[i].OneLine());
                }
            }

            return snapshot;
        }

        // ================== 汇总 ==================

        /// <summary>退出时的一行汇总；没有模组相关错误时返回 null（一字不提）。</summary>
        internal static string Summary()
        {
            lock (Gate)
            {
                if (incidents.Count == 0)
                {
                    return null;
                }

                long total = 0;
                foreach (ErrorIncident incident in incidents)
                {
                    total += incident.Count;
                }

                string text = $"[Polaris] Recorded {incidents.Count} classes of error this session, {total} occurrences in total.";

                if (Storms > 0)
                {
                    text += $" {Storms} of them are happening repeatedly.";
                }

                if (Suppressed > 0)
                {
                    text += $" {Suppressed} more classes were not recorded because the cap was reached.";
                }

                if (VanillaOnly > 0)
                {
                    text += $" {VanillaOnly} more mod-unrelated errors were not archived.";
                }

                return text;
            }
        }
    }
}
