using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PclDl.Core.Logging;
using PclDl.Core.Models;
using PclDl.Core.Transport;

namespace PclDl.Core.Catalog
{
    /// <summary>版本清单：官方 + 镜像回退，本地缓存。</summary>
    public sealed class VersionCatalog
    {
        public const string OfficialUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

        private readonly HttpClient _http;
        private readonly MirrorMap _mirror;
        private readonly bool _preferMirror;
        private readonly ILog _log;
        private readonly string _cachePath;

        public readonly List<ManifestEntry> Versions = new List<ManifestEntry>();
        public string LatestRelease { get; private set; }
        public string LatestSnapshot { get; private set; }
        public DateTime FetchedAt { get; private set; }

        public VersionCatalog(HttpClient http, MirrorMap mirror, bool preferMirror, ILog log, string cachePath)
        {
            _http = http; _mirror = mirror; _preferMirror = preferMirror; _log = log ?? new NullLog();
            _cachePath = cachePath;
        }

        /// <summary>上次成功使用的源，用于下次优先。</summary>
        public static string PreferredUrl;

        /// <summary>最近一次拉取耗时（毫秒）。</summary>
        public int LastFetchMilliseconds { get; private set; }

        private async Task<Tuple<string, string, int>> FetchManifestAsync(string url)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8)))
            using (var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                watch.Stop();
                return Tuple.Create(text, url, (int)watch.ElapsedMilliseconds);
            }
        }

        public async Task LoadAsync(bool force = false)
        {
            if (!force && Versions.Count > 0) return;

            if (!force && TryLoadCache()) return;

            // 与 PCL 一致：多个源同时发车，谁先返回用谁（PCL 的 DlSourceLoader 也是并行竞速）
            var candidates = new List<string>(_mirror.Candidates(OfficialUrl, _preferMirror));
            if (PreferredUrl != null)
            {
                candidates.Remove(PreferredUrl);
                candidates.Insert(0, PreferredUrl);
            }

            Exception last = null;
            var racing = candidates.Select(url => FetchManifestAsync(url)).ToList();
            while (racing.Count > 0)
            {
                var finished = await Task.WhenAny(racing).ConfigureAwait(false);
                racing.Remove(finished);
                try
                {
                    var result = await finished.ConfigureAwait(false);
                    Parse(result.Item1);
                    SaveCache(result.Item1);
                    PreferredUrl = result.Item2;
                    LastFetchMilliseconds = result.Item3;
                    _log.Info("版本清单：" + result.Item2 + "（" + result.Item3 + " ms）");
                    return;
                }
                catch (Exception ex) { last = ex; _log.Debug("清单源失败 :: " + ex.Message); }
            }
            throw new InvalidOperationException("无法获取版本清单（官方与镜像均失败）", last);
        }

        public void Parse(string text)
        {
            var json = JObject.Parse(text);
            Versions.Clear();
            LatestRelease = (string)(json["latest"] == null ? null : json["latest"]["release"]);
            LatestSnapshot = (string)(json["latest"] == null ? null : json["latest"]["snapshot"]);
            foreach (var item in (JArray)json["versions"])
            {
                Versions.Add(new ManifestEntry
                {
                    Id = (string)item["id"],
                    Type = (string)item["type"],
                    Url = (string)item["url"],
                    Sha1 = (string)item["sha1"],
                    Size = item["size"] == null ? 0 : (long)item["size"],
                    ReleaseTime = (string)item["releaseTime"]
                });
            }
            FetchedAt = DateTime.Now;
        }

        public ManifestEntry Find(string id)
        {
            return Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private bool TryLoadCache()
        {
            try
            {
                if (string.IsNullOrEmpty(_cachePath) || !File.Exists(_cachePath)) return false;
                var info = new FileInfo(_cachePath);
                if (DateTime.Now - info.LastWriteTime > TimeSpan.FromMinutes(10)) return false;
                Parse(File.ReadAllText(_cachePath));
                return Versions.Count > 0;
            }
            catch { return false; }
        }

        private void SaveCache(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(_cachePath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
                File.WriteAllText(_cachePath, text);
            }
            catch (Exception ex) { _log.Debug("清单缓存写入失败：" + ex.Message); }
        }
    }
}
