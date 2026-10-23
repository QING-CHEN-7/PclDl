# PCL2 下载插件 · 逻辑架构（v3）

> 定位：**塞进官方 PCL2 的进程内插件**。不改 EXE、不注入线程、不替换方法体，只把被裁掉的 UI 入口重新接回 PCL 自己的下载引擎。
>
> 依据：工作区 `PCL2-source`（2.99.0.0，commit 6b9a50d）与 `PCL2-source-2.14.0.0`（commit ade6e04）源码实测，以及本机 VS2022 / MSBuild 17.13 / .NET Framework 4.8 环境。

---

## 0. 一句话定位

插件是**控制面**，不是数据面：它只负责把被裁掉的 UI 入口重新接回 PCL 自己的下载引擎，下载、镜像、校验、落盘全部仍由 PCL 执行。

这决定了三件事：

- 插件很薄，不需要自研下载引擎；
- 不需要 IL 补丁（2.99 没有可补的分支）；
- 官方 EXE 一个字节都不改。

---

## 1. 事实前提（决定了架构形状）

2.99 的下载限制是**编译期常量**导致的分支消除，不是运行时开关。6 个使用点：

| 位置 | 形态 | 编译期后果 |
|---|---|---|
| `Modules/ModSecret.vb:18` | `Public Const RestrictMcDownload As Boolean = True` | 全部内联 |
| `Pages/PageDownload/PageDownloadInstall.xaml.vb:17` | `If RestrictMcDownload Then 关闭下载 Else 正常` | **Else 分支被整段删除** |
| `Pages/PageDownload/PageDownloadModify.vb:315` | `= If(Restrict, Collapsed, Visible)` | 折叠为常量 `Collapsed` |
| `Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb:275` | `If Not Restrict AndAlso ...` | 整块删除 |
| `Modules/Minecraft/ModModpack.vb:11` | 入口早退 | 函数体塌缩 |
| `Modules/Minecraft/ModModpack.vb:88` | `Throw` | 保留 |
| `FormMain.xaml.vb:1079` | 比对异常字符串 | 运行时路径，不折叠 |

结论：

1. **改常量字段的值无效**——值已内联，且正常分支不存在，没有分支可翻。
2. **恢复页面的入口是"重放被删掉的那一行"**，而不是"恢复被删掉的代码"。

被删掉的那一行原文（`PageDownloadInstall.xaml.vb:20`）：

```vb
PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, Nothing, DlClientListLoader, AddressOf LoadMinecraft_OnFinish)
```

它引用的每一件东西在 2.99 里都还在：

| 依赖 | 位置 | 可见性 |
|---|---|---|
| `LoaderInit` | `PageDownloadInstall.xaml.vb:15` | `Public Overridable` |
| `DlClientListLoader` | `Modules/Minecraft/ModDownload.vb:197` | `Public`（`LoaderTask(Of String, DlClientListResult)`） |
| `PageLoaderInit(Of TIn, TOut)` | `Controls/MyPageRight.vb:62` | `Public` 泛型 |
| `LoadMinecraft_OnFinish()` | `PageDownloadInstall.xaml.vb:446` | `Private`，**无参**（VB 宽松委托才能绑 `Action(Of LoaderTask(...))`） |
| `PanLoad` / `PanAllBack` | XAML 生成字段 | `Friend`，反射可读写 |
| `ModMain.FrmDownloadInstall` | `Modules/ModMain.vb:381` | `Public` 静态字段 |

---

## 2. 逻辑分层

```text
┌───────────────────────────────────────────────────────────┐
│ L0 宿主层  Host                                            │
│    PclDl.Launcher（探针 + 环境变量 + 进程创建）              │
│    PclDl.Bootstrap（AppDomainManager，CLR 启动期挂钩）       │
├───────────────────────────────────────────────────────────┤
│ L1 装配层  Composition                                     │
│    PluginEntry：生命周期、UI 线程调度、全局异常兜底          │
│    PluginConfig：开关、日志级别、钩子启用表                  │
├───────────────────────────────────────────────────────────┤
│ L2 绑定层  Binding                                         │
│    PclBinder：类型/字段/方法句柄解析 + 签名校验              │
│    ReflectionHelper：字段读写、泛型构造、委托构造            │
├───────────────────────────────────────────────────────────┤
│ L3 接管层  Hooks（可插拔，每个入口一个）                     │
│    InstallPageHook    ← M1（恢复下载安装页 + 全部加载器）    │
│    ModifyPageHook     ← M2（恢复版本修改页 MC 卡片）        │
│    ResourcePageHook   ← M3（恢复整合包按钮）                │
│    ModpackEntryHook   ← M3（恢复整合包安装入口）            │
├───────────────────────────────────────────────────────────┤
│ L4 观测层  Diagnostics                                     │
│    PluginLog / Fingerprint / DegradeReporter               │
└───────────────────────────────────────────────────────────┘
```

