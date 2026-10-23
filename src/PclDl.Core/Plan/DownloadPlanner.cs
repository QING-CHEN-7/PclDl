using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PclDl.Core.Layout;
using PclDl.Core.Logging;
using PclDl.Core.Models;
using PclDl.Core.Resolve;
using PclDl.Core.Transport;

namespace PclDl.Core.Plan
{
    /// <summary>把合并后的版本描述转换成可下载的文件清单。</summary>
    public sealed class DownloadPlanner
    {
        private readonly MirrorMap _mirror;
        private readonly bool _preferMirror;
        private readonly DataLayout _layout;
        private readonly ILog _log;

        public DownloadPlanner(MirrorMap mirror, bool preferMirror, DataLayout layout, ILog log)
        {
            _mirror = mirror; _preferMirror = preferMirror; _layout = layout; _log = log ?? new NullLog();
        }

        public PlanResult PlanCore(ResolvedVersion version)
        {
            var plan = new PlanResult();

            // 客户端 JAR
            if (version.Client != null)
            {
                var url = (string)version.Client["url"];
                var jarOwner = string.IsNullOrEmpty(version.ClientVersionId) ? version.Id : version.ClientVersionId;
                plan.Files.Add(new PlannedFile
                {
                    Target = _layout.VersionJar(jarOwner),
                    Urls = new List<string>(_mirror.VersionClientCandidates(version.Id, url, _preferMirror)),
                    Size = version.Client["size"] == null ? 0 : (long)version.Client["size"],
                    Sha1 = (string)version.Client["sha1"],
                    Kind = PlannedKind.Client
                });
            }

            // 资源索引
            if (version.AssetIndex != null)
            {
                var indexId = version.AssetIndexId;
                plan.AssetIndexId = indexId;
                plan.AssetIndexTarget = _layout.AssetIndex(indexId);
                if (!string.IsNullOrEmpty((string)version.AssetIndex["url"]))
                {
                    plan.Files.Add(new PlannedFile
                    {
                        Target = plan.AssetIndexTarget,
                        Urls = new List<string>(_mirror.Candidates((string)version.AssetIndex["url"], _preferMirror)),
                        Size = version.AssetIndex["size"] == null ? 0 : (long)version.AssetIndex["size"],
                        Sha1 = (string)version.AssetIndex["sha1"],
                        Kind = PlannedKind.AssetIndex
                    });
                }
            }

            // 库 + natives
            foreach (var lib in version.Libraries)
            {
                if (!RuleEvaluator.IsAllowed(lib["rules"])) continue;

                var downloads = lib["downloads"] as JObject;
                if (downloads == null)
                {
                    // 只有 maven 坐标 + 仓库地址（Fabric 等加载器 profile 的写法）
                    var mavenName = (string)lib["name"];
                    var repo = (string)lib["url"];
                    var rel = Loaders.FabricLoader.MavenToPath(mavenName);
                    if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(repo))
                    {
                        var baseUrl = repo.EndsWith("/") ? repo : repo + "/";
                        plan.Files.Add(new PlannedFile
                        {
                            Target = _layout.Library(rel),
                            Urls = new List<string>(_mirror.Candidates(baseUrl + rel, _preferMirror)),
                            Size = lib["size"] == null ? 0 : (long)lib["size"],
                            Sha1 = (string)lib["sha1"],
                            Kind = PlannedKind.Library
                        });
                    }
                    continue;
                }
                {
                    var artifact = downloads["artifact"] as JObject;
                    if (artifact != null)
                    {
                        var rel = (string)artifact["path"];
                        if (!string.IsNullOrEmpty(rel))
                        {
                            plan.Files.Add(new PlannedFile
                            {
                                Target = _layout.Library(rel),
                                Urls = new List<string>(_mirror.Candidates((string)artifact["url"], _preferMirror)),
                                Size = artifact["size"] == null ? 0 : (long)artifact["size"],
                                Sha1 = (string)artifact["sha1"],
                                Kind = PlannedKind.Library
                            });
                        }
                    }

                    var natives = lib["natives"] as JObject;
                    if (natives != null)
                    {
                        var classifier = (string)natives["windows"];
                        if (!string.IsNullOrEmpty(classifier))
                        {
                            var classifiers = downloads["classifiers"] as JObject;
                            var native = classifiers == null ? null : classifiers[classifier] as JObject;
                            if (native != null)
                            {
                                var rel = (string)native["path"];
                                var excludes = new List<string>();
                                var extract = lib["extract"] as JObject;
                                if (extract != null && extract["exclude"] is JArray)
                                    foreach (var ex in (JArray)extract["exclude"]) excludes.Add((string)ex);

                                var file = new PlannedFile
                                {
                                    Target = _layout.Library(rel),
                                    Urls = new List<string>(_mirror.Candidates((string)native["url"], _preferMirror)),
                                    Size = native["size"] == null ? 0 : (long)native["size"],
                                    Sha1 = (string)native["sha1"],
                                    Kind = PlannedKind.Native,
                                    ExtractTo = _layout.VersionNatives(version.Id),
                                    Exclude = excludes
                                };
                                plan.Files.Add(file);
                                plan.Natives.Add(file);
                            }
                        }
                    }
                }
            }

