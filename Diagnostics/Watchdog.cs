using System;
using System.Diagnostics;
using System.Threading;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// 卡死看门狗：后台线程监视主线程是否还在推进帧（<see cref="MainThreadBeat"/>），弥补异常捕获无法
    /// 发现死循环/死锁的缺口。两级升级：先警告，超阈值再写报告并上膛下一局告知；默认不杀进程。
    /// 该线程绝不能访问 Unity API（主线程此刻可能正卡着），所有 Unity 侧数据都经主线程采样。
    /// </summary>
    internal static class Watchdog
    {
        /// <summary>轮询间隔。判定阈值是十几秒到几十秒，一秒一次的精度远远够用。</summary>
        const int PollMillis = 1000;

        /// <summary>心跳落盘间隔。</summary>
        const int FlushMillis = 5000;

        /// <summary>看门狗自身被饿到这个程度就不作判断（系统休眠/挂起会让所有线程一起停摆）。</summary>
        const int SelfStarvationMillis = 5000;

        /// <summary>一局最多写几份卡死报告。反复卡住又反复恢复的情况下，第五份开始就没有新信息了。</summary>
        const int MaxHangReports = 4;

        /// <summary><see cref="ExpectStall"/> 允许声明的最长时间，防止一个笔误把看门狗关掉一整局。</summary>
        const double MaxExpectSeconds = 600d;

        static Thread thread;
        static bool installed;
        static volatile bool stopping;

        /// <summary>窗口失焦/被系统挂起期间为 true。见 <see cref="SetPaused"/>。</summary>
        static volatile bool paused;

        static int activeStalls;
        static long stallDeadlineMillis;
        static volatile string stallReason;
        static bool warnedAboutLeak;

        // 当前停摆事件的状态，主线程恢复后整体清零。
        static bool warned;
        static bool reported;
        static double episodePeakSeconds;

        static int hangReports;

        /// <summary>判定疑似卡死时在后台线程触发；订阅者不能碰 Unity API。</summary>
        internal static event Action<HangReport> HangSuspected;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            installed = true;

            try
            {
                thread = new Thread(Loop)
                {
                    // 后台线程，避免拖住进程退出。
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "Polaris.Watchdog",
                };
                thread.Start();
            }
            catch (Exception e)
            {
                installed = false;
                thread = null;
                DiagnosticsRuntime.Logger.LogWarning($"[Polaris] Hang detection thread failed to start; no hang judgement this session: {e.Message}");
            }
        }

        /// <summary>停掉看门狗；必须在退出流程一开始就调用，否则正常退出会被误判成卡死。</summary>
        internal static void Uninstall()
        {
            if (!installed)
            {
                return;
            }

            installed = false;
            stopping = true;

            try
            {
                thread?.Interrupt();
            }
            catch (Exception)
            {
                // 打断失败无所谓，IsBackground 线程不会拖住进程退出。
            }

            thread = null;
        }

        /// <summary>
        /// 由 <c>Plugin.OnApplicationFocus</c>/<c>OnApplicationPause</c> 调用，避免窗口失焦期间
        /// 停止 <c>Update</c> 被误判为卡死；恢复时顺手重置心跳基线。
        /// </summary>
        internal static void SetPaused(bool value)
        {
            if (!value)
            {
                MainThreadBeat.ResetBaseline();
            }

            paused = value;
        }

        /// <summary>声明接下来一段时间不推进帧是正常的；<paramref name="seconds"/> 是硬上限，防止忘记释放。</summary>
        internal static IDisposable ExpectStall(string reason, double seconds)
        {
            double clamped = seconds > 0d ? Math.Min(seconds, MaxExpectSeconds) : 1d;
            long deadline = MainThreadBeat.ElapsedMillis + (long)(clamped * 1000d);

            stallReason = reason;
            Interlocked.Increment(ref activeStalls);

            // 多个声明同时存在时取最晚的那个截止时间。
            while (true)
            {
                long current = Volatile.Read(ref stallDeadlineMillis);
                if (deadline <= current)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref stallDeadlineMillis, deadline, current) == current)
                {
                    break;
                }
            }

            return new StallToken();
        }

        /// <summary>本局判定过几次卡死。</summary>
        internal static int HangCount => hangReports;

        // ================== 主循环 ==================

        static void Loop()
        {
            long previous = MainThreadBeat.ElapsedMillis;
            long lastFlush = previous;

            while (!stopping)
            {
                try
                {
                    Thread.Sleep(PollMillis);
                }
                catch (ThreadInterruptedException)
                {
                    return;
                }

                if (stopping)
                {
                    return;
                }

                try
                {
                    long now = MainThreadBeat.ElapsedMillis;
                    long sinceLastPoll = now - previous;
                    previous = now;

                    // 心跳照刷，即使这一轮不做判定，崩溃检测需要这个记录。
                    if (now - lastFlush >= FlushMillis)
                    {
                        lastFlush = now;
                        SessionSentinel.Flush();
                    }

                    if (sinceLastPoll > SelfStarvationMillis)
                    {
                        // 自己都被停了这么久，这一轮读数不能当证据。
                        ResetEpisode(silent: true);
                        continue;
                    }

                    if (!Judgeable(now))
                    {
                        ResetEpisode(silent: true);
                        continue;
                    }

                    Judge(now);
                }
                catch (Exception)
                {
                    // 看门狗自己抛异常不能让线程死掉，也不记日志（避免每轮刷屏）。
                }
            }
        }

        /// <summary>这一轮该不该作判断。</summary>
        static bool Judgeable(long now)
        {
            if (paused || !DiagnosticsConfig.WatchdogEnabled)
            {
                return false;
            }

            // 挂着调试器时停顿通常是有人在看某一行代码。
            if (Debugger.IsAttached)
            {
                return false;
            }

            return !Suppressed(now);
        }

        /// <summary>调用方有没有声明"这段时间不推进帧是正常的"。</summary>
        static bool Suppressed(long now)
        {
            if (Volatile.Read(ref activeStalls) <= 0)
            {
                warnedAboutLeak = false;
                return false;
            }

            if (now < Volatile.Read(ref stallDeadlineMillis))
            {
                return true;
            }

            // 声明超时还没释放（可能是那段代码抛异常跳过了 Dispose），不再顺着它，避免检测被永久关掉。
            if (!warnedAboutLeak)
            {
                warnedAboutLeak = true;
                DiagnosticsRuntime.Logger.LogWarning(
                    $"[Polaris] An \"expected stall\" declaration ({stallReason ?? "unnamed"}) outlived the limit it declared"
                    + " without being released. Hang detection continues to work normally.");
            }

            return false;
        }

        static void Judge(long now)
        {
            double stall = MainThreadBeat.SecondsSinceBeat;
            bool boot = !MainThreadBeat.HasBeaten;

            double reportAt = boot ? DiagnosticsConfig.BootReportSeconds : DiagnosticsConfig.ReportSeconds;

            // 启动阶段警告线跟着报告线一起抬，避免每局都误报"启动慢"。
            double warnAt = boot
                ? Math.Max(DiagnosticsConfig.WarnSeconds, reportAt / 3d)
                : DiagnosticsConfig.WarnSeconds;

            if (stall < warnAt)
            {
                ResetEpisode(silent: false);
                return;
            }

            if (stall > episodePeakSeconds)
            {
                episodePeakSeconds = stall;
            }

            if (!warned)
            {
                warned = true;
                DiagnosticsRuntime.Logger.LogWarning(
                    $"[Polaris] The main thread has not advanced for {stall:0}s"
                    + (boot ? " (still in startup)" : $" (frame {MainThreadBeat.LastFrame})")
                    + $". Currently executing: {MainThreadBeat.ActivityChain() ?? "(not inside any Polaris instrumentation point)"}."
                    + $" Past {reportAt:0}s a hang report will be written.");

                // 立刻落盘，此刻的面包屑最接近病根，不等下一次周期性心跳。
                SessionSentinel.Flush();
            }

            if (reported || stall < reportAt)
            {
                return;
            }

            reported = true;
            Report(stall, boot);
        }

        /// <summary>主线程恢复了（或这一轮不作判断）：清掉本轮停摆事件的状态。</summary>
        static void ResetEpisode(bool silent)
        {
            if (warned && !silent)
            {
                DiagnosticsRuntime.Logger.LogMessage(
                    $"[Polaris] The main thread has recovered (it was stalled for about {episodePeakSeconds:0}s in total).");
            }

            warned = false;
            reported = false;
            episodePeakSeconds = 0d;
        }

        static void Report(double stall, bool boot)
        {
            hangReports++;

            var report = new HangReport
            {
                DetectedAt = DateTime.Now,
                StallSeconds = stall,
                LastFrame = MainThreadBeat.LastFrame,
                Scene = MainThreadBeat.SceneName,
                Activity = MainThreadBeat.ActivityChain(),
                Culprit = MainThreadBeat.CurrentOwner(),
                Index = hangReports,
                DuringBoot = boot,
            };

            // 反复卡住又反复恢复时，超过上限就不再重复写报告/刷日志。
            bool worthRecording = hangReports <= MaxHangReports;

            // 先落盘再打日志（与 ErrorRegistry/FatalRegistry 一致）。
            if (worthRecording)
            {
                ErrorReportWriter.AppendHang(report);
            }

            // 哨兵无条件更新：即使报告不再写，它仍是下一局唯一的信息来源。
            SessionSentinel.MarkHung(report);

            if (worthRecording)
            {
                Log(report);
            }

            Raise(report);

            if (DiagnosticsConfig.KillOnHang)
            {
                Kill();
            }
        }

        static void Log(HangReport report)
        {
            DiagnosticsRuntime.Logger.LogError($"[Polaris] Suspected hang: {report.OneLine()}");

            if (report.Culprit != null)
            {
                string owner;
                try
                {
                    owner = AssemblyOwnerIndex.Of(report.Culprit)?.Describe() ?? report.Culprit.GetName().Name;
                }
                catch (Exception)
                {
                    owner = "(owner could not be determined)";
                }

                DiagnosticsRuntime.Logger.LogError($"[Polaris] The code executing when it stopped responding belongs to: {owner}");
            }

            string path = ErrorReportWriter.LastWrittenPath;
            DiagnosticsRuntime.Logger.LogError(path != null
                ? $"[Polaris] Hang report: {path}"
                : "[Polaris] Failed to write the report file; the only clues are in this log.");

            DiagnosticsRuntime.Logger.LogError(
                "[Polaris] The game may already be unresponsive. The title screen will remind you about this on the next launch."
                + (DiagnosticsConfig.KillOnHang ? "" : " (Polaris will not end the game by itself; see _polaris_diagnostics.cfg)"));
        }

        static void Raise(HangReport report)
        {
            Action<HangReport> handlers = HangSuspected;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<HangReport>)handler)(report);
                }
                catch (Exception)
                {
                    // 订阅者写坏了不该连累其它订阅者或看门狗线程本身。
                }
            }
        }

        /// <summary>直接杀进程；不用 <c>Application.Quit</c>（Unity API，主线程卡着调不了）或 <c>Environment.Exit</c>（同样可能卡住）。</summary>
        static void Kill()
        {
            DiagnosticsRuntime.Logger.LogError("[Polaris] KillOnHang is enabled; ending the game process.");

            try
            {
                Process.GetCurrentProcess().Kill();
            }
            catch (Exception e)
            {
                DiagnosticsRuntime.Logger.LogError($"[Polaris] Failed to end the process; please close the game manually: {e.Message}");
            }
        }

        sealed class StallToken : IDisposable
        {
            bool released;

            public void Dispose()
            {
                if (released)
                {
                    return;
                }

                released = true;
                Interlocked.Decrement(ref activeStalls);
            }
        }
    }
}
