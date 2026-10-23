using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PclDl.Core.Layout;
using PclDl.Core.Logging;
using PclDl.Core.Models;
using PclDl.Core.Plan;
using PclDl.Core.Resolve;
using PclDl.Core.Transport;

namespace PclDl.Core.Install
{
    public sealed class RepairResult
    {
        public int Total;
        public int Ok;
        public int Broken;
        public int Downloaded;
        public int Failed;
        public int Remaining;
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Errors = new List<string>();

        public bool Success { get { return Remaining == 0; } }
    }

    /// <summary>
    /// 校验并修复：把缺失、尺寸不对、sha1 不符的文件重新下载，最后再复查一遍。
    /// 和"安装"共用同一套 Planner / FileFetcher，所以镜像、竞速、断点续传都自动继承。
    /// </summary>
    public sealed class RepairService
    {
        private readonly ILog _log;

        public RepairService(ILog log) { _log = log ?? new NullLog(); }

        public async Task<RepairResult> RepairAsync(string gameDir, string versionId, bool dryRun, CancellationToken ct)
        {
            gameDir = DataLayout.NormalizeDir(gameDir);
            var layout = new DataLayout(gameDir);
            if (!File.Exists(layout.VersionJson(versionId)))
                throw new FileNotFoundException("找不到版本 JSON", layout.VersionJson(versionId));

            var version = LoadMerged(layout, versionId);
            var planner = new DownloadPlanner(MirrorMap.LoadOrCreate(gameDir), false, layout, _log);
            var plan = planner.PlanCore(version);

            var files = new List<PlannedFile>(plan.Files);
            if (!string.IsNullOrEmpty(plan.AssetIndexTarget) && File.Exists(plan.AssetIndexTarget))
            {
                var indexJson = JObject.Parse(File.ReadAllText(plan.AssetIndexTarget));
                files.AddRange(planner.PlanAssets(indexJson));
            }

            var result = new RepairResult { Total = files.Count };
            var broken = new List<PlannedFile>();
            var reasons = new List<string>();
            foreach (var file in files)
            {
                var reason = WhyBroken(file);
                if (reason == null) { result.Ok++; continue; }
                broken.Add(file);
                reasons.Add(reason);
            }
            result.Broken = broken.Count;
            for (int i = 0; i < Math.Min(10, broken.Count); i++) result.Details.Add(reasons[i] + " " + broken[i].Target);

            if (broken.Count == 0 || dryRun)
            {
                result.Remaining = broken.Count;
                return result;
            }

            // 尺寸/sha1 不对的要先删，否则断点续传会接着坏数据往下写
            for (int i = 0; i < broken.Count; i++)
            {
                if (reasons[i] == "SHA1" || reasons[i] == "SIZE")
                {
                    try { File.Delete(broken[i].Target); } catch { }
                }
            }

            using (var fetcher = new FileFetcher(_log, new FetchOptions { Concurrency = 16 }))
            {
                var fetch = await fetcher.FetchAllAsync(broken, null, ct).ConfigureAwait(false);
                result.Downloaded = fetch.Downloaded;
                result.Failed = fetch.Failed;
                result.Errors.AddRange(fetch.Errors);
            }

            // natives 的 jar 可能是刚补回来的，需要重新解压
            foreach (var native in plan.Natives)
            {
                if (!File.Exists(native.Target)) continue;
                try { NativeExtractor.Extract(native.Target, native.ExtractTo, native.Exclude, _log); }
                catch (Exception ex) { _log.Warn("重新解压 natives 失败：" + ex.Message); }
            }

            foreach (var file in files) if (WhyBroken(file) != null) result.Remaining++;
            return result;
        }

        /// <summary>返回损坏原因（MISSING / SIZE / SHA1），完好返回 null。</summary>
        public static string WhyBroken(PlannedFile file)
        {
            return Verifier.Check(file.Target, file.Size, file.Sha1);
        }

        /// <summary>沿 inheritsFrom 链合并版本 JSON（加载器版本自身没有 assetIndex/完整库列表）。</summary>
        public static ResolvedVersion LoadMerged(DataLayout layout, string versionId)
        {
            var chain = new List<JObject>();
            var ids = new List<string>();
            var currentId = versionId;
            for (int depth = 0; depth < 8; depth++)
            {
                var path = layout.VersionJson(currentId);
                if (!File.Exists(path)) break;
                var node = JObject.Parse(File.ReadAllText(path));
                chain.Insert(0, node);
                ids.Insert(0, currentId);
                var parent = (string)node["inheritsFrom"];
                if (string.IsNullOrEmpty(parent)) break;
                currentId = parent;
            }
            return VersionResolver.Merge(chain, ids);
        }
    }
}
