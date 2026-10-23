# PCL2 下载插件 · 逻辑架构（v4：自包含下载面）

> 变更原因：上游把限制当作"随时可能从参数开关升级为整体删除"的功能。因此插件**不能依赖 PCL 的任何下载相关类型**，必须自带下载能力（数据面 + UI），只借用 PCL 的主窗口与页面基类作为宿主。
>
> 依据：工作区 `PCL2-source`（2.99.0.0，6b9a50d）与 `PCL2-source-2.14.0.0`（ade6e04）源码实测。

---

## 0. 定位（与 v3 的本质区别）

| | v3（接回） | v4（代替） |
|---|---|---|
| 数据面 | 使用 PCL 的 `ModDownload`/`ModNet` | **自带引擎**，不碰 PCL 下载代码 |
| UI | 复用 `PageDownloadInstall` | **自带页面**，借 PCL 的页面基类外壳 |
| 依赖 PCL 的下载类型 | 强依赖（`DlClientListLoader` 等） | **零依赖** |
| PCL 下次整体删除下载代码 | 插件全废 | **仍然可用** |
| 工作量 | 半天级 | 周级 |

插件形态仍是"进程内插件"，但它是**寄生在 PCL 窗口里的独立下载器**，而不是"修复 PCL 的补丁"。

---

## 1. 依赖收敛：只借三个稳定点

| 依赖点 | 用途 | 稳定性 | 缺失时 |
|---|---|---|---|
| `FormMain.PanMainRight` / `PanMainLeft`（Border，`Child` 属性） | 右侧内容区 / 左侧导航区的宿主容器 | 极高（主窗口骨架） | 退到 E3 独立窗口 |
| `MyPageRight` / `MyPageLeft`（类） | 页面外壳基类 | 极高（UI 基类） | 退到 E3 |
| `FormMain.PanLeft`（Grid） | 自建导航入口的挂载点 | 高 | 退到标题栏按钮 |

**明确不依赖**：`PageDownloadInstall`、`PageDownloadLeft`、`ModDownload`、`ModNet`、`ModModpack`、`RestrictMcDownload`、`PageLoaderInit`。上游把这些全删掉，插件照常工作。

### 1.1 页面外壳的构造技巧（不需要编译期引用 PCL）

`PageChangeAnim` 会把 `PanMainRight.Child` 强转成 `MyPageRight`（`FormMain.xaml.vb:1524`、`:1549`、`:1551`），所以宿主必须是这个类型。但 `MyPageRight` 继承自 `AdornerDecorator`，其 `Child` 是**公开依赖属性**：

```text
page = (FrameworkElement)Activator.CreateInstance(binder.Type("MyPageRight"))
page.SetValue(Decorator.ChildProperty, ourUserControl)
PanMainRight.Child = page
```

这样：
- 满足 PCL 的类型强转；
- 内容是**我们自己的 WPF 控件**；
- **不需要引用 PCL.exe**，纯反射，版本漂移时只换别名。

实现注意：`PageOnForceExit` 在无 loader 时会直接访问 `Child.Visibility`（`MyPageRight.vb:242-243`），**壳的 Child 必须非空**，否则空引用。因此壳永远挂我们的控件，不做空壳。

---

## 2. 逻辑分层

```text
┌──────────────────────── L0 宿主层 Host ───────────────────────┐
│ PclDl.Launcher   注入启动 + 能力探测（只决定"挂在哪"）          │
│ PclDl.Bootstrap  AppDomainManager（CLR 启动期挂钩）             │
│ PclDl.Plugin     装配与生命周期                                 │
├──────────────────────── L1 挂载层 Mount ──────────────────────┤
│ MountStrategy    E1 复用下载入口 / E2 自建入口 / E3 独立窗口     │
│ PageShellFactory 反射造 MyPageRight/MyPageLeft 外壳              │
│ NavInjector      导航入口注入与高亮联动                          │
├──────────────────────── L2 表示层 UI ─────────────────────────┤
│ VersionListView / InstallOptionsView / ProgressView              │
│ TaskQueueView / 设置项                                           │
├──────────────────────── L3 领域层 Domain ─────────────────────┤
│ Catalog          版本清单（Mojang / BMCLAPI 镜像）               │
│ Resolver         版本 JSON 合并（inheritsFrom）、rules 求值       │
│ Planner          文件清单：client/libraries/assets/natives       │
│ LoaderProvider   Fabric / Forge / NeoForge / OptiFine           │
│ InstallService   安装编排、命名、冲突检查、回滚                   │
├──────────────────────── L4 传输层 Transport ──────────────────┤
│ Fetcher          多源回退、并发、重试、断点、限速                 │
│ Verifier         size + sha1 + 原子落盘                          │
│ MirrorMap        官方 → BMCLAPI 等镜像规则（外置 JSON）           │
├──────────────────────── L5 契约层 Contract ───────────────────┤
│ DataLayout       versions/ libraries/ assets/ 布局               │
│ InstanceRegistrar 写 .json/.jar + PCL\Setup.ini，让 PCL 识别      │
├──────────────────────── L6 观测层 Diagnostics ────────────────┤
│ PluginLog / CapabilityReport / CrashGuard                       │
└──────────────────────────────────────────────────────────────┘
```