            // 日志配置
            if (version.Logging != null && version.Logging["client"] is JObject)
            {
                var file = version.Logging["client"]["file"] as JObject;
                if (file != null && !string.IsNullOrEmpty((string)file["url"]))
                {
                    var logId = (string)file["id"];
                    plan.Files.Add(new PlannedFile
                    {
                        Target = System.IO.Path.Combine(_layout.LogConfigsDir, logId),
                        Urls = new List<string>(_mirror.Candidates((string)file["url"], _preferMirror)),
                        Size = file["size"] == null ? 0 : (long)file["size"],
                        Sha1 = (string)file["sha1"],
                        Kind = PlannedKind.LogConfig
                    });
                }
            }

            return plan;
        }

        /// <summary>把一组库条目（含 Fabric 这类 maven-only 写法）转换成下载清单。</summary>
        public List<PlannedFile> PlanMavenLibraries(IEnumerable<JObject> libraries)
        {
            var list = new List<PlannedFile>();
            foreach (var lib in libraries)
            {
                if (!RuleEvaluator.IsAllowed(lib["rules"])) continue;

                var downloads = lib["downloads"] as JObject;
                var artifact = downloads == null ? null : downloads["artifact"] as JObject;
                string relativePath;
                string url;
                string sha1;
                long size;

                if (artifact != null)
                {
                    relativePath = (string)artifact["path"];
                    url = (string)artifact["url"];
                    sha1 = (string)artifact["sha1"];
                    size = artifact["size"] == null ? 0 : (long)artifact["size"];
                }
                else
                {
                    relativePath = Loaders.FabricLoader.MavenToPath((string)lib["name"]);
                    var repo = (string)lib["url"];
                    if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(repo)) continue;
                    var baseUrl = repo.EndsWith("/") ? repo : repo + "/";
                    url = baseUrl + relativePath;
                    sha1 = (string)lib["sha1"];
                    size = lib["size"] == null ? 0 : (long)lib["size"];
                }

                list.Add(new PlannedFile
                {
                    Target = _layout.Library(relativePath),
                    Urls = new List<string>(_mirror.Candidates(url, _preferMirror)),
                    Size = size,
                    Sha1 = sha1,
                    Kind = PlannedKind.Library
                });
            }
            return list;
        }

        /// <summary>资源索引内容 -> 资源对象下载清单。</summary>
        public List<PlannedFile> PlanAssets(JObject indexJson)
        {
            var list = new List<PlannedFile>();
            var objects = indexJson["objects"] as JObject;
            if (objects == null) return list;

            foreach (var prop in objects.Properties())
            {
                var obj = prop.Value as JObject;
                if (obj == null) continue;
                var hash = (string)obj["hash"];
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

                var url = "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash;
                list.Add(new PlannedFile
                {
                    Target = _layout.AssetObject(hash),
                    Urls = new List<string>(_mirror.Candidates(url, _preferMirror)),
                    Size = obj["size"] == null ? 0 : (long)obj["size"],
                    Sha1 = hash,
                    Kind = PlannedKind.Asset
                });
            }
            return list;
        }
    }
}
