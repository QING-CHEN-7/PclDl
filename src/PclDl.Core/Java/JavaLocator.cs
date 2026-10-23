using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PclDl.Core.Java
{
    /// <summary>一个可用的 Java 运行时。</summary>
    public sealed class JavaRuntime
    {
        public string JavaPath;
        public string Home;
        public int MajorVersion;
        public string Version;
        public string Source;

        public override string ToString()
        {
            return "Java " + MajorVersion + (string.IsNullOrEmpty(Version) ? "" : "（" + Version + "）") +
                   " · " + (Home ?? JavaPath);
        }
    }

    /// <summary>
    /// 查找本机 Java 运行时，并按 Minecraft 版本挑出合适的那个。
    /// PCL 自己的规则：1.16 及以前要 Java 8，1.17 要 16+，1.18+ 要 17+，1.20.5+ 要 21+。
    /// </summary>
    public static class JavaLocator
    {
        private static readonly object Gate = new object();
        private static List<JavaRuntime> _cache;

        public static IReadOnlyList<JavaRuntime> Discover(bool forceRefresh = false, string extraRoot = null)
        {
            lock (Gate)
            {
                if (_cache != null && !forceRefresh) return _cache;
                var found = new List<JavaRuntime>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                Action<string, string> addHome = (home, source) =>
                {
                    if (string.IsNullOrWhiteSpace(home)) return;
                    var java = Path.Combine(home.Trim('"', ' '), "bin", "java.exe");
                    if (!File.Exists(java)) return;
                    if (!seen.Add(java)) return;
                    var runtime = Inspect(java);
                    if (runtime == null) return;
                    runtime.Source = source;
                    found.Add(runtime);
                };

                // 1) 显式指定
                var explicitJava = Environment.GetEnvironmentVariable("PCLDL_JAVA");
                if (!string.IsNullOrWhiteSpace(explicitJava) && File.Exists(explicitJava))
                {
                    var runtime = Inspect(explicitJava);
                    if (runtime != null)
                    {
                        runtime.Source = "PCLDL_JAVA";
                        if (seen.Add(runtime.JavaPath)) found.Add(runtime);
                    }
                }

                // 2) JAVA_HOME
                addHome(Environment.GetEnvironmentVariable("JAVA_HOME"), "JAVA_HOME");

                // 3) PATH
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var java = Path.Combine(dir.Trim(), "java.exe");
                    if (File.Exists(java) && seen.Add(java))
                    {
                        var runtime = Inspect(java);
                        if (runtime != null)
                        {
                            runtime.Source = "PATH";
                            found.Add(runtime);
                        }
                    }
                }

                // 4) 常见安装位置 + 启动器自带的 runtime 目录
                var roots = new List<string>();
                if (!string.IsNullOrWhiteSpace(extraRoot)) roots.Add(extraRoot);
                var envRoots = Environment.GetEnvironmentVariable("PCLDL_JAVA_DIRS");
                if (!string.IsNullOrWhiteSpace(envRoots))
                    roots.AddRange(envRoots.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries));

                roots.AddRange(new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java")
                });

                foreach (var root in roots)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                        if (File.Exists(Path.Combine(root, "bin", "java.exe")))
                        {
                            addHome(root, "目录扫描");
                            continue;
                        }
                        foreach (var child in Directory.GetDirectories(root))
                        {
                            if (File.Exists(Path.Combine(child, "bin", "java.exe"))) addHome(child, "目录扫描");
                            // launcher 的 runtime 目录是 runtime/<组件>/<平台>/bin/java.exe
                            else
                            {
                                foreach (var grand in SafeDirectories(child))
                                    if (File.Exists(Path.Combine(grand, "bin", "java.exe"))) addHome(grand, "目录扫描");
                            }
                        }
                    }
                    catch { }
                }

                _cache = found.OrderByDescending(r => r.MajorVersion).ToList();
                return _cache;
            }
        }

        private static IEnumerable<string> SafeDirectories(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static JavaRuntime Inspect(string javaPath)
        {
            try
            {
                var home = Path.GetDirectoryName(Path.GetDirectoryName(javaPath));
                var version = ReadReleaseVersion(home) ?? ProbeVersion(javaPath);
                var major = ParseMajor(version);
                if (major <= 0) return null;
                return new JavaRuntime { JavaPath = javaPath, Home = home, Version = version, MajorVersion = major };
            }
            catch { return null; }
        }

        /// <summary>JDK 安装目录里的 release 文件里就有版本号，读它比执行 java 快得多。</summary>
        private static string ReadReleaseVersion(string home)
        {
            try
            {
                if (string.IsNullOrEmpty(home)) return null;
                var release = Path.Combine(home, "release");
                if (!File.Exists(release)) return null;
                foreach (var line in File.ReadAllLines(release))
                {
                    if (!line.StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase)) continue;
                    return line.Substring("JAVA_VERSION=".Length).Trim().Trim('"');
                }
            }
            catch { }
            return null;
        }

        private static string ProbeVersion(string javaPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = javaPath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null) return null;
                    var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    // openjdk version "17.0.14" 2025-01-21
                    var first = text.IndexOf('"');
                    var last = text.IndexOf('"', first + 1);
                    if (first >= 0 && last > first) return text.Substring(first + 1, last - first - 1);
                }
            }
            catch { }
            return null;
        }

        /// <summary>"1.8.0_392" → 8；"17.0.14" → 17；"21" → 21。</summary>
        public static int ParseMajor(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return 0;
            var text = version.Trim().Trim('"');
            var parts = text.Split('.', '_', '-', '+');
            if (parts.Length == 0) return 0;

            int first;
            if (!int.TryParse(parts[0], out first)) return 0;
            if (first == 1 && parts.Length > 1)
            {
                int second;
                return int.TryParse(parts[1], out second) ? second : 0;
            }
            return first;
        }

        /// <summary>按 PCL 的规则给出这个版本需要的 Java 主版本。</summary>
        /// <param name="mcVersion">用于兜底推断的 MC 版本号。</param>
        /// <param name="javaVersion">版本 JSON 的 javaVersion 对象（{component, majorVersion}），也兼容整份 JSON。</param>
        public static int RequiredMajorFor(string mcVersion, JObject javaVersion = null)
        {
            // 版本 JSON 里写了就用它（最权威）
            var info = javaVersion;
            if (info != null && info["majorVersion"] == null && info["javaVersion"] is JObject)
                info = (JObject)info["javaVersion"];
            if (info != null && info["majorVersion"] != null)
            {
                int major;
                if (int.TryParse(info["majorVersion"].ToString(), out major) && major > 0) return major;
            }

            var parts = (mcVersion ?? "").Split('.');
            int minor = 0, patch = 0;
            if (parts.Length >= 2) int.TryParse(parts[1], out minor);
            if (parts.Length >= 3) int.TryParse(parts[2], out patch);
            if (parts.Length < 2) return 8;

            if (minor <= 16) return 8;
            if (minor == 17) return 16;
            if (minor <= 20) return 17;
            return 21;   // 1.21+
        }

        /// <summary>从候选里挑一个：优先精确匹配，其次挑满足要求且最低的版本。</summary>
        public static JavaRuntime SelectBest(IEnumerable<JavaRuntime> runtimes, int requiredMajor)
        {
            if (runtimes == null || requiredMajor <= 0) return null;
            var list = runtimes.Where(r => r != null).ToList();
            var exact = list.Where(r => r.MajorVersion == requiredMajor)
                            .OrderBy(r => r.MajorVersion).FirstOrDefault();
            if (exact != null) return exact;

            // Java 8 时代的版本对高版本 JVM 兼容性很差，只有 needed<=8 时不给别的版本兜底
            if (requiredMajor <= 8) return null;

            return list.Where(r => r.MajorVersion >= requiredMajor)
                       .OrderBy(r => r.MajorVersion)
                       .FirstOrDefault();
        }

        /// <summary>找不到时给用户的提示。</summary>
        public static string DescribeMissing(int requiredMajor, IEnumerable<JavaRuntime> runtimes)
        {
            var list = (runtimes ?? Enumerable.Empty<JavaRuntime>()).ToList();
            var found = list.Count == 0
                ? "本机没有找到任何 Java 运行时"
                : "本机只找到 " + string.Join("、", list.Select(r => "Java " + r.MajorVersion).Distinct());
            return "需要 Java " + requiredMajor + " 才能运行安装器，但" + found +
                   "。请安装 Java " + requiredMajor + "，或设置 PCLDL_JAVA 指向它的 java.exe。";
        }
    }
}
