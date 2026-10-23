using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PclDl.Core.Catalog;
using PclDl.Core.Java;
using PclDl.Core.Layout;
using PclDl.Core.Loaders;
using PclDl.Core.Logging;
using PclDl.Core.Models;
using PclDl.Core.Plan;
using PclDl.Core.Resolve;
using PclDl.Core.Transport;

namespace PclDl.Core.Install
{
    public sealed class InstallOptions
    {
        public string GameDir;
        public string VersionId;
        public bool PreferMirror = true;
        public int Concurrency = 8;
        public bool DryRun;
        /// <summary>加载器：none / fabric。</summary>
        public string Loader = "none";
        public string LoaderVersion;
        public Action<FetchProgress> OnProgress;
        public Action<string> OnStage;
    }

    public sealed class InstallSummary
    {
        public bool Success;
        public int Downloaded;
        public int Skipped;
        public int Failed;
        public List<string> Errors = new List<string>();
        public string InstalledVersionId;
        public TimeSpan Elapsed;
    }

    /// <summary>原版安装编排：清单 -> 版本 JSON -> 计划 -> 下载 -> 资源 -> natives。</summary>
    public sealed class InstallService
    {
        private readonly ILog _log;

        public InstallService(ILog log) { _log = log ?? new NullLog(); }

