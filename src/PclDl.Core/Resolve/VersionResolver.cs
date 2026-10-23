using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PclDl.Core.Logging;
using PclDl.Core.Models;
using PclDl.Core.Transport;

namespace PclDl.Core.Resolve
{
    /// <summary>
    /// 拉取版本 JSON 并按 inheritsFrom 链自底向上合并。
    /// lookup：由版本 ID 查父版本清单项（加载器版本需要）。
    /// </summary>
    public sealed class VersionResolver
    {
        private readonly HttpClient _http;
        private readonly MirrorMap _mirror;
        private readonly bool _preferMirror;
        private readonly ILog _log;

        public VersionResolver(HttpClient http, MirrorMap mirror, bool preferMirror, ILog log)
        {
            _http = http; _mirror = mirror; _preferMirror = preferMirror; _log = log ?? new NullLog();
        }

        public async Task<string> DownloadJsonAsync(string url, string versionId = null)
        {
            Exception last = null;
            var candidates = _mirror.VersionJsonCandidates(versionId, url, _preferMirror).ToList();
            if (Catalog.VersionCatalog.PreferredUrl != null)
            {
                var isReferenced = url != null && Catalog.VersionCatalog.PreferredUrl.StartsWith("https://launchermeta", StringComparison.OrdinalIgnoreCase);
                if (isReferenced) candidates = candidates.OrderBy(c => c.StartsWith("https://launchermeta", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            }

            // 与 PCL 一样并行竞速，最快的源先返回
            var racing = candidates.Select(candidate => FetchJsonAsync(candidate)).ToList();
            while (racing.Count > 0)
            {
                var finished = await Task.WhenAny(racing).ConfigureAwait(false);
                racing.Remove(finished);
                try { return await finished.ConfigureAwait(false); }
                catch (Exception ex) { last = ex; }
            }
            throw new InvalidOperationException("无法拉取版本 JSON：" + url, last);
        }

        private async Task<string> FetchJsonAsync(string candidate)
        {
            using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12)))
            using (var resp = await _http.GetAsync(candidate, cts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        public async Task<ResolvedVersion> ResolveAsync(ManifestEntry entry, Func<string, ManifestEntry> lookup)
        {
            var chain = new List<JObject>();
            var ids = new List<string>();

            var url = entry.Url;
            var currentId = entry.Id;
            for (int depth = 0; depth < 8; depth++)
            {
                var text = await DownloadJsonAsync(url, currentId).ConfigureAwait(false);
                var json = JObject.Parse(text);
                chain.Add(json);
                ids.Add((string)json["id"] ?? currentId);

                var inheritsFrom = (string)json["inheritsFrom"];
                if (string.IsNullOrEmpty(inheritsFrom)) break;

                var parent = lookup == null ? null : lookup(inheritsFrom);
                if (parent == null)
                    throw new InvalidOperationException("父版本不在清单中：" + inheritsFrom);

                url = parent.Url;
                currentId = inheritsFrom;
            }

            return Merge(chain, ids);
        }

        public static ResolvedVersion Merge(IList<JObject> chain, IList<string> ids)
        {
            // chain[0] 是最底层（原版），chain[last] 是最上层（加载器）
            var result = new ResolvedVersion();
            var rawRoot = chain[chain.Count - 1];
            result.RawJson = rawRoot.ToString(Formatting.None);
            result.Id = (string)rawRoot["id"];
            result.Type = (string)rawRoot["type"];
            if (ids != null) result.InheritanceChain.AddRange(ids);

            result.MainClass = FirstString(chain, "mainClass");
            result.Assets = FirstString(chain, "assets");
            result.Arguments = MergeArguments(chain);
            result.LegacyArguments = FirstString(chain, "minecraftArguments");
            result.Jar = FirstString(chain, "jar");
            result.JavaVersion = FirstObject(chain, "javaVersion");
            result.Logging = FirstObject(chain, "logging");
            result.AssetIndex = FirstObject(chain, "assetIndex");

            JObject downloads = null;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var candidate = chain[i]["downloads"] as JObject;
                if (candidate == null || candidate["client"] == null) continue;
                downloads = candidate;
                result.ClientVersionId = ids != null && i < ids.Count ? ids[i] : (string)chain[i]["id"];
                break;
            }
            result.Client = downloads == null ? null : downloads["client"] as JObject;
            if (string.IsNullOrEmpty(result.ClientVersionId)) result.ClientVersionId = result.Id;

            foreach (var json in chain)
            {
                var libs = json["libraries"] as JArray;
                if (libs == null) continue;
                foreach (var lib in libs.OfType<JObject>()) result.Libraries.Add(lib);
            }

            return result;
        }

        /// <summary>
        /// arguments.jvm / arguments.game 要按链自底向上拼接：
        /// 加载器的 JSON 只写自己那几条，原版的 --username/--version 等必须保留。
        /// </summary>
        private static JObject MergeArguments(IList<JObject> chain)
        {
            var jvm = new JArray();
            var game = new JArray();
            for (int i = 0; i < chain.Count; i++)
            {
                var arguments = chain[i]["arguments"] as JObject;
                if (arguments == null) continue;
                if (arguments["jvm"] is JArray a) foreach (var token in a) jvm.Add(token.DeepClone());
                if (arguments["game"] is JArray b) foreach (var token in b) game.Add(token.DeepClone());
            }
            if (jvm.Count == 0 && game.Count == 0) return null;
            var merged = new JObject();
            if (jvm.Count > 0) merged["jvm"] = jvm;
            if (game.Count > 0) merged["game"] = game;
            return merged;
        }

        private static string FirstString(IList<JObject> chain, string prop)
        {
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var token = chain[i][prop];
                if (token != null && token.Type == JTokenType.String) return (string)token;
            }
            return null;
        }

        private static JObject FirstObject(IList<JObject> chain, string prop)
        {
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var token = chain[i][prop];
                if (token is JObject) return (JObject)token;
            }
            return null;
        }
    }
}
