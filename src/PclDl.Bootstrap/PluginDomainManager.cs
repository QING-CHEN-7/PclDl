using System;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Policy;

namespace PclDl.Bootstrap
{
    /// <summary>
    /// 通过 APPDOMAIN_MANAGER_ASM / APPDOMAIN_MANAGER_TYPE 环境变量被 CLR 在 Main 之前加载。
    /// 只做三件事：装私有 AssemblyResolve、起后台线程加载插件、任何异常都吞掉。
    /// </summary>
    public sealed class PluginDomainManager : AppDomainManager
    {
        public override AppDomain CreateDomain(string friendlyName, Evidence securityInfo, AppDomainSetup appDomainInfo)
        {
            // 返回 null 表示沿用默认域，不创建新域
            return null;
        }

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            base.InitializeNewDomain(appDomainInfo);
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var pluginDir = Path.Combine(baseDir, "PCL2Plugins");
                var logPath = Path.Combine(pluginDir, "plugin.log");

                Log(logPath, "bootstrap: domain=" + AppDomain.CurrentDomain.FriendlyName + " base=" + baseDir);
                if (string.Equals(Environment.GetEnvironmentVariable("PCLDL_SAFE"), "1", StringComparison.Ordinal))
                {
                    Log(logPath, "bootstrap: SAFE mode, manager only");
                    return;
                }
                Diagnose(baseDir, pluginDir);
                HookFirstChance(pluginDir);

                AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
                {
                    try
                    {
                        var simpleName = new AssemblyName(e.Name).Name;
                        var candidate = Path.Combine(pluginDir, simpleName + ".dll");
                        if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                    }
                    catch { }
                    return null;
                };

                var pluginDll = Path.Combine(pluginDir, "PclDl.Plugin.dll");
                if (!File.Exists(pluginDll))
                {
                    Log(logPath, "bootstrap: plugin dll not found, skipped");
                    return;
                }

                var asm = Assembly.LoadFrom(pluginDll);
                var entry = asm.GetType("PclDl.Plugin.PluginEntry", false);
                if (entry == null)
                {
                    Log(logPath, "bootstrap: PluginEntry type not found");
                    return;
                }

                var start = entry.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
                if (start == null)
                {
                    Log(logPath, "bootstrap: PluginEntry.Start not found");
                    return;
                }

                start.Invoke(null, null);
                Log(logPath, "bootstrap: plugin start invoked");
            }
            catch (Exception ex)
            {
                try
                {
                    var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL2Plugins");
                    Directory.CreateDirectory(dir);
                    Log(Path.Combine(dir, "plugin.log"), "bootstrap: FAILED " + ex);
                }
                catch { }
            }
        }

        private static void HookFirstChance(string pluginDir)
        {
            try
            {
                AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
                {
                    try
                    {
                        var ex = e.Exception;
                        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + ex.ToString()
                                   + "\r\n--- Environment.StackTrace ---\r\n" + Environment.StackTrace;
                        File.AppendAllText(Path.Combine(pluginDir, "firstchance.log"), line + Environment.NewLine + Environment.NewLine);
                    }
                    catch { }
                };
            }
            catch { }
        }

        private static void Diagnose(string baseDir, string pluginDir)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("AppContext.BaseDirectory=" + AppContext.BaseDirectory);
                sb.AppendLine("AppDomain.BaseDirectory=" + AppDomain.CurrentDomain.BaseDirectory);
                sb.AppendLine("CurrentDirectory=" + Environment.CurrentDirectory);
                sb.AppendLine("EntryAssembly=" + (Assembly.GetEntryAssembly() == null ? "null" : Assembly.GetEntryAssembly().Location));
                sb.AppendLine("User=" + Environment.UserName + " 64bit=" + Environment.Is64BitProcess);
                sb.AppendLine("IsFullyTrusted=" + AppDomain.CurrentDomain.IsFullyTrusted);
                sb.AppendLine("PermissionSet=" + (AppDomain.CurrentDomain.PermissionSet == null ? "null" : AppDomain.CurrentDomain.PermissionSet.ToString()));
                try
                {
                    var perm = new System.Security.Permissions.FileIOPermission(
                        System.Security.Permissions.FileIOPermissionAccess.AllAccess,
                        Path.Combine(baseDir, "PCL"));
                    perm.Demand();
                    sb.AppendLine("FileIOPermission.Demand=OK");
                }
                catch (Exception ex) { sb.AppendLine("FileIOPermission.Demand=FAIL " + ex.GetType().Name + ": " + ex.Message); }
                try
                {
                    var d = Path.Combine(baseDir, "PCL");
                    Directory.CreateDirectory(d);
                    var f = Path.Combine(d, "probe.tmp");
                    File.WriteAllText(f, "x");
                    File.Delete(f);
                    sb.AppendLine("PCL-write=OK");
                }
                catch (Exception ex) { sb.AppendLine("PCL-write=FAIL " + ex.GetType().Name + ": " + ex.Message); }

                try
                {
                    var mc = Assembly.LoadFrom(Path.Combine(baseDir, "MeloongCore.dll"));
                    var dirType = mc.GetType("MeloongCore.DirectoryUtils");
                    var fileType = mc.GetType("MeloongCore.FileUtils");
                    var exists = dirType.GetMethod("Exists", new[] { typeof(string) });
                    var write = fileType.GetMethod("Write", new[] { typeof(string), typeof(string), typeof(System.Text.Encoding) });
                    var folder = Path.Combine(baseDir, "PCL") + "\\";
                    sb.AppendLine("MC-DirExists=" + exists.Invoke(null, new object[] { folder }));
                    var testFile = Path.Combine(folder, "probe2.tmp");
                    write.Invoke(null, new object[] { testFile, "x", null });
                    sb.AppendLine("MC-Write=OK");
                    File.Delete(testFile);
                }
                catch (Exception ex) { sb.AppendLine("MC-FAIL " + ex.GetType().Name + ": " + ex.Message + " | inner=" + (ex.InnerException == null ? "null" : ex.InnerException.GetType().Name + " " + ex.InnerException.Message)); }

                File.AppendAllText(Path.Combine(pluginDir, "diag.log"), sb.ToString());
            }
            catch { }
        }

        internal static void Log(string path, string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
