using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PclDl.Plugin.Host;
using PclDl.Plugin.Ui;

namespace PclDl.Plugin
{
    /// <summary>装配与生命周期：等主窗口就绪 -> 绑定锚点 -> 按页面挂载。全程 fail-open。</summary>
    public static class PluginEntry
    {
        private static readonly object Gate = new object();
        private static string _logPath;
        private static bool _mounted;
        private static FrameworkElement _shell;
        private static string _lastError;
        private static readonly System.Collections.Generic.List<Action> _exitHooks = new System.Collections.Generic.List<Action>();
        private static bool _exitRan;

        /// <summary>注册退出钩子（取消下载、清理 .part 等，v4 §6）。</summary>
        public static void RegisterExitHook(Action hook)
        {
            if (hook == null) return;
            lock (_exitHooks) _exitHooks.Add(hook);
        }

        /// <summary>PCL 关闭时：先取消所有任务，再删掉正在写的临时文件，最后写收尾日志。</summary>
        public static void RunExitHooks()
        {
            lock (_exitHooks)
            {
                if (_exitRan) return;
                _exitRan = true;
                foreach (var hook in _exitHooks)
                {
                    try { hook(); } catch (Exception ex) { LogOnce("exit hook: " + ex.Message); }
                }
            }
            try { PclDl.Core.Transport.FileFetcher.CleanupActiveParts(); } catch { }
            Log("plugin: 退出，未完成任务已取消");
        }

