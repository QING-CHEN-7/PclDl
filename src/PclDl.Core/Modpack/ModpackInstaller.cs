using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PclDl.Core.Install;
using PclDl.Core.Layout;
using PclDl.Core.Logging;
using PclDl.Core.Plan;
using PclDl.Core.Transport;

namespace PclDl.Core.Modpack
{
    public sealed class ModpackResult
    {
        public string InstanceId;
        public string BaseVersionId;
        public string Loader;
        public string LoaderVersion;
        public string Format;
        public int FilesDownloaded;
        public int FilesSkipped;
        public int FilesFailed;
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>
    /// 整合包安装：支持 Modrinth (.mrpack) 与 CurseForge (manifest.json) 两种格式。
    /// 安装为一个"版本实例"：versions/&lt;name&gt;/，其 JSON 继承自原版+指定加载器。
    /// </summary>
    public sealed class ModpackInstaller
    {
        private readonly ILog _log;

        public ModpackInstaller(ILog log) { _log = log ?? new NullLog(); }

        public Task<ModpackResult> InstallAsync(string packPath, string gameDir, string instanceName,
            bool preferMirror, Action<string> stage, CancellationToken ct)
        {
            return InstallAsync(packPath, gameDir, instanceName, preferMirror, stage, ct, null);
        }

