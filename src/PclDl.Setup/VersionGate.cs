using System;
using System.Collections.Generic;
using System.Text;

namespace PclDl.Setup
{
    /// <summary>
    /// 版本判定闸门：只做一件事——把"启动器版本 是否 低于某个下限"判断得稳。
    /// 规则（收束后）：
    ///   1) 两边都能解析成数字段才比较；
    ///   2) 忽略前缀 v/V、空白、以及第一段非数字之后的附加信息（如 2.99.0-beta、3.16.4+233）；
    ///   3) 缺失段按 0 补齐（2.99 == 2.99.0 == 2.99.0.0）；
    ///   4) 任何一边解析不出来 → 结果就是 Unknown，调用方不得据此改动启动器设置。
    /// </summary>
    internal static class VersionGate
    {
        internal enum Verdict
        {
            Unknown,
            TooOld,
            NewEnough
        }

        internal static Verdict Decide(string actual, string floor)
        {
            var a = Parse(actual);
            var b = Parse(floor);
            if (a == null || b == null) return Verdict.Unknown;
            return Compare(a, b) < 0 ? Verdict.TooOld : Verdict.NewEnough;
        }

        /// <summary>把 "v2.99.0.0" / "2.99.0-beta" / "3.16.4+233" / "1.20.1-47.1" 解析成数字段。</summary>
        internal static List<int> Parse(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            var text = version.Trim();
            if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text.Substring(1);

            var parts = new List<int>();
            var current = new StringBuilder();
            var stopped = false;

            foreach (var ch in text)
            {
                if (char.IsDigit(ch))
                {
                    if (stopped) break;              // 第一段非数字之后的内容一律不看
                    current.Append(ch);
                    continue;
                }

                if (ch == '.' && current.Length > 0)
                {
                    if (!Flush(parts, current)) return null;
                    continue;
                }

                // 非数字、非点：如果还没拿到任何段，说明版本号不是以数字开头 → 不可解析
                if (current.Length > 0) { if (!Flush(parts, current)) return null; stopped = true; }
                else if (parts.Count == 0) return null;
                else stopped = true;
            }

            if (current.Length > 0 && !stopped && !Flush(parts, current)) return null;
            if (parts.Count == 0) return null;
            return parts;
        }

        private static bool Flush(List<int> parts, StringBuilder current)
        {
            int value;
            if (!int.TryParse(current.ToString(), out value)) return false;
            parts.Add(value);
            current.Clear();
            return true;
        }

        internal static int Compare(IList<int> left, IList<int> right)
        {
            var length = Math.Max(left.Count, right.Count);
            for (var i = 0; i < length; i++)
            {
                var x = i < left.Count ? left[i] : 0;
                var y = i < right.Count ? right[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        internal static string Describe(string actual, string floor)
        {
            var verdict = Decide(actual, floor);
            switch (verdict)
            {
                case Verdict.TooOld: return actual + " < " + floor;
                case Verdict.NewEnough: return actual + " >= " + floor;
                default: return "无法判定（" + (actual ?? "null") + "）";
            }
        }
    }
}
