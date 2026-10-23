using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PclDl.Core.Logging;

namespace PclDl.Core.Loaders
{
    public sealed class LoaderResult
    {
        public string VersionId;
        public string MinecraftVersion;
        public string LoaderVersion;
        public string MainClass;
        public string JsonText;
        public readonly List<JObject> Libraries = new List<JObject>();
    }

    /// <summary>Fabric 加载器：纯 HTTP + profile JSON，不依赖安装器。</summary>
    public sealed class FabricLoader
    {
        private const string MetaUrl = "https://meta.fabricmc.net/v2/versions/loader/{0}";
        private const string FabricMaven = "https://maven.fabricmc.net/";
        private readonly HttpClient _http;
        private readonly ILog _log;

        public FabricLoader(HttpClient http, ILog log)
        {
            _http = http;
            _log = log ?? new NullLog();
        }

        public async Task<LoaderResult> ResolveAsync(string minecraftVersion, string loaderVersion = null)
        {
            var url = string.Format(CultureInfo.InvariantCulture, MetaUrl, minecraftVersion);
            var text = await GetStringAsync(url).ConfigureAwait(false);
            var entries = JArray.Parse(text);
            if (entries.Count == 0) throw new InvalidOperationException("Fabric 没有可用于 " + minecraftVersion + " 的加载器");

            JObject chosen;
            if (!string.IsNullOrEmpty(loaderVersion))
            {
                chosen = entries.OfType<JObject>().FirstOrDefault(e => (string)e["loader"]["version"] == loaderVersion);
                if (chosen == null) throw new InvalidOperationException("Fabric 加载器版本不存在：" + loaderVersion);
            }
            else
            {
                chosen = entries.OfType<JObject>().FirstOrDefault(e => e["loader"]["stable"] != null && (bool)e["loader"]["stable"]);
                if (chosen == null) chosen = (JObject)entries[0];
            }

            var version = (string)chosen["loader"]["version"];
            var launcherMeta = (JObject)chosen["launcherMeta"];
            // mainClass 在部分版本里是对象 { client, server }，新版才是字符串
            var mainClassToken = launcherMeta["mainClass"];
            string mainClass;
            if (mainClassToken != null && mainClassToken.Type == JTokenType.Object)
                mainClass = (string)mainClassToken["client"];
            else
                mainClass = (string)mainClassToken;
            if (string.IsNullOrEmpty(mainClass))
                mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient";

            var result = new LoaderResult
            {
                MinecraftVersion = minecraftVersion,
                LoaderVersion = version,
                VersionId = minecraftVersion + "-fabric-" + version,
                MainClass = mainClass
            };

            // 0.19 之后 launcherMeta.libraries 只剩 asm/mixin，loader 本体和 intermediary
            // 要像官方安装器那样自己补上，否则主类 KnotClient 根本不在 classpath 里
            result.Libraries.Add(new JObject
            {
                ["name"] = "net.fabricmc:intermediary:" + minecraftVersion,
                ["url"] = FabricMaven
            });
            result.Libraries.Add(new JObject
            {
                ["name"] = "net.fabricmc:fabric-loader:" + version,
                ["url"] = FabricMaven
            });

            var libraries = launcherMeta["libraries"] as JObject;
            foreach (var section in new[] { "common", "client" })
            {
                var list = libraries == null ? null : libraries[section] as JArray;
                if (list == null) continue;
                foreach (var lib in list.OfType<JObject>()) result.Libraries.Add(lib);
            }

            // 去重（同一个坐标只留一份）
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = result.Libraries.Count - 1; i >= 0; i--)
            {
                var name = (string)result.Libraries[i]["name"];
                if (string.IsNullOrEmpty(name) || seen.Add(name)) continue;
                result.Libraries.RemoveAt(i);
            }

            var profileLibs = new JArray();
            foreach (var lib in result.Libraries)
            {
                profileLibs.Add(new JObject
                {
                    ["name"] = lib["name"],
                    ["url"] = lib["url"]
                });
            }

            var profile = new JObject
            {
                ["id"] = result.VersionId,
                ["inheritsFrom"] = minecraftVersion,
                ["type"] = "release",
                ["mainClass"] = mainClass,
                ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00"),
                ["releaseTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00"),
                ["libraries"] = profileLibs
            };
            result.JsonText = profile.ToString(Formatting.Indented);

            _log.Info("Fabric：" + result.VersionId + "，库 " + result.Libraries.Count + " 个");
            return result;
        }

        private async Task<string> GetStringAsync(string url)
        {
            using (var resp = await _http.GetAsync(url).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        /// <summary>maven 坐标转相对路径：group:artifact:version[:classifier]</summary>
        public static string MavenToPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var parts = name.Split(':');
            if (parts.Length < 3) return null;

            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 ? "-" + parts[3] : "";
            return group + "/" + artifact + "/" + version + "/" + artifact + "-" + version + classifier + ".jar";
        }
    }
}