        public async Task<InstallSummary> InstallAsync(InstallOptions options, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            var summary = new InstallSummary();
            var layout = new DataLayout(options.GameDir);
            var mirror = MirrorMap.LoadOrCreate(options.GameDir);

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            using (var fetcher = new FileFetcher(_log, new FetchOptions { Concurrency = options.Concurrency }))
            {
                // 部分镜像（BMCLAPI 的 CDN）对没有 User-Agent 的请求会直接返回 403
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PclDl/0.1");
                var catalog = new VersionCatalog(http, mirror, options.PreferMirror, _log,
                    Path.Combine(options.GameDir, "PCL2Plugins", "cache", "manifest.json"));

                Stage(options, "获取版本清单");
                await catalog.LoadAsync().ConfigureAwait(false);

                var entry = catalog.Find(options.VersionId);
                if (entry == null) throw new InvalidOperationException("版本清单中不存在：" + options.VersionId);
                _log.Info("目标版本：" + entry.Id + "（" + entry.Type + "，发布于 " + entry.ReleaseTime + "）");

                Stage(options, "解析版本 JSON");
                var resolver = new VersionResolver(http, mirror, options.PreferMirror, _log);
                var version = await resolver.ResolveAsync(entry, id => catalog.Find(id)).ConfigureAwait(false);

                if (string.IsNullOrEmpty(version.Id)) throw new InvalidOperationException("版本 JSON 缺少 id");
                if (version.Client == null) throw new InvalidOperationException("版本 JSON 缺少 downloads.client");

                LoaderResult loaderResult = null;
                var isForge = options.Loader != null && options.Loader.Equals("forge", StringComparison.OrdinalIgnoreCase);
                var isNeoForge = options.Loader != null && options.Loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase);
                var isOptiFine = options.Loader != null && options.Loader.Equals("optifine", StringComparison.OrdinalIgnoreCase);
                string forgeVersion = null;
                if (isForge)
                {
                    Stage(options, "解析 Forge 版本");
                    var forge = new ForgeLoader(http, _log);
                    forgeVersion = await forge.ResolveVersionAsync(version.Id, options.LoaderVersion).ConfigureAwait(false);
                    _log.Info("Forge 版本：" + version.Id + "-" + forgeVersion);
                }
                else if (isNeoForge)
                {
                    Stage(options, "解析 NeoForge 版本");
                    var forge = new ForgeLoader(http, _log);
                    forgeVersion = await forge.ResolveNeoForgeVersionAsync(version.Id, options.LoaderVersion, options.GameDir).ConfigureAwait(false);
                    _log.Info("NeoForge 版本：" + forgeVersion);
                }
                else if (isOptiFine)
                {
                    Stage(options, "解析 OptiFine 版本");
                    var optifine = new OptiFineLoader(http, _log);
                    var optifineEntry = await optifine.ResolveLatestAsync(version.Id, options.LoaderVersion).ConfigureAwait(false);
                    _log.Info("OptiFine：" + optifineEntry.FileName);
                }
                else if (!string.IsNullOrEmpty(options.Loader) &&
                    !options.Loader.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    if (options.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
                    {
                        Stage(options, "解析 Fabric 加载器");
                        var fabric = new FabricLoader(http, _log);
                        loaderResult = await fabric.ResolveAsync(version.Id, options.LoaderVersion).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new NotSupportedException("暂不支持的加载器：" + options.Loader);
                    }
                }

                var planner = new DownloadPlanner(mirror, options.PreferMirror, layout, _log);
                var plan = planner.PlanCore(version);
                if (loaderResult != null)
                    plan.Files.AddRange(planner.PlanMavenLibraries(loaderResult.Libraries));

                layout.EnsureBaseDirectories();
                WriteVersionJson(layout.VersionJson(version.Id), version.RawJson);
                PclDl.Core.Layout.InstanceRegistrar.Register(layout, version.Id);
                if (loaderResult != null)
                {
                    WriteVersionJson(layout.VersionJson(loaderResult.VersionId), loaderResult.JsonText);
                    _log.Info("已写入加载器版本：" + loaderResult.VersionId);
                }

                if (options.DryRun)
                {
                    _log.Info("DryRun：核心 " + plan.Files.Count + " 个文件（资源清单需下载后才能展开）");
                    foreach (var f in plan.Files) _log.Info("  [" + f.Kind + "] " + f.Target);
                    summary.Success = true;
                    summary.Elapsed = watch.Elapsed;
                    return summary;
                }

                Stage(options, "下载核心文件");
                var coreResult = await fetcher.FetchAllAsync(plan.Files, MakeProgress(options), ct).ConfigureAwait(false);
                Accumulate(summary, coreResult);

                // 资源索引 -> 资源对象
                if (plan.AssetIndexTarget != null && File.Exists(plan.AssetIndexTarget))
                {
                    Stage(options, "展开资源索引");
                    var indexJson = JObject.Parse(File.ReadAllText(plan.AssetIndexTarget));
                    var assetFiles = planner.PlanAssets(indexJson);
                    _log.Info("资源对象：" + assetFiles.Count + " 个");

                    Stage(options, "下载资源对象");
                    var assetResult = await fetcher.FetchAllAsync(assetFiles, MakeProgress(options), ct).ConfigureAwait(false);
                    Accumulate(summary, assetResult);
                }

                // natives
                if (plan.Natives.Count > 0)
                {
                    Stage(options, "解压 natives");
                    foreach (var native in plan.Natives)
                    {
                        NativeExtractor.Extract(native.Target, native.ExtractTo, native.Exclude, _log);
                    }
                }

                // 加载器安装器对 Java 版本很挑（1.16 及以前必须 Java 8），先按版本挑一个
                string loaderJava = null;
                if ((isForge || isNeoForge || isOptiFine) && summary.Failed == 0)
                {
                    var requiredMajor = JavaLocator.RequiredMajorFor(version.Id, version.JavaVersion);
                    var runtimes = JavaLocator.Discover(false, Path.Combine(options.GameDir, "runtime"));
                    var picked = JavaLocator.SelectBest(runtimes, requiredMajor);
                    if (picked != null)
                    {
                        loaderJava = picked.JavaPath;
                        _log.Info("选择 Java：" + picked + "（" + version.Id + " 需要 Java " + requiredMajor + "）");
                    }
                    else
                    {
                        _log.Warn(JavaLocator.DescribeMissing(requiredMajor, runtimes) + " 先按默认 Java 继续。");
                    }
                }

                if (isForge && summary.Failed == 0)
                {
                    var forge = new ForgeLoader(http, _log);
                    summary.InstalledVersionId = await forge
                        .InstallAsync(version.Id, forgeVersion, options.GameDir, stage => Stage(options, stage), ct, loaderJava)
                        .ConfigureAwait(false);
                    _log.Info("Forge 安装完成：" + summary.InstalledVersionId);
                }
                else if (isOptiFine && summary.Failed == 0)
                {
                    var optifine = new OptiFineLoader(http, _log);
                    summary.InstalledVersionId = await optifine
                        .InstallAsync(version.Id, options.GameDir, options.LoaderVersion, stage => Stage(options, stage), ct, loaderJava)
                        .ConfigureAwait(false);
                    _log.Info("OptiFine 安装完成：" + summary.InstalledVersionId);
                }
                else if (isNeoForge && summary.Failed == 0)
                {
                    var neo = new ForgeLoader(http, _log);
                    summary.InstalledVersionId = await neo
                        .InstallNeoForgeAsync(version.Id, forgeVersion, options.GameDir, stage => Stage(options, stage), ct, loaderJava)
                        .ConfigureAwait(false);
                    _log.Info("NeoForge 安装完成：" + summary.InstalledVersionId);
                }

                if (summary.InstalledVersionId == null)
                    summary.InstalledVersionId = loaderResult != null ? loaderResult.VersionId : version.Id;

                summary.Success = summary.Failed == 0;
                summary.Elapsed = watch.Elapsed;
                return summary;
            }
        }

        private static void Stage(InstallOptions options, string stage)
        {
            if (options.OnStage != null) options.OnStage(stage);
        }

        private static IProgress<FetchProgress> MakeProgress(InstallOptions options)
        {
            if (options.OnProgress == null) return null;
            return new Progress<FetchProgress>(options.OnProgress);
        }

        private static void Accumulate(InstallSummary summary, FetchResult result)
        {
            summary.Downloaded += result.Downloaded;
            summary.Skipped += result.Skipped;
            summary.Failed += result.Failed;
            summary.Errors.AddRange(result.Errors);
        }

        private static void WriteVersionJson(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json);
        }
    }
}