依赖单向：L2 → L3 → L4/L5，L1 只依赖 PCL 反射绑定，L3 不感知 WPF。

---

## 3. 启动与挂载决策（探测目的变了）

```text
Launcher 启动
  ├─ 1. 定位 PCL2.exe / 数据目录 / Java
  ├─ 2. 探测宿主能力（只为了选挂载方式）
  │      P1: FormMain.PanMainRight 存在？
  │      P2: MyPageRight / MyPageLeft 可反射构造？
  │      P3: PCL 的下载页类型是否还存在（不作为依赖，仅用于 E1 机会判断）
  ├─ 3. 选择挂载策略
  │      P1+P2 且 P3 存在 → E1：接管"下载"入口，把 PCL 的下载页换成我们的
  │      P1+P2 且 P3 不存在 → E2：自建导航入口 + 我们的页面占满内容区
  │      只有 P1 → E3：独立窗口（Owner = PCL 主窗口，视觉贴靠）
  │      都不满足 → 不启动 PCL 改造，提示并退出
  └─ 4. 注入启动（APPDOMAIN_MANAGER_*）→ 插件挂载 → 下载功能由插件承担
```

**关键语义变化**：探测不再回答"PCL 是否需要被修复"，而只回答"我们挂在哪里"。插件在任何情况下都会自带下载能力。

---

## 4. 领域层设计（真正的工作量）

### 4.1 Catalog（版本清单）

- 主源：`launchermeta.mojang.com/mc/game/version_manifest_v2.json`
- 镜像：`bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json`
- 输出：`(id, type, url, sha1, releaseTime)` 列表 + 分组（正式/预览/远古/愚人节）
- 缓存：本地 `PCL2Plugins\cache\manifest.json`（TTL 10 分钟）

### 4.2 Resolver（版本解析）

- 下载版本 JSON，按 `inheritsFrom` 链**自底向上合并**（loader 在前、原版在后）
- `rules` 求值：`os.name`（windows/osx/linux）、`os.arch`、`features`（`is_demo_user` 等一律 false）
- 产出归一化的 `ResolvedVersion`：`mainClass`、`classpath`、`assetIndex`、`arguments`、`javaVersion`
- **不做有损转换**：原始 `id.json` 原样落盘，PCL 启动时要读它

### 4.3 Planner（文件清单）

| 类别 | 来源 | 目标路径 |
|---|---|---|
| 客户端 JAR | `downloads.client` | `versions\<id>\<id>.jar` |
| 版本 JSON | 原样 | `versions\<id>\<id>.json` |
| 库 | `libraries[].downloads.artifact` | `libraries\<path>` |
| natives | `libraries[].downloads.classifiers` | 解压到 `versions\<id>\natives` |
| 资源索引 | `assetIndex` | `assets\indexes\<id>.json` |
| 资源对象 | 索引内 `objects[*].hash` | `assets\objects\<h[0:2]>\<hash>` |
| 日志配置 | `logging.client` | `assets\log_configs\<id>` |

每条任务统一为 `FileJob { Urls[], LocalPath, Size, Sha1, Kind }`。

### 4.4 LoaderProvider（加载器）

| 加载器 | 实现方式 | 难度 |
|---|---|---|
| Fabric / Quilt | 纯 HTTP：`meta.fabricmc.net` 生成 profile JSON（带 `inheritsFrom`），库全在 maven | 低 |
| Forge / NeoForge | 取 `install_profile.json` + installer jar，**复用 PCL 自带的 `forge-installer.jar`/`transformer.jar` 思路**，运行 processors（需 Java） | 高 |
| OptiFine | 运行 OptiFine installer jar（含 GUI 参数），或解析其内部库清单 | 中高 |

