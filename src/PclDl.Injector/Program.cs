using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PclDl.Injector
{
    /// <summary>
    /// 注入器：先正常启动 PCL（让它自己初始化 CLR 与配置），
    /// 等 clr.dll 出现后，用远程 LoadLibrary 注入 C++/CLI 桥接 DLL。
    /// 注入失败不影响 PCL 使用；不修改 PCL 的任何文件。
    /// </summary>
    internal static class Program
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint MEM_RELEASE = 0x8000;

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, uint allocationType, uint protect);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, uint freeType);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, IntPtr stackSize,
            IntPtr startAddress, IntPtr parameter, uint creationFlags, out IntPtr threadId);

        [DllImport("kernel32", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr thread, out IntPtr exitCode);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string applicationName, string commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var selfDir = AppDomain.CurrentDomain.BaseDirectory;
            var pclPath = Args.Get(args, "--pcl");
            var bridge = Args.Get(args, "--dll");
            var autoMount = Args.Has(args, "--auto-mount");

            if (string.IsNullOrEmpty(pclPath)) pclPath = Path.Combine(selfDir, "Plain Craft Launcher 2.exe");
            if (string.IsNullOrEmpty(bridge)) bridge = Path.Combine(selfDir, "PclDl.Bridge.dll");
            pclPath = Path.GetFullPath(pclPath);
            bridge = Path.GetFullPath(bridge);

            if (!File.Exists(pclPath)) { Console.Error.WriteLine("找不到 PCL：" + pclPath); return 2; }

            var pclDir = Path.GetDirectoryName(pclPath);
            var pluginDir = Path.Combine(pclDir, "PCL2Plugins");
            Directory.CreateDirectory(pluginDir);
            Console.WriteLine("插件目录：" + pluginDir);
            RemoveLegacyShadowCopies(pclDir);
            InstallPluginFiles(selfDir, pluginDir);

            if (Args.Has(args, "--mount-always")) Environment.SetEnvironmentVariable("PCLDL_AUTO_MOUNT", "always");
            else if (autoMount) Environment.SetEnvironmentVariable("PCLDL_AUTO_MOUNT", "1");

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;
            if (!CreateProcess(pclPath, "\"" + pclPath + "\"", IntPtr.Zero, IntPtr.Zero, false,
                    0, IntPtr.Zero, pclDir, ref si, out pi))
            {
                Console.Error.WriteLine("CreateProcess 失败：" + Marshal.GetLastWin32Error());
                return 1;
            }

            Console.WriteLine("已启动 PCL，PID=" + pi.dwProcessId);
            CloseHandle(pi.hThread);

            var injected = false;
            for (int attempt = 1; attempt <= 40 && !injected; attempt++)
            {
                Thread.Sleep(500);
                try
                {
                    injected = RemoteLoadLibrary(pi.hProcess, bridge);
                }
                catch (Exception ex)
                {
                    if (attempt % 8 == 0) Console.WriteLine("注入重试 " + attempt + "：" + ex.Message);
                }
            }

            CloseHandle(pi.hProcess);
            Console.WriteLine(injected ? "注入成功：" + bridge : "注入失败（PCL 仍可正常使用）");
            return injected ? 0 : 3;
        }

        /// <summary>旧版本插件曾把依赖放在 PCL 根目录，会遮蔽 LoadFrom 的解析，必须清掉。</summary>
        private static void RemoveLegacyShadowCopies(string pclDir)
        {
            foreach (var name in new[] { "PclDl.Core.dll", "PclDl.Plugin.dll", "Newtonsoft.Json.dll", "PclDl.Bootstrap.dll" })
            {
                var stale = Path.Combine(pclDir, name);
                if (!File.Exists(stale)) continue;
                try
                {
                    File.Delete(stale);
                    Console.WriteLine("[clean] 删除旧版残留：" + stale);
                }
                catch (Exception ex) { Console.WriteLine("[warn] 旧版残留无法删除：" + stale + "（" + ex.Message + "）"); }
            }
        }

        private static void InstallPluginFiles(string selfDir, string pluginDir)
        {
            foreach (var name in new[] { "PclDl.Plugin.dll", "PclDl.Core.dll", "Newtonsoft.Json.dll" })
            {
                var src = Path.Combine(selfDir, name);
                if (!File.Exists(src)) { Console.WriteLine("[warn] 缺少：" + src); continue; }
                var dst = Path.Combine(pluginDir, name);
                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[skip] 源与目标相同：" + name);
                    continue;
                }
                var copied = false;
                for (int attempt = 1; attempt <= 6 && !copied; attempt++)
                {
                    try { File.Copy(src, dst, true); copied = true; }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[warn] 复制 " + name + " 第 " + attempt + " 次失败：" + ex.Message);
                        System.Threading.Thread.Sleep(500);
                    }
                }
                if (!copied) Console.WriteLine("[error] 无法更新 " + name + "，插件可能运行旧版本");
            }
        }

        private static bool RemoteLoadLibrary(IntPtr process, string dllPath)
        {
            var bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            var remote = VirtualAllocEx(process, IntPtr.Zero, (IntPtr)bytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero) throw new InvalidOperationException("VirtualAllocEx：" + Marshal.GetLastWin32Error());

            try
            {
                IntPtr written;
                if (!WriteProcessMemory(process, remote, bytes, (IntPtr)bytes.Length, out written))
                    throw new InvalidOperationException("WriteProcessMemory：" + Marshal.GetLastWin32Error());

                var loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                if (loadLibrary == IntPtr.Zero) throw new InvalidOperationException("找不到 LoadLibraryW");

                IntPtr threadId;
                var thread = CreateRemoteThread(process, IntPtr.Zero, IntPtr.Zero, loadLibrary, remote, 0, out threadId);
                if (thread == IntPtr.Zero) throw new InvalidOperationException("CreateRemoteThread：" + Marshal.GetLastWin32Error());

                try
                {
                    if (WaitForSingleObject(thread, 15000) != 0) return false;
                    IntPtr exitCode;
                    GetExitCodeThread(thread, out exitCode);
                    return exitCode != IntPtr.Zero;
                }
                finally { CloseHandle(thread); }
            }
            finally { VirtualFreeEx(process, remote, IntPtr.Zero, MEM_RELEASE); }
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
