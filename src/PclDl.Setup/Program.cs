using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PclDl.Setup
{
    /// <summary>
    /// 单文件一键安装器：放进启动器文件夹双击运行一次。
    /// 自动识别 PCL / HMCL 及其版本，把插件（或 HMCL 适配件）铺好，并写出安装报告。
    /// 重复运行是安全的（幂等覆盖）。
    /// </summary>
    internal static class Program
    {
        private const string PayloadName = "PclDl.Setup.payload.zip";
        private const string PluginFolder = "PCL2Plugins";

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string targetDir = null, forced = null;
            var silent = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--dir": targetDir = i + 1 < args.Length ? args[++i] : null; break;
                    case "--pcl": forced = "pcl"; break;
                    case "--hmcl": forced = "hmcl"; break;
                    case "--silent": silent = true; break;
                    case "--help": case "-h": Usage(); return 0;
                }
            }

            var startDir = Path.GetFullPath(targetDir ?? AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\', '/');
            Console.WriteLine("PclDl 一键安装 · 起始目录：" + startDir);
            Console.WriteLine();

            // 自适应：启动器可能就在这一层，也可能在这一层的子目录里，或者本程序被放进了子目录
            var baseDir = startDir;
            var launcher = DetectLauncher(baseDir, forced);
            if (launcher == null)
            {
                var searchDirs = new List<string>();
                try { searchDirs.AddRange(Directory.GetDirectories(startDir)); } catch { }
                try
                {
                    var parent = Path.GetDirectoryName(startDir);
                    if (!string.IsNullOrEmpty(parent)) searchDirs.Add(parent);
                }
                catch { }
                foreach (var dir in searchDirs)
                {
                    var found = DetectLauncher(dir, forced);
                    if (found != null) { baseDir = dir; launcher = found; break; }
                }
            }
            if (launcher != null && baseDir != startDir)
            {
                Console.WriteLine("（在子目录/上级目录找到启动器：" + baseDir + "）");
                Console.WriteLine();
            }
            if (launcher == null)
            {
                Console.Error.WriteLine("没有在这找到 PCL 或 HMCL。");
                Console.Error.WriteLine("把本程序放进启动器所在文件夹再双击；或用 --dir <文件夹> 指定。");
                return 2;
            }

            Console.WriteLine("识别到启动器：" + launcher.Describe());
            Console.WriteLine();

            var payload = ExtractPayload();
            if (payload == null) { Console.Error.WriteLine("安装包内容缺失（payload 未嵌入），请重新构建。"); return 3; }

            var report = new List<string>();
            if (launcher.Kind == "pcl" && IsPclRunning())
            {
                Console.Error.WriteLine("PCL 正在运行，插件文件会写入失败。");
                Console.Error.WriteLine("请先完全关闭 PCL（包括托盘/后台进程）后再运行本程序。");
                try { Directory.Delete(payload, true); } catch { }
                return 5;
            }

            try
            {
                if (launcher.Kind == "pcl") InstallForPcl(baseDir, launcher, payload, report);
                else InstallForHmcl(baseDir, launcher, payload, report);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("安装失败：" + ex.Message);
                return 4;
            }
            finally
            {
                try { Directory.Delete(payload, true); } catch { }
            }

            foreach (var line in report) Console.WriteLine("  " + line);
            Console.WriteLine();

            var reportPath = Path.Combine(baseDir, "PclDl-安装报告.txt");
            try
            {
                File.WriteAllLines(reportPath, new[]
                {
                    "PclDl 一键安装报告",
                    "时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "目标目录：" + baseDir,
                    "启动器：" + launcher.Describe(),
                    ""
                }.Concat(report).ToArray(), new UTF8Encoding(false));
                Console.WriteLine("报告：" + reportPath);
            }
            catch { }

            Console.WriteLine();
            Console.WriteLine(launcher.Kind == "pcl"
                ? "完成。以后用同目录下的「启动 PCL2（插件版）」快捷方式启动即可，无需再运行本程序。"
                : "完成。HMCL 的文件已经放好，具体用法见 PclDl\\HMCL-适配说明.txt。");

            if (!silent && Environment.UserInteractive)
            {
                Console.WriteLine("（3 秒后自动关闭）");
                System.Threading.Thread.Sleep(3000);
            }
            return 0;
        }

        private static void Usage()
        {
            Console.WriteLine("PclDl 一键安装");
            Console.WriteLine("  PclDl.Setup.exe                 在自身所在文件夹识别启动器并安装");
            Console.WriteLine("  PclDl.Setup.exe --dir <文件夹>   指定启动器文件夹");
            Console.WriteLine("  PclDl.Setup.exe --pcl | --hmcl   强制按某类启动器处理");
            Console.WriteLine("  PclDl.Setup.exe --silent         不等待");
        }

        internal sealed class LauncherInfo
        {
            public string Kind;
            public string ExePath;
            public string Version;
            public string Product;

            public string Describe()
            {
                return (Kind == "pcl" ? "PCL（Plain Craft Launcher 2）" : "HMCL") +
                       " 版本 " + (string.IsNullOrEmpty(Version) ? "未知" : Version) +
                       " · " + Path.GetFileName(ExePath ?? "");
            }
        }

        private static LauncherInfo DetectLauncher(string dir, string forced)
        {
            if (forced != "hmcl")
            {
                var pcl = FindPcl(dir);
                if (pcl != null && forced != "pcl") return pcl;
                if (pcl != null && forced == "pcl") return pcl;
            }
            var hmcl = FindHmcl(dir);
            return hmcl;
        }

        private static LauncherInfo FindPcl(string dir)
        {
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (name.IndexOf("Plain Craft Launcher", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var info = FileVersionInfo.GetVersionInfo(file);
                    return new LauncherInfo
                    {
                        Kind = "pcl",
                        ExePath = file,
                        Version = info.FileVersion ?? info.ProductVersion,
                        Product = info.ProductName
                    };
                }
            }
            catch { }
            return null;
        }

        private static LauncherInfo FindHmcl(string dir)
        {
            try
            {
                var candidates = Directory.GetFiles(dir, "HMCL*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .ToList();
                if (candidates.Count == 0) return null;

                var pick = candidates[0];
                var version = ParseVersion(Path.GetFileNameWithoutExtension(pick));
                return new LauncherInfo { Kind = "hmcl", ExePath = pick, Version = version, Product = "HMCL" };
            }
            catch { }
            return null;
        }

        /// <summary>HMCL-3.16.4 → 3.16.4</summary>
        private static string ParseVersion(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name, @"(\d+(?:\.\d+)+)");
            return m.Success ? m.Groups[1].Value : name;
        }

        private static string ExtractPayload()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(PayloadName))
            {
                if (stream == null) return null;
                var dir = Path.Combine(Path.GetTempPath(), "pcldl-setup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var zipPath = Path.Combine(dir, "payload.zip");
                using (var file = File.Create(zipPath)) stream.CopyTo(file);
                var outDir = Path.Combine(dir, "payload");
                ZipFile.ExtractToDirectory(zipPath, outDir);
                return outDir;
            }
        }

        private static readonly string[] PluginFiles =
        {
            "PclDl.Injector.exe", "PclDl.Bridge.dll", "PclDl.Plugin.dll",
            "PclDl.Core.dll", "Newtonsoft.Json.dll", "PclDl.Probe.exe"
        };

        private static bool IsPclRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("Plain Craft Launcher 2");
                return processes != null && processes.Length > 0;
            }
            catch { return false; }
        }

        private static void InstallForPcl(string dir, LauncherInfo launcher, string payload, List<string> report)
        {
            var pluginDir = Path.Combine(dir, PluginFolder);
            Directory.CreateDirectory(pluginDir);

            var copied = 0;
            foreach (var name in PluginFiles)
            {
                var src = Path.Combine(payload, name);
                if (!File.Exists(src)) { report.Add("缺少产物：" + name); continue; }
                File.Copy(src, Path.Combine(pluginDir, name), true);
                copied++;
            }
            report.Add("已写入插件目录：" + pluginDir + "（" + copied + " 个文件）");

            // 旧版残留（曾放在 PCL 根目录）
            foreach (var name in new[] { "PclDl.Core.dll", "PclDl.Plugin.dll", "Newtonsoft.Json.dll", "PclDl.Bootstrap.dll" })
            {
                var stale = Path.Combine(dir, name);
                if (File.Exists(stale)) { try { File.Delete(stale); report.Add("清理旧版残留：" + name); } catch { } }
            }

            // PCL 的运行环境目录
            var pclDir = Path.Combine(dir, "PCL");
            if (!Directory.Exists(pclDir)) { Directory.CreateDirectory(pclDir); report.Add("已创建 PCL 运行目录"); }

            var version = ReadPayloadVersion(payload);
            var manifest = "{"
                + "\"product\":\"PclDl\","
                + "\"version\":\"" + version + "\","
                + "\"installedAt\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\","
                + "\"launcherKind\":\"PCL\","
                + "\"launcherVersion\":\"" + (launcher.Version ?? "unknown") + "\","
                + "\"pclExe\":\"" + launcher.ExePath.Replace("\\", "\\\\") + "\""
                + "}";
            File.WriteAllText(Path.Combine(pluginDir, "install.json"), manifest, new UTF8Encoding(false));
            report.Add("install.json：PCL " + (launcher.Version ?? "unknown"));

            var shortcut = Path.Combine(dir, "启动 PCL2（插件版）.lnk");
            if (!File.Exists(shortcut))
            {
                try
                {
                    var shellType = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic shell = Activator.CreateInstance(shellType);
                    var lnk = shell.CreateShortcut(shortcut);
                    lnk.TargetPath = Path.Combine(pluginDir, "PclDl.Injector.exe");
                    lnk.Arguments = "--pcl \"" + launcher.ExePath + "\" --auto-mount";
                    lnk.WorkingDirectory = pluginDir;
                    lnk.IconLocation = launcher.ExePath + ",0";
                    lnk.Description = "通过 PclDl 插件启动 PCL2";
                    lnk.Save();
                    report.Add("已创建快捷方式：启动 PCL2（插件版）.lnk");
                }
                catch (Exception ex) { report.Add("快捷方式创建失败（不影响使用）：" + ex.Message); }
            }
            else report.Add("快捷方式已存在，跳过");
        }

        private static void InstallForHmcl(string dir, LauncherInfo launcher, string payload, List<string> report)
        {
            var workDir = Path.Combine(dir, "PclDl");
            Directory.CreateDirectory(workDir);

            // 引擎 + CLI（HMCL 是 Java 程序，无法进程内挂插件，这里给的是"给 HMCL 装版本"的工具链）
            foreach (var name in new[] { "PclDl.Core.dll", "Newtonsoft.Json.dll", "build.json" })
            {
                var src = Path.Combine(payload, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(workDir, name), true);
            }
            var cli = Path.Combine(payload, "cli");
            if (Directory.Exists(cli))
            {
                foreach (var file in Directory.GetFiles(cli)) File.Copy(file, Path.Combine(workDir, Path.GetFileName(file)), true);
                report.Add("已写入 HMCL 适配工具链：" + workDir);
            }

            var note = new[]
            {
                "PclDl × HMCL 适配说明",
                "",
                "识别到的 HMCL：" + Path.GetFileName(launcher.ExePath) + "（版本 " + (launcher.Version ?? "未知") + "）",
                "",
                "HMCL 是 Java 程序，无法像 PCL 那样在进程内挂载插件面板；",
                "所以这里提供的是「用 PclDl 引擎给 HMCL 装版本/整合包」的工具链：",
                "",
                "  1) 装原版/加载器版本：",
                "     PclDl.Cli.exe install 1.20.1 --dir <HMCL 的 .minecraft 目录> --loader fabric",
                "  2) 装整合包：",
                "     PclDl.Cli.exe install-modpack 包.mrpack --dir <HMCL 的 .minecraft 目录>",
                "  3) 校验/修复：",
                "     PclDl.Cli.exe verify <版本ID> --dir <目录>",
                "     PclDl.Cli.exe repair <版本ID> --dir <目录>",
                "",
                "装完的实例就是标准 .minecraft 布局（versions/ libraries/ assets/），HMCL 会直接识别。",
                "",
                "生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            File.WriteAllLines(Path.Combine(workDir, "HMCL-适配说明.txt"), note, new UTF8Encoding(false));
            report.Add("已写说明：PclDl\\HMCL-适配说明.txt");
        }

        private static string ReadPayloadVersion(string payload)
        {
            try
            {
                var versionFile = Path.Combine(payload, "version.txt");
                if (File.Exists(versionFile)) return File.ReadAllText(versionFile).Trim();

                var build = Path.Combine(payload, "build.json");
                if (!File.Exists(build)) return "unknown";
                var text = File.ReadAllText(build);
                var key = text.IndexOf("\"version\"", StringComparison.Ordinal);
                if (key < 0) return "unknown";
                var colon = text.IndexOf(':', key);
                if (colon < 0) return "unknown";
                var q1 = text.IndexOf('"', colon + 1);
                if (q1 < 0) return "unknown";
                var q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) return "unknown";
                return text.Substring(q1 + 1, q2 - q1 - 1);
            }
            catch { return "unknown"; }
        }
    }
}
