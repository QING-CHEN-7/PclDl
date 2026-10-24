using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PclDl.Core.Catalog;
using PclDl.Core.Install;
using PclDl.Core.Logging;
using PclDl.Core.Transport;

namespace PclDl.Plugin.Ui
{
    /// <summary>下载页：布局与控件尽量使用 PCL 原生样式（MyCard / MyButton / 主题画刷）。</summary>
    public sealed class DownloadPanel : UserControl
    {
        private readonly Func<string> _gameDirProvider;
        private readonly PclUi _ui;

        private readonly TextBlock _hint;
        private readonly TextBlock _status;
        private readonly StackPanel _listPanel;
        private readonly TextBox _versionBox;
        private readonly ProgressBar _progress;
        private readonly FrameworkElement _installButton;
        private readonly FrameworkElement _cancelButton;

        private VersionCatalog _catalog;
        private bool _loading;
        private bool _autoStarted;
        private HashSet<string> _installed;
        private int _releaseShown = 40;
        private int _snapshotShown = 20;
        private int _ancientShown = 30;
        private TextBox _searchBox;
        private string _query = "";
        private const int PageStep = 60;
        private long _lastRenderMs;
        private int _lastRowCount;
        private TextBlock _detail;
        private TextBox _packBox;
        private TextBox _pathBox;
        private TextBlock _packStatus;
        private PclDl.Core.Models.ManifestEntry _selected;
        private CancellationTokenSource _installCts;
        private readonly List<InstallTask> _tasks = new List<InstallTask>();
        private StackPanel _taskPanel;
        private bool _pumping;
        private int _taskSeq;

        /// <summary>任务队列里的一条安装任务（v4 §5 的任务队列）。</summary>
        private sealed class InstallTask
        {
            public int Id;
            public string VersionId;
            public string LoaderLabel;
            public string Loader;
            public string Status = "排队";
            public bool Running;
            public string Message;
            public DateTime Created = DateTime.Now;
        }

        public DownloadPanel(Func<string> gameDirProvider, PclUi ui)
        {
            _gameDirProvider = gameDirProvider;
            _ui = ui ?? new PclUi(null);
            Background = Brushes.Transparent;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Theme.Apply(scroll, Control.BackgroundProperty, "ColorBrush8", Brushes.Transparent);

            var page = new StackPanel { Margin = new Thickness(25, 10, 25, 40) };
            scroll.Content = page;

            // —— 卡片 1：安装 ——
            var installRow = new WrapPanel { Orientation = Orientation.Horizontal };
            installRow.Children.Add(_ui.Label("版本 ID", 13));
            _versionBox = (TextBox)_ui.TextBox("", 160);
            _versionBox.Margin = new Thickness(8, 0, 16, 8);
            installRow.Children.Add(_versionBox);

            installRow.Children.Add(_ui.Label("加载器", 13));
            var loaderBox = new ComboBox { Width = 130, Height = 28, Margin = new Thickness(8, 0, 16, 8), VerticalContentAlignment = VerticalAlignment.Center };
            loaderBox.Items.Add("无加载器");
            loaderBox.Items.Add("Fabric");
            loaderBox.Items.Add("Forge");
            loaderBox.Items.Add("NeoForge");
            loaderBox.Items.Add("OptiFine");
            loaderBox.SelectedIndex = 0;
            installRow.Children.Add(loaderBox);
            _loaderBox = loaderBox;

            _installButton = _ui.Button("安装", true, () => StartInstall(), 30, new Thickness(0, 0, 10, 8));
            _installButton.IsEnabled = false;
            installRow.Children.Add(_installButton);

            _cancelButton = _ui.Button("取消", false, () =>
            {
                if (_installCts != null) _installCts.Cancel();
            }, 30, new Thickness(0, 0, 0, 8));
            _cancelButton.IsEnabled = false;
            installRow.Children.Add(_cancelButton);

            var repairButton = _ui.Button("校验修复", false, () => StartRepair(), 30, new Thickness(0, 0, 0, 8));
            repairButton.ToolTip = "检查已填版本的所有文件（sha1），把缺失或损坏的重新下回来";
            installRow.Children.Add(repairButton);

            var installBody = new StackPanel();
            installBody.Children.Add(installRow);

            // v4 §5 的"安装路径"：默认跟随 PCL 当前文件夹，可改成任意目录
            var pathRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            pathRow.Children.Add(_ui.Label("安装路径", 13));
            _pathBox = (TextBox)_ui.TextBox(SafeGameDir(), 420);
            _pathBox.Margin = new Thickness(8, 0, 8, 0);
            _pathBox.ToolTip = "留空则使用 PCL 当前选择的 Minecraft 文件夹";
            _pathBox.TextChanged += (s, e) =>
            {
                if (_hint != null) _hint.Text = "目标目录：" + TargetGameDir();
            };
            pathRow.Children.Add(_pathBox);
            pathRow.Children.Add(_ui.Button("浏览…", false, () =>
            {
                var picked = PickFolder("选择安装目标文件夹");
                if (!string.IsNullOrEmpty(picked)) _pathBox.Text = picked;
            }, 28, new Thickness(0, 0, 8, 0)));
            pathRow.Children.Add(_ui.Button("恢复默认", false, () => { _pathBox.Text = SafeGameDir(); }, 28, new Thickness(0, 0, 0, 0)));
            installBody.Children.Add(pathRow);

            _hint = _ui.Hint("目标目录：" + SafeGameDir());
            _hint.Margin = new Thickness(0, 2, 0, 8);
            installBody.Children.Add(_hint);

            _detail = _ui.Hint("在上方列表中选择一个版本，或直接填写版本 ID");
            _detail.Margin = new Thickness(0, 0, 0, 8);
            installBody.Children.Add(_detail);

            _progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = 0, Margin = new Thickness(0, 0, 0, 4) };
            installBody.Children.Add(_progress);

