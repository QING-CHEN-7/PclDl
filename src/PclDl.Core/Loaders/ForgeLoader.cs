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
    /// Forge / NeoForge：走官方 installer（java -jar ... --installClient）。
    /// 安装器自己会下载库、跑 processors、生成版本 JSON，我们只负责解析版本号、下载安装器、执行并校验。
    /// </summary>
    public sealed class ForgeLoader
    {
        private const string PromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        private const string NeoForgeMaven = "https://maven.neoforged.net/releases";
        private const string NeoForgeMavenMirror = "https://bmclapi2.bangbang93.com/maven";
        private static readonly string[] MavenBases = { NeoForgeMavenMirror, NeoForgeMaven };
        private const string NeoForgeInstallerUrlFormat = "https://maven.neoforged.net/releases/net/neoforged/neoforge/{0}/neoforge-{0}-installer.jar";
        private const string InstallerUrlFormat =
            "https://maven.minecraftforge.net/net/minecraftforge/forge/{0}-{1}/forge-{0}-{1}-installer.jar";

        private readonly HttpClient _http;
        private readonly ILog _log;

        public ForgeLoader(HttpClient http, ILog log)
        {
            _http = http;
            _log = log ?? new NullLog();
        }

        public async Task<string> ResolveVersionAsync(string minecraftVersion, string requested = null)
        {
            if (!string.IsNullOrEmpty(requested)) return requested;

            var text = await GetStringAsync(PromotionsUrl).ConfigureAwait(false);
            var json = JObject.Parse(text);
            var promos = (JObject)json["promos"];
            if (promos == null) throw new InvalidOperationException("Forge promotions 格式异常");

            var recommended = (string)promos[minecraftVersion + "-recommended"];
            var latest = (string)promos[minecraftVersion + "-latest"];
            var version = !string.IsNullOrEmpty(recommended) ? recommended : latest;
            if (string.IsNullOrEmpty(version))
                throw new InvalidOperationException("Forge 没有 " + minecraftVersion + " 的推荐或最新版本");
            return version;
        }

        /// <summary>下载安装器并用 Java 执行，返回生成的版本 ID。</summary>
        public async Task<string> InstallAsync(string minecraftVersion, string forgeVersion,
            string gameDir, Action<string> stage, CancellationToken ct, string javaPath = null)
        {
            var existing = FindInstalledVersion(gameDir, minecraftVersion, forgeVersion);
            if (existing != null)
            {
                if (stage != null) stage("Forge 已安装，跳过");
                _log.Info("检测到已安装的 Forge 版本：" + existing + "，跳过安装器");
                return existing;
            }

            gameDir = PclDl.Core.Layout.DataLayout.NormalizeDir(gameDir);
            var java = string.IsNullOrEmpty(javaPath) ? FindJava() : javaPath;
            if (java == null) throw new InvalidOperationException("找不到 Java，无法运行 Forge 安装器");

            var installerUrl = string.Format(InstallerUrlFormat, minecraftVersion, forgeVersion);
            var cacheDir = Path.Combine(gameDir, "PCL2Plugins", "cache", "forge");
            Directory.CreateDirectory(cacheDir);
            var installerPath = Path.Combine(cacheDir, "forge-" + minecraftVersion + "-" + forgeVersion + "-installer.jar");

            if (!File.Exists(installerPath) || new FileInfo(installerPath).Length < 1024 * 1024)
            {
                if (stage != null) stage("下载 Forge 安装器 " + forgeVersion);
                _log.Info("下载 Forge 安装器：" + installerUrl);
                var bytes = await _http.GetByteArrayAsync(installerUrl).ConfigureAwait(false);
                File.WriteAllBytes(installerPath, bytes);
                _log.Info("安装器已保存：" + installerPath + "（" + bytes.Length + " 字节）");
            }

            // Forge 安装器要求目标目录存在 launcher_profiles.json
            var profiles = Path.Combine(gameDir, "launcher_profiles.json");
            if (!File.Exists(profiles))
            {
                File.WriteAllText(profiles, "{\"profiles\":{},\"settings\":{},\"version\":3}");
                _log.Info("已创建 launcher_profiles.json（Forge 安装器前置要求）");
            }

            if (stage != null) stage("运行 Forge 安装器（需要几分钟）");
            var exit = await RunJavaAsync(java, installerPath, gameDir, ct).ConfigureAwait(false);
            if (exit != 0) throw new InvalidOperationException("Forge 安装器退出码 " + exit +
                (string.IsNullOrEmpty(_lastInstallerOutput) ? "" : "：" + _lastInstallerOutput));

            var expected = minecraftVersion + "-forge-" + forgeVersion;
            var versionJson = Path.Combine(gameDir, "versions", expected, expected + ".json");
            if (!File.Exists(versionJson))
            {
                // 有些版本会写成 forge-<mc>-<ver>，扫一遍
                var versionsDir = Path.Combine(gameDir, "versions");
                if (Directory.Exists(versionsDir))
                {
                    var match = Directory.GetDirectories(versionsDir)
                        .FirstOrDefault(d => Path.GetFileName(d).IndexOf(forgeVersion, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                             Path.GetFileName(d).IndexOf(minecraftVersion, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match != null) return Path.GetFileName(match);
                }
                throw new InvalidOperationException("安装器已执行，但未找到生成的版本目录：" + expected);
            }
            return expected;
        }

        /// <summary>NeoForge：MC 版本映射到前缀（1.21.1 → 21.1），从 maven 元数据里取最高版本。</summary>
        public async Task<string> ResolveNeoForgeVersionAsync(string minecraftVersion, string requested = null, string gameDir = null)
        {
            if (!string.IsNullOrEmpty(requested)) return requested;

            var target = NeoForgeTargetFor(minecraftVersion);
            if (target == null) throw new InvalidOperationException("无法把 MC 版本映射到 NeoForge 版本：" + minecraftVersion);

            // maven-metadata.xml 有几十 KB，而且镜像经常抽风；缓存 24 小时，取不到网再退回旧缓存
            var cacheFile = MetadataCachePath(gameDir, target);
            if (cacheFile != null && File.Exists(cacheFile) &&
                File.GetLastWriteTimeUtc(cacheFile) > DateTime.UtcNow.AddHours(-24))
            {
                var cached = SelectNeoForgeVersion(File.ReadAllText(cacheFile), target.VersionFilter);
                if (cached != null)
                {
                    _log.Info("NeoForge 版本（缓存）：" + cached);
                    return cached;
                }
            }

            var urls = new List<string>();
            foreach (var baseUrl in MavenBases) urls.Add(baseUrl + "/" + target.ArtifactPath + "/maven-metadata.xml");

            string xml;
            try
            {
                xml = await GetStringFromCandidatesAsync(urls, 2).ConfigureAwait(false);
                if (cacheFile != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                    File.WriteAllText(cacheFile, xml);
                }
            }
            catch (Exception ex)
            {
                if (cacheFile != null && File.Exists(cacheFile))
                {
                    _log.Warn("拉取 NeoForge 版本清单失败，改用旧缓存：" + ex.Message);
                    xml = File.ReadAllText(cacheFile);
                }
                else throw;
            }

            var picked = SelectNeoForgeVersion(xml, target.VersionFilter);
            if (picked == null)
                throw new InvalidOperationException("NeoForge 没有匹配 " + minecraftVersion + "（" + target.ArtifactPath +
                                                    " 前缀 " + target.VersionFilter + "）的版本");
            return picked;
        }

        private static string MetadataCachePath(string gameDir, NeoForgeTarget target)
        {
            if (string.IsNullOrEmpty(gameDir) || target == null) return null;
            var name = target.ArtifactPath.Replace('/', '_') + ".maven-metadata.xml";
            return Path.Combine(gameDir, "PCL2Plugins", "cache", "neoforge", name);
        }

        /// <summary>
        /// NeoForge 的坐标随版本变了：
        /// 1.20.1 → net.neoforged:forge，版本号带 MC 前缀（1.20.1-47.1.106）
        /// 1.20.2+ → net.neoforged:neoforge，版本号就是 20.4.x / 21.1.x
        /// </summary>
        public sealed class NeoForgeTarget
        {
            public string ArtifactPath;
            public string FileStem;
            public string VersionFilter;
        }

        public static NeoForgeTarget NeoForgeTargetFor(string minecraftVersion)
        {
            if (string.IsNullOrEmpty(minecraftVersion)) return null;
            if (minecraftVersion == "1.20.1")
                return new NeoForgeTarget
                {
                    ArtifactPath = "net/neoforged/forge",
                    FileStem = "forge",
                    VersionFilter = "1.20.1-47.1"
                };

            var prefix = NeoForgePrefix(minecraftVersion);
            if (prefix == null) return null;
            return new NeoForgeTarget
            {
                ArtifactPath = "net/neoforged/neoforge",
                FileStem = "neoforge",
                VersionFilter = prefix
            };
        }

        public static string NeoForgeInstallerUrl(string baseUrl, NeoForgeTarget target, string version)
        {
            return baseUrl + "/" + target.ArtifactPath + "/" + version + "/" +
                   target.FileStem + "-" + version + "-installer.jar";
        }

        /// <summary>在 versions\ 下找已安装的加载器版本（<mc>-forge-<ver> / neoforge-<ver>）。</summary>
        private static string FindInstalledVersion(string gameDir, string minecraftVersion, string loaderVersion)
        {
            var versionsDir = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versionsDir)) return null;

            var suffix = loaderVersion.IndexOf('-') >= 0
                ? loaderVersion.Substring(loaderVersion.LastIndexOf('-') + 1)
                : loaderVersion;

            var direct = new[]
            {
                minecraftVersion + "-forge-" + loaderVersion,
                minecraftVersion + "-neoforge-" + loaderVersion,
                "neoforge-" + loaderVersion,
                loaderVersion
            };
            foreach (var id in direct)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (File.Exists(Path.Combine(versionsDir, id, id + ".json"))) return id;
            }

            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                if (id.IndexOf(suffix, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (id.IndexOf("forge", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(minecraftVersion) &&
                    id.IndexOf(minecraftVersion, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (File.Exists(Path.Combine(dir, id + ".json"))) return id;
            }
            return null;
        }

        /// <summary>从 maven-metadata.xml 里挑出该前缀下最高的版本（按数字段比较）。</summary>
        public static string SelectNeoForgeVersion(string xml, string prefix)
        {
            if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(prefix)) return null;
            var versions = new List<string>();
            var index = 0;
            while (true)
            {
                var start = xml.IndexOf("<version>", index, StringComparison.Ordinal);
                if (start < 0) break;
                var end = xml.IndexOf("</version>", start, StringComparison.Ordinal);
                if (end < 0) break;
                var value = xml.Substring(start + 9, end - start - 9).Trim();
                if (value.StartsWith(prefix + ".", StringComparison.Ordinal)) versions.Add(value);
                index = end + 10;
            }
            if (versions.Count == 0) return null;
            return versions.OrderByDescending(v => v, Comparer<string>.Create(CompareVersions)).First();
        }

        /// <summary>按数字段比较版本号：21.1.217 > 21.1.99。</summary>
        public static int CompareVersions(string left, string right)
        {
            var a = (left ?? "").Split('.');
            var b = (right ?? "").Split('.');
            int length = Math.Max(a.Length, b.Length);
            for (int i = 0; i < length; i++)
            {
                int x = 0, y = 0;
                if (i < a.Length) int.TryParse(a[i], out x);
                if (i < b.Length) int.TryParse(b[i], out y);
                if (x != y) return x.CompareTo(y);
            }
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public static string NeoForgePrefix(string minecraftVersion)
        {
            if (string.IsNullOrEmpty(minecraftVersion)) return null;

            // NeoForge 在 1.20.1 时代用的是 47.1.x，和 MC 小版本号没有关系（1.20.1 → 47.1.106 之类）
            if (minecraftVersion == "1.20.1") return "47.1";

            var parts = minecraftVersion.Split('.');
            if (parts.Length < 2) return null;
            var major = parts[1];
            var minor = parts.Length >= 3 ? parts[2] : "0";
            int ignore;
            if (!int.TryParse(major, out ignore) || !int.TryParse(minor, out ignore)) return null;
            return major + "." + minor;
        }

        /// <summary>
        /// 从安装器 jar 里读 install_profile.json / version.json，把其中的库用镜像补到 libraries\。
        /// 只补本地缺失的；下载失败只记警告，安装器还有机会自己下。
        /// </summary>
        private async Task SeedLibrariesAsync(string installerPath, string gameDir, CancellationToken ct)
        {
            try
            {
                var librariesDir = Path.Combine(gameDir, "libraries");
                Directory.CreateDirectory(librariesDir);

                var entries = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                using (var zip = System.IO.Compression.ZipFile.OpenRead(installerPath))
                {
                    foreach (var name in new[] { "install_profile.json", "version.json" })
                    {
                        var entry = zip.GetEntry(name);
                        if (entry == null) continue;
                        using (var reader = new StreamReader(entry.Open()))
                        {
                            var json = JObject.Parse(reader.ReadToEnd());
                            var libraries = json["libraries"] as JArray;
                            if (libraries == null) continue;
                            foreach (var library in libraries.OfType<JObject>())
                            {
                                var path = LibraryRelativePath(library);
                                if (path != null) entries[path] = library;
                            }
                        }
                    }
                }

                var downloaded = 0;
                var skipped = 0;
                var failed = 0;
                foreach (var pair in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.Combine(librariesDir, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target)) { skipped++; continue; }

                    var artifact = pair.Value["downloads"]?["artifact"] as JObject;
                    var sha1 = artifact == null ? null : (string)artifact["sha1"];
                    var urls = new List<string>();
                    foreach (var baseUrl in MavenBases) urls.Add(baseUrl + "/" + pair.Key);

                    var got = false;
                    foreach (var url in urls)
                    {
                        try
                        {
                            var bytes = await GetBytesWithTimeoutAsync(url).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(sha1) && !string.Equals(PclDl.Core.Transport.Hashing.Sha1(bytes), sha1, StringComparison.OrdinalIgnoreCase))
                            {
                                _log.Warn("镜像文件校验不符，跳过：" + url);
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.WriteAllBytes(target, bytes);
                            downloaded++;
                            got = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            _log.Debug("预置库失败 " + url + "：" + ex.Message);
                        }
                    }
                    if (!got)
                    {
                        failed++;
                        _log.Warn("预置库失败：" + pair.Key + "（安装器可能需要联网自取）");
                    }
                }

                _log.Info("NeoForge 库预置：新增 " + downloaded + "，已存在 " + skipped + "，失败 " + failed);
            }
            catch (Exception ex)
            {
                _log.Warn("预置 NeoForge 库时出错（不影响继续尝试安装器）：" + ex.Message);
            }
        }

        /// <summary>把 name（group:artifact:version[:classifier][@ext]）换算成 maven 相对路径。</summary>
        public static string LibraryRelativePath(JObject library)
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
            var artifactId = parts[1];
            var version = parts[2];
            var classifier = parts.Length >= 4 ? "-" + parts[3] : "";
            return group + "/" + artifactId + "/" + version + "/" + artifactId + "-" + version + classifier + "." + ext;
        }

        /// <summary>下载并运行 NeoForge 安装器。</summary>
        public async Task<string> InstallNeoForgeAsync(string minecraftVersion, string neoVersion,
            string gameDir, Action<string> stage, CancellationToken ct, string javaPath = null)
        {
            var existing = FindInstalledVersion(gameDir, minecraftVersion, neoVersion);
            if (existing != null)
            {
                if (stage != null) stage("NeoForge 已安装，跳过");
                _log.Info("检测到已安装的 NeoForge 版本：" + existing + "，跳过安装器");
                return existing;
            }

            gameDir = PclDl.Core.Layout.DataLayout.NormalizeDir(gameDir);
            var java = string.IsNullOrEmpty(javaPath) ? FindJava() : javaPath;
            if (java == null) throw new InvalidOperationException("找不到 Java，无法运行 NeoForge 安装器");

            var target = NeoForgeTargetFor(minecraftVersion);
            if (target == null) throw new InvalidOperationException("无法把 MC 版本映射到 NeoForge 版本：" + minecraftVersion);

            var cacheDir = Path.Combine(gameDir, "PCL2Plugins", "cache", "neoforge");
            Directory.CreateDirectory(cacheDir);
            var installerPath = Path.Combine(cacheDir, target.FileStem + "-" + neoVersion + "-installer.jar");

            if (!File.Exists(installerPath) || new FileInfo(installerPath).Length < 512 * 1024)
            {
                if (stage != null) stage("下载 NeoForge 安装器 " + neoVersion);
                var urls = new List<string>();
                foreach (var baseUrl in MavenBases) urls.Add(NeoForgeInstallerUrl(baseUrl, target, neoVersion));
                _log.Info("下载 NeoForge 安装器：" + urls[0]);
                var bytes = await GetBytesFromCandidatesAsync(urls).ConfigureAwait(false);
                File.WriteAllBytes(installerPath, bytes);
                _log.Info("安装器已保存：" + installerPath + "（" + bytes.Length + " 字节）");
            }

            var profiles = Path.Combine(gameDir, "launcher_profiles.json");
            if (!File.Exists(profiles))
                File.WriteAllText(profiles, "{\"profiles\":{},\"settings\":{},\"version\":3}");

            // maven.neoforged.net 在部分网络下不可达，安装器会因下载不到自己的库而失败。
            // 先用镜像把这些库预置到 libraries\ 里，安装器看到本地校验通过就会跳过下载。
            await SeedLibrariesAsync(installerPath, gameDir, ct).ConfigureAwait(false);

            if (stage != null) stage("运行 NeoForge 安装器（需要几分钟）");
            var exit = await RunJavaAsync(java, installerPath, gameDir, ct).ConfigureAwait(false);
            if (exit != 0) throw new InvalidOperationException("NeoForge 安装器退出码 " + exit +
                (string.IsNullOrEmpty(_lastInstallerOutput) ? "" : "：" + _lastInstallerOutput));

            var versionsDir = Path.Combine(gameDir, "versions");
            if (Directory.Exists(versionsDir))
            {
                // NeoForge 47.x（1.20.1 时代）的构件叫 forge，生成的目录是 1.20.1-forge-47.1.106，
                // 新版本的才是 neoforge-47.1.x / 20.4.x，所以按版本号匹配，名字里带 neoforge 的优先。
                // 版本号可能带 MC 前缀（1.20.1-47.1.106），而目录名是 1.20.1-forge-47.1.106，
                // 所以用版本号最后一段（47.1.106）去匹配，并要求目录名里带 MC 版本。
                var needle = neoVersion.IndexOf('-') >= 0
                    ? neoVersion.Substring(neoVersion.LastIndexOf('-') + 1)
                    : neoVersion;
                var candidates = Directory.GetDirectories(versionsDir)
                    .Where(d =>
                    {
                        var id = Path.GetFileName(d);
                        if (id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) return false;
                        return string.IsNullOrEmpty(minecraftVersion) ||
                               id.IndexOf(minecraftVersion, StringComparison.OrdinalIgnoreCase) >= 0;
                    })
                    .ToList();
                var match = candidates.FirstOrDefault(d => Path.GetFileName(d).IndexOf("neoforge", StringComparison.OrdinalIgnoreCase) >= 0)
                            ?? candidates.FirstOrDefault();
                if (match != null) return Path.GetFileName(match);
            }
            throw new InvalidOperationException("NeoForge 安装器已执行，但未找到生成的版本目录（" + neoVersion + "）");
        }

        /// <summary>
        /// 给命令行参数加引号：以反斜杠结尾的值会与闭合引号组成 \" 转义，
        /// 导致安装器把路径读成 "G:\x\.minecraft\""（少了引号、多了字符）。这里先补一个反斜杠。
        /// </summary>
        public static string Quote(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value + (value.EndsWith("\\") || value.EndsWith("/") ? "\\" : "") + "\"";
        }

        private async Task<int> RunJavaAsync(string java, string installer, string gameDir, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = java,
                Arguments = "-jar " + Quote(installer) + " --installClient " + Quote(gameDir),
                WorkingDirectory = gameDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var tail = new Queue<string>();
            Action<string> remember = line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                lock (tail)
                {
                    tail.Enqueue(line.Trim());
                    while (tail.Count > 15) tail.Dequeue();
                }
            };

            using (var process = new Process { StartInfo = psi })
            using (ct.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    _log.Debug("[forge] " + e.Data);
                    remember(e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    _log.Warn("[forge] " + e.Data);
                    remember(e.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await Task.Run(() => process.WaitForExit(), ct).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    // 失败时把安装器最后几行直接带进日志，否则只剩一个退出码，排查无从下手
                    string[] lines;
                    lock (tail) lines = tail.ToArray();
                    foreach (var line in lines) _log.Info("[forge]   " + line);
                    _lastInstallerOutput = string.Join(" | ", lines);
                }
                return process.ExitCode;
            }
        }

        private string _lastInstallerOutput;

        public static string FindJava()
        {
            var home = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(home))
            {
                var candidate = Path.Combine(home, "bin", "java.exe");
                if (File.Exists(candidate)) return candidate;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var candidate = Path.Combine(dir.Trim(), "java.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        private const int AttemptTimeoutSeconds = 30;

        private async Task<string> GetStringFromCandidatesAsync(IEnumerable<string> urls, int retries = 3)
        {
            Exception last = null;
            foreach (var url in urls)
            {
                for (int attempt = 1; attempt <= retries; attempt++)
                {
                    try { return await GetStringWithTimeoutAsync(url).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        last = ex;
                        _log.Warn("拉取失败 " + url + "（第 " + attempt + " 次）：" + ex.Message);
                        await Task.Delay(800 * attempt).ConfigureAwait(false);
                    }
                }
            }
            throw new InvalidOperationException("所有候选地址都不可用", last);
        }

        private async Task<byte[]> GetBytesFromCandidatesAsync(IEnumerable<string> urls)
        {
            Exception last = null;
            foreach (var url in urls)
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try { return await GetBytesWithTimeoutAsync(url).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        last = ex;
                        _log.Warn("下载失败 " + url + "（第 " + attempt + " 次）：" + ex.Message);
                        await Task.Delay(800 * attempt).ConfigureAwait(false);
                    }
                }
            }
            throw new InvalidOperationException("所有候选地址都不可用", last);
        }

        private async Task<string> GetStringWithTimeoutAsync(string url)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AttemptTimeoutSeconds)))
            using (var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private async Task<byte[]> GetBytesWithTimeoutAsync(string url)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AttemptTimeoutSeconds)))
            using (var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
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