依赖方向严格单向：L3 → L2 → L1 → L0。L3 各 hook 之间互不引用，任一 hook 失败只影响自身。

---

## 3. 组件职责与失败行为

| 组件 | 职责 | 失败行为 |
|---|---|---|
| `Launcher` | 探测 PCL 能力 → 组装环境变量 → 起 PCL2.exe | 探测失败/不支持 → 按原样起 PCL（无插件） |
| `Bootstrap` | `AppDomainManager`；装私有 `AssemblyResolve`；后台线程加载插件 | **吞异常**，PCL 继续启动 |
| `PluginEntry` | 等主窗口就绪 → 启动 hook 调度器 → 汇总状态 | 记 `Disabled`，不再动作 |
| `PclBinder` | 解析锚点表并校验签名 | 缺失 → 对应 hook `Unavailable` |
| `InstallPageHook` | 轮询页面单例 → 执行 loader 回放 | `Degraded`，页面保持官方行为 |
| `ModifyPageHook` | 轮询页面单例 → 后置修复可见性 | 同上 |
| `PluginLog` | 落盘 `PCL2Plugins\plugin.log`，环形 2 MB | 写失败静默 |

---

## 4. 启动与决策流程（控制面主干）

```text
Launcher 启动
   │
   ├─ 1. 定位 PCL2.exe 与数据目录（PCL\PCL.ini、.minecraft）
   │
   ├─ 2. 能力探测（只读，不动文件）
   │      Probe.Capability()
   │      ├─ A: 下载安装页入口是否被裁（LoaderInit 方法体形态 / 是否引用 ClientListLoader）
   │      ├─ B: 引擎锚点是否齐全（DlClientListLoader、NetFile、PageLoaderInit）
   │      ├─ C: 扩展入口锚点（InstallPackCurseForge/Modrinth/HMCL/MMC/MCBBS）
   │      └─ D: 版本修改页 / 资源详情页受限点
   │
   ├─ 3. 判定（四态，无"可补丁"态）
   │      D0 完整     → 不插手，直接启动（如 2.14）
   │      D1 入口被裁 → 插件接管安装页（2.99 走这条）
   │      D2 引擎残缺 → 插件接管 + 自研引擎（本架构暂不支持，直接报错退出）
   │      D3 未知     → 不接管，直接启动，记录报告
   │
   ├─ 4. 选择动作
   │      D0/D3 → 裸启动 PCL2.exe
   │      D1    → 设 APPDOMAIN_MANAGER_* 环境变量 → 启动 PCL2.exe
   │
   └─ 5. 运行期
          Bootstrap 挂钩 → PluginEntry → PclBinder → InstallPageHook → 回放 loader
          → 页面恢复为 PCL 原生下载页（原版 + Forge/NeoForge/Fabric/OptiFine/... 全部可用）
          → 之后插件退出数据路径，下载由 PCL 自己执行
```

**关键**：判定只依据"锚点存在性与方法体形态"，不依据版本号、不依据配置文件。配置文件里的 `RestrictMcDownload` 字段不存在，也不应作为判据。

---

## 5. 进程与文件布局

```text
PCL2\
├─ Plain Craft Launcher 2.exe      ← 一个字节都不动
├─ PclDl.Bootstrap.dll             ← 唯一放进 PCL 目录的文件（约 20 KB，零依赖）
└─ PCL2Plugins\
   ├─ PclDl.Plugin.dll
   └─ plugin.log
```

`Bootstrap` 只做三件事：