            _status = _ui.Hint("正在读取版本清单…");
            _status.Margin = new Thickness(0, 6, 0, 0);
            installBody.Children.Add(_status);

            page.Children.Add(_ui.Card("安装 Minecraft", installBody));

            // —— 卡片 2：版本列表 ——
            var listBody = new StackPanel();
            var listBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            listBar.Children.Add(_ui.Button("刷新版本清单", false, () => LoadCatalog(true), 28, new Thickness(0, 0, 12, 8)));
            _searchBox = (TextBox)_ui.TextBox("", 200);
            _searchBox.Margin = new Thickness(0, 0, 8, 8);
            _searchBox.ToolTip = "按版本号搜索，例如 1.20 / 1.7.10 / 23w";
            _searchBox.TextChanged += (s, e) =>
            {
                var text = (_searchBox.Text ?? "").Trim();
                if (text == _query) return;
                _query = text;
                // 搜索时把已显示页数放大，命中的版本一次看全
                _releaseShown = 400;
                _snapshotShown = 400;
                _ancientShown = 400;
                BuildVersionList();
            };
            listBar.Children.Add(_searchBox);
            listBar.Children.Add(_ui.Hint("点击列表项可填入版本 ID；支持正式版、快照与远古版本"));
            listBody.Children.Add(listBar);

            _listPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            var listScroll = new ScrollViewer
            {
                Height = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _listPanel
            };
            Theme.Apply(listScroll, Control.BackgroundProperty, "ColorBrush8", Brushes.Transparent);
            listBody.Children.Add(listScroll);

            page.Children.Add(_ui.Card("版本列表", listBody));

            // —— 卡片 2b：任务队列 ——
            var taskBody = new StackPanel();
            var taskBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            taskBar.Children.Add(_ui.Hint("安装会排队依次执行；失败的可重试"));
            taskBar.Children.Add(_ui.Button("清理已完成", false, () =>
            {
                _tasks.RemoveAll(x => !x.Running && (x.Status == "已完成" || x.Status == "已取消" || x.Status == "失败"));
                RenderTasks();
            }, 28, new Thickness(12, 0, 0, 0)));
            taskBody.Children.Add(taskBar);

            _taskPanel = new StackPanel();
            taskBody.Children.Add(_taskPanel);
            page.Children.Add(_ui.Card("任务队列", taskBody));

