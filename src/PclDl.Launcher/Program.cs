using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PclDl.Launcher
{
    /// <summary>
    /// 注入式启动器：定位 PCL、安装插件文件、设置 APPDOMAIN_MANAGER_* 环境变量、拉起 PCL。
    /// 只写 pluginDir 与（必要时）复制 Bootstrap.dll 到 PCL 目录；不修改 PCL 的任何可执行内容。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var pclPath = Args.Get(args, "--pcl");
            var wait = Args.Has(args, "--wait");
            var autoMount = Args.Has(args, "--auto-mount");

            if (string.IsNullOrEmpty(pclPath))
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plain Craft Launcher 2.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Plain Craft Launcher 2.exe")
                })
                {
                    var full = Path.GetFullPath(candidate);
                    if (File.Exists(full)) { pclPath = full; break; }
                }
            }

            if (string.IsNullOrEmpty(pclPath) || !File.Exists(pclPath))
            {
                Console.Error.WriteLine("找不到 PCL 可执行文件。用法：PclDl.Launcher --pcl <PCL.exe 路径> [--auto-mount] [--wait]");
                return 2;
            }

            var pclDir = Path.GetDirectoryName(Path.GetFullPath(pclPath));
            var pluginDir = Path.Combine(pclDir, "PCL2Plugins");
            Directory.CreateDirectory(pluginDir);

            // 安装插件文件
            var selfDir = AppDomain.CurrentDomain.BaseDirectory;
            var copied = 0;
            foreach (var name in new[] { "PclDl.Bootstrap.dll", "PclDl.Core.dll", "PclDl.Plugin.dll", "Newtonsoft.Json.dll" })
            {
                var src = Path.Combine(selfDir, name);
                if (!File.Exists(src)) { Console.WriteLine("[warn] 缺少插件文件：" + src); continue; }

                var dst = name == "PclDl.Bootstrap.dll" ? Path.Combine(pclDir, name) : Path.Combine(pluginDir, name);
                if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)
                {
                    File.Copy(src, dst, true);
                    copied++;
                }
            }
            Console.WriteLine("插件文件已就绪（复制 " + copied + " 个）：" + pluginDir);

            var bootstrap = Path.Combine(pclDir, "PclDl.Bootstrap.dll");
            if (!File.Exists(bootstrap))
            {
                Console.Error.WriteLine("缺少 PclDl.Bootstrap.dll，无法注入");
                return 1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(pclPath),
                WorkingDirectory = pclDir,
                UseShellExecute = false
            };
            psi.EnvironmentVariables["APPDOMAIN_MANAGER_ASM"] = "PclDl.Bootstrap, Version=1.0.0.0, Culture=neutral, PublicKeyToken=d6c8cb76436a00d2";
            psi.EnvironmentVariables["APPDOMAIN_MANAGER_TYPE"] = "PclDl.Bootstrap.PluginDomainManager";
            if (autoMount) psi.EnvironmentVariables["PCLDL_AUTO_MOUNT"] = "1";

            Console.WriteLine("启动：" + psi.FileName);
            Console.WriteLine("  APPDOMAIN_MANAGER_ASM=" + psi.EnvironmentVariables["APPDOMAIN_MANAGER_ASM"]);
            Console.WriteLine("  PCLDL_AUTO_MOUNT=" + (autoMount ? "1" : "0"));

            var process = Process.Start(psi);
            if (process == null) { Console.Error.WriteLine("进程创建失败"); return 1; }

            Console.WriteLine("已启动 PID=" + process.Id);
            if (wait) { process.WaitForExit(); Console.WriteLine("PCL 已退出，退出码 " + process.ExitCode); }
            return 0;
        }
    }

    internal static class Args
    {
        public static string Get(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1].Trim('"');
            return null;
        }

        public static bool Has(string[] args, string name)
        {
            foreach (var a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
