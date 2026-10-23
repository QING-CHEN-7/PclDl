using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PclDl.Core.Resolve
{
    /// <summary>
    /// Mojang rules 求值。语义：默认不允许；按顺序匹配，最后一条匹配规则的 action 生效。
    /// 当前平台固定为 Windows x64、所有 features 均为 false（与正式启动器一致）。
    /// </summary>
    public static class RuleEvaluator
    {
        public static bool IsAllowed(JToken rules)
        {
            if (rules == null || rules.Type != JTokenType.Array) return true;
            var array = (JArray)rules;
            if (array.Count == 0) return true;

            bool allowed = false;
            foreach (var rule in array)
            {
                if (!Matches(rule)) continue;
                var action = (string)rule["action"];
                allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
            }
            return allowed;
        }

        private static bool Matches(JToken rule)
        {
            var os = rule["os"];
            if (os != null)
            {
                var name = (string)os["name"];
                if (!string.IsNullOrEmpty(name) && !string.Equals(name, "windows", StringComparison.OrdinalIgnoreCase))
                    return false;

                var arch = (string)os["arch"];
                if (!string.IsNullOrEmpty(arch))
                {
                    var current = Environment.Is64BitProcess ? "x86_64" : "x86";
                    if (!string.Equals(arch, current, StringComparison.OrdinalIgnoreCase) &&
                        !(arch == "x86" && !Environment.Is64BitProcess))
                        return false;
                }

                var version = (string)os["version"];
                if (!string.IsNullOrEmpty(version))
                {
                    if (!Regex.IsMatch(Environment.OSVersion.Version.ToString(), version)) return false;
                }
            }

            var features = rule["features"];
            if (features != null)
            {
                foreach (var prop in ((JObject)features).Properties())
                {
                    if (prop.Value.Type != JTokenType.Boolean) continue;
                    // 正式启动器默认全部 features=false；只有规则显式要求 false 才匹配
                    if ((bool)prop.Value) return false;
                }
            }

            return true;
        }
    }
}
