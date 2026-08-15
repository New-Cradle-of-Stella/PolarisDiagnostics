using System;

namespace Polaris.Diagnostics
{
    /// <summary>按异常类型名（不看实例，兼容纯文本来源）给出更具体的诊断，补充归因结论。</summary>
    internal static class ExceptionShapes
    {
        internal sealed class Shape
        {
            internal string Diagnosis { get; set; }
            internal string Action { get; set; }
        }

        /// <summary>没有特别形状时返回 null——不硬凑诊断，说不出所以然就别说。</summary>
        internal static Shape Of(string exceptionTypeName, string message)
        {
            if (string.IsNullOrEmpty(exceptionTypeName))
            {
                return null;
            }

            string name = Simplify(exceptionTypeName);

            switch (name)
            {
                case "MissingMethodException":
                case "MissingFieldException":
                case "MissingMemberException":
                case "TypeLoadException":
                    return new Shape
                    {
                        Diagnosis = "Version mismatch: some code is looking for a method/field/type that does not exist right now."
                                    + " Usually a mod compiled against a different version of the game (or of Polaris).",
                        Action = "Check that the game version matches what the mod requires; update the mod first, and only then consider rolling the game back.",
                    };

                case "ReflectionTypeLoadException":
                    return new Shape
                    {
                        Diagnosis = "Some assembly has types that will not load, usually because a dll it depends on is missing or is the wrong version.",
                        Action = "Look at the assembly named in the exception message below and install the missing dependency.",
                    };

                case "FileNotFoundException":
                    return LooksLikeAssembly(message)
                        ? new Shape
                        {
                            Diagnosis = "Missing dependency: a mod is trying to load an assembly that is not there.",
                            Action = "Install the dll it depends on as described by that mod (Polaris-family dependencies belong in plugins/Polaris/libs/).",
                        }
                        : null;

                case "BadImageFormatException":
                    return new Shape
                    {
                        Diagnosis = "A dll cannot be read: the file is corrupt, was downloaded incompletely, or was built for a different runtime/bitness.",
                        Action = "Re-download that dll and confirm it is the build for BepInEx 6 (Mono).",
                    };

                case "AmbiguousMatchException":
                    return new Shape
                    {
                        Diagnosis = "Reflection/Harmony matched several members with the same name without specifying parameter types."
                                    + " A game update that adds one overload triggers this out of nowhere -- Polaris itself got caught by this on TX.Get.",
                        Action = "Report it to that mod's author with the method name below; this is a one-line fix on the mod side.",
                    };

                case "InvalidCastException":
                    return SameTypeOnBothSides(message)
                        ? new Shape
                        {
                            Diagnosis = "The same type was loaded twice: usually the same dependency dll exists in more than one copy under plugins.",
                            Action = "Search for that dll under the plugins directory and keep only one copy (the highest version).",
                        }
                        : null;

                case "OutOfMemoryException":
                    return new Shape
                    {
                        Diagnosis = "Out of memory. Most common with a lot of high-resolution asset mods installed.",
                        Action = "Enable fewer asset mods at the same time, and confirm the game is running 64-bit.",
                    };

                case "DllNotFoundException":
                    return new Shape
                    {
                        Diagnosis = "Missing native library (unmanaged dll).",
                        Action = "Install the mod's native dependency as it describes, and confirm antivirus has not blocked it.",
                    };

                default:
                    return null;
            }
        }

        /// <summary>去掉命名空间与泛型标记，只留类名。</summary>
        static string Simplify(string typeName)
        {
            int dot = typeName.LastIndexOf('.');
            return dot >= 0 && dot < typeName.Length - 1 ? typeName.Substring(dot + 1) : typeName;
        }

        /// <summary>
        /// <c>FileNotFoundException</c> 也可能只是模组读不到自己的 png。消息里带程序集全名的
        /// 特征（<c>Culture=</c>/<c>PublicKeyToken=</c>/<c>.dll</c>）才当成缺依赖。
        /// </summary>
        static bool LooksLikeAssembly(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            return message.IndexOf("Culture=", StringComparison.OrdinalIgnoreCase) >= 0
                   || message.IndexOf("PublicKeyToken=", StringComparison.OrdinalIgnoreCase) >= 0
                   || message.IndexOf("Could not load file or assembly", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// <c>InvalidCastException</c> 的消息形如 "Unable to cast object of type 'X' to type 'X'"。
        /// 两边同名就是典型的"一个类型被两个程序集各加载了一份"。
        /// </summary>
        static bool SameTypeOnBothSides(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            if (!TryReadQuoted(message, 0, out string left, out int afterLeft)
                || !TryReadQuoted(message, afterLeft, out string right, out _))
            {
                return false;
            }

            return left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
        }

        /// <summary>读出 <paramref name="from"/> 之后第一段 <c>'…'</c> 里的内容，并给出它结束的位置。</summary>
        static bool TryReadQuoted(string text, int from, out string value, out int next)
        {
            value = null;
            next = from;

            int open = text.IndexOf('\'', from);
            if (open < 0)
            {
                return false;
            }

            int close = text.IndexOf('\'', open + 1);
            if (close < 0)
            {
                return false;
            }

            value = text.Substring(open + 1, close - open - 1);
            next = close + 1;
            return true;
        }
    }
}
