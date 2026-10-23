using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using PclDl.Core.Install;
using PclDl.Core.Java;
using PclDl.Core.Layout;
using PclDl.Core.Models;
using PclDl.Core.Resolve;

namespace PclDl.Core.Launch
{
    /// <summary>启动计划：命令行 + 依赖完整性检查结果。</summary>
    public sealed class LaunchPlan
    {
        public string VersionId;
        public string VanillaId;
        public string MainClass;
        public string JavaPath;
        public int JavaMajor;
        public int RequiredJavaMajor;
        public string NativesDir;
        public string AssetIndexPath;
        public string AssetIndexId;
        public int AssetTotal;
        public int AssetMissing;
        public string CommandLine;
        /// <summary>命令行参数（不含 java 本身），可直接写成 Java 的 @argfile。</summary>
        public readonly List<string> Arguments = new List<string>();

        public readonly List<string> Classpath = new List<string>();
        public readonly List<string> MissingClasspath = new List<string>();
        public readonly List<string> MissingNatives = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public bool Ok { get { return MissingClasspath.Count == 0 && AssetMissing == 0 && MissingNatives.Count == 0 && MainClass != null; } }
    }

    /// <summary>
    /// 按 PCL/官方启动器的规则把版本 JSON 拼成一条 java 命令行，并检查它引用的文件是否真的存在。
    /// 这是"数据面是否自洽"的离线验证：不需要真的拉起游戏就能知道会不会缺文件。
    /// </summary>
    public static class LaunchPlanner
    {
        private const int DefaultWidth = 856;
        private const int DefaultHeight = 482;

