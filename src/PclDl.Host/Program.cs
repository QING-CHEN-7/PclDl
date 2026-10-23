using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using PclDl.Plugin;

namespace PclDl.Host
{
    /// <summary>
    /// 宿主式加载：不改 PCL 的 EXE、不做进程注入、不用 AppDomainManager。
    /// 本程序放在 PCL 目录中，把 PCL 程序集当作库加载并调用它的入口点，
    /// 因此在同一进程、同一 UI 线程、同一 AppDomain 中，路径与单例行为都不变。
    /// </summary>
    internal static class Program
    {
        private static string _pclPath;
        private static string _logPath;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _logPath = Path.Combine(baseDir, "PCL2Plugins", "host.log");
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));

                _pclPath = Args.Get(args, "--pcl");
                if (string.IsNullOrEmpty(_pclPath))
                    _pclPath = Path.Combine(baseDir, "Plain Craft Launcher 2.exe");
                _pclPath = Path.GetFullPath(_pclPath);

                if (!File.Exists(_pclPath))
                {
                    Log("host: PCL 不存在：" + _pclPath);
                    return 2;
                }

                Directory.SetCurrentDirectory(baseDir);
                Log("host: base=" + baseDir + " pcl=" + _pclPath);

                var asm = Assembly.LoadFrom(_pclPath);
                var entry = asm.EntryPoint;
                if (entry == null)
                {
                    Log("host: 找不到入口点");
                    return 3;
                }
                Log("host: entry=" + entry.DeclaringType.FullName + "." + entry.Name);

                // 关键：WPF 默认从入口程序集解析 Application 的 XAML 资源。
                // 入口程序集是 Host 而资源在 PCL 里，必须先把 ResourceAssembly 指过去，否则 PCL 初始化失败。
                try
                {
                    // WPF 在 Application 类型初始化时就把 ResourceAssembly 设成了入口程序集，
                    // 公开属性此时已不可改，只能改私有静态字段，让 App.xaml 从 PCL 程序集解析。
                    var field = typeof(Application).GetField("_resourceAssembly",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (field != null)
                    {
                        field.SetValue(null, asm);
                        Log("host: _resourceAssembly 已指向 " + asm.GetName().Name
                            + "，当前值=" + (Application.ResourceAssembly == null ? "null" : Application.ResourceAssembly.GetName().Name));
                    }
                    else Log("host: 未找到 _resourceAssembly 字段");
                }
                catch (Exception ex) { Log("host: 设置 ResourceAssembly 失败 " + ex.GetType().Name + " " + ex.Message); }

                // 先启动插件（后台等待主窗口出现后挂载）
                try { PluginEntry.Start(); }
                catch (Exception ex) { Log("host: PluginEntry.Start 失败 " + ex); }

                // 调用 PCL 的入口点（WPF Application.Run 会阻塞到退出）
                var parameters = entry.GetParameters().Length == 0
                    ? null
                    : new object[] { args };
                var result = entry.Invoke(null, parameters);
                var code = result is int ? (int)result : 0;
                Log("host: PCL 已退出，返回值 " + code);
                return code;
            }
            catch (Exception ex)
            {
                Log("host: 致命错误 " + ex);
                return 1;
            }
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    internal static class Args
    {
        public static string Get(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
