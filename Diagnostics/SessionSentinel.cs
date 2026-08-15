using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Polaris.Diagnostics
{
    /// <summary>
    /// 会话哨兵：启动时在 <see cref="DiagnosticsRuntime.StateDir"/> 写标记文件，由看门狗线程定期刷心跳
    /// （时间/帧号/场景/面包屑/错误摘要），正常退出时删除；下次启动文件还在即说明未正常结束。
    /// 写盘直接覆盖（不做原子替换），因为文件存在本身就是结论，内容截断可接受。
    /// </summary>
    internal static class SessionSentinel
    {
        /// <summary>文件名带 pid，避免多开游戏时多个进程互相盖写哨兵文件。</summary>
        const string FilePrefix = "_session_";
        const string FileSuffix = ".txt";

        /// <summary>心跳里最多带几条错误摘要，和告知页能显示的条数对齐。</summary>
        const int MaxErrorLines = 5;

        /// <summary>判不出对应进程还在不在时，超过这个天数的哨兵文件一律当成陈旧的清掉。</summary>
        const int StaleAfterDays = 1;

        const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        static readonly object Gate = new();

        static bool installed;
        static bool disabled;
        static string path;

        static DateTime processStart;
        static int processId;

        // 卡死判定结果：一旦判定就一直保留，即使主线程后来恢复了。
        static bool hung;
        static double hungStallSeconds;
        static string hungActivity;

        /// <summary>上一局的结局；上一局正常退出（或这是第一次运行）时为 null。</summary>
        internal static LastSessionInfo LastSession { get; private set; }

        /// <summary>读上一局哨兵时失败（目录不存在/权限不足）；此时"没找到"不等于"正常退出"。</summary>
        static bool readFailed;

        /// <summary>上一局结束方式；<see cref="SessionEndKind.Clean"/> 也涵盖"第一次运行"这种情况。</summary>
        internal static SessionEndKind LastEnd
        {
            get
            {
                if (LastSession != null)
                {
                    return LastSession.Kind;
                }

                return !installed || readFailed ? SessionEndKind.Unknown : SessionEndKind.Clean;
            }
        }

        /// <summary>由 <c>Plugin.Awake</c> 尽早调用：先读上一局哨兵再写本局的，顺序不能反（pid 会被回收复用）。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            installed = true;

            try
            {
                Process self = Process.GetCurrentProcess();
                processId = self.Id;
                processStart = self.StartTime;
            }
            catch (Exception)
            {
                processId = 0;
                processStart = DateTime.Now;
            }

            LastSession = ReadStale();
            Flush();
        }

        /// <summary>刷一次心跳，由看门狗线程调用（放在后台线程避免主线程被同步写盘卡住）。</summary>
        internal static void Flush()
        {
            if (disabled)
            {
                return;
            }

            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(DiagnosticsRuntime.StateDir);
                    File.WriteAllText(FilePath(), Compose(), Encoding.UTF8);
                }
                catch (Exception)
                {
                    // 写不了盘不会自己好，不重试，也不记日志（避免每几秒刷一遍同样的抱怨）。
                    disabled = true;
                }
            }
        }

        /// <summary>记下本局被判定为卡死并立刻落盘，不等下一次周期性心跳。</summary>
        internal static void MarkHung(HangReport report)
        {
            if (report == null)
            {
                return;
            }

            hung = true;
            hungStallSeconds = report.StallSeconds;
            hungActivity = report.Activity;

            Flush();
        }

        /// <summary>正常退出时删掉哨兵；必须在 <see cref="PolarisErrorNotice.PersistPending"/> 之后调用。</summary>
        internal static void Close()
        {
            lock (Gate)
            {
                try
                {
                    string file = FilePath();
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // 删不掉只会让下一局多报一次误判，不值得为此中断正在退出的流程。
                }
            }
        }

        static string FilePath()
            => path ??= Path.Combine(DiagnosticsRuntime.StateDir, FilePrefix + processId + FileSuffix);

        // ================== 写 ==================

        static string Compose()
        {
            var b = new StringBuilder();

            b.Append("polaris_session=1\n");
            b.Append("pid=").Append(processId).Append('\n');
            b.Append("started=").Append(processStart.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\n');
            b.Append("polaris=").Append(DiagnosticsRuntime.PluginVersion).Append('\n');
            b.Append("kind=").Append(hung ? "hung" : "running").Append('\n');
            b.Append("alive=").Append(DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\n');
            b.Append("frame=").Append(MainThreadBeat.LastFrame).Append('\n');
            b.Append("scene=").Append(Clean(MainThreadBeat.SceneName)).Append('\n');

            // 已判定卡死时用当时固定的面包屑，而非主线程恢复后的实时值。
            b.Append("activity=").Append(Clean(hung ? hungActivity : MainThreadBeat.ActivityChain())).Append('\n');
            b.Append("stall=").Append(hungStallSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append('\n');
            b.Append("report=").Append(Clean(ErrorReportWriter.LastWrittenPath)).Append('\n');

            ErrorRegistry.Snapshot snapshot = ErrorRegistry.Take(MaxErrorLines);
            b.Append("errors=").Append(snapshot.Kinds).Append('\n');
            b.Append("more=").Append(snapshot.More).Append('\n');
            b.Append("storms=").Append(snapshot.Storms).Append('\n');

            for (int i = 0; i < snapshot.Lines.Count; i++)
            {
                b.Append("error").Append(i + 1).Append('=').Append(Clean(snapshot.Lines[i])).Append('\n');
            }

            return b.ToString();
        }

        /// <summary>去掉换行，避免撕裂 <c>键=值</c> 格式（异常消息常带换行）。</summary>
        static string Clean(string value)
            => string.IsNullOrEmpty(value) ? "" : value.Replace('\r', ' ').Replace('\n', ' ');

        // ================== 读 ==================

        /// <summary>扫状态目录找出上一局留下的哨兵（确认不属于仍在跑的进程），读完即删。</summary>
        static LastSessionInfo ReadStale()
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(DiagnosticsRuntime.StateDir))
                {
                    // 目录应已由 EnsureDirectories 建好；不存在说明那一步失败了（只读/权限不足）。
                    readFailed = true;
                    return null;
                }

                files = new List<string>(
                    Directory.GetFiles(DiagnosticsRuntime.StateDir, FilePrefix + "*" + FileSuffix));
            }
            catch (Exception)
            {
                readFailed = true;
                return null;
            }

            LastSessionInfo newest = null;
            var consumed = new List<string>();

            foreach (string file in files)
            {
                Dictionary<string, string> fields = TryParse(file);
                if (fields == null)
                {
                    continue;
                }

                int pid = Int(fields, "pid");
                DateTime started = Time(fields, "started");
                DateTime alive = Time(fields, "alive");

                // 自己的 pid 不可能是别的进程。
                bool mine = pid == processId;
                bool old = alive != DateTime.MinValue && (DateTime.Now - alive).TotalDays >= StaleAfterDays;

                if (!mine && !old && StillRunning(pid, started))
                {
                    // 玩家同时开着另一份游戏，其哨兵不是崩溃证据。
                    continue;
                }

                consumed.Add(file);

                LastSessionInfo info = Build(fields, alive, started);
                if (newest == null || info.LastAliveAt > newest.LastAliveAt)
                {
                    newest = info;
                }
            }

            foreach (string file in consumed)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // 删不掉就下次再删，LastSession 已读出，功能不受影响。
                }
            }

            return newest;
        }

        static LastSessionInfo Build(Dictionary<string, string> fields, DateTime alive, DateTime started)
        {
            bool wasHung = string.Equals(Str(fields, "kind"), "hung", StringComparison.Ordinal);

            var lines = new List<string>(MaxErrorLines);
            for (int i = 1; i <= MaxErrorLines; i++)
            {
                string line = Str(fields, "error" + i);
                if (!string.IsNullOrEmpty(line))
                {
                    lines.Add(line);
                }
            }

            return new LastSessionInfo
            {
                Kind = wasHung ? SessionEndKind.Hung : SessionEndKind.NotClosed,
                StartedAt = started,
                LastAliveAt = alive,
                LastFrame = Int(fields, "frame"),
                Scene = Str(fields, "scene"),
                Activity = Str(fields, "activity"),
                StallSeconds = Num(fields, "stall"),
                ReportPath = Str(fields, "report"),
                PolarisVersion = Str(fields, "polaris"),
                ErrorKinds = Int(fields, "errors"),
                MoreErrorKinds = Int(fields, "more"),
                StormKinds = Int(fields, "storms"),
                ErrorLines = lines,
            };
        }

        static Dictionary<string, string> TryParse(string file)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception)
            {
                return null;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                int split = line.IndexOf('=');
                if (split > 0)
                {
                    fields[line.Substring(0, split)] = line.Substring(split + 1);
                }
            }

            // 认不出格式就不认，防止把目录里别的文件误当成哨兵。
            return fields.ContainsKey("polaris_session") ? fields : null;
        }

        /// <summary>判断 pid 对应的进程是否还在跑；判不出来时回答"是"（宁可漏报也不误判崩溃）。</summary>
        static bool StillRunning(int pid, DateTime startedAt)
        {
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return false;
                }

                try
                {
                    // pid 会被回收复用，还要核对启动时间。
                    if (startedAt != DateTime.MinValue
                        && Math.Abs((process.StartTime - startedAt).TotalSeconds) > 5d)
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                    // 读不到启动时间（权限不足）就只按 pid 判断。
                }

                return true;
            }
            catch (ArgumentException)
            {
                // 没有这个 pid：上一局真的没了。
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        static string Str(Dictionary<string, string> fields, string key)
            => fields.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value) ? value : null;

        static int Int(Dictionary<string, string> fields, string key)
            => fields.TryGetValue(key, out string value)
               && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;

        static double Num(Dictionary<string, string> fields, string key)
            => fields.TryGetValue(key, out string value)
               && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 0d;

        static DateTime Time(Dictionary<string, string> fields, string key)
            => fields.TryGetValue(key, out string value)
               && DateTime.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out DateTime parsed)
                ? parsed
                : DateTime.MinValue;
    }
}