        /// <summary>兜底：插件里任何未捕获异常都只记日志，不带走 PCL（v4 §7）。</summary>
        private static void InstallCrashGuard()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    try { CrashLog("AppDomain", e.ExceptionObject as Exception); } catch { }
                };
            }
            catch { }
        }

        internal static void CrashLog(string source, Exception ex)
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL2Plugins", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + source + "] " +
                                          (ex == null ? "(null)" : ex.ToString()) + Environment.NewLine);
            }
            catch { }
        }

        public static int Bootstrap(string argument)
        {
            try { Start(); return 0; }
            catch (Exception ex) { try { Log("plugin: Bootstrap 失败 " + ex); } catch { } return 1; }
        }

        public static void Start()
        {
            lock (Gate)
            {
                if (_logPath != null) return;
                _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL2Plugins", "plugin.log");
            }

            Log("plugin: attached, base=" + AppDomain.CurrentDomain.BaseDirectory);
            InstallCrashGuard();

            var thread = new Thread(Pump) { IsBackground = true, Name = "PclDl.Plugin" };
            thread.Start();
        }

        private static void Pump()
        {
            Log("plugin: waiting for main window");

            Window window = null;
            for (int i = 0; i < 1800 && window == null; i++)
            {
                try
                {
                    var app = Application.Current;
                    if (app == null) Log("plugin: Application.Current 为空，CrashGuard 只保留 AppDomain 兜底");
                    if (app != null)
                    {
                        var candidate = app.Dispatcher.Invoke(new Func<Window>(() => app.MainWindow));
                        if (candidate != null &&
                            candidate.Dispatcher.Invoke(new Func<bool>(() => candidate.IsLoaded)))
                        {
                            window = candidate;
                        }
                    }
                }
                catch (Exception ex) { LogOnce("pump: " + ex.GetType().Name + " " + ex.Message); }
                if (window == null) Thread.Sleep(200);
            }

            if (window == null) { Log("plugin: 等待主窗口超时"); return; }

            try
            {
                window.Dispatcher.Invoke(new Action(() =>
                {
                    window.Closed += (s, e) => RunExitHooks();

                    // 只兜住我们自己的异常：打日志后标记 Handled，PCL 继续运行。
                    // 注意要用 Application.DispatcherUnhandledException——
                    // Dispatcher 上的同名事件不会收到 BeginInvoke 回调里的异常。
                    var app = Application.Current;
                    if (app != null)
                    {
                        app.DispatcherUnhandledException += (s, e) =>
                        {
                            try { CrashLog("dispatcher", e.Exception); } catch { }
                            e.Handled = true;
                        };
                        Log("plugin: CrashGuard 已安装（Application.DispatcherUnhandledException）");
                    }
                }));
            }
            catch (Exception ex) { LogOnce("plugin: 绑定退出/异常钩子失败 " + ex.Message); }

            PclBinder binder;
            try { binder = PclBinder.TryBind(window.GetType().Assembly); }
            catch (Exception ex) { Log("plugin: 绑定异常 " + ex); return; }

            Log("plugin: bind=" + (binder.IsBound ? "ok" : "incomplete")
                + (binder.Missing.Count > 0 ? " missing=" + string.Join(",", binder.Missing) : ""));
            Log("plugin: 指纹 " + Fingerprint(binder) + Environment.NewLine + binder.Describe());
            SelfCheck = binder.Describe();
            var forceE3 = Environment.GetEnvironmentVariable("PCLDL_TEST_E3") == "1";
            if (!binder.IsBound || forceE3)
            {
                // v4 §3/§7：只有 P1（或锚点被改坏）时走 E3——独立窗口，Owner 贴住 PCL 主窗口
                Log("plugin: 锚点" + (forceE3 ? "被强制" : "不足") + "，改用 E3 独立窗口挂载");
                MountStandaloneWindow(window, binder);
                return;
            }

            StartCatalogWarmup(window);

            // 调试用：程序化触发 PCL 的页面切换（等价于点「下载」），便于无焦点环境做验证
            var testPage = Environment.GetEnvironmentVariable("PCLDL_TEST_PAGE");
            if (!string.IsNullOrEmpty(testPage))
            {
                string pageName = null, subName = null;
                if (testPage.Equals("download", StringComparison.OrdinalIgnoreCase)) pageName = "Download";
                else if (testPage.Equals("instanceselect", StringComparison.OrdinalIgnoreCase)) pageName = "InstanceSelect";
                else if (testPage.Equals("mod", StringComparison.OrdinalIgnoreCase)) { pageName = "Download"; subName = "DownloadMod"; }
                else if (testPage.Equals("pack", StringComparison.OrdinalIgnoreCase)) { pageName = "Download"; subName = "DownloadPack"; }
                else if (testPage.Equals("resourcepack", StringComparison.OrdinalIgnoreCase)) { pageName = "Download"; subName = "DownloadResourcePack"; }
                else if (testPage.Equals("shader", StringComparison.OrdinalIgnoreCase)) { pageName = "Download"; subName = "DownloadShader"; }
                if (pageName != null)
                {
                    try
                    {
                        var capturedPage = pageName;
                        var capturedSub = subName;
                        window.Dispatcher.Invoke(new Action(() => NavigateTo(window, capturedPage, capturedSub)));
                        Log("plugin: 已触发 PageChange(" + pageName + (subName == null ? "" : " / " + subName) + ")");
                    }
                    catch (Exception ex) { Log("plugin: 触发页面切换失败 " + ex.Message); }
                }
            }
            var mode = Environment.GetEnvironmentVariable("PCLDL_AUTO_MOUNT") ?? "";
            if (mode.Length == 0) { Log("plugin: attached only (未设置 PCLDL_AUTO_MOUNT)"); return; }

            if (string.Equals(mode, "always", StringComparison.OrdinalIgnoreCase))
            {
                var currentPage = ReadPageName(window, binder);
                var currentSub = ReadPageSub(window, binder);
                if (currentPage == "Download" && IsCommunitySubPage(currentSub))
                {
                    Log("plugin: 启动时停在社区资源页（sub=" + currentSub + "），改为监听模式（不覆盖 PCL）");
                }
                else
                {
                    MountOnce(window, binder);
                    return;
                }
            }

            // 页面锚点不可用时无法做"进入下载页再接管"，退化为启动即挂载，保证功能可用
            if (binder.PageCurrent == null)
            {
                Log("plugin: PageCurrent 缺失，退化为启动即挂载");
                MountOnce(window, binder);
                return;
            }

            Log("plugin: 进入页面监听模式 mode=" + mode);
            // download 模式：进入下载页时接管，离开后由 PCL 自己换回
            var lastPage = "";
            var lastKey = "";
            var lastMountAt = DateTime.MinValue;
            Log("plugin: 初始页面=" + (ReadPageName(window, binder) ?? "(null)"));
            while (true)
            {
                try
                {
                    var page = ReadPageName(window, binder);
                    var sub = ReadPageSub(window, binder);

                    // 页面 + 次级页面组合成 key，只在变化时打日志，避免 250ms 刷屏
                    var key = (page ?? "") + "/" + sub;
                    if (key != lastKey)
                    {
                        if (!string.IsNullOrEmpty(page) && !(page == "Download" && sub == 0))
                            Log("plugin: 页面 -> " + page + (sub >= 0 ? " sub=" + sub : ""));
                        lastKey = key;
                    }

                    if (page == "Download" && IsCommunitySubPage(sub))
                    {
                        // 社区资源页：交还给 PCL，不接管、不重挂
                    }
                    else if (page == "Download")
                    {
                        var entering = lastPage != "Download";
                        // PCL 内部的页面刷新可能把我们的壳换掉，因此除了进入页面，
                        // 还要在"仍是下载页但壳不在位"时重新挂载（带冷却，避免抖动）
                        var needRemount = !entering &&
                                          (DateTime.Now - lastMountAt).TotalMilliseconds > 1500 &&
                                          !IsMounted(window, binder);
                        if (entering || needRemount)
                        {
                            if (entering) Thread.Sleep(500); // 等 PCL 自己的页面切换动画先完成
                            window.Dispatcher.Invoke(new Action(() => MountOnce(window, binder)));
                            lastMountAt = DateTime.Now;
                        }
                    }
                    lastPage = page;
                }
                catch (Exception ex) { LogOnce("pump loop: " + ex.Message); }
                Thread.Sleep(250);
            }
        }

        private static Window _standalone;

        /// <summary>E3：不碰 PCL 的页面结构，另开一个属于 PCL 主窗口的独立窗口承载下载面板。</summary>
        private static void MountStandaloneWindow(Window owner, PclBinder binder)
        {
            try
            {
                owner.Dispatcher.Invoke(new Action(() =>
                {
                    if (_standalone != null) { _standalone.Activate(); return; }

                    Func<string> gameDirProvider = () => ResolveGameDir(owner);
                    PclUi ui = null;
                    try { ui = new PclUi(owner.GetType().Assembly); } catch { ui = new PclUi(null); }

                    UserControl panel;
                    try { panel = new DownloadPanel(gameDirProvider, ui); }
                    catch (Exception ex)
                    {
                        LogOnce("e3: 下载面板构造失败，降级占位 " + ex.Message);
                        panel = new PlaceholderPanel(gameDirProvider());
                    }

                    var window = new Window
                    {
                        Title = "PCL2 下载插件（E3 独立窗口）",
                        Width = 980,
                        Height = 700,
                        MinWidth = 720,
                        MinHeight = 480,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner
                    };
                    Theme.Apply(window, Control.BackgroundProperty, "ColorBrush8", System.Windows.Media.Brushes.White);
                    window.Content = panel;

                    try { if (owner.IsLoaded) window.Owner = owner; } catch { }
                    window.Closed += (s, e) => { RunExitHooks(); _standalone = null; };
                    _standalone = window;
                    window.Show();
                    Log("plugin: 已使用独立窗口挂载（E3）");
                }));
            }
            catch (Exception ex)
            {
                Log("plugin: E3 独立窗口创建失败 " + ex);
            }
        }

        private static string ReadPageName(FrameworkElement window, PclBinder binder)
        {
            if (binder.PageCurrent == null) return "";
            var stack = binder.PageCurrent.Get(window);
            if (stack == null) return "";

            // PageStackData.Page 是字段而不是属性（VB 结构体），两种都试
            var type = stack.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object value = null;
            var field = type.GetField("Page", flags);
            if (field != null) value = field.GetValue(stack);
            else
            {
                var prop = type.GetProperty("Page", flags);
                if (prop != null) value = prop.GetValue(stack, null);
            }
            return value == null ? "" : value.ToString();
        }

        /// <summary>当前次级页面序号；读不到返回 -1（视为未知）。</summary>
        private static int ReadPageSub(FrameworkElement window, PclBinder binder)
        {
            try
            {
                if (binder.PageCurrentSub == null) return -1;
                var value = binder.PageCurrentSub.Get(window);
                if (value == null) return -1;
                return Convert.ToInt32(value);
            }
            catch { return -1; }
        }

        /// <summary>
        /// 社区资源页（Mod/整合包/数据包/资源包/光影）在 PCL 里是 Download 的次级页面，
        /// 用户明确要求「社区资源下载不要动」，所以这些子页面一律不挂载。
        /// </summary>
        private static bool IsCommunitySubPage(int sub)
        {
            return sub >= 2 && sub <= 6;
        }

        private static bool IsMounted(Window window, PclBinder binder)
        {
            try
            {
                var host = binder.PanMainRight == null ? null : binder.PanMainRight.Get(window) as Border;
                return host != null && _shell != null && ReferenceEquals(host.Child, _shell);
            }
            catch { return false; }
        }

        private static void MountOnce(Window window, PclBinder binder)
        {
            try
            {
                var host = binder.PanMainRight == null ? null : binder.PanMainRight.Get(window) as Border;
                if (host == null) { Log("plugin: 找不到 PanMainRight 实例"); return; }

                if (_shell != null && ReferenceEquals(host.Child, _shell))
                {
                    Log("plugin: 已经是插件页面，跳过");
                    return;
                }

                Func<string> gameDirProvider = () => ResolveGameDir(window);
                var ui = new PclUi(window.GetType().Assembly);

                // v4 §7：页面壳构造失败要降级到一个能看能用的简单面板，而不是留空白
                UserControl panel;
                if (Environment.GetEnvironmentVariable("PCLDL_TEST_PLACEHOLDER") == "1")
                {
                    panel = new PlaceholderPanel(gameDirProvider());
                    Log("plugin: 测试钩子强制使用占位面板");
                }
                else try
                {
                    panel = new DownloadPanel(gameDirProvider, ui);
                }
                catch (Exception ex)
                {
                    Log("plugin: 下载面板构造失败，降级到占位面板 " + ex.Message);
                    panel = new PlaceholderPanel(gameDirProvider());
                }

                var shell = binder.CreateShell(panel);
                host.Child = shell;
                _shell = shell;
                _mounted = true;
                Log(panel is DownloadPanel ? "plugin: 已挂载下载面板到 PanMainRight" : "plugin: 已挂载降级面板到 PanMainRight");
            }
            catch (Exception ex)
            {
                Log("plugin: 挂载失败 " + ex);
            }
        }

        /// <summary>优先使用 PCL 自己选中的 Minecraft 文件夹；取不到时退回 PCL 目录下的 .minecraft。</summary>
        /// <summary>插件一挂上就预热版本清单，等用户点开下载页时直接命中缓存（与 PCL 的预加载思路一致）。</summary>
        private static void StartCatalogWarmup(Window window)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var gameDir = ResolveGameDir(window);
                    var cache = Path.Combine(gameDir, "PCL2Plugins", "cache", "manifest.json");
                    using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                    {
                        var catalog = new PclDl.Core.Catalog.VersionCatalog(
                            http, PclDl.Core.Transport.MirrorMap.CreateBmclapi(), true,
                            new PclDl.Core.Logging.NullLog(), cache);
                        catalog.LoadAsync(true).GetAwaiter().GetResult();
                        Log("plugin: 版本清单预热完成，" + catalog.Versions.Count + " 项，耗时 " +
                            catalog.LastFetchMilliseconds + " ms");
                    }
                }
                catch (Exception ex) { Log("plugin: 版本清单预热失败 " + ex.Message); }
            })
            { IsBackground = true, Name = "PclDl.CatalogWarmup" };
            thread.Start();
        }

        /// <summary>反射调用 PCL 的 FormMain.PageChange(PageType.Download)，用于自动化验证。</summary>
        private static void NavigateTo(Window window, string pageName, string subName = null)
        {
            var formMainType = window.GetType();
            var stackType = formMainType.GetNestedType("PageStackData");
            var pageType = formMainType.GetNestedType("PageType");
            if (stackType == null || pageType == null) throw new InvalidOperationException("找不到 PageStackData/PageType");

            var stack = Activator.CreateInstance(stackType);
            stackType.GetField("Page").SetValue(stack, Enum.Parse(pageType, pageName));

            var method = formMainType.GetMethods()
                .FirstOrDefault(m => m.Name == "PageChange" && m.GetParameters().Length == 2);
            if (method == null) throw new InvalidOperationException("找不到 PageChange(PageStackData, PageSubType)");

            object sub = Type.Missing;
            if (!string.IsNullOrEmpty(subName))
            {
                var subType = formMainType.GetNestedType("PageSubType");
                if (subType != null) sub = Enum.Parse(subType, subName);
            }
            method.Invoke(window, new object[] { stack, sub });
        }

        /// <summary>供面板展示的自检信息。</summary>
        public static string SelfCheck = "";

        private static string Fingerprint(PclBinder binder)
        {
            try
            {
                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plain Craft Launcher 2.exe");
                var hash = File.Exists(exe) ? PclDl.Core.Transport.Hashing.Sha1File(exe) : "n/a";
                return "exe=" + Path.GetFileName(exe) + " sha1=" + hash.Substring(0, Math.Min(12, hash.Length));
            }
            catch { return "exe=unknown"; }
        }

        private static string ResolveGameDir(Window window)
        {
            try
            {
                var type = window.GetType().Assembly.GetType("PCL.ModMinecraft", false);
                if (type != null)
                {
                    var prop = type.GetProperty("McFolderSelected",
                        BindingFlags.Public | BindingFlags.Static);
                    if (prop != null)
                    {
                        var value = prop.GetValue(null, null) as string;
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                }
            }
            catch (Exception ex) { Log("plugin: 读取 McFolderSelected 失败 " + ex.Message); }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft");
        }

        private static void LogOnce(string message)
        {
            if (_lastError == message) return;
            _lastError = message;
            Log(message);
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
