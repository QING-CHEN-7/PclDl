using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PclDl.Core.Logging;

namespace PclDl.Core.Loaders
{
    /// <summary>
    /// OptiFine：下载官方安装器 jar，然后在临时 HOME 里以无界面方式运行
    /// `java -cp OptiFine.jar optifine.Installer`，最后把生成的版本目录拷回游戏目录。
    /// 思路与 PCL 的"方式 A"一致（-Duser.home + appdata 指向临时目录，避免污染真实用户目录）。
    /// </summary>
    public sealed class OptiFineLoader
    {
        private const string ListUrl = "https://bmclapi2.bangbang93.com/optifine/{0}";
        private const string DownloadUrl = "https://bmclapi2.bangbang93.com/optifine/{0}/{1}/{2}";

        private readonly HttpClient _http;
        private readonly ILog _log;

        public OptiFineLoader(HttpClient http, ILog log)
        {
            _http = http;
            _log = log ?? new NullLog();
        }

        public sealed class OptiFineEntry
        {
            public string McVersion;
            public string Type;      // HD_U
            public string Patch;     // I6
            public string FileName;
        }

        public async Task<OptiFineEntry> ResolveLatestAsync(string minecraftVersion, string requestedPatch = null)
        {
            var url = string.Format(ListUrl, minecraftVersion);
            var text = await GetStringAsync(url).ConfigureAwait(false);
            var array = JArray.Parse(text);
            var entries = new List<OptiFineEntry>();
            foreach (var item in array.OfType<JObject>())
            {
                var type = (string)item["type"] ?? "";
                var filename = (string)item["filename"] ?? "";
                if (!type.StartsWith("HD_U", StringComparison.OrdinalIgnoreCase)) continue;
                if (filename.StartsWith("preview_", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new OptiFineEntry
                {
                    McVersion = (string)item["mcversion"] ?? minecraftVersion,
                    Type = type,
                    Patch = (string)item["patch"] ?? "",
                    FileName = filename
                });
            }
            if (entries.Count == 0) throw new InvalidOperationException("OptiFine 没有 " + minecraftVersion + " 的正式版");

            if (!string.IsNullOrEmpty(requestedPatch))
            {
                var hit = entries.FirstOrDefault(e => string.Equals(e.Patch, requestedPatch, StringComparison.OrdinalIgnoreCase));
                if (hit == null) throw new InvalidOperationException("OptiFine 没有补丁版本 " + requestedPatch);
                return hit;
            }
            // patch 形如 I5 / I6 / J1，按字母+数字排序取最大
            return entries.OrderByDescending(e => e.Patch, StringComparer.OrdinalIgnoreCase).First();
        }

        public async Task<string> InstallAsync(string minecraftVersion, string gameDir, string patch,
            Action<string> stage, CancellationToken ct, string javaPath = null)
        {
            var java = string.IsNullOrEmpty(javaPath) ? ForgeLoader.FindJava() : javaPath;
            if (java == null) throw new InvalidOperationException("找不到 Java，无法运行 OptiFine 安装器");

            var entry = await ResolveLatestAsync(minecraftVersion, patch).ConfigureAwait(false);
            _log.Info("OptiFine：" + entry.FileName);

            var cacheDir = Path.Combine(gameDir, "PCL2Plugins", "cache", "optifine");
            Directory.CreateDirectory(cacheDir);
            var jarPath = Path.Combine(cacheDir, entry.FileName);
            if (!File.Exists(jarPath) || new FileInfo(jarPath).Length < 300 * 1024)
            {
                if (stage != null) stage("下载 OptiFine 安装器 " + entry.Patch);
                var downloadUrl = string.Format(DownloadUrl, minecraftVersion, entry.Type, entry.Patch);
                _log.Info("下载 OptiFine：" + downloadUrl);
                var bytes = await _http.GetByteArrayAsync(downloadUrl).ConfigureAwait(false);
                File.WriteAllBytes(jarPath, bytes);
            }

            // 临时 HOME，避免污染真实用户目录
            var tempHome = Path.Combine(Path.GetTempPath(), "pcldl-of-" + Guid.NewGuid().ToString("N"));
            var tempMc = Path.Combine(tempHome, ".minecraft");
            Directory.CreateDirectory(tempMc);
            try
            {
                if (stage != null) stage("准备 OptiFine 安装环境");
                File.WriteAllText(Path.Combine(tempMc, "launcher_profiles.json"),
                    "{\"profiles\":{},\"settings\":{},\"version\":3}");

                // 原版 json + jar
                var vanillaJson = Path.Combine(gameDir, "versions", minecraftVersion, minecraftVersion + ".json");
                var vanillaJar = Path.Combine(gameDir, "versions", minecraftVersion, minecraftVersion + ".jar");
                if (!File.Exists(vanillaJson) || !File.Exists(vanillaJar))
                    throw new InvalidOperationException("缺少原版文件，无法安装 OptiFine：" + minecraftVersion);

                var targetVersionDir = Path.Combine(tempMc, "versions", minecraftVersion);
                Directory.CreateDirectory(targetVersionDir);
                File.Copy(vanillaJson, Path.Combine(targetVersionDir, minecraftVersion + ".json"), true);
                if (!File.Exists(Path.Combine(targetVersionDir, minecraftVersion + ".jar")))
                    File.Copy(vanillaJar, Path.Combine(targetVersionDir, minecraftVersion + ".jar"), true);

                if (stage != null) stage("运行 OptiFine 安装器（可能需要几分钟）");
                var exit = await RunInstallerAsync(java, jarPath, tempHome, ct).ConfigureAwait(false);
                if (exit != 0) throw new InvalidOperationException("OptiFine 安装器退出码 " + exit);

                // 找生成的版本目录（排除原版）
                var produced = Directory.GetDirectories(Path.Combine(tempMc, "versions"))
                    .Select(Path.GetFileName)
                    .Where(id => !string.Equals(id, minecraftVersion, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (produced.Count == 0) throw new InvalidOperationException("OptiFine 安装器没有生成版本目录");

                if (stage != null) stage("复制 OptiFine 版本文件");
                foreach (var id in produced)
                {
                    var src = Path.Combine(tempMc, "versions", id);
                    var dst = Path.Combine(gameDir, "versions", id);
                    CopyDirectory(src, dst);
                    _log.Info("OptiFine 版本已写入：" + dst);
                }

                // 安装器还会把 optifine:OptiFine / optifine:launchwrapper-of 之类的库写进
                // <temp>/.minecraft/libraries，不一起带回去的话实例 classpath 会缺 jar。
                var tempLibraries = Path.Combine(tempMc, "libraries");
                var copiedLibraries = CopyMissingLibraries(tempLibraries, Path.Combine(gameDir, "libraries"));
                if (copiedLibraries > 0) _log.Info("OptiFine 库文件已写入：" + copiedLibraries + " 个");

                return produced[0];
            }
            finally
            {
                try { Directory.Delete(tempHome, true); } catch { }
            }
        }

        /// <summary>把临时 HOME 里新生成的库拷进游戏目录（已存在的不覆盖，只补缺）。</summary>
        private static int CopyMissingLibraries(string sourceRoot, string targetRoot)
        {
            if (!Directory.Exists(sourceRoot)) return 0;
            var count = 0;
            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine(targetRoot, relative);
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, false);
                count++;
            }
            return count;
        }

        private async Task<int> RunInstallerAsync(string java, string jarPath, string tempHome, CancellationToken ct)
        {
            var arguments = "-Duser.home=\"" + tempHome + "\" -cp \"" + jarPath + "\" optifine.Installer";
            var psi = new ProcessStartInfo
            {
                FileName = java,
                Arguments = arguments,
                WorkingDirectory = tempHome,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["appdata"] = tempHome;

            using (var process = new Process { StartInfo = psi })
            using (ct.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
            {
                process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _log.Debug("[optifine] " + e.Data); };
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _log.Warn("[optifine] " + e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await Task.Run(() => process.WaitForExit(), ct).ConfigureAwait(false);
                return process.ExitCode;
            }
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

        private async Task<string> GetStringAsync(string url)
        {
            using (var resp = await _http.GetAsync(url).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }
    }
}