        public async Task<ModpackResult> InstallAsync(string packPath, string gameDir, string instanceName,
            bool preferMirror, Action<string> stage, CancellationToken ct, string curseForgeKey)
        {
            if (!File.Exists(packPath)) throw new FileNotFoundException("找不到整合包", packPath);

            gameDir = DataLayout.NormalizeDir(gameDir);
            var layout = new DataLayout(gameDir);
            layout.EnsureBaseDirectories();

            var tempDir = Path.Combine(Path.GetTempPath(), "pcldl-mrpack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                if (stage != null) stage("解压整合包");
                ZipFile.ExtractToDirectory(packPath, tempDir);

                var modrinth = Path.Combine(tempDir, "modrinth.index.json");
                var curseforge = Path.Combine(tempDir, "manifest.json");

                if (File.Exists(modrinth))
                    return await InstallModrinthAsync(modrinth, tempDir, layout, gameDir, instanceName, preferMirror, stage, ct).ConfigureAwait(false);
                if (File.Exists(curseforge))
                    return await InstallCurseForgeAsync(curseforge, tempDir, layout, gameDir, instanceName, preferMirror, stage, ct, curseForgeKey).ConfigureAwait(false);

                throw new InvalidOperationException("不是有效的整合包（既没有 modrinth.index.json，也没有 manifest.json）");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ---------------------------------------------------------------- Modrinth

        private async Task<ModpackResult> InstallModrinthAsync(string indexPath, string tempDir, DataLayout layout,
            string gameDir, string instanceName, bool preferMirror, Action<string> stage, CancellationToken ct)
        {
            var index = JObject.Parse(File.ReadAllText(indexPath));
            var name = instanceName;
            if (string.IsNullOrEmpty(name)) name = (string)index["name"];
            if (string.IsNullOrEmpty(name)) name = "modpack";
            name = SanitizeName(name);

            var deps = index["dependencies"] as JObject ?? new JObject();
            var mcVersion = (string)deps["minecraft"];
            if (string.IsNullOrEmpty(mcVersion)) throw new InvalidOperationException("整合包未声明 Minecraft 版本");

            var loader = "none";
            string loaderVersion = null;
            if (deps["fabric-loader"] != null) { loader = "fabric"; loaderVersion = (string)deps["fabric-loader"]; }
            else if (deps["forge"] != null) { loader = "forge"; loaderVersion = (string)deps["forge"]; }
            else if (deps["neoforge"] != null) { loader = "neoforge"; loaderVersion = (string)deps["neoforge"]; }

            var result = new ModpackResult { InstanceId = name, Loader = loader, LoaderVersion = loaderVersion, Format = "Modrinth" };
            var versionDir = await InstallBaseAsync(layout, gameDir, mcVersion, loader, loaderVersion, preferMirror, stage, ct, result).ConfigureAwait(false);
            result.BaseVersionId = result.BaseVersionId ?? mcVersion;

            var overrides = Path.Combine(tempDir, "overrides");
            if (Directory.Exists(overrides))
            {
                if (stage != null) stage("复制 overrides");
                CopyDirectory(overrides, versionDir);
            }

            var files = index["files"] as JArray;
            if (files != null && files.Count > 0)
            {
                if (stage != null) stage("下载整合包文件（" + files.Count + " 个）");
                var plan = new List<PlannedFile>();
                foreach (var file in files.OfType<JObject>())
                {
                    var env = file["env"] as JObject;
                    if (env != null && env["client"] != null && (string)env["client"] == "unsupported") continue;

                    var relative = (string)file["path"];
                    if (string.IsNullOrEmpty(relative)) continue;

                    var urls = new List<string>();
                    var downloads = file["downloads"] as JArray;
                    if (downloads != null) urls.AddRange(downloads.Select(d => (string)d).Where(u => !string.IsNullOrEmpty(u)));
                    if (urls.Count == 0) continue;

                    var hashes = file["hashes"] as JObject;
                    plan.Add(new PlannedFile
                    {
                        Target = Path.Combine(versionDir, relative.Replace('/', Path.DirectorySeparatorChar)),
                        Urls = urls,
                        Size = file["fileSize"] == null ? 0 : (long)file["fileSize"],
                        Sha1 = hashes == null ? null : (string)hashes["sha1"],
                        Kind = PlannedKind.Library
                    });
                }
                await FetchAsync(plan, result, ct).ConfigureAwait(false);
            }

            _log.Info("整合包安装完成：" + name + "（基础版本 " + result.BaseVersionId + "）");
            return result;
        }

        // -------------------------------------------------------------- CurseForge

        private async Task<ModpackResult> InstallCurseForgeAsync(string manifestPath, string tempDir, DataLayout layout,
            string gameDir, string instanceName, bool preferMirror, Action<string> stage, CancellationToken ct, string apiKey)
        {
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            var name = instanceName;
            if (string.IsNullOrEmpty(name)) name = (string)manifest["name"];
            if (string.IsNullOrEmpty(name)) name = "modpack";
            name = SanitizeName(name);

            var minecraft = manifest["minecraft"] as JObject ?? new JObject();
            var mcVersion = (string)minecraft["version"];
            if (string.IsNullOrEmpty(mcVersion)) throw new InvalidOperationException("整合包未声明 Minecraft 版本");

            var loader = "none";
            string loaderVersion = null;
            var modLoaders = minecraft["modLoaders"] as JArray;
            if (modLoaders != null)
            {
                var primary = modLoaders.OfType<JObject>().FirstOrDefault(m => m["primary"] != null && (bool)m["primary"])
                              ?? modLoaders.OfType<JObject>().FirstOrDefault();
                if (primary != null)
                {
                    var id = (string)primary["id"] ?? "";
                    var parsed = ParseCurseForgeLoader(id, mcVersion);
                    loader = parsed.Item1;
                    loaderVersion = parsed.Item2;
                }
            }

            var result = new ModpackResult { InstanceId = name, Loader = loader, LoaderVersion = loaderVersion, Format = "CurseForge" };

            // 先把文件清单解析出来：没有 Key 就没必要先装半天基础版本
            var entries = (manifest["files"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            if (entries.Count > 0 && string.IsNullOrWhiteSpace(apiKey))
                apiKey = Environment.GetEnvironmentVariable("PCLDL_CURSEFORGE_KEY");
            if (entries.Count > 0 && string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("CurseForge 整合包需要 API Key：请设置环境变量 PCLDL_CURSEFORGE_KEY 后重试");

            var versionDir = await InstallBaseAsync(layout, gameDir, mcVersion, loader, loaderVersion, preferMirror, stage, ct, result).ConfigureAwait(false);
            result.BaseVersionId = result.BaseVersionId ?? mcVersion;

            var overridesName = (string)manifest["overrides"];
            if (string.IsNullOrEmpty(overridesName)) overridesName = "overrides";
            var overrides = Path.Combine(tempDir, overridesName);
            if (Directory.Exists(overrides))
            {
                if (stage != null) stage("复制 " + overridesName);
                CopyDirectory(overrides, versionDir);
            }

            if (entries.Count > 0)
            {
                if (stage != null) stage("解析 CurseForge 文件（" + entries.Count + " 个）");
                var plan = new List<PlannedFile>();
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PclDl/0.1");
                    var resolver = new CurseForgeResolver(http, _log, apiKey);
                    var failed = 0;
                    var consecutive = 0;
                    foreach (var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var projectId = entry["projectID"] == null ? 0 : (long)entry["projectID"];
                        var fileId = entry["fileID"] == null ? 0 : (long)entry["fileID"];
                        if (projectId <= 0 || fileId <= 0) continue;
                        try
                        {
                            var resolved = await resolver.ResolveFileAsync(projectId, fileId).ConfigureAwait(false);
                            plan.Add(new PlannedFile
                            {
                                Target = Path.Combine(versionDir, "mods", resolved.FileName),
                                Urls = new List<string> { resolved.Url },
                                Size = resolved.Size,
                                Kind = PlannedKind.Library
                            });
                            consecutive = 0;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            consecutive++;
                            result.Errors.Add("CurseForge " + projectId + "/" + fileId + "：" + ex.Message);
                            // 连续 3 个失败基本是 Key 无效/被限流，没必要再耗下去
                            if (consecutive >= 3) throw;
                        }
                    }
                    if (failed > 0) result.FilesFailed += failed;
                }

                if (result.FilesFailed > 0 && plan.Count == 0)
                    throw new InvalidOperationException("CurseForge 文件全部解析失败：" +
                        (result.Errors.Count > 0 ? result.Errors[0] : "未知原因"));

                if (stage != null) stage("下载整合包文件（" + plan.Count + " 个）");
                await FetchAsync(plan, result, ct).ConfigureAwait(false);
            }

            _log.Info("整合包安装完成：" + name + "（" + result.Format + "，基础版本 " + result.BaseVersionId + "）");
            return result;
        }

        /// <summary>把 "forge-47.2.0" / "forge-1.20.1-47.2.0" / "fabric-0.15.11" 拆成 (加载器, 版本)。</summary>
        public static Tuple<string, string> ParseCurseForgeLoader(string id, string mcVersion)
        {
            if (string.IsNullOrWhiteSpace(id)) return Tuple.Create("none", (string)null);
            var text = id.Trim();
            var dash = text.IndexOf('-');
            if (dash <= 0) return Tuple.Create("none", (string)null);

            var kind = text.Substring(0, dash).ToLowerInvariant();
            var rest = text.Substring(dash + 1);
            switch (kind)
            {
                case "forge": break;
                case "fabric": kind = "fabric"; break;
                case "neoforge": kind = "neoforge"; break;
                default: return Tuple.Create("none", (string)null);
            }

            if (!string.IsNullOrEmpty(mcVersion) && rest.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring(mcVersion.Length + 1);

            return Tuple.Create(kind, rest);
        }

        // ----------------------------------------------------------------- 公共

        private async Task<string> InstallBaseAsync(DataLayout layout, string gameDir, string mcVersion,
            string loader, string loaderVersion, bool preferMirror, Action<string> stage, CancellationToken ct, ModpackResult result)
        {
            if (stage != null) stage("安装基础版本 " + mcVersion + (loader == "none" ? "" : "（" + loader + "）"));
            var baseInstaller = new InstallService(_log);
            var baseSummary = await baseInstaller.InstallAsync(new InstallOptions
            {
                GameDir = gameDir,
                VersionId = mcVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
                PreferMirror = preferMirror,
                Concurrency = 16,
                OnStage = s => { if (stage != null) stage(s); }
            }, ct).ConfigureAwait(false);

            var baseVersionId = baseSummary.InstalledVersionId ?? mcVersion;
            result.BaseVersionId = baseVersionId;

            var versionDir = layout.VersionDir(result.InstanceId);
            Directory.CreateDirectory(versionDir);

            if (stage != null) stage("写入实例 JSON");
            // PCL 的 McInstance.Check 要求版本 JSON 自带 mainClass，
            // 所以把基础版本的 inheritsFrom 链合并成"自包含"JSON，而不是只写一个 inheritsFrom 指针。
            var instanceJson = BuildSelfContainedJson(layout, baseVersionId, result.InstanceId);
            File.WriteAllText(layout.VersionJson(result.InstanceId), instanceJson.ToString());
            InstanceRegistrar.Register(layout, result.InstanceId);
            return versionDir;
        }

        private async Task FetchAsync(List<PlannedFile> plan, ModpackResult result, CancellationToken ct)
        {
            if (plan.Count == 0) return;
            using (var fetcher = new FileFetcher(_log, new FetchOptions { Concurrency = 16 }))
            {
                var fetchResult = await fetcher.FetchAllAsync(plan, null, ct).ConfigureAwait(false);
                result.FilesDownloaded = fetchResult.Downloaded;
                result.FilesSkipped = fetchResult.Skipped;
                result.FilesFailed = fetchResult.Failed;
                result.Errors.AddRange(fetchResult.Errors);
            }
        }

        /// <summary>把基础版本及其 inheritsFrom 链合并成一个自包含的版本 JSON。</summary>
        private static JObject BuildSelfContainedJson(DataLayout layout, string baseVersionId, string instanceName)
        {
            var chain = new List<JObject>();
            var ids = new List<string>();
            var current = baseVersionId;
            for (int depth = 0; depth < 8; depth++)
            {
                var path = layout.VersionJson(current);
                if (!File.Exists(path)) break;
                var node = JObject.Parse(File.ReadAllText(path));
                chain.Insert(0, node);
                ids.Insert(0, current);
                var parent = (string)node["inheritsFrom"];
                if (string.IsNullOrEmpty(parent)) break;
                current = parent;
            }

            var merged = new JObject();
            if (chain.Count == 0)
            {
                merged["id"] = instanceName;
                merged["type"] = "release";
                return merged;
            }

            var resolved = Resolve.VersionResolver.Merge(chain, ids);
            merged["id"] = instanceName;
            merged["type"] = "release";
            merged["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00");
            merged["releaseTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00");
            if (!string.IsNullOrEmpty(resolved.MainClass)) merged["mainClass"] = resolved.MainClass;
            if (!string.IsNullOrEmpty(resolved.Assets)) merged["assets"] = resolved.Assets;
            if (resolved.AssetIndex != null) merged["assetIndex"] = resolved.AssetIndex;
            if (resolved.JavaVersion != null) merged["javaVersion"] = resolved.JavaVersion;
            if (resolved.Logging != null) merged["logging"] = resolved.Logging;
            if (resolved.Arguments != null) merged["arguments"] = resolved.Arguments;
            if (!string.IsNullOrEmpty(resolved.LegacyArguments)) merged["minecraftArguments"] = resolved.LegacyArguments;
            if (resolved.Client != null)
                merged["downloads"] = new JObject { ["client"] = resolved.Client };
            merged["libraries"] = new JArray(resolved.Libraries);

            // PCL 的 McLibListGet 用 RealInstance 目录下的 <id>.jar 当客户端 Jar：
            // 没有 jar/inheritsFrom 时它会去 versions\<实例>\<实例>.jar 找一个不存在的文件，
            // 因此这里显式指向链最底部的原版版本（基础安装一定已经准备好它的 jar）。
            var vanillaId = ids.Count > 0 ? ids[0] : null;
            if (!string.IsNullOrEmpty(vanillaId) && vanillaId != instanceName) merged["jar"] = vanillaId;
            return merged;
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "modpack";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            return string.IsNullOrEmpty(cleaned) ? "modpack" : cleaned;
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }
    }
}
