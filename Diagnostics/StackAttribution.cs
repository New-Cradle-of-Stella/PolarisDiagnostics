using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace Polaris.Diagnostics
{
    /// <summary>走堆栈把每一帧标上归属，并查出沿途原版方法被谁改过；同时支持异常对象与纯文本两种来路。</summary>
    internal static class StackAttribution
    {
        /// <summary>堆栈再深也没有分析价值，超过这个数就截断，免得报告变成裹脚布。</summary>
        const int MaxFrames = 48;

        // ================== 有 Exception 对象的那条路 ==================

        /// <summary>从异常对象走栈，信息最全，能直接做补丁反查与 DMD 帧还原。</summary>
        internal static List<ErrorFrame> FromException(Exception exception)
        {
            var frames = new List<ErrorFrame>();
            if (exception == null)
            {
                return frames;
            }

            StackFrame[] raw;
            try
            {
                raw = new StackTrace(exception, false).GetFrames();
            }
            catch (Exception)
            {
                return frames;
            }

            if (raw == null)
            {
                return frames;
            }

            foreach (StackFrame frame in raw)
            {
                if (frames.Count >= MaxFrames)
                {
                    break;
                }

                MethodBase method = ResolveMethod(frame);
                if (method == null)
                {
                    continue;
                }

                Type declaring = SafeDeclaringType(method);
                var entry = new ErrorFrame
                {
                    TypeName = declaring?.FullName ?? "<unknown type>",
                    MethodName = method.Name,
                    Owner = AssemblyOwnerIndex.Of(declaring?.Assembly),
                };

                Annotate(entry, method);
                frames.Add(entry);
            }

            return frames;
        }

        /// <summary>把一帧解析成方法；打过 Harmony 补丁的帧须先还原成原始方法，否则归属只会是"动态生成"。</summary>
        static MethodBase ResolveMethod(StackFrame frame)
        {
            try
            {
                MethodBase original = Harmony.GetOriginalMethodFromStackframe(frame);
                if (original != null)
                {
                    return original;
                }
            }
            catch (Exception)
            {
                // 还原失败就用原始帧，能读多少读多少。
            }

            try
            {
                return frame.GetMethod();
            }
            catch (Exception)
            {
                return null;
            }
        }

        static Type SafeDeclaringType(MethodBase method)
        {
            try
            {
                return method.DeclaringType;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ================== 只有字符串的那条路 ==================

        /// <summary>解析 Unity/Mono 字符串堆栈；拿不到 <see cref="MethodBase"/>，归属靠命名空间/名字反查。</summary>
        internal static List<ErrorFrame> FromText(string stackTrace)
        {
            var frames = new List<ErrorFrame>();
            if (string.IsNullOrEmpty(stackTrace))
            {
                return frames;
            }

            foreach (string line in stackTrace.Split('\n'))
            {
                if (frames.Count >= MaxFrames)
                {
                    break;
                }

                ErrorFrame frame = ParseLine(line);
                if (frame != null)
                {
                    frames.Add(frame);
                }
            }

            return frames;
        }

        static ErrorFrame ParseLine(string line)
        {
            string text = line.Trim();
            if (text.Length == 0)
            {
                return null;
            }

            if (text.StartsWith("at ", StringComparison.Ordinal))
            {
                text = text.Substring(3).TrimStart();
            }

            // 签名是第一个左括号之前的部分。
            int paren = text.IndexOf('(');
            string signature = (paren > 0 ? text.Substring(0, paren) : text).Trim();
            if (signature.Length == 0)
            {
                return null;
            }

            string typeName;
            string methodName;

            // 冒号形式（Unity）和点号形式（Mono）混在一起出现。
            int colon = signature.LastIndexOf(':');
            int dot = signature.LastIndexOf('.');

            if (colon > 0 && colon > dot)
            {
                typeName = signature.Substring(0, colon);
                methodName = signature.Substring(colon + 1);
            }
            else if (dot > 0)
            {
                typeName = signature.Substring(0, dot);
                methodName = signature.Substring(dot + 1);
            }
            else
            {
                return null;
            }

            var entry = new ErrorFrame
            {
                TypeName = typeName,
                MethodName = methodName,
                Owner = AssemblyOwnerIndex.OfTypeName(typeName),
            };

            if (entry.Owner.Kind == OwnerKind.Vanilla)
            {
                Annotate(entry, PatchSuspects.FindPatched(typeName, methodName));
            }

            return entry;
        }

        // ================== 补丁标注 ==================

        /// <summary>只给原版帧做补丁反查，纠正"原版方法被改过却看起来像原版的锅"这类误判。</summary>
        static void Annotate(ErrorFrame frame, MethodBase method)
        {
            if (method == null || frame.Owner.Kind != OwnerKind.Vanilla)
            {
                return;
            }

            PatchSuspects.Scan scan = PatchSuspects.Of(method, Display(frame));
            if (!scan.Any)
            {
                return;
            }

            frame.IsPatched = true;
            frame.PatchNote = scan.Note;
        }

        /// <summary>补丁说明里指代这一帧的方法名，仅用于展示。</summary>
        static string Display(ErrorFrame frame) => $"{frame.TypeName}.{frame.MethodName}";

        /// <summary>收集整条堆栈上的补丁嫌疑人，去重后供定责使用。</summary>
        internal static List<ErrorSuspect> CollectSuspects(IReadOnlyList<ErrorFrame> frames)
        {
            var result = new List<ErrorSuspect>();
            var seen = new HashSet<AssemblyOwner>();

            foreach (ErrorFrame frame in frames)
            {
                if (!frame.IsPatched)
                {
                    continue;
                }

                MethodBase method = PatchSuspects.FindPatched(frame.TypeName, frame.MethodName);
                PatchSuspects.Scan scan = PatchSuspects.Of(method, Display(frame));

                foreach (ErrorSuspect suspect in scan.Suspects)
                {
                    if (seen.Add(suspect.Owner))
                    {
                        result.Add(suspect);
                    }
                }
            }

            return result;
        }
    }
}