1. 注册**私有** `AssemblyResolve`（`try/catch` 包住，绝不打断 PCL 自己的嵌入资源加载）；
2. 起后台线程；
3. `Assembly.LoadFrom("PCL2Plugins\\PclDl.Plugin.dll")`。

任何异常都必须吞掉：插件挂了，PCL 必须照常起来。

---

## 6. 接管机制：回放，不替换

插件执行的，就是源码第 20 行那句的反射等价物：

```text
PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, Nothing,
               ModDownload.DlClientListLoader, AddressOf LoadMinecraft_OnFinish)
```

实现要点：

- 泛型方法：`PageLoaderInit.MakeGenericMethod(String, DlClientListResult)`；
  结果类型从 `DlClientListLoader` 字段的泛型参数反推，**不硬编码类型名**。
- `FinishedInvoke`：用 `Expression.Lambda(actionType, Call(page, loadFinishMethod), param).Compile()`
  把无参的 `LoadMinecraft_OnFinish` 包装成 `Action(Of LoaderTask(Of String, DlClientListResult))`。
- 容器字段 `PanLoad` / `PanAllBack` 为 `Friend`，用反射读写。

**不做的事**：不 override `LoaderInit`、不用 Harmony、不做 ReJIT、不做 detour、不写方法体。

**回报**：`ControlInit()` 已经把 Forge / NeoForge / Fabric / FabricAPI / OptiFine / OptiFabric / LiteLoader
全部挂在同一个页面（`PageDownloadInstall.xaml.vb:3-13`），恢复一个入口，整页加载器能力一起回来。

接管时机：`FrmDownloadInstall` 由 `PageDownloadLeft.xaml.vb:24` 懒加载；
用 `DispatcherTimer`（200 ms）轮询 `ModMain.FrmDownloadInstall`，
一旦非空且 `IsInitialized`，在 UI 线程执行回放。最坏情况是"关闭下载"提示闪一帧。

---

## 7. 四个受限点处理矩阵

| 受限点 | 2.99 现状 | 插件动作 | 阶段 |
|---|---|---|---|
| 下载安装页 | `LoaderInit` 只剩"关闭下载" | **回放**真实 loader | M1 |
| 版本修改页 MC 卡片 | 折叠为 `Collapsed`，但 `LoadInstanceFinished` 的初始化完整保留 | 轮询 `FrmDownloadModify`，初始化跑完后把 `CardMinecraft.Visibility` 设回 `Visible` | M2 |
| 资源详情页整合包按钮 | 按钮所在整块被裁 | 自建按钮 + 反射调仍存在的 `InstallPack*` | M3 |
| `ModModpack.ModpackInstall()` | 早退为 `Hint` | 自建入口 + 调 `ModpackInstall(File)` / `InstallPack*` | M3 |

M1 一件事覆盖约 90% 实际需求（原版 + 加载器安装）。

---

## 8. 生命周期状态机

```text
Launched ──► Bootstrapped ──► Attached ──► Bound ──► Active
                  │              │           │          │
                  │              │           │          └─ 全部 hook 成功
                  │              │           └─ 至少一个锚点缺失 → Degraded（部分 hook）
                  │              └─ 插件程序集加载失败 → Disabled（PCL 正常）
                  └─ 环境变量被忽略 → Disabled
```

`Disabled` / `Degraded` 都是**合法终态**，不是错误路径。插件设计上允许"什么都不做"。

---

## 9. 线程模型

| 线程 | 职责 | 规则 |
|---|---|---|
| CLR 启动线程 | Bootstrap 初始化 | 不做任何 UI/WPF 操作 |
| 后台调度线程 | 轮询页面单例、推进状态 | 只做判空，动作一律 `RunOnUi` |
| PCL UI 线程 | 回放、字段写入、可见性修复 | 所有 WPF 交互必须在此 |
| PCL 工作线程 | 下载（插件不碰） | 插件只读状态，不写 |

`RunOnUi` 基于 `Application.Current.Dispatcher`，在 `MainWindow.Loaded` 之后才可用；之前所有动作排队。

---

## 10. 失败与降级矩阵