MVP 顺序：Fabric → Forge/NeoForge（调官方 installer）→ OptiFine。

### 4.5 Transport / Contract

- `Fetcher`：源列表轮转、指数退避、`.part` 临时文件、Range 续传、并发 8、限速可选
- `Verifier`：size → sha1 → **同盘原子 move**
- `MirrorMap`：外置 JSON，规则化 `launchermeta/launcher/piston-meta/resources/maven → BMCLAPI`，可热更新
- `DataLayout`：严格按 PCL 期望布局写入
- `InstanceRegistrar`：写 `versions\<id>\<id>.json` + `.jar` + `PCL\Setup.ini`（`Logo`/`LogoCustom`/`DisplayType`/`IsStar`），保证 PCL 的 `McInstanceListLoader` 扫 `versions\` 时能识别

---

## 5. UI 设计（自带页面）

| 视图 | 内容 |
|---|---|
| 版本列表 | 正式/预览/远古分组，搜索，已安装标记，选中态 |
| 安装选项 | 目标名称、加载器（Fabric/Forge/NeoForge/OptiFine）、安装路径、内存预设（可选） |
| 进度视图 | 总进度、已下载/总量、速度、当前文件、剩余时间、取消 |
| 任务队列 | 排队/进行中/已完成/失败，重试与清理 |

样式：复用 PCL 的 `Custom.xaml` 资源键（只读引用，不依赖代码）。视觉目标是"看不出是外挂页面"。

---

## 6. 线程与生命周期

| 线程 | 职责 |
|---|---|
| CLR 启动线程 | Bootstrap，禁止 UI 操作 |
| 插件调度线程 | 探测、挂载、轮询宿主状态 |
| PCL UI 线程 | 页面构造、控件操作（`Dispatcher`） |
| 引擎工作线程池 | 下载、校验、解压、安装 |
| 进度聚合线程 | 节流 100 ms 推 UI，避免刷爆 Dispatcher |

退出：PCL 关闭时取消所有 `FileJob`，删除 `.part`，写最终日志。

---

## 7. 失败与降级

| 故障 | 行为 |
|---|---|
| Bootstrap 未生效 | `Disabled`，PCL 正常 |
| 页面壳构造失败 | 降级到 E3（独立窗口） |
| 主窗口结构变化 | 走 E3 |
| 引擎下载失败 | 任务级重试 → 源切换 → 失败标记，不崩进程 |
| sha1 不符 | 删除临时文件重下一次，再失败则报错 |
| 插件抛出未捕获异常 | `CrashGuard` 兜底记日志，绝不影响 PCL |

---

## 8. 里程碑（按"代替"重新排）

| 阶段 | 交付 | 预估 |
|---|---|---|
| M1 | 引擎骨架：Catalog + Resolver + Planner + Fetcher/Verifier（纯 HTTP 层）+ 单元测试 | 3-5 天 |
| M2 | 原版安装全流程：1.20.1 落盘，PCL 能识别并启动 | 2-3 天 |
| M3 | 挂载层：Bootstrap 注入 + PageShellFactory + 最小 UI（版本列表 + 进度） | 3-4 天 |
| M4 | Fabric；任务队列/取消/重试 | 2-3 天 |
| M5 | Forge / NeoForge（installer 流程） | 5-7 天 |
| M6 | OptiFine、整合包（后续） | — |

M1/M2 可以完全脱离 PCL 开发（控制台跑通），这是关键：**先把数据面做对，再谈挂载**。

---

## 9. 与 PCL 的不变式

1. 不修改 EXE / `app.config` / 任何自带文件。
2. 不调用 PCL 的下载代码（不依赖其存在）。
3. 只写 `.minecraft` 的标准布局 + `PCL\Setup.ini`。
4. 不替换方法体、不做 detour。
5. 插件崩溃不得带走启动器。
6. 卸载 = 删 `PCL2Plugins\`，PCL 立即回到官方行为。

---

## 10. 版本记录

| 版本 | 说明 |
|---|---|
| v1-v2 | 源码对比、A/B/C 概览、注入可行性 |
| v3 | 进程内插件"回放锚点"路线（依赖 PCL 下载代码） |
| v4 | 改为自包含下载面：自带引擎 + 自带 UI，仅借主窗口与页面基类；引入 E1/E2/E3 挂载策略 |
