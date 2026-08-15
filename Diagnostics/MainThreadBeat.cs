using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEngine.SceneManagement;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// 主线程的心跳信号与"正在执行谁的代码"面包屑，供 <see cref="Watchdog"/> 在后台线程判断卡死。
    /// 写入方只有主线程、读取方只有看门狗线程，故不加锁，改用 <see cref="Volatile"/> 读写。
    /// </summary>
    internal static class MainThreadBeat
    {
        /// <summary>用 <see cref="Stopwatch"/> 计时（单调，不受系统时间/回绕影响）。</summary>
        static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>面包屑栈的深度上限。嵌套超过这个数只累加深度、不再占槽（见 <see cref="Push"/>）。</summary>
        const int MaxDepth = 8;

        /// <summary>每隔多少帧采一次当前场景名。采样而不是每帧读，是因为 <c>Scene.name</c> 会分配字符串。</summary>
        const int SceneSampleFrames = 30;

        static int mainThreadId;
        static bool installed;

        static long beatMillis;
        static int beatFrame;
        static bool beaten;
        static string sceneName;

        static readonly string[] activities = new string[MaxDepth];
        static readonly Assembly[] owners = new Assembly[MaxDepth];
        static int depth;

        /// <summary>由 <c>Plugin.Awake</c> 最先调用，记住主线程是哪一个并给心跳一个初值。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            installed = true;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Volatile.Write(ref beatMillis, Clock.ElapsedMilliseconds);
        }

        /// <summary>面包屑栈只能由主线程写；非主线程调用 <see cref="Enter"/> 会直接返回空作用域。</summary>
        static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == mainThreadId;

        // ================== 心跳 ==================

        /// <summary>由 <c>Plugin.Update</c> 每帧调用。必须便宜：一次 Stopwatch 读 + 两次字段写。</summary>
        internal static void Beat(int frame)
        {
            Volatile.Write(ref beatMillis, Clock.ElapsedMilliseconds);
            beatFrame = frame;
            beaten = true;

            if (frame % SceneSampleFrames == 0)
            {
                SampleScene();
            }
        }

        /// <summary>
        /// 主线程本来就该推进却被外部原因停下（窗口失焦、系统休眠）之后重新开始跑时，
        /// 由主线程调一次把基线抹平——否则看门狗会把"停在后台的那五分钟"当成卡死。
        /// </summary>
        internal static void ResetBaseline()
        {
            Volatile.Write(ref beatMillis, Clock.ElapsedMilliseconds);
        }

        /// <summary>看门狗线程用的单调时钟读数，毫秒。</summary>
        internal static long ElapsedMillis => Clock.ElapsedMilliseconds;

        /// <summary>主线程上一次推进到现在过了几秒。</summary>
        internal static double SecondsSinceBeat
            => Math.Max(0L, Clock.ElapsedMilliseconds - Volatile.Read(ref beatMillis)) / 1000d;

        /// <summary>主线程是否已经至少推进过一帧（用来区分"启动阶段"和"游戏中"，两者阈值不同）。</summary>
        internal static bool HasBeaten => beaten;

        /// <summary>最后一次推进时的帧号。</summary>
        internal static int LastFrame => beatFrame;

        /// <summary>最近采到的场景名；还没采到为 null。</summary>
        internal static string SceneName => sceneName;

        static void SampleScene()
        {
            try
            {
                sceneName = SceneManager.GetActiveScene().name;
            }
            catch (Exception)
            {
                // 场景名只是线索，读不到就不读，不能让每帧心跳抛异常。
            }
        }

        // ================== 面包屑 ==================

        /// <summary>进入一段"正在替某个模组执行代码"的区间；返回值为 struct 作用域，using 不装箱。</summary>
        internal static Scope Enter(string what, Assembly owner)
        {
            if (string.IsNullOrEmpty(what) || !OnMainThread)
            {
                return default;
            }

            Push(what, owner);
            return new Scope(true);
        }

        static void Push(string what, Assembly owner)
        {
            int d = Math.Max(0, depth);

            if (d < MaxDepth)
            {
                // 先写槽再发布深度，确保看门狗读到新深度时槽已有内容。
                activities[d] = what;
                owners[d] = owner;
            }

            Volatile.Write(ref depth, d + 1);
        }

        static void Pop()
        {
            int d = Volatile.Read(ref depth) - 1;
            if (d < 0)
            {
                Volatile.Write(ref depth, 0);
                return;
            }

            // 先发布深度再清槽（与 Push 相反），保证看门狗读到的深度总指向有效槽。
            Volatile.Write(ref depth, d);

            if (d < MaxDepth)
            {
                activities[d] = null;
                owners[d] = null;
            }
        }

        /// <summary>栈顶那一条的责任程序集。没有埋点、或调用方没给出责任方时为 null。</summary>
        internal static Assembly CurrentOwner()
        {
            int top = Top();
            return top >= 0 ? owners[top] : null;
        }

        /// <summary>整条面包屑链，由外到内连接，比只看栈顶更能定位卡住的位置。</summary>
        internal static string ActivityChain()
        {
            int d = Volatile.Read(ref depth);
            if (d <= 0)
            {
                return null;
            }

            int used = Math.Min(d, MaxDepth);
            var parts = new List<string>(used);
            for (int i = 0; i < used; i++)
            {
                string part = activities[i];
                if (!string.IsNullOrEmpty(part))
                {
                    parts.Add(part);
                }
            }

            if (parts.Count == 0)
            {
                return null;
            }

            string chain = string.Join(" -> ", parts.ToArray());
            return d > MaxDepth ? chain + $" -> ... ({d - MaxDepth} more levels)" : chain;
        }

        static int Top()
        {
            int d = Volatile.Read(ref depth);
            return d > 0 ? Math.Min(d, MaxDepth) - 1 : -1;
        }

        /// <summary><see cref="Enter"/> 的作用域；非主线程或空标题时为 default，Dispose 什么都不做。</summary>
        internal readonly struct Scope : IDisposable
        {
            readonly bool active;

            internal Scope(bool active) => this.active = active;

            public void Dispose()
            {
                if (active)
                {
                    Pop();
                }
            }
        }
    }
}