| 故障 | 检测点 | 行为 |
|---|---|---|
| 环境变量未生效 | 日志无 `attached` | `Disabled`，PCL 正常 |
| 插件程序集加载失败 | Bootstrap catch | `Disabled` + 日志 |
| PCL 版本不匹配（锚点缺失） | PclBinder | 该 hook `Unavailable` |
| `PageLoaderInit` 签名变化 | `MakeGenericMethod` 失败 | M1 `Degraded` |
| 回放后 loader 抛异常 | 订阅 loader 状态变化 | `Degraded`，保留官方提示 |
| 用户已切到其它子页 | 单例存在但非当前页 | 延迟到下次进入 |
| 插件自身未处理异常 | `AppDomain.UnhandledException` 兜底 | 只记日志，不吞 PCL 异常 |

---

## 11. 与 PCL 的不变式

1. 不修改 `.exe`、`app.config`、任何 PCL 自带文件。
2. 不替换方法体（无 Harmony / 无 ReJIT / 无 detour）。
3. 不接管下载数据流，不写 `.minecraft`。
4. 不注册 PCL 之外的主窗口。
5. 异常不外抛，插件崩溃不得带走启动器。
6. 卸载 = 删 `PCL2Plugins\\` + 恢复快捷方式，PCL 立刻回到官方行为。

---

## 12. 工程结构

```text
pcl2-mcdl-plugin/
├─ src/
│  ├─ PclDl.Launcher/           # 探测 + 环境变量 + 进程创建
│  ├─ PclDl.Bootstrap/          # AppDomainManager，net48，零依赖
│  ├─ PclDl.Probe/              # 只读能力探测（可被 Launcher 复用）
│  │   ├─ PeReader.cs
│  │   ├─ IlSignatureProbe.cs
│  │   └─ CapabilityReport.cs
│  └─ PclDl.Plugin/             # 主体，不引用 PCL.exe
│      ├─ PluginEntry.cs
│      ├─ Host/PclBinder.cs
│      ├─ Host/ReflectionHelper.cs
│      ├─ Hooks/InstallPageHook.cs
│      ├─ Hooks/ModifyPageHook.cs
│      ├─ Hooks/ResourcePageHook.cs
│      ├─ Hooks/ModpackEntryHook.cs
│      └─ Diagnostics/PluginLog.cs
├─ tests/
│  └─ fingerprint/              # 用 2.99 / 2.14 EXE 各跑一次探测
└─ dist/
```

`PclDl.Plugin` **不引用 `Plain Craft Launcher 2.exe`**，全程反射 + `PclBinder` 签名校验：
签名对不上就禁用自己并写日志（fail-open），保证 PCL 小版本迭代不会把启动器搞崩。

---

## 13. 里程碑

| 阶段 | 交付 | 验收 |
|---|---|---|
| M1a | Launcher + Probe：输出能力报告 | 2.99 判 D1、2.14 判 D0 |
| M1b | Bootstrap 能加载插件，日志出现 `attached` | PCL 正常启动 |
| M1c | PclBinder 解析 5 个锚点 | 报告 `Bound` |
| M1d | InstallPageHook 回放 | 下载页出现真实版本列表（BMCLAPI） |
| M1e | 安装 1.20.1 | PCL 自身引擎跑完，游戏能启动 |
| M2 | ModifyPageHook | 版本修改页 MC 卡片恢复 |
| M3 | ResourcePageHook + ModpackEntryHook | 资源页整合包安装可用 |

---

## 14. 已知约束

1. **版本锁定是事实**：插件只认方法/字段签名，不认版本号；签名变化自动降级。
2. **会闪一帧**"关闭下载"提示（`LoaderInit` 执行无法被阻止，只能在其后接管）。消闪属于后续体验优化。
3. **合规由使用者承担**：插件不改 EXE、不重新分发游戏文件，但"恢复被上游因合规原因关闭的功能"本身需要自行评估。

---

## 15. 版本记录

| 版本 | 说明 |
|---|---|
| v1 | 源码对比、构建验证、A/B/C 概览 |
| v2 | 整合注入分析；补充可行性矩阵、决策树、测试与回滚 |
| v3 | 定形为进程内插件：改为"回放锚点"路线，删除补丁态，补 L0~L4 分层、状态机、线程模型、失败矩阵 |
