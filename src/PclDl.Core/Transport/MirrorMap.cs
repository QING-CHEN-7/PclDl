using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;

namespace PclDl.Core.Transport
{
    /// <summary>
    /// 官方地址 -> 镜像地址的规则映射。规则按顺序匹配，命中第一条即停止。
    /// 目前内置 BMCLAPI 规则；后续可改为外置 JSON 热更新。
    /// </summary>
    public sealed class MirrorMap
    {
        public sealed class Rule
        {
            public string Name;
            public string FromPrefix;
            public string ToPrefix;

            public Rule(string name, string fromPrefix, string toPrefix)
            {
                Name = name; FromPrefix = fromPrefix; ToPrefix = toPrefix;
            }
        }

        private readonly List<Rule> _rules = new List<Rule>();

        /// <summary>从外置 JSON 读取规则（v4 §4.5：MirrorMap 外置、可热更新）。</summary>
        public static MirrorMap FromJson(string json)
        {
            var map = new MirrorMap();
            if (string.IsNullOrWhiteSpace(json)) return map;
            var root = JToken.Parse(json);
            var rules = root is JArray ? (JArray)root : root["rules"] as JArray;
            if (rules == null) return map;
            foreach (var item in rules.OfType<JObject>())
            {
                var from = (string)item["from"];
                var to = (string)item["to"];
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) continue;
                map.Add((string)item["name"] ?? from, from, to);
            }
            return map;
        }

        /// <summary>
        /// 读取 &lt;游戏目录&gt;\PCL2Plugins\mirror.json：文件不存在就写一份默认的；
        /// 文件更新过（mtime 变化）就重新加载，实现"改完不用重启"。
        /// </summary>
        public static MirrorMap LoadOrCreate(string gameDir)
        {
            var defaults = CreateBmclapi();
            try
            {
                if (string.IsNullOrWhiteSpace(gameDir)) return defaults;
                var dir = Path.Combine(gameDir, "PCL2Plugins");
                var path = Path.Combine(dir, "mirror.json");
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(path, ToJson(defaults), new System.Text.UTF8Encoding(false));
                    return defaults;
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                lock (CacheGate)
                {
                    if (_cache != null && _cachePath == path && _cacheStamp == stamp) return _cache;
                    var loaded = FromJson(File.ReadAllText(path));
                    if (loaded.Rules.Count == 0) loaded = defaults;
                    _cache = loaded;
                    _cachePath = path;
                    _cacheStamp = stamp;
                    return loaded;
                }
            }
            catch
            {
                return defaults;
            }
        }

        private static readonly object CacheGate = new object();
        private static MirrorMap _cache;
        private static string _cachePath;
        private static DateTime _cacheStamp;

        public static string ToJson(MirrorMap map)
        {
            var rules = new JArray();
            foreach (var rule in (map ?? CreateBmclapi()).Rules)
                rules.Add(new JObject { ["name"] = rule.Name, ["from"] = rule.FromPrefix, ["to"] = rule.ToPrefix });
            return new JObject { ["rules"] = rules }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        public IReadOnlyList<Rule> Rules { get { return _rules; } }

        public MirrorMap Add(string name, string fromPrefix, string toPrefix)
        {
            _rules.Add(new Rule(name, fromPrefix.TrimEnd('/'), toPrefix.TrimEnd('/')));
            return this;
        }

        public static MirrorMap CreateBmclapi()
        {
            return new MirrorMap()
                .Add("launchermeta", "https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com")
                .Add("launcher", "https://launcher.mojang.com", "https://bmclapi2.bangbang93.com")
                .Add("resources", "https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets")
                .Add("libraries", "https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven")
                .Add("maven-fabric", "https://maven.fabricmc.net", "https://bmclapi2.bangbang93.com/maven")
                .Add("meta-fabric", "https://meta.fabricmc.net", "https://bmclapi2.bangbang93.com/fabric-meta")
                .Add("maven-forge", "https://maven.minecraftforge.net", "https://bmclapi2.bangbang93.com/maven")
                .Add("maven-neoforge", "https://maven.neoforged.net/releases", "https://bmclapi2.bangbang93.com/maven")
                .Add("forge-files", "https://files.minecraftforge.net", "https://bmclapi2.bangbang93.com/forge");
        }

        /// <summary>版本 JSON：BMCLAPI 的形态是 /version/&lt;id&gt;/json。</summary>
        public IReadOnlyList<string> VersionJsonCandidates(string versionId, string officialUrl, bool preferMirror)
        {
            return VersionEndpointCandidates(versionId, officialUrl, "json", preferMirror);
        }

        /// <summary>客户端 JAR：BMCLAPI 的形态是 /version/&lt;id&gt;/client。</summary>
        public IReadOnlyList<string> VersionClientCandidates(string versionId, string officialUrl, bool preferMirror)
        {
            return VersionEndpointCandidates(versionId, officialUrl, "client", preferMirror);
        }

        private IReadOnlyList<string> VersionEndpointCandidates(string versionId, string officialUrl, string kind, bool preferMirror)
        {
            var list = new List<string>();
            string mirror = null;
            if (!string.IsNullOrEmpty(versionId))
                mirror = "https://bmclapi2.bangbang93.com/version/" + Uri.EscapeDataString(versionId) + "/" + kind;

            if (preferMirror && mirror != null) list.Add(mirror);
            if (!string.IsNullOrEmpty(officialUrl)) list.Add(officialUrl);
            if (!preferMirror && mirror != null) list.Add(mirror);
            return list;
        }

        /// <summary>返回镜像地址；无匹配规则时返回 null。</summary>
        public string ToMirror(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            foreach (var rule in _rules)
            {
                if (url.StartsWith(rule.FromPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return rule.ToPrefix + url.Substring(rule.FromPrefix.Length);
                }
            }
            return null;
        }

        /// <summary>按偏好返回候选地址序列：镜像优先或官方优先，去重。</summary>
        public IReadOnlyList<string> Candidates(string url, bool preferMirror)
        {
            var list = new List<string>();
            var mirror = ToMirror(url);
            if (preferMirror)
            {
                if (mirror != null) list.Add(mirror);
                if (!string.IsNullOrEmpty(url)) list.Add(url);
            }
            else
            {
                if (!string.IsNullOrEmpty(url)) list.Add(url);
                if (mirror != null) list.Add(mirror);
            }
            return list;
        }
    }
}