            // —— 卡片 3：导入本地整合包文件（不是社区资源浏览器）——
            var packBody = new StackPanel();
            var packRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _packBox = (TextBox)_ui.TextBox("", 360);
            _packBox.Margin = new Thickness(0, 0, 8, 8);
            packRow.Children.Add(_packBox);
            packRow.Children.Add(_ui.Button("浏览…", false, () =>
            {
                try
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件 (*.*)|*.*"
                    };
                    if (dialog.ShowDialog() == true) _packBox.Text = dialog.FileName;
                }
                catch (Exception ex) { UpdatePackStatus("选择文件失败：" + ex.Message); }
            }, 28, new Thickness(0, 0, 8, 8)));
            packRow.Children.Add(_ui.Button("导入并安装", true, () => StartModpack(_packBox.Text), 30, new Thickness(0, 0, 0, 8)));
            // 明确语义：这里只处理本地文件，不做社区资源浏览/下载

            packBody.Children.Add(packRow);

            _packStatus = _ui.Hint("只处理你已下载到本地的 .mrpack / CurseForge .zip 文件（PCL 的社区资源下载不受影响）；CurseForge 包需在 PCL2Plugins\\curseforge.key 中填写 API Key");
            _packStatus.Margin = new Thickness(0, 0, 0, 4);
            packBody.Children.Add(_packStatus);

            page.Children.Add(_ui.Card("导入本地整合包文件", packBody));

            // —— 卡片 4：插件自检 ——
            var checkBody = new StackPanel();
            var checkText = _ui.Hint(PluginEntry.SelfCheck ?? "(尚未完成绑定)");
            checkText.Margin = new Thickness(0, 0, 0, 4);
            checkBody.Children.Add(checkText);
            var logHint = _ui.Hint("日志：" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL2Plugins", "plugin.log"));
            checkBody.Children.Add(logHint);
            page.Children.Add(_ui.Card("插件自检", checkBody));

            Content = scroll;

            Loaded += (s, e) =>
            {
                LoadCatalog(false);

                // 测试钩子：验证崩溃兜底与退出清理
                if (Environment.GetEnvironmentVariable("PCLDL_TEST_CRASH") == "1")
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        PclUi.Diag("crash-test: 即将抛出未捕获异常");
                        throw new InvalidOperationException("PCLDL 测试异常（应当被 CrashGuard 兜住）");
                    }));
                }
                if (Environment.GetEnvironmentVariable("PCLDL_TEST_EXIT") == "1")
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        PclUi.Diag("exit-test: 手动触发退出钩子");
                        PluginEntry.RunExitHooks();
                    }));
                }
            };

            // PCL 关闭时取消在跑的任务（v4 §6）
            PluginEntry.RegisterExitHook(() =>
            {
                try { if (_installCts != null) _installCts.Cancel(); } catch { }
                PclUi.Diag("exit: 已请求取消任务");
            });
        }

        private ComboBox _loaderBox;

        /// <summary>
        /// 复用 PCL 自己的文件夹选择器（PCL.Dialogs.SelectFolder），
        /// 免得多引一个 WinForms 依赖，视觉也与原版一致。
        /// </summary>
        private string PickFolder(string title)
        {
            try
            {
                var pcl = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Plain Craft Launcher 2");
                if (pcl == null) { PclUi.Diag("path: 找不到 PCL 程序集，无法选择文件夹"); return null; }

                var dialogs = pcl.GetType("PCL.Dialogs", false);
                var method = dialogs == null ? null : dialogs.GetMethod("SelectFolder",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method == null) { PclUi.Diag("path: 找不到 Dialogs.SelectFolder"); return null; }

                var result = method.Invoke(null, new object[] { title, false, null }) as System.Collections.IEnumerable;
                if (result == null) return null;
                foreach (var item in result) { if (item != null) return item.ToString(); }
            }
            catch (Exception ex)
            {
                PclUi.Diag("path: 选择文件夹失败 " + ex.Message);
                UpdateStatus("选择目录失败：" + ex.Message);
            }
            return null;
        }

        /// <summary>实际使用的目标目录：安装路径框里有值就用它，否则跟随 PCL。</summary>
        private string TargetGameDir()
        {
            try
            {
                var text = (_pathBox == null ? null : _pathBox.Text) ?? "";
                text = text.Trim();
                if (text.Length == 0) return SafeGameDir();
                return PclDl.Core.Layout.DataLayout.NormalizeDir(text);
            }
            catch { return SafeGameDir(); }
        }

        private string SafeGameDir()
        {
            try { return _gameDirProvider(); } catch { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft"); }
        }

        private static string ExtractId(string line)
        {
            var index = line.LastIndexOf("] ", StringComparison.Ordinal);
            return index >= 0 ? line.Substring(index + 2) : line;
        }

        private async void LoadCatalog(bool force)
        {
            if (_loading) return;
            _loading = true;
            UpdateStatus("正在读取版本清单…");

            try
            {
                var testLoader = Environment.GetEnvironmentVariable("PCLDL_TEST_LOADER");
                if (!string.IsNullOrEmpty(testLoader) && testLoader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
                    _loaderBox.SelectedItem = "Fabric";

                var mirror = MirrorMap.CreateBmclapi();
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var cache = Path.Combine(SafeGameDir(), "PCL2Plugins", "cache", "manifest.json");
                    _catalog = new VersionCatalog(http, mirror, true, new NullLog(), cache);
                    await _catalog.LoadAsync(force).ConfigureAwait(true);

                    var usedCache = !force && _catalog.LastFetchMilliseconds == 0;
                    BuildVersionList();
                    _installButton.IsEnabled = true;
                    UpdateStatus("版本清单已加载（" + _catalog.Versions.Count + " 项"
                                 + (usedCache ? "，缓存" : "，" + _catalog.LastFetchMilliseconds + " ms")
                                 + "，首屏 " + _lastRenderMs + " ms / " + _lastRowCount + " 行）");

                    // 命中缓存时后台静默刷新，用户不用等
                    if (usedCache) Dispatcher.BeginInvoke(new Action(() => { if (!_loading) LoadCatalog(true); }));

                    var testSearch = Environment.GetEnvironmentVariable("PCLDL_TEST_SEARCH");
                    if (!string.IsNullOrEmpty(testSearch) && _searchBox != null)
                    {
                        _searchBox.Text = testSearch;
                        PclUi.Diag("search: 输入 " + testSearch + " 后 " + _lastRowCount + " 行");
                    }

                    var testSelect = Environment.GetEnvironmentVariable("PCLDL_TEST_SELECT");
                    if (!string.IsNullOrEmpty(testSelect))
                    {
                        var hit = _catalog.Versions.FirstOrDefault(v => string.Equals(v.Id, testSelect, StringComparison.OrdinalIgnoreCase));
                        if (hit != null) SelectVersion(hit);
                    }

                    var testPack = Environment.GetEnvironmentVariable("PCLDL_TEST_MODPACK");
                    if (!string.IsNullOrEmpty(testPack) && !_autoStarted)
                    {
                        _autoStarted = true;
                        _packBox.Text = testPack;
                        StartModpack(testPack);
                    }

                    if (Environment.GetEnvironmentVariable("PCLDL_TEST_REFRESH") == "1")
                    {
                        PclUi.Diag("refresh: 触发测试刷新");
                        RequestPclVersionRefresh();
                    }

                    var testPath = Environment.GetEnvironmentVariable("PCLDL_TEST_PATH");
                    if (!string.IsNullOrEmpty(testPath) && _pathBox != null)
                    {
                        _pathBox.Text = testPath;
                        PclUi.Diag("path: 测试钩子设置安装路径 " + testPath);
                    }

                    var testRepair = Environment.GetEnvironmentVariable("PCLDL_TEST_REPAIR");
                    if (!string.IsNullOrEmpty(testRepair) && !_autoStarted)
                    {
                        _autoStarted = true;
                        _versionBox.Text = testRepair;
                        StartRepair();
                    }

                    var testQueue = Environment.GetEnvironmentVariable("PCLDL_TEST_QUEUE");
                    if (!string.IsNullOrEmpty(testQueue) && !_autoStarted)
                    {
                        _autoStarted = true;
                        foreach (var part in testQueue.Split(','))
                        {
                            var bits = part.Split('|');
                            var id = bits[0].Trim();
                            if (id.Length == 0) continue;
                            var loader = bits.Length > 1 ? bits[1].Trim().ToLowerInvariant() : "none";
                            var label = loader == "none" ? "无加载器" : loader;
                            Enqueue(id, loader, label);
                        }
                    }

                    var testVersion = Environment.GetEnvironmentVariable("PCLDL_TEST_INSTALL");
                    if (!string.IsNullOrEmpty(testVersion) && !_autoStarted)
                    {
                        _autoStarted = true;
                        _versionBox.Text = testVersion;
                        StartInstall();
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("读取失败：" + ex.Message);
            }
            finally { _loading = false; }
        }

        private void RefreshInstalled()
        {
            _installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var versionsDir = Path.Combine(SafeGameDir(), "versions");
                if (Directory.Exists(versionsDir))
                {
                    foreach (var dir in Directory.GetDirectories(versionsDir))
                        _installed.Add(Path.GetFileName(dir));
                }
            }
            catch { }
        }

        private int _renderToken;

        private void BuildVersionList()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _renderToken++;
            var token = _renderToken;

            _listPanel.Children.Clear();
            if (_catalog == null) return;

            RefreshInstalled();

            var all = _catalog.Versions;
            if (!string.IsNullOrEmpty(_query))
                all = all.Where(v => v.Id.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var releases = all.Where(v => v.Type == "release").Take(_releaseShown).ToList();
            var snapshots = all.Where(v => v.Type == "snapshot").Take(_snapshotShown).ToList();
            var ancients = all.Where(v => IsAncient(v.Type)).Take(_ancientShown).ToList();

            if (all.Count == 0)
            {
                _listPanel.Children.Add(_ui.Hint("没有匹配 \"" + _query + "\" 的版本"));
                _lastRenderMs = watch.ElapsedMilliseconds;
                _lastRowCount = 0;
                PclUi.Diag("render rows=0 query=" + _query);
                return;
            }

            // 先放分组标题与首页若干行，剩余行分帧补齐，避免一次性构建卡住 UI
            var pending = new List<FrameworkElement>();
            if (releases.Count > 0)
            {
                _listPanel.Children.Add(_ui.Section("正式版"));
                foreach (var v in releases) pending.Add(Row(v));
            }
            if (snapshots.Count > 0)
            {
                _listPanel.Children.Add(_ui.Section("快照"));
                foreach (var v in snapshots) pending.Add(Row(v));
            }
            if (ancients.Count > 0)
            {
                _listPanel.Children.Add(_ui.Section("远古版本"));
                foreach (var v in ancients) pending.Add(Row(v));
            }

            const int firstBatch = 16;
            const int batchSize = 24;
            for (int i = 0; i < Math.Min(firstBatch, pending.Count); i++) _listPanel.Children.Add(pending[i]);

            var index = firstBatch;
            Action pump = null;
            pump = () =>
            {
                if (token != _renderToken) return;
                var stop = Math.Min(index + batchSize, pending.Count);
                for (; index < stop; index++) _listPanel.Children.Add(pending[index]);
                if (index < pending.Count) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, pump);
                else AppendLoadMoreButton();
            };
            if (pending.Count > firstBatch) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, pump);
            else AppendLoadMoreButton();

            watch.Stop();
            _lastRenderMs = watch.ElapsedMilliseconds;
            _lastRowCount = pending.Count;
            PclUi.Diag("render first=" + _lastRenderMs + "ms rows=" + pending.Count +
                       " release=" + releases.Count + " snapshot=" + snapshots.Count + " ancient=" + ancients.Count +
                       (_query.Length > 0 ? " query=" + _query : ""));
        }

        private static bool IsAncient(string type)
        {
            return type == "old_beta" || type == "old_alpha" || type == "alpha" || type == "beta";
        }

        private void AppendLoadMoreButton()
        {
            if (!string.IsNullOrEmpty(_query)) return;   // 搜索时一次显示全部命中项

            var source = _catalog.Versions;
            var totalRelease = source.Count(v => v.Type == "release");
            var totalSnapshot = source.Count(v => v.Type == "snapshot");
            var totalAncient = source.Count(v => IsAncient(v.Type));
            var hasMore = _releaseShown < totalRelease || _snapshotShown < totalSnapshot || _ancientShown < totalAncient;
            var more = _ui.Button(hasMore ? "加载更多" : "已全部显示", false, LoadMore, 28, new Thickness(0, 10, 0, 0));
            more.IsEnabled = hasMore;
            _listPanel.Children.Add(more);
        }

        private FrameworkElement Row(PclDl.Core.Models.ManifestEntry entry)
        {
            var date = entry.ReleaseTime;
            if (!string.IsNullOrEmpty(date) && date.Length >= 16) date = date.Substring(0, 16).Replace('T', ' ');
            var installed = _installed != null && _installed.Contains(entry.Id);
            var info = date + (installed ? "   已安装" : "");
            return _ui.ListItem(entry.Id, info, () => SelectVersion(entry), PclUi.VersionLogo(entry.Type));
        }

        private void SelectVersion(PclDl.Core.Models.ManifestEntry entry)
        {
            _selected = entry;
            _versionBox.Text = entry.Id;

            var typeText = entry.Type == "release" ? "正式版" : entry.Type == "snapshot" ? "快照" : entry.Type;
            var date = entry.ReleaseTime;
            if (!string.IsNullOrEmpty(date) && date.Length >= 16) date = date.Substring(0, 16).Replace('T', ' ');
            var installed = _installed != null && _installed.Contains(entry.Id);
            _detail.Text = "已选择 " + entry.Id + "（" + typeText + "，" + date + "）"
                           + (installed ? " · 当前已安装" : "") + "，点击「安装」开始下载";
        }

        private void LoadMore()
        {
            _releaseShown += PageStep;
            _snapshotShown += PageStep;
            _ancientShown += PageStep;
            BuildVersionList();
        }

        private void StartInstall()
        {
            var versionId = (_versionBox.Text ?? "").Trim();
            if (versionId.Length == 0) { UpdateStatus("请先填写版本 ID，或在列表中选中一个版本"); return; }

            var loaderName = (_loaderBox.SelectedItem as string) ?? "无加载器";
            var loader = loaderName == "Fabric" ? "fabric"
                : loaderName == "Forge" ? "forge"
                : loaderName == "NeoForge" ? "neoforge"
                : loaderName == "OptiFine" ? "optifine"
                : "none";

            Enqueue(versionId, loader, loaderName);
        }

        /// <summary>把任务放进队列，然后交给队列泵依次执行。</summary>
        private void Enqueue(string versionId, string loader, string loaderLabel)
        {
            var task = new InstallTask
            {
                Id = ++_taskSeq,
                VersionId = versionId,
                Loader = loader,
                LoaderLabel = loaderLabel
            };
            _tasks.Add(task);
            RenderTasks();
            UpdateStatus("已加入队列：" + versionId + "（" + loaderLabel + "），队列 " + _tasks.Count(x => x.Status == "排队") + " 个等待");

            if (!_pumping) _ = PumpAsync();
        }

        /// <summary>队列泵：一次只跑一个安装，跑完自动接下一个。</summary>
        private async Task PumpAsync()
        {
            if (_pumping) return;
            _pumping = true;
            try
            {
                while (true)
                {
                    var task = _tasks.FirstOrDefault(x => x.Status == "排队");
                    if (task == null) break;

                    task.Running = true;
                    task.Status = "进行中";
                    task.Message = null;
                    RenderTasks();
                    await ExecuteTaskAsync(task).ConfigureAwait(true);
                    task.Running = false;
                    RenderTasks();
                }
            }
            finally
            {
                _pumping = false;
                PclUi.Diag("queue: 队列清空（累计 " + _tasks.Count + " 条）");
            }
        }

        private async Task ExecuteTaskAsync(InstallTask task)
        {
            var gameDir = TargetGameDir();
            if (!string.Equals(gameDir, SafeGameDir(), StringComparison.OrdinalIgnoreCase))
                PclUi.Diag("install: 使用自定义安装路径 " + gameDir);
            _installCts = new CancellationTokenSource();
            _installButton.IsEnabled = false;
            _cancelButton.IsEnabled = true;
            _progress.Value = 0;
            UpdateStatus("开始安装 " + task.VersionId + "（" + task.LoaderLabel + "）到 " + gameDir);
            PclUi.Diag("queue: 开始 " + task.VersionId + "（" + task.LoaderLabel + "）");

            try
            {
                var options = new InstallOptions
                {
                    GameDir = gameDir,
                    VersionId = task.VersionId,
                    Loader = task.Loader,
                    PreferMirror = true,
                    Concurrency = 8,
                    OnStage = stage => Dispatcher.Invoke(new Action(() => UpdateStatus(stage))),
                    OnProgress = p => Dispatcher.Invoke(new Action(() => UpdateProgress(p)))
                };

                var service = new InstallService(new PclUi.DiagLog());
                var summary = await service.InstallAsync(options, _installCts.Token).ConfigureAwait(true);

                task.Status = summary.Success ? "已完成" : "失败";
                task.Message = summary.Success
                    ? ("下载 " + summary.Downloaded + "，跳过 " + summary.Skipped + "，耗时 " + summary.Elapsed.TotalSeconds.ToString("0.0") + " 秒")
                    : ("失败 " + summary.Failed + " 个文件");
                if (summary.Success)
                {
                    _progress.Value = 100;
                    RequestPclVersionRefresh();
                }
                else
                {
                    _progress.Value = 0;
                }
                UpdateStatus(task.VersionId + "：" + task.Status + "（" + task.Message + "）");
                PclUi.Diag("queue: 结束 " + task.VersionId + " -> " + task.Status + "（" + task.Message + "）");
            }
            catch (OperationCanceledException)
            {
                task.Status = "已取消";
                task.Message = "用户取消";
                _progress.Value = 0;
                UpdateStatus("已取消：" + task.VersionId);
                PclUi.Diag("queue: 取消 " + task.VersionId);
            }
            catch (Exception ex)
            {
                task.Status = "失败";
                task.Message = ex.Message;
                _progress.Value = 0;
                UpdateStatus("安装异常：" + ex.Message);
                PclUi.Diag("queue: 异常 " + task.VersionId + "：" + ex.Message);
            }
            finally
            {
                _installCts.Dispose();
                _installCts = null;
                _installButton.IsEnabled = true;
                _cancelButton.IsEnabled = false;
            }
        }

        /// <summary>重画任务队列（条数很少，直接清空重建）。</summary>
        private void RenderTasks()
        {
            if (_taskPanel == null) return;
            _taskPanel.Children.Clear();
            if (_tasks.Count == 0)
            {
                _taskPanel.Children.Add(_ui.Hint("（暂无任务）"));
                return;
            }

            foreach (var task in _tasks.OrderByDescending(x => x.Id))
            {
                var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                var color = task.Status == "已完成" ? "ColorBrush2"
                    : task.Status == "失败" ? "ColorBrush2"
                    : task.Status == "进行中" ? "ColorBrush1"
                    : "ColorBrush2";
                var text = new TextBlock
                {
                    Text = "#" + task.Id + "  " + task.VersionId + "（" + task.LoaderLabel + "）  " + task.Status +
                           (string.IsNullOrEmpty(task.Message) ? "" : "  ·  " + task.Message),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Theme.Apply(text, TextBlock.ForegroundProperty, color, Brushes.DimGray);
                row.Children.Add(text);

                if (task.Status == "失败" || task.Status == "已取消")
                {
                    var retryTask = task;
                    row.Children.Add(_ui.Button("重试", false, () =>
                    {
                        retryTask.Status = "排队";
                        retryTask.Message = null;
                        RenderTasks();
                        if (!_pumping) _ = PumpAsync();
                    }, 24, new Thickness(0, 0, 8, 0)));
                }
                _taskPanel.Children.Add(row);
            }
        }

        /// <summary>
        /// 让 PCL 立刻刷新版本列表。
        /// 优先复用 PCL 自己的 LoaderFolderRun（与进入版本页时的行为一致），
        /// 失败再退回 McInstanceListLoader.Start。
        /// </summary>
        private void RequestPclVersionRefresh()
        {
            try
            {
                var pcl = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Plain Craft Launcher 2");
                if (pcl == null) { PclUi.Diag("refresh: 找不到 PCL 程序集"); return; }

                var modLoader = pcl.GetType("PCL.ModLoader", false);
                var modMinecraft = pcl.GetType("PCL.ModMinecraft", false);
                if (modLoader == null || modMinecraft == null) { PclUi.Diag("refresh: 缺少 ModLoader/ModMinecraft"); return; }

                var loaderField = modMinecraft.GetField("McInstanceListLoader",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var loader = loaderField == null ? null : loaderField.GetValue(null);
                var folderProp = modMinecraft.GetProperty("McFolderSelected",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var folder = folderProp == null ? SafeGameDir() : (folderProp.GetValue(null, null) as string ?? SafeGameDir());

                if (loader != null && modLoader != null)
                {
                    var runType = pcl.GetType("PCL.ModLoader+LoaderFolderRunType", false);
                    var folderRun = modLoader.GetMethods()
                        .FirstOrDefault(m => m.Name == "LoaderFolderRun" && m.GetParameters().Length == 6);
                    if (runType != null && folderRun != null)
                    {
                        var forceRun = Enum.Parse(runType, "ForceRun");
                        folderRun.Invoke(null, new object[] { loader, folder, forceRun, 1, "versions\\", true });
                        PclUi.Diag("refresh: LoaderFolderRun 完成（" + folder + "）");
                        return;
                    }
                }

                var startMethod = loader == null ? null : loader.GetType().GetMethod("Start",
                    new[] { typeof(object), typeof(bool) });
                if (startMethod != null)
                {
                    startMethod.Invoke(loader, new object[] { folder, true });
                    PclUi.Diag("refresh: 退回 McInstanceListLoader.Start（" + folder + "）");
                }
                else PclUi.Diag("refresh: 两种刷新方式都不可用");
            }
            catch (Exception ex) { PclUi.Diag("refresh: 失败 " + ex.Message); }
        }

        private void UpdateProgress(FetchProgress p)
        {
            if (p == null) return;
            if (p.BytesTotal > 0) _progress.Value = 100.0 * p.BytesDone / p.BytesTotal;
            else if (p.FilesTotal > 0) _progress.Value = 100.0 * p.FilesDone / p.FilesTotal;

            var parts = new System.Collections.Generic.List<string>();
            parts.Add("进度 " + _progress.Value.ToString("0.0") + "%");
            if (p.BytesTotal > 0)
                parts.Add((p.BytesDone / 1048576.0).ToString("0.0") + "/" + (p.BytesTotal / 1048576.0).ToString("0.0") + " MB");
            parts.Add(p.FilesDone + "/" + p.FilesTotal + " 文件");
            if (p.BytesPerSecond > 1024)
                parts.Add((p.BytesPerSecond / 1048576.0).ToString("0.0") + " MB/s");
            if (p.Eta.HasValue)
                parts.Add("剩余 " + FormatEta(p.Eta.Value));
            if (!string.IsNullOrEmpty(p.CurrentFile))
                parts.Add("当前 " + p.CurrentFile);

            UpdateStatus(string.Join(" · ", parts.ToArray()));
        }

        private static string FormatEta(TimeSpan eta)
        {
            if (eta.TotalHours >= 1) return ((int)eta.TotalHours) + "h" + eta.Minutes + "m";
            if (eta.TotalMinutes >= 1) return eta.Minutes + "m" + eta.Seconds + "s";
            return Math.Max(1, (int)eta.TotalSeconds) + "s";
        }

        private void UpdatePackStatus(string text)
        {
            if (_packStatus != null) _packStatus.Text = text;
        }

        private async void StartModpack(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                UpdatePackStatus("请先选择一个整合包文件");
                return;
            }
            if (_installCts != null) { UpdatePackStatus("已有任务在运行"); return; }

            _installCts = new CancellationTokenSource();
            _installButton.IsEnabled = false;
            _cancelButton.IsEnabled = true;
            _progress.Value = 0;
            UpdatePackStatus("开始安装整合包：" + Path.GetFileName(path));

            try
            {
                var installer = new PclDl.Core.Modpack.ModpackInstaller(new PclUi.DiagLog());
                var result = await installer.InstallAsync(path, SafeGameDir(), null, true,
                    stage => Dispatcher.Invoke(new Action(() => UpdatePackStatus(stage))), _installCts.Token,
                    CurseForgeKey())
                    .ConfigureAwait(true);

                PclUi.Diag("modpack: 完成 " + result.InstanceId + "（" + result.Format + "，基础 " + result.BaseVersionId + "）");
                UpdatePackStatus("整合包安装完成：" + result.InstanceId + "（" + result.Format +
                                 "，基础版本 " + result.BaseVersionId +
                                 "，文件 " + result.FilesDownloaded + " 个）");
                _progress.Value = 100;
                RefreshInstalled();
            }
            catch (OperationCanceledException)
            {
                UpdatePackStatus("已取消整合包安装");
            }
            catch (Exception ex)
            {
                PclUi.Diag("modpack: 失败 " + ex);
                UpdatePackStatus("整合包安装失败：" + ex.Message);
            }
            finally
            {
                _installCts.Dispose();
                _installCts = null;
                _installButton.IsEnabled = true;
                _cancelButton.IsEnabled = false;
            }
        }

        private async void StartRepair()
        {
            var versionId = (_versionBox.Text ?? "").Trim();
            if (versionId.Length == 0) { UpdateStatus("请先填写要校验的版本 ID"); return; }
            if (_installCts != null) { UpdateStatus("已有任务在运行"); return; }

            _installCts = new CancellationTokenSource();
            _installButton.IsEnabled = false;
            UpdateStatus("正在校验 " + versionId + " 的文件…");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var service = new PclDl.Core.Install.RepairService(new PclUi.DiagLog());
                var targetDir = TargetGameDir();
                var result = await service.RepairAsync(targetDir, versionId, false, _installCts.Token).ConfigureAwait(true);
                watch.Stop();

                var summaryText = result.Broken == 0
                    ? ("校验完成：" + result.Total + " 个文件全部完好（" + watch.ElapsedMilliseconds + " ms）")
                    : ("校验完成：共 " + result.Total + " 个文件，修复 " + result.Downloaded +
                       " 个，剩余 " + result.Remaining + " 个不完整（" + watch.ElapsedMilliseconds + " ms）");
                UpdateStatus(summaryText);
                if (_detail != null) _detail.Text = summaryText + " · " + TargetGameDir();
                PclUi.Diag("repair: version=" + versionId + " total=" + result.Total + " broken=" + result.Broken +
                           " downloaded=" + result.Downloaded + " remaining=" + result.Remaining +
                           " ms=" + watch.ElapsedMilliseconds);
                if (result.Remaining == 0) _progress.Value = 100;
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("已取消校验");
            }
            catch (Exception ex)
            {
                PclUi.Diag("repair: 失败 " + ex.Message);
                UpdateStatus("校验失败：" + ex.Message);
            }
            finally
            {
                _installCts.Dispose();
                _installCts = null;
                _installButton.IsEnabled = true;
            }
        }

        /// <summary>CurseForge API Key：环境变量优先，其次是 PCL2Plugins\curseforge.key。</summary>
        private static string CurseForgeKey()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("PCLDL_CURSEFORGE_KEY");
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL2Plugins", "curseforge.key");
                if (File.Exists(path)) return File.ReadAllText(path).Trim();
            }
            catch { }
            return null;
        }

        private void UpdateStatus(string text)
        {
            _status.Text = text;
        }
    }
}
