using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using PclDl.Core.Catalog;
using PclDl.Core.Install;
using PclDl.Core.Logging;
using PclDl.Core.Transport;

namespace PclDl.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0) { Usage(); return 2; }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "install": return CmdInstall(args.Skip(1).ToArray());
                    case "manifest": return CmdManifest(args.Skip(1).ToArray());
                    case "list": return CmdList(args.Skip(1).ToArray());
                    case "verify": return CmdVerify(args.Skip(1).ToArray());
                    case "repair": return CmdRepair(args.Skip(1).ToArray());
                    case "launch-plan": return CmdLaunchPlan(args.Skip(1).ToArray());
                    case "launch": return CmdLaunch(args.Skip(1).ToArray());
                    case "install-modpack": return CmdInstallModpack(args.Skip(1).ToArray());
                    default: Usage(); return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[error] " + ex.Message);
                if (ex.InnerException != null) Console.Error.WriteLine("        " + ex.InnerException.Message);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.WriteLine("PclDl 0.1 - Minecraft 版本下载/安装内核（PCL2 下载插件）");
            Console.WriteLine();
            Console.WriteLine("  pcl2dl install <版本ID> [--dir <目录>] [--source mirror|official] [--concurrency N] [--loader fabric|forge|neoforge|optifine] [--loader-version X] [--dry-run]");
            Console.WriteLine("  pcl2dl manifest [--dir <目录>] [--refresh]");
            Console.WriteLine("  pcl2dl list [--dir <目录>]");
            Console.WriteLine("  pcl2dl verify <版本ID> [--dir <目录>]");
            Console.WriteLine("  pcl2dl repair <版本ID> [--dir <目录>] [--dry-run]     校验并补下载缺失/损坏的文件");
            Console.WriteLine("  pcl2dl launch-plan <版本ID> [--dir <目录>] [--print]   拼出启动命令行并检查依赖是否齐全");
            Console.WriteLine("  pcl2dl launch <版本ID> [--dir <目录>] [--wait]        直接启动游戏（自检/兜底用）");
            Console.WriteLine("  pcl2dl install-modpack <整合包.zip> [--dir <目录>] [--name <实例名>] [--source mirror|official] [--cf-key <CurseForge Key>]");
            Console.WriteLine();
            Console.WriteLine("示例： pcl2dl install 1.20.1 --dir G:\\mc-test");
        }

        private static int CmdInstall(string[] a)
        {
            if (a.Length == 0) { Usage(); return 2; }
            var versionId = a[0];
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i].StartsWith("--"))
                {
                    var key = a[i].Substring(2);
                    if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) { opts[key] = a[++i]; }
                    else flags.Add(key);
                }
            }

            var gameDir = opts.ContainsKey("dir") ? opts["dir"] : Path.Combine(Environment.CurrentDirectory, ".minecraft");
            var preferMirror = !(opts.ContainsKey("source") && opts["source"].Equals("official", StringComparison.OrdinalIgnoreCase));
            var concurrency = opts.ContainsKey("concurrency") ? int.Parse(opts["concurrency"]) : 8;
            var dryRun = flags.Contains("dry-run");
            var loader = opts.ContainsKey("loader") ? opts["loader"] : "none";
            var loaderVersion = opts.ContainsKey("loader-version") ? opts["loader-version"] : null;

            var log = new ConsoleLog(verbose: flags.Contains("verbose"));
            Console.WriteLine("目标目录：" + Path.GetFullPath(gameDir));
            Console.WriteLine("下载源：" + (preferMirror ? "镜像优先（BMCLAPI）" : "官方优先"));

            var options = new InstallOptions
            {
                GameDir = gameDir,
                VersionId = versionId,
                PreferMirror = preferMirror,
                Concurrency = concurrency,
                DryRun = dryRun,
                Loader = loader,
                LoaderVersion = loaderVersion,
                OnStage = s => { Console.WriteLine(); Console.WriteLine("== " + s + " =="); },
                OnProgress = p => RenderProgress(p)
            };

            var service = new InstallService(log);
            using (var cts = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler handler = (s, e) => { e.Cancel = true; cts.Cancel(); };
                Console.CancelKeyPress += handler;
                try
                {
                    var summary = service.InstallAsync(options, cts.Token).GetAwaiter().GetResult();
                    Console.WriteLine();
                    Console.WriteLine("完成：下载 " + summary.Downloaded + "，跳过 " + summary.Skipped +
                                      "，失败 " + summary.Failed + "，耗时 " + summary.Elapsed.TotalSeconds.ToString("0.0") + "s");
                    if (!string.IsNullOrEmpty(summary.InstalledVersionId))
                        Console.WriteLine("版本 ID：" + summary.InstalledVersionId);
                    if (summary.Errors.Count > 0)
                    {
                        Console.WriteLine("失败清单：");
                        foreach (var e in summary.Errors.Take(20)) Console.WriteLine("  " + e);
                    }
                    return summary.Success ? 0 : 1;
                }
                finally { Console.CancelKeyPress -= handler; }
            }
        }

        private static void RenderProgress(FetchProgress p)
        {
            if (p == null || p.FilesTotal == 0) return;
            var pct = p.BytesTotal > 0 ? (100.0 * p.BytesDone / p.BytesTotal) : (100.0 * p.FilesDone / p.FilesTotal);
            var mb = p.BytesDone / 1024.0 / 1024.0;
            var totalMb = p.BytesTotal / 1024.0 / 1024.0;
            var line = "  " + p.FilesDone + "/" + p.FilesTotal + " 文件  " +
                       mb.ToString("0.0") + "/" + totalMb.ToString("0.0") + " MB  " + pct.ToString("0.0") + "%";
            try
            {
                var width = Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 100;
                if (line.Length > width) line = line.Substring(0, width);
                Console.Write("\r" + line.PadRight(width));
            }
            catch { }
        }

        private static int CmdManifest(string[] a)
        {
            var gameDir = Path.Combine(Environment.CurrentDirectory, ".minecraft");
            var refresh = false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == "--dir" && i + 1 < a.Length) gameDir = a[++i];
                if (a[i] == "--refresh") refresh = true;
            }

            var log = new ConsoleLog(true);
            var mirror = MirrorMap.CreateBmclapi();
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                var catalog = new VersionCatalog(http, mirror, true, log, Path.Combine(gameDir, "PCL2Plugins", "cache", "manifest.json"));
                catalog.LoadAsync(refresh).GetAwaiter().GetResult();
                Console.WriteLine("版本数：" + catalog.Versions.Count);
                Console.WriteLine("最新正式版：" + catalog.LatestRelease);
                Console.WriteLine("最新快照：" + catalog.LatestSnapshot);
                foreach (var v in catalog.Versions.Take(20))
                    Console.WriteLine("  " + v.Id.PadRight(20) + v.Type.PadRight(10) + v.ReleaseTime);
            }
            return 0;
        }


        private static int CmdInstallModpack(string[] a)
        {
            if (a.Length == 0) { Usage(); return 2; }
            var pack = a[0];
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i].StartsWith("--") && i + 1 < a.Length) opts[a[i].Substring(2)] = a[++i];
            }

            var gameDir = opts.ContainsKey("dir") ? opts["dir"] : Path.Combine(Environment.CurrentDirectory, ".minecraft");
            var name = opts.ContainsKey("name") ? opts["name"] : null;
            var preferMirror = !(opts.ContainsKey("source") && opts["source"].Equals("official", StringComparison.OrdinalIgnoreCase));

            var key = opts.ContainsKey("cf-key") ? opts["cf-key"] : null;
            var log = new ConsoleLog(true);
            Console.WriteLine("整合包：" + Path.GetFullPath(pack));
            Console.WriteLine("目标目录：" + Path.GetFullPath(gameDir));

            using (var cts = new CancellationTokenSource())
            {
                var installer = new PclDl.Core.Modpack.ModpackInstaller(log);
                var result = installer.InstallAsync(pack, gameDir, name, preferMirror,
                    stage => Console.WriteLine("== " + stage + " =="), cts.Token).GetAwaiter().GetResult();

                Console.WriteLine("完成：实例 " + result.InstanceId + "（" + result.Format + "，基础版本 " + result.BaseVersionId +
                                  "，加载器 " + result.Loader + "）");
                Console.WriteLine("文件：下载 " + result.FilesDownloaded + "，跳过 " + result.FilesSkipped +
                                  "，失败 " + result.FilesFailed);
                foreach (var e in result.Errors.Take(10)) Console.WriteLine("  " + e);
                return result.FilesFailed == 0 ? 0 : 1;
            }
        }

        private static int CmdVerify(string[] a)
        {
            if (a.Length == 0) { Usage(); return 2; }
            var versionId = a[0];
            var gameDir = Path.Combine(Environment.CurrentDirectory, ".minecraft");
            for (int i = 1; i < a.Length; i++) if (a[i] == "--dir" && i + 1 < a.Length) gameDir = a[++i];

            var layout = new PclDl.Core.Layout.DataLayout(gameDir);
            var jsonPath = layout.VersionJson(versionId);
            if (!File.Exists(jsonPath))
            {
                Console.Error.WriteLine("找不到版本 JSON：" + jsonPath);
                return 1;
            }

            // 沿 inheritsFrom 链向上找出父版本，再自底向上合并（加载器版本自身没有 assetIndex）
            var chain = new System.Collections.Generic.List<Newtonsoft.Json.Linq.JObject>();
            var ids = new System.Collections.Generic.List<string>();
            var currentId = versionId;
            for (int depth = 0; depth < 8; depth++)
            {
                var path = layout.VersionJson(currentId);
                if (!File.Exists(path)) break;
                var node = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                chain.Insert(0, node);
                ids.Insert(0, currentId);
                var parent = (string)node["inheritsFrom"];
                if (string.IsNullOrEmpty(parent)) break;
                currentId = parent;
            }

            var version = PclDl.Core.Resolve.VersionResolver.Merge(chain, ids);

            var planner = new PclDl.Core.Plan.DownloadPlanner(MirrorMap.CreateBmclapi(), false, layout, new NullLog());
            var plan = planner.PlanCore(version);

            var files = new System.Collections.Generic.List<PclDl.Core.Plan.PlannedFile>(plan.Files);
            if (!string.IsNullOrEmpty(plan.AssetIndexTarget) && File.Exists(plan.AssetIndexTarget))
            {
                var indexJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(plan.AssetIndexTarget));
                files.AddRange(planner.PlanAssets(indexJson));
            }
            else
            {
                Console.WriteLine("[warn] 资源索引缺失，无法校验资源对象：" + plan.AssetIndexTarget);
            }

            int ok = 0, missing = 0, bad = 0;
            var problems = new System.Collections.Generic.List<string>();
            foreach (var f in files)
            {
                if (!File.Exists(f.Target)) { missing++; problems.Add("MISSING " + f.Target); continue; }
                var info = new FileInfo(f.Target);
                if (f.Size > 0 && info.Length != f.Size) { bad++; problems.Add("SIZE    " + f.Target + " (" + info.Length + " != " + f.Size + ")"); continue; }
                if (!string.IsNullOrEmpty(f.Sha1))
                {
                    var actual = PclDl.Core.Transport.Hashing.Sha1File(f.Target);
                    if (!string.Equals(actual, f.Sha1, StringComparison.OrdinalIgnoreCase)) { bad++; problems.Add("SHA1    " + f.Target); continue; }
                }
                ok++;
            }

            Console.WriteLine("校验版本：" + versionId);
            Console.WriteLine("  文件总数：" + files.Count);
            Console.WriteLine("  通过：" + ok + "　缺失：" + missing + "　损坏：" + bad);
            foreach (var p2 in problems.Take(10)) Console.WriteLine("  " + p2);
            return (missing == 0 && bad == 0) ? 0 : 1;
        }

        /// <summary>用 launch-plan 的结果直接拉起游戏；--wait 时阻塞到进程退出。</summary>
        private static int CmdLaunch(string[] a)
        {
            if (a.Length == 0) { Usage(); return 2; }
            var versionId = a[0];
            var gameDir = Path.Combine(Environment.CurrentDirectory, ".minecraft");
            var wait = false;
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i] == "--dir" && i + 1 < a.Length) gameDir = a[++i];
                else if (a[i] == "--wait") wait = true;
            }

            var plan = PclDl.Core.Launch.LaunchPlanner.Build(gameDir, versionId);
            if (!plan.Ok)
            {
                Console.Error.WriteLine("[error] 依赖不完整，先跑 repair：" +
                                        plan.MissingClasspath.Count + " 个 classpath / " +
                                        plan.MissingNatives.Count + " 个 natives / " +
                                        plan.AssetMissing + " 个资源对象缺失");
                return 1;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = plan.JavaPath,
                Arguments = string.Join(" ", plan.Arguments.Select(PclDl.Core.Launch.LaunchPlanner.Quote)),
                WorkingDirectory = PclDl.Core.Layout.DataLayout.NormalizeDir(gameDir),
                // 不等待退出时用 ShellExecute：子进程不继承我们的控制台，
                // 否则调用方的控制台要等到游戏退出才释放（实测会卡住脚本）。
                UseShellExecute = !wait
            };

            var process = System.Diagnostics.Process.Start(psi);
            Console.WriteLine("已启动：" + versionId + "（PID " + process.Id + "，" + plan.JavaPath + "）");
            if (!wait) return 0;

            process.WaitForExit();
            Console.WriteLine("进程退出，退出码 " + process.ExitCode);
            return process.ExitCode;
        }

        /// <summary>检查这个版本能不能直接启动：classpath / natives / 资源对象 / Java，并打印命令行。</summary>
        private static int CmdLaunchPlan(string[] a)
        {
            if (a.Length == 0) { Usage(); return 2; }
            var versionId = a[0];
            var gameDir = Path.Combine(Environment.CurrentDirectory, ".minecraft");
            var print = false;
            var printClasspath = false;
            var writeArgs = (string)null;
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i] == "--dir" && i + 1 < a.Length) gameDir = a[++i];
                else if (a[i] == "--print") print = true;
                else if (a[i] == "--print-classpath") printClasspath = true;
                else if (a[i] == "--write-args" && i + 1 < a.Length) writeArgs = a[++i];
            }

            PclDl.Core.Launch.LaunchPlan plan;
            try
            {
                plan = PclDl.Core.Launch.LaunchPlanner.Build(gameDir, versionId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[error] " + ex.Message);
                if (Environment.GetEnvironmentVariable("PCLDL_DEBUG") == "1") Console.Error.WriteLine(ex.ToString());
                return 1;
            }

            Console.WriteLine("版本：" + plan.VersionId + (plan.VanillaId != plan.VersionId ? "（原版 " + plan.VanillaId + "）" : ""));
            Console.WriteLine("主类：" + (plan.MainClass ?? "(缺少 mainClass)"));
            Console.WriteLine("Java：" + (plan.JavaPath ?? "(未找到)") +
                              (plan.JavaMajor > 0 ? "（Java " + plan.JavaMajor + "，建议 " + plan.RequiredJavaMajor + "）" : ""));
            Console.WriteLine("classpath：" + plan.Classpath.Count + " 项，缺失 " + plan.MissingClasspath.Count);
            foreach (var path in plan.MissingClasspath.Take(5)) Console.WriteLine("  MISSING " + path);
            Console.WriteLine("natives：" + plan.NativesDir + "，缺失 " + plan.MissingNatives.Count);
            foreach (var name in plan.MissingNatives.Take(5)) Console.WriteLine("  MISSING " + name);
            Console.WriteLine("资源：" + (plan.AssetIndexId ?? "(无)") + "，对象 " + plan.AssetTotal +
                              "，缺失 " + plan.AssetMissing);
            foreach (var warning in plan.Warnings) Console.WriteLine("  [warn] " + warning);

            if (!string.IsNullOrEmpty(writeArgs))
            {
                // Java 9+ 的 @argfile：一行一个参数，含空格的加引号
                var lines = new System.Collections.Generic.List<string>();
                foreach (var argument in plan.Arguments)
                {
                    var text = argument;
                    if (text.IndexOf(' ') >= 0 || text.IndexOf('"') >= 0 || text.IndexOf('\\') >= 0)
                        text = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                    lines.Add(text);
                }
                File.WriteAllLines(writeArgs, lines);
                Console.WriteLine("参数文件：" + Path.GetFullPath(writeArgs) + "（" + lines.Count + " 行）");
                Console.WriteLine("用法：" + plan.JavaPath + " @" + Path.GetFullPath(writeArgs));
                return plan.Ok ? 0 : 1;
            }

            if (printClasspath)
            {
                Console.WriteLine(string.Join(";", plan.Classpath));
                return plan.Ok ? 0 : 1;
            }

            if (print)
            {
                Console.WriteLine();
                Console.WriteLine("命令行：");
                Console.WriteLine(plan.CommandLine);
            }

            Console.WriteLine(plan.Ok ? "结论：依赖齐全，可以直接启动" : "结论：依赖不完整，需要先 repair");
            return plan.Ok ? 0 : 1;
        }

        /// <summary>校验并修复：把缺失、尺寸不对、sha1 不符的文件重新下载一遍。</summary>
        private static int CmdRepair(string[] a)
        {
            if (a.Length == 0) { Usage(); return 2; }
            var versionId = a[0];
            var gameDir = Path.Combine(Environment.CurrentDirectory, ".minecraft");
            var dryRun = false;
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i] == "--dir" && i + 1 < a.Length) gameDir = a[++i];
                else if (a[i] == "--dry-run") dryRun = true;
            }

            var log = new ConsoleLog(true);
            var service = new PclDl.Core.Install.RepairService(log);
            PclDl.Core.Install.RepairResult result;
            try
            {
                result = service.RepairAsync(gameDir, versionId, dryRun, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[error] " + ex.Message);
                return 1;
            }

            Console.WriteLine("检查版本：" + versionId);
            Console.WriteLine("  文件总数：" + result.Total + "　完好：" + result.Ok + "　待修复：" + result.Broken);
            foreach (var line in result.Details) Console.WriteLine("  " + line);

            if (result.Broken == 0) { Console.WriteLine("无需修复。"); return 0; }
            if (dryRun) { Console.WriteLine("DryRun：未实际下载。"); return 1; }

            Console.WriteLine("修复完成：下载 " + result.Downloaded + "，失败 " + result.Failed);
            foreach (var err in result.Errors.Take(10)) Console.WriteLine("  " + err);
            Console.WriteLine("复查：仍有 " + result.Remaining + " 个文件不完整");
            return result.Success ? 0 : 1;
        }

        private static int CmdList(string[] a)
        {
            var gameDir = Path.Combine(Environment.CurrentDirectory, ".minecraft");
            for (int i = 0; i < a.Length; i++) if (a[i] == "--dir" && i + 1 < a.Length) gameDir = a[++i];

            var versionsDir = Path.Combine(Path.GetFullPath(gameDir), "versions");
            if (!Directory.Exists(versionsDir)) { Console.WriteLine("没有 versions 目录：" + versionsDir); return 1; }
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                var json = Path.Combine(dir, id + ".json");
                var jar = Path.Combine(dir, id + ".jar");
                Console.WriteLine((File.Exists(json) ? "json" : "----") + " " + (File.Exists(jar) ? "jar " : "----") + " " + id);
            }
            return 0;
        }
    }
}