        public static LaunchPlan Build(string gameDir, string versionId, string javaPath = null)
        {
            gameDir = DataLayout.NormalizeDir(gameDir);
            var layout = new DataLayout(gameDir);
            if (!File.Exists(layout.VersionJson(versionId)))
                throw new FileNotFoundException("找不到版本 JSON：" + layout.VersionJson(versionId));
            var version = RepairService.LoadMerged(layout, versionId);

            var plan = new LaunchPlan
            {
                VersionId = versionId,
                VanillaId = !string.IsNullOrEmpty(version.Jar)
                    ? version.Jar
                    : (version.InheritanceChain.Count > 0 ? version.InheritanceChain[0] : versionId),
                MainClass = version.MainClass,
                NativesDir = layout.VersionNatives(versionId),
                AssetIndexId = version.AssetIndexId,
                AssetIndexPath = string.IsNullOrEmpty(version.AssetIndexId) ? null : layout.AssetIndex(version.AssetIndexId),
                RequiredJavaMajor = JavaLocator.RequiredMajorFor(
                    !string.IsNullOrEmpty(version.Jar) ? version.Jar : plan_vanilla(version), version.JavaVersion)
            };

            // ---- classpath ----
            foreach (var library in version.Libraries)
            {
                if (!RuleEvaluator.IsAllowed(library["rules"])) continue;
                var native = library["natives"] as JObject;
                if (native != null) continue;   // natives 不进 classpath

                var relative = LibraryPath(library);
                if (relative == null) continue;
                var full = layout.Library(relative);
                plan.Classpath.Add(full);
                if (!File.Exists(full)) plan.MissingClasspath.Add(full);
            }

            // 实例可能有 jar 字段（整合包指向原版）；其次是 downloads.client 所属版本；最后才是自己
            var jarOwner = !string.IsNullOrEmpty(version.Jar)
                ? version.Jar
                : (string.IsNullOrEmpty(version.ClientVersionId) ? versionId : version.ClientVersionId);
            var clientJar = layout.VersionJar(jarOwner);
            if (!File.Exists(clientJar))
            {
                // 实例可能通过 jar 字段指向原版；找不到就退回自己的目录
                clientJar = layout.VersionJar(versionId);
            }
            plan.Classpath.Add(clientJar);
            if (!File.Exists(clientJar)) plan.MissingClasspath.Add(clientJar);

            // ---- natives ----
            // 不猜 DLL 名字：直接打开本地的 natives jar，看压缩包里到底有哪些 .dll。
            // natives 目录可能落在几个地方（PCL 约定 <id>-natives / 旧版 natives /
            // 原版版本目录），所以要先把"需要哪些 DLL"算出来，再挑一个真的有的目录。
            var expectedDlls = new List<string>();
            foreach (var library in version.Libraries)
            {
                if (!RuleEvaluator.IsAllowed(library["rules"])) continue;
                var native = library["natives"] as JObject;
                if (native == null) continue;
                var classifier = (string)native["windows"];
                if (string.IsNullOrEmpty(classifier)) continue;
                classifier = classifier.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");

                var jarPath = NativeJarPath(layout, library, classifier);
                if (jarPath == null || !File.Exists(jarPath))
                {
                    plan.Warnings.Add("natives jar 缺失：" + (jarPath ?? (string)library["name"]));
                    continue;
                }

                try
                {
                    using (var zip = System.IO.Compression.ZipFile.OpenRead(jarPath))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                            if (entry.FullName.IndexOf('/') >= 0) continue;   // 只要根目录下的
                            if (!expectedDlls.Contains(entry.FullName)) expectedDlls.Add(entry.FullName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    plan.Warnings.Add("读取 natives jar 失败（" + Path.GetFileName(jarPath) + "）：" + ex.Message);
                }
            }

            var candidates = NativesCandidates(layout, versionId, plan.VanillaId);
            var bestDir = candidates.FirstOrDefault(dir => expectedDlls.Count > 0 && expectedDlls.All(d => File.Exists(Path.Combine(dir, d))))
                          ?? candidates.FirstOrDefault(dir => expectedDlls.Any(d => File.Exists(Path.Combine(dir, d))))
                          ?? candidates[0];
            plan.NativesDir = bestDir;
            foreach (var dll in expectedDlls)
            {
                if (!File.Exists(Path.Combine(bestDir, dll))) plan.MissingNatives.Add(dll);
            }

            // ---- assets ----
            if (plan.AssetIndexPath != null && File.Exists(plan.AssetIndexPath))
            {
                var index = JObject.Parse(File.ReadAllText(plan.AssetIndexPath));
                var objects = index["objects"] as JObject;
                if (objects != null)
                {
                    plan.AssetTotal = objects.Count;
                    foreach (var property in objects.Properties())
                    {
                        var hash = (string)property.Value["hash"];
                        if (string.IsNullOrEmpty(hash)) continue;
                        if (!File.Exists(layout.AssetObject(hash))) plan.AssetMissing++;
                    }
                }
            }
            else
            {
                plan.Warnings.Add("资源索引缺失：" + plan.AssetIndexPath);
            }

            // ---- java ----
            var runtimes = JavaLocator.Discover(false, Path.Combine(gameDir, "runtime"));
            var best = JavaLocator.SelectBest(runtimes, plan.RequiredJavaMajor);
            plan.JavaPath = !string.IsNullOrEmpty(javaPath) ? javaPath : (best != null ? best.JavaPath : ForgeLoaderFindJava());
            if (!string.IsNullOrEmpty(plan.JavaPath))
            {
                var runtime = runtimes.FirstOrDefault(r => string.Equals(r.JavaPath, plan.JavaPath, StringComparison.OrdinalIgnoreCase));
                plan.JavaMajor = runtime != null ? runtime.MajorVersion : 0;
            }
            if (string.IsNullOrEmpty(plan.JavaPath)) plan.Warnings.Add("没有可用的 Java");
            else if (plan.JavaMajor > 0 && plan.JavaMajor != plan.RequiredJavaMajor)
                plan.Warnings.Add("Java 版本为 " + plan.JavaMajor + "，该版本建议 " + plan.RequiredJavaMajor);

            var clientJarName = plan.Classpath.Count > 0 ? plan.Classpath[plan.Classpath.Count - 1] : null;
            BuildArguments(plan, version, layout, gameDir, clientJarName);
            var builder = new StringBuilder();
            builder.Append(Quote(plan.JavaPath ?? "java"));
            foreach (var argument in plan.Arguments) builder.Append(' ').Append(Quote(argument));
            plan.CommandLine = builder.ToString();
            return plan;
        }

        private static string plan_vanilla(ResolvedVersion version)
        {
            return version.InheritanceChain.Count > 0 ? version.InheritanceChain[0] : version.Id;
        }

        private static string ForgeLoaderFindJava()
        {
            try { return PclDl.Core.Loaders.ForgeLoader.FindJava(); } catch { return null; }
        }

        /// <summary>group:artifact:version[:classifier][@ext] → maven 相对路径。</summary>
        public static string LibraryPath(JObject library)
        {
            var artifact = library["downloads"]?["artifact"] as JObject;
            if (artifact != null && artifact["path"] != null) return artifact["path"].ToString();
            var name = (string)library["name"];
            if (string.IsNullOrEmpty(name)) return null;
            var ext = "jar";
            var at = name.IndexOf('@');
            if (at >= 0) { ext = name.Substring(at + 1); name = name.Substring(0, at); }
            var parts = name.Split(':');
            if (parts.Length < 3) return null;
            var group = parts[0].Replace('.', '/');
            var classifier = parts.Length >= 4 ? "-" + parts[3] : "";
            return group + "/" + parts[1] + "/" + parts[2] + "/" + parts[1] + "-" + parts[2] + classifier + "." + ext;
        }

        /// <summary>natives 目录的候选位置（顺序即优先级）。</summary>
        private static List<string> NativesCandidates(DataLayout layout, string versionId, string vanillaId)
        {
            var list = new List<string>
            {
                layout.VersionNatives(versionId),
                Path.Combine(layout.VersionDir(versionId), "natives")
            };
            if (!string.IsNullOrEmpty(vanillaId) && !string.Equals(vanillaId, versionId, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(layout.VersionNatives(vanillaId));
                list.Add(Path.Combine(layout.VersionDir(vanillaId), "natives"));
            }
            return list;
        }

        /// <summary>natives jar 在 libraries 里的路径（优先 downloads.classifiers，其次按 maven 名字推导）。</summary>
        private static string NativeJarPath(DataLayout layout, JObject library, string classifier)
        {
            var downloads = library["downloads"] as JObject;
            var classifiers = downloads == null ? null : downloads["classifiers"] as JObject;
            var native = classifiers == null ? null : classifiers[classifier] as JObject;
            var relative = native == null ? null : (string)native["path"];
            if (string.IsNullOrEmpty(relative))
            {
                var name = (string)library["name"];
                if (string.IsNullOrEmpty(name)) return null;
                var parts = name.Split(':');
                if (parts.Length < 3) return null;
                var group = parts[0].Replace('.', '/');
                relative = group + "/" + parts[1] + "/" + parts[2] + "/" + parts[1] + "-" + parts[2] + "-" + classifier + ".jar";
            }
            return layout.Library(relative);
        }

        private static void BuildArguments(LaunchPlan plan, ResolvedVersion version, DataLayout layout, string gameDir, string clientJarName)
        {
            var arguments = plan.Arguments;

            // JVM 段：优先新格式 arguments.jvm，其次老式的 -Djava.library.path
            var jvm = version.Arguments == null ? null : version.Arguments["jvm"];
            if (jvm is JArray)
            {
                foreach (var token in Flatten(jvm)) arguments.Add(token);
            }
            else
            {
                arguments.Add("-Djava.library.path=${natives_directory}");
            }

            arguments.Add("-cp");
            arguments.Add("${classpath}");
            arguments.Add(plan.MainClass ?? "(缺少 mainClass)");

            var game = version.Arguments == null ? null : version.Arguments["game"];
            if (game is JArray)
            {
                foreach (var token in Flatten(game)) arguments.Add(token);
            }
            else if (!string.IsNullOrEmpty(version.LegacyArguments))
            {
                foreach (var token in SplitArguments(version.LegacyArguments)) arguments.Add(token);
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "auth_player_name", "Player" },
                { "auth_uuid", "00000000000000000000000000000000" },
                { "auth_access_token", "0" },
                { "auth_session", "-" },
                { "user_properties", "{}" },
                { "user_type", "legacy" },
                { "clientid", "" },
                { "auth_xuid", "" },
                { "version_name", plan.VersionId },
                { "version_type", "PclDl" },
                { "game_directory", gameDir },
                { "assets_root", layout.AssetsDir },
                { "game_assets", Path.Combine(layout.AssetsDir, "virtual", "legacy") },
                { "assets_index_name", plan.AssetIndexId ?? "legacy" },
                { "natives_directory", plan.NativesDir },
                { "library_directory", layout.LibrariesDir },
                { "classpath_separator", ";" },
                { "classpath", string.Join(";", plan.Classpath) },
                { "launcher_name", "PclDl" },
                { "launcher_version", "1" },
                { "resolution_width", DefaultWidth.ToString() },
                { "resolution_height", DefaultHeight.ToString() }
            };

            var substituted = new List<string>();
            foreach (var token in arguments)
            {
                var text = Substitute(token, values);
                if (string.IsNullOrEmpty(text)) continue;

                // FML 的 -DignoreList 用 version_name.jar 排除游戏 jar。实例（整合包）把 jar 指到
                // 原版时名字对不上，原版 jar 会被当成自动模块，触发 "Module minecraft contains package …"。
                if (text.StartsWith("-DignoreList=", StringComparison.OrdinalIgnoreCase))
                {
                    var jarName = Path.GetFileName(clientJarName);
                    if (!string.IsNullOrEmpty(jarName) && text.IndexOf(jarName, StringComparison.OrdinalIgnoreCase) < 0)
                        text += "," + jarName;
                }
                substituted.Add(text);
            }
            arguments.Clear();
            arguments.AddRange(substituted);
        }

        private static IEnumerable<string> Flatten(JToken array)
        {
            foreach (var token in array)
            {
                if (token.Type == JTokenType.String) { yield return (string)token; continue; }
                // 规则条目：{ "rules": [...], "value": "x" | ["x","y"] }
                var value = token["value"];
                if (value == null) continue;
                if (!RuleEvaluator.IsAllowed(token["rules"])) continue;
                if (value.Type == JTokenType.Array)
                {
                    foreach (var item in value) if (item.Type == JTokenType.String) yield return (string)item;
                }
                else if (value.Type == JTokenType.String) yield return (string)value;
            }
        }

        private static IEnumerable<string> SplitArguments(string text)
        {
            var current = new StringBuilder();
            var quoted = false;
            foreach (var ch in text)
            {
                if (ch == '"') { quoted = !quoted; continue; }
                if (ch == ' ' && !quoted)
                {
                    if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                    continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0) yield return current.ToString();
        }

        private static string Substitute(string token, IDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(token) || token.IndexOf('$') < 0) return token;
            var result = token;
            foreach (var pair in values)
                result = result.Replace("${" + pair.Key + "}", pair.Value);
            return result;
        }

        public static string Quote(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value + (value.EndsWith("\\") ? "\\" : "") + "\"";
        }
    }
}
