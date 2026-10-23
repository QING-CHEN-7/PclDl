# PCL2 下载能力整合方案（v2 评审稿）

> 基于工作区 `PCL2-source`（2.99.0.0）、`PCL2-source-2.14.0.0` 以及本机 VS2022 / .NET Framework 4.8 环境整理。
>
> 本文整合了此前的源码对比、构建验证、IL 检查和注入式插件讨论，作为后续选型与实施的单一评审稿。

---

## 0. 结论摘要

| 目标 | 推荐方案 |
|---|---|
| 尽快恢复完整 MC 下载功能，允许替换 PCL2 EXE | **方案 A：源码构建版** |
| 官方 PCL2 EXE 不动，下载功能可以放在外部工具 | **方案 B：外置下载桥** |
| 官方 EXE 不动，且下载 UI 必须嵌在 PCL2 内部 | **方案 C：运行时注入插件** |
| 只想改配置/内存/IL 常量来恢复 2.99 下载 | **不可行**，原因见 2.3 |

**默认建议：先做方案 A。**  
它只需要改一个编译期常量并重新构建，完整复用 PCL2 现有的下载引擎、镜像源、加载器和整合包逻辑，成本和风险都最低。  
方案 C 技术上可行，但它不是“JIT 把删掉的代码变回来”，而是“注入插件 + 替换仍然存在的方法 + 补写被裁掉的入口逻辑”，维护成本明显更高。

---

## 1. 背景与边界

### 1.1 现状

| 目录 | 版本 | 状态 |
|---|---|---|
| `PCL2-source` | 2.99.0.0 | 带 `RestrictMcDownload = True`，MC 下载入口被限制 |
| `PCL2-source-2.14.0.0` | 2.14.0.0 | MC 下载正常 |

两者源码只有 **11 个文件**不同，核心差异是：

- `Modules/ModSecret.vb:18` 的 `Public Const RestrictMcDownload As Boolean = True`
- 7 处使用该常量的 UI/入口限制
- 版本号、更新日志、关于页赞助者名单
- `MeloongCore` 子模块回退

**下载链路本身没有删除。** `ModDownload.vb`、`ModNet.vb`、`ModModpack.vb`、`PCLCS` 的下载/元数据能力在 2.99 中仍然完整。

### 1.2 目标

- 恢复或提供 Minecraft 原版、加载器、整合包下载能力。
- 尽量复用 PCL2 现有逻辑，不重复实现下载引擎。
- 明确哪些方案稳定、哪些属于原型/高风险方案。
- 给出可执行的构建、测试、发布和回滚路径。

### 1.3 非目标

- 不重写 PCL2 的 UI 框架。
- 不修改 Minecraft 游戏文件格式。
- 不把“运行时注入”当成长期稳定 API。
- 不在没有合规评估的情况下公开分发改造版。

### 1.4 合规边界

上游把该限制描述为“为遵守相关法律法规”。因此：

- 本地自用/研究、内部验证，与公开分发，风险不同。
- 公开分发改造版、注入版或外置下载器前，应先做合规评估。
- 本文只讨论技术架构，不替代法律意见。

---

## 2. 事实核验

### 2.1 构建环境已验证

本机实际验证：

- Visual Studio 2022 Community
- MSBuild 17.13.19
- .NET Framework 4.8 Targeting Pack
- .NET SDK 9.0.203
- `dotnet restore PCLCS\PCLCS.csproj` 成功
- `msbuild "Plain Craft Launcher 2.sln" -p:Configuration=Release` 成功
- 输出：`Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe`（约 5.9 MB）
- `Resources\Help.zip` 已存在，不需要再克隆 PCL2Help

结论：**源码构建路径在当前机器上可直接落地，不需要额外搭环境。**

### 2.2 PCL2 的运行模型

PCL2 是 .NET Framework 4.8 WPF 程序，不是“运行后现编译”：

```text
.vb / .xaml / .cs
      │ MSBuild / Visual Studio（构建期，一次）
      ▼
IL + 元数据 + BAML + 嵌入资源
      │
      ▼
Plain Craft Launcher 2.exe
      │ 运行时
      ▼
CLR 加载 → AssemblyResolve 加载嵌入 DLL → JIT 逐方法编译 → WPF 加载 BAML
```

关键点：

- `Application.xaml.vb:218-233` 通过 `AppDomain.AssemblyResolve` 从 EXE 资源加载 `PCLCS`、`MeloongCore` 等程序集。
- `Application.xaml:9` 的 `StartupUri="FormMain.xaml"` 创建主窗口。
- `FormMain_Loaded` 启动 Java 检测、版本列表、服务器配置等后台加载池。
- `PageDownloadLeft.PageGet` 懒加载 `PageDownloadInstall`。
- 运行时只做程序集加载和方法级 JIT，不会读取 `.vb` 源码重新编译。

### 2.3 为什么官方 2.99 不能靠 IL 补丁恢复

`RestrictMcDownload` 是 `Public Const Boolean = True`。Release + Optimize 下，VB 编译器会做常量折叠和不可达代码消除。

IL 检查结果：

| 方法 | 2.99（`True`） | 2.14 | 2.99 改为 `False` 后重编译 |
|---|---|---|---|
| `PageDownloadInstall.LoaderInit` | 94 字节，**无 `DlClientListLoader` 引用** | 62 字节，有 | 51 字节，有 |
| `ModpackInstall()` | 19 字节，只调用 `Hint` 后返回 | 正常选择文件 | 123 字节，调用 `Dialogs.SelectFile` + `RunInThread` |
| `ModpackInstall(File)` | 保留压缩包解析，安装分发分支被裁掉 | 完整 | 993 字节，`Select Case` 跳转表完整 |
| `PageDownloadModify.LoadInstanceFinished` | 只保留 `Visibility.Collapsed` | 正常分支 | 恢复 `Visible` |

因此：

- 改 `RestrictMcDownload` 字段：无效，常量已内联。
- 把 `ldc.i4.1` 改成 `ldc.i4.0`：没有普通分支可改。
- 翻转 `brtrue/brfalse`：没有目标可翻转。
- “分支目标重定向”：没有目标可重定向。

**结论：官方 2.99 的普通下载 UI/安装分支根本不在 EXE 里。要恢复，只能重新编译源码，或者用插件补写缺失入口。**

### 2.4 2.99 中仍然存在的“锚点”

虽然普通分支被裁掉，但很多成员仍然存在，这决定了方案 C 的可行性：

- `PageDownloadInstall.LoaderInit` 还在（但只剩限制路径）
- `PageDownloadInstall.LoadMinecraft_OnFinish` 还在（private，有 RVA）
- `ModDownload.DlClientListLoader` 还在（public 字段）
- `PageDownloadModify.LoadInstanceFinished` 还在
- `ModpackInstall(File)` 还在，但安装分发分支被裁掉
- `InstallPackCurseForge`、`InstallPackModrinth`、`InstallPackHMCL`、`InstallPackMMC`、`InstallPackMCBBS` 等辅助方法仍有 RVA

所以方案 C 不是完全无解，但它是“**替换现有方法 + 补写入口逻辑**”，不是“JIT 恢复被删代码”。

---

## 3. 方案总览与决策树

### 3.1 三条路线

| 方案 | 核心思路 | 是否改 PCL2 | UI 位置 | 适用场景 |
|---|---|---|---|---|
| **A 源码构建版** | 2.99 源码改常量后重建 | 是（构建期） | PCL2 内 | 自用、可控分发、追求稳定 |
| **B 外置下载桥** | 独立下载器写入 `.minecraft` | 否 | PCL2 外 | 插件式产品、官方 EXE 不动 |
| **C 运行时注入插件** | 注入 DLL，替换/挂钩方法 | 是（运行时） | PCL2 内 | 官方 EXE 不动且必须内嵌 UI |

### 3.2 决策树

```text
是否允许替换 PCL2 的 EXE？
├─ 是 → 方案 A（首选）
│        └─ 需要“插件化”交付？
│             ├─ 否 → A 直接发布
│             └─ 是 → A + 外置配置/补丁包
└─ 否
   ├─ 下载 UI 可以放在外部？ → 方案 B
   └─ 下载 UI 必须嵌在 PCL2 内？ → 方案 C
```

### 3.3 不建议的做法

- 直接对官方 2.99 做 IL 补丁。
- 试图通过配置、注册表、内存修改 `RestrictMcDownload`。
- 为了一个下载功能重写整套下载引擎。
- 第一版就做完整的注入式 UI 接管。
- 在未评估合规风险的情况下公开分发改造版。

---

## 4. 方案 A：源码构建版（推荐）

### 4.1 定位

在 2.99 源码上做最小补丁构建，恢复 PCL2 原有的 MC 下载页面和下载能力。产物是一个可独立运行的 PCL2 EXE，不依赖注入器、外部启动器或额外运行时。

### 4.2 分层架构

```text
┌─────────────────────────────────────────────────────┐
│ UI 层（PCL2 现有）                                   │
│  PageDownloadInstall / PageDownloadLeft              │
│  PageDownloadModify / PageDownloadResourceDetail     │
├─────────────────────────────────────────────────────┤
│ 业务编排层（PCL2 现有）                              │
│  ModDownload：DlClientListLoader / DlClientFix       │
│                DlSource* 镜像源选择                  │
│  ModModpack：CurseForge / Modrinth / HMCL / MMC ...  │
├─────────────────────────────────────────────────────┤
│ 下载引擎层（PCL2 现有）                              │
│  ModNet：NetFile / LoaderDownload / FileChecker      │
│  多源回退、重试、断点续传、分片、限速                 │
├─────────────────────────────────────────────────────┤
│ 数据与元数据层（PCL2 现有）                          │
│  ModMinecraft：McInstance / 版本扫描                 │
│  PCLCS：McVersion / McInstallManifest                │
│  MeloongCore：FileUtils / PathUtils / Config         │
├─────────────────────────────────────────────────────┤
│ 构建与发布                                           │
│  MSBuild + .NET Framework 4.8                        │
│  单文件 EXE（PCLCS/MeloongCore 嵌入 Resources）      │
│  开源版无自动更新                                     │
└─────────────────────────────────────────────────────┘
```

### 4.3 最小改动清单

| 文件 | 改动 | 必要性 |
|---|---|---|
| `Plain Craft Launcher 2/Modules/ModSecret.vb:18` | `RestrictMcDownload = False` | **必须** |
| `Plain Craft Launcher 2/FormMain.xaml.vb:14-16` | 删除/改写“删除 MC 下载功能”的正式版更新日志 | 建议 |
| `Plain Craft Launcher 2/FormMain.xaml.vb:106-108` | 同上（Snapshot 分支） | 建议 |
| `Plain Craft Launcher 2/Modules/Base/ModBase.vb` | 可选：版本名加 `-DL` 后缀 | 可选 |
| `Plain Craft Launcher 2/My Project/AssemblyInfo.vb` | 可选：同步程序集版本 | 可选 |
| `ModSecret.vb` 的 OAuth/CurseForge 字段 | 如需微软登录/CurseForge，配置自己的 Key | 按需 |

建议保留 7 处 `If RestrictMcDownload Then` 守卫，只把常量改为 `False`。补丁最小，未来切换/对比也方便。

### 4.4 推荐工程结构

```text
pcl2-dl/
├─ upstream/                 # PCL2-source（2.99，git submodule 或 subtree）
├─ patches/
│  └─ 0001-unrestrict.patch  # 常量 + 更新日志
├─ build/
│  └─ build.ps1              # restore + msbuild + 产物校验
├─ branding/                 # 版本名、图标、说明
├─ tests/
│  └─ smoke/                 # 下载/启动冒烟测试
└─ docs/
```

### 4.5 构建流程

```powershell
# 1. 还原 NuGet
dotnet restore PCLCS\PCLCS.csproj

# 2. Release 重建
msbuild "Plain Craft Launcher 2.sln" -p:Configuration=Release -t:Rebuild

# 3. 产物
# Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe
```

部署：把 EXE 放到有写权限的目录即可。PCL 会在同级创建 `PCL\` 和 `.minecraft\`，不需要安装器。

### 4.6 测试矩阵

| 类别 | 用例 |
|---|---|
| 原版 | 最新正式版、最新快照、旧版本（1.7.10 / 1.12.2 / 1.20.1） |
| 下载源 | 自动、BMCLAPI、Mojang 官方；切换源后重试 |
| 加载器 | Forge、NeoForge、Fabric、Fabric API、OptiFine、OptiFabric、LiteLoader |
| 整合包 | CurseForge、Modrinth、HMCL、MMC、MCBBS、带启动器的压缩包 |
| 下载行为 | 取消、重试、断点续传、哈希校验失败、403/429、代理 |
| 路径/权限 | 中文路径、空格路径、只读目录、磁盘空间不足 |
| 启动 | Java 自动检测、内存设置、版本隔离、启动游戏 |
| 回归 | 版本修改页、组件安装、更新日志、设置页 |

### 4.7 风险

| 风险 | 说明 | 应对 |
|---|---|---|
| 合规风险 | 上游把限制描述为“为遵守相关法律法规” | 仅本地研究/自用；公开分发前做合规评估 |
| 密钥缺失 | 开源版 `OAuthClientId`、`CurseForgeAPIKey` 为空 | MC 下载不需要；微软登录/CurseForge 自行配置 |
| 上游漂移 | 2.99 是特殊版本，后续可能变化 | 补丁最小化，保存为 patch，便于 rebase |
| 版本混淆 | 版本号仍是 2.99.0.0 | 显示名加 `-DL` 后缀；`RegFolder = PCLDebug` 已隔离 |
| 无自动更新 | 开源版没有更新器 | 手动重建/替换；安全更新需重新构建 |
| 分发 | 无签名，SmartScreen 可能拦截 | 公开分发时使用代码签名证书 |

---

## 5. 方案 B：外置下载桥（不改 PCL2）

### 5.1 定位

如果目标是“插件”而不是“改造版启动器”，做一个独立下载器：它把原版/加载器/整合包安装到 PCL2 的 `.minecraft` 目录，PCL2 只负责识别和启动。官方 PCL2 完全不动。

### 5.2 架构

```text
PCL2-DL-Bridge（独立 WPF/CLI）
├─ PclLocator       定位 PCL2.exe、.minecraft、PCL.ini
├─ Catalog           Mojang / BMCLAPI 版本清单
├─ Resolver          版本 JSON、inheritsFrom、libraries、assets、natives
├─ Downloader        多源、断点续传、哈希校验、重试
├─ Installer         原版、Forge/NeoForge/Fabric/OptiFine
├─ DataAdapter       写入 versions/libraries/assets + PCL/Setup.ini
└─ Launcher          启动 PCL2，或提示刷新版本列表
```

### 5.3 数据流

1. 定位 PCL2 与游戏目录。
2. 拉取版本清单，用户选择 MC 版本和加载器。
3. 解析版本 JSON，生成文件清单（客户端 JAR、libraries、assets、natives）。
4. 下载到临时目录，校验 SHA1/大小，原子移动。
5. 按标准布局写入 `.minecraft`：

   ```text
   versions/<id>/<id>.json
   versions/<id>/<id>.jar
   libraries/...
   assets/indexes/...
   assets/objects/...
   ```

6. 可选写入 `versions/<id>/PCL/Setup.ini`（Logo、Info、VersionVanilla 等展示信息）。
7. 启动 PCL2，PCL2 扫描 `versions/` 后即可看到并启动新版本。

### 5.4 实现建议

- 复用 PCL2 的 `DlSource*` 规则：Mojang → BMCLAPI 的 URL 映射、`FileChecker` 的大小/SHA1 语义。
- 原版安装可考虑 `CmlLib.Core`，减少自己处理 `inheritsFrom`、natives、asset index 的工作量。
- 加载器安装复杂，建议先做原版 + Fabric，再扩展 Forge/NeoForge/OptiFine。
- 整合包安装是最大工作量；如果只需要“下载游戏”，不要第一版就做整合包。

### 5.5 优缺点

| 优点 | 缺点 |
|---|---|
| 官方 PCL2 不动，合规风险相对低 | UI 在 PCL2 外面，不是真正的“内置页面” |
| 可作为独立工具分发 | 需要自己实现/封装下载与安装逻辑 |
| 可支持多个启动器 | 加载器/整合包安装复杂度高 |
| 不受 PCL2 版本升级影响 | 用户需要运行两个程序 |

---

## 6. 方案 C：运行时注入插件（官方 EXE 不动，UI 内嵌）

### 6.1 概念澄清

“JIT 注入”不是一个标准术语。需要拆开：

| 概念 | 作用 |
|---|---|
| **JIT** | 运行时把 IL 编译成本机代码 |
| **Injection** | 把插件程序集送进 PCL 进程 |
| **ReJIT** | 通过 CLR Profiling API 重新编译指定方法，可替换 IL |
| **Harmony / detour** | 运行时替换方法入口或生成替换方法 |
| **Roslyn / CodeDom** | 运行时编译 C#/VB 源码，和 JIT 不是一回事 |

因此，“通过 JIT 做注入式插件”实际是：

```text
注入插件程序集
    → CLR 加载并 JIT 编译插件
    → 用 ReJIT / Harmony / native detour 替换目标方法
```

JIT 本身不会加载插件，也不会从源码重建 PCL。

### 6.2 对 2.99 的可行性

| 目标 | 可行性 | 说明 |
|---|---|---|
| 运行时改 `RestrictMcDownload` | **不可行** | 常量已内联，普通分支被删 |
| 恢复原版下载页 | **高** | `LoaderInit` 还在，可替换为正常 `PageLoaderInit` 调用 |
| 恢复版本修改页 MC 卡片 | **高** | postfix 改 `Visibility` 即可 |
| 恢复资源页整合包安装按钮 | **中** | 需要重建列表项/点击处理 |
| 恢复整合包安装入口 | **中高** | `ModpackInstall()` 只剩 Hint；需要重写入口并调用 `InstallPack*` |
| 完整恢复所有下载 UI | **高成本** | 逐个页面补，版本锁定，维护成本高 |

### 6.3 三条实现路线

| 路线 | 机制 | 优点 | 缺点 |
|---|---|---|---|
| **CLR Profiling API + ReJIT** | 启动时通过 `COR_PROFILER` 加载 profiler，在 JIT 前改 IL 或 `RequestReJIT` | 最接近官方支持；可在 JIT 前改 | 复杂；profiler 必须在 CLR 启动前加载；ReJIT 有方法限制 |
| **托管注入 + Harmony** | 注入 DLL，`Harmony.Patch` 替换方法 | 原型最快；能直接 patch 现有方法 | 需要注入器；杀软误报；内联方法可能失效 |
| **原生 detour / JIT hook** | Hook 方法入口或 `compileMethod` | 直接、底层 | 最脆弱；x64/CLR 版本相关；不建议第一版 |

### 6.4 最小 PoC 思路

目标：只恢复原版下载页，不碰整合包。

```csharp
[HarmonyPatch(typeof(PageDownloadInstall), "LoaderInit")]
static class LoaderInitPatch
{
    static bool Prefix(PageDownloadInstall __instance)
    {
        // 伪代码：
        // 1. 反射取 LoadMinecraft / PanLoad / PanAllBack
        // 2. 反射取 LoadMinecraft_OnFinish
        // 3. 调用 PageLoaderInit(..., ModDownload.DlClientListLoader, onFinish)
        // 4. 返回 false，跳过原来的“关闭下载”逻辑
        return false;
    }
}
```

还需要：

- `PageDownloadModify.LoadInstanceFinished` postfix：把 `CardMinecraft.Visibility` 设为 `Visible`。
- `PageDownloadResourceDetail` 的版本列表方法：恢复整合包安装按钮（工作量较大）。
- `ModpackInstall()` prefix：重新弹文件选择框，并调用 `ModpackInstall(File)` 或 `InstallPack*`。

### 6.5 关键约束

1. **必须在下载页首次初始化前 patch**
   `PageDownloadLeft.PageGet` 是懒加载：

   ```vb
   If FrmDownloadInstall Is Nothing Then FrmDownloadInstall = New PageDownloadInstall
   ```

   如果页面已经创建，`LoaderInit` 已经跑完，再 patch 就晚了。最好用启动器包装/早期注入。

2. **Harmony 对已经内联的方法无效**
   `LoaderInit` 是虚方法，通常不会被内联，但插件应在首次调用前完成 patch。

3. **PCL 更新会破坏插件**
   方法签名、字段、RVA 都可能变；这是版本锁定型插件。

4. **WPF UI 线程和内部成员**
   很多控件是 `Friend`/`Private`，需要反射；UI 操作必须回到 Dispatcher。

5. **杀软与合规**
   进程注入和运行时修改容易被安全软件拦截；公开分发前需评估。

### 6.6 适用结论

方案 C 适合：

- 官方 PCL2 EXE 绝对不能替换。
- 下载 UI 必须出现在 PCL2 内部。
- 可以接受版本锁定、注入风险和较高维护成本。
- 先做原版下载 PoC，再逐步扩展。

不适合：

- 想长期稳定支持多个 PCL 版本。
- 想低成本交付完整整合包/加载器功能。
- 对杀软误报、代码签名、用户支持有较高要求。

---

## 7. 方案对比与推荐

| 维度 | A 源码构建版 | B 外置下载桥 | C 运行时注入插件 |
|---|---|---|---|
| 恢复 MC 下载 | 完整恢复 | 完整（独立工具） | 可恢复，但需逐项补 |
| 是否改 PCL2 | 是（构建期） | 否 | 是（运行时） |
| UI 是否在 PCL2 内 | 是 | 否 | 是 |
| 开发成本 | **最低** | 中 | 最高 |
| 稳定性 | **最高** | 中 | 低 |
| 维护成本 | 低 | 中 | 高 |
| 合规风险 | 中 | 相对低 | 中 |
| 适合场景 | 自用/可控分发 | 插件式产品/多启动器 | 官方 EXE 不可替换且必须内嵌 UI |

### 推荐顺序

1. **方案 A**：默认选择。先改常量、构建、验证原版下载与启动。
2. **方案 B**：如果产品硬性要求“不改 PCL2”，做外置下载桥，先原版后加载器。
3. **方案 C**：只在“官方 EXE 不动 + UI 必须内嵌”同时成立时采用；先做原版下载 PoC，不直接做完整 UI。

### 混合路线

```text
Phase 0：确认合规边界、交付形态、目标版本
Phase 1：方案 A 最小构建，验证下载链路（作为 baseline）
Phase 2：根据产品要求选择：
         ├─ 继续 A：品牌化、测试、发布
         ├─ 转 B：外置下载桥
         └─ 转 C：注入式 PoC
Phase 3：完整功能、测试、签名、文档、回滚
```

---

## 8. 实施计划

### 8.1 Phase 0：决策（0.5 天）

- 确认本地自用还是公开分发。
- 确认是否允许替换 PCL2 EXE。
- 确认下载 UI 是否必须内嵌。
- 确认首版范围：原版 / 加载器 / 整合包。
- 确认是否需要微软登录、CurseForge。
- 完成合规评估。

### 8.2 Phase 1：方案 A 最小验证（0.5-1 天）

- 应用 `0001-unrestrict.patch`。
- Release 构建。
- 启动 PCL2，打开下载页。
- 下载一个原版版本，确认 `versions/<id>/<id>.json`、`.jar`、`libraries`、`assets` 完整。
- 启动游戏，确认 Java/内存/版本隔离正常。

### 8.3 Phase 2：功能验证（1-2 天）

- 测试 BMCLAPI/Mojang 源切换。
- 测试 Forge/Fabric/NeoForge/OptiFine。
- 测试整合包。
- 测试取消、重试、断点续传、403/429、中文路径、只读目录。
- 记录问题，必要时调整补丁。

### 8.4 Phase 3：交付（0.5-1 天）

- 版本名/图标/关于页品牌化。
- 生成发布包和校验值。
- 代码签名（如公开分发）。
- 编写使用说明、回滚说明、合规声明。

### 8.5 如果选方案 B

| 阶段 | 内容 | 预估 |
|---|---|---|
| B1 | PCL 定位 + 版本清单 + 原版安装 | 1-2 天 |
| B2 | Fabric/Forge/NeoForge | 3-5 天 |
| B3 | 整合包 + UI | 5-10 天 |

### 8.6 如果选方案 C

| 阶段 | 内容 | 预估 |
|---|---|---|
| C1 | 注入器 + Harmony + 原版下载页 PoC | 2-4 天 |
| C2 | 版本修改页/资源页入口 | 3-5 天 |
| C3 | 整合包入口 + 稳定性/兼容性 | 5-10 天 |

> 以上为粗略估计，实际取决于注入器选型、PCL 版本数量和测试范围。

---

## 9. 测试与验收

### 9.1 功能验收

- 下载页能正常打开，不再显示“关闭下载”提示。
- 原版版本列表能加载，能选择版本并开始下载。
- 下载完成后 PCL2 能识别版本并启动。
- 加载器/整合包按选定范围正常工作。

### 9.2 兼容性验收

- Windows 10/11 x64。
- .NET Framework 4.8。
- 中文路径、空格路径、非系统盘。
- 代理、限速、断网重试。
- 与官方 PCL2 数据目录隔离（`PCLDebug`）。

### 9.3 负向测试

- 取消下载。
- 磁盘空间不足。
- 只读目录。
- 哈希校验失败。
- BMCLAPI 403/429。
- 下载源切换。
- 页面重复进入/退出。

### 9.4 注入方案额外验收（C）

- PCL 启动不崩溃。
- 下载页首次打开前完成 patch。
- 多次打开/关闭下载页稳定。
- 不与官方更新/其他插件冲突。
- 卸载插件后 PCL 能恢复正常（重启即可）。
- 记录支持的 PCL 版本白名单。

---

## 10. 风险与回滚

| 风险 | 影响 | 应对 |
|---|---|---|
| 合规风险 | 公开分发可能违规 | 先评估；保留合规声明；必要时仅内部使用 |
| 上游变更 | 补丁/插件失效 | A 保存 patch；C 建立版本白名单 |
| 密钥缺失 | 微软登录/CurseForge 不可用 | 配置自己的 Key；或明确不支持 |
| 注入被杀软拦截 | 用户无法运行 | 代码签名、白名单、说明文档；优先选 A/B |
| 版本锁定 | 新 PCL 版本插件失效 | 只支持指定版本；启动时校验版本 |
| 数据损坏 | 下载中断/覆盖 | 临时文件 + 校验 + 原子移动；备份 |
| 无自动更新 | 安全更新滞后 | 手动发布新版本；提供更新说明 |

### 回滚

- 方案 A：保留原版 EXE 备份，替换回即可；数据目录不变。
- 方案 B：删除写入的版本目录；不影响 PCL2 本体。
- 方案 C：退出 PCL2，移除注入器/插件；不修改 EXE 文件，重启即恢复。

---

## 11. 需要确认的问题

1. 目标是本地自用/研究，还是公开分发？
2. 是否允许替换 PCL2 的 EXE？
3. 下载 UI 必须在 PCL2 内部，还是独立工具也可以？
4. 首版范围是只做原版下载，还是必须包含 Forge/Fabric/整合包？
5. 是否需要微软登录、CurseForge 等依赖密钥的功能？
6. 需要支持哪些 PCL 版本？只支持 2.99，还是未来版本也要跟进？
7. 是否接受注入方案的杀软误报和版本锁定风险？

---

## 12. 附录

### 12.1 关键源码位置

| 文件 | 作用 |
|---|---|
| `Modules/ModSecret.vb:18` | `RestrictMcDownload` 常量 |
| `Modules/Minecraft/ModDownload.vb` | 版本列表、客户端 JAR、镜像源 |
| `Modules/Base/ModNet.vb` | `NetFile` / `LoaderDownload` / 下载引擎 |
| `Modules/Minecraft/ModModpack.vb` | 整合包安装 |
| `Pages/PageDownload/PageDownloadInstall.xaml.vb` | 下载安装页 |
| `Pages/PageDownload/PageDownloadModify.vb` | 版本修改页 |
| `Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb` | 资源详情页（实际类名 `PageDownloadResourceDetail`） |
| `FormMain.xaml.vb` | 更新日志、主窗口 |
| `Application.xaml.vb` | 启动流程、嵌入程序集加载 |
| `Pages/PageDownload/PageDownloadLeft.xaml.vb:24` | `PageDownloadInstall` 懒加载 |

### 12.2 IL 检查摘要

- 官方 2.99 `PageDownloadInstall.LoaderInit`：94 字节，无 `DlClientListLoader` 引用。
- 2.14 同方法：62 字节，含 `DlClientListLoader`。
- 2.99 改为 `False` 后重编译：51 字节，含 `DlClientListLoader`。
- 官方 2.99 `ModpackInstall()`：19 字节，仅 `Hint`。
- 改为 `False` 后：123 字节，含 `Dialogs.SelectFile` + `RunInThread`。
- 官方 2.99 `ModpackInstall(File)`：751 字节，压缩包解析保留，安装分发分支被裁掉。
- 改为 `False` 后：993 字节，`Select Case` 跳转表完整。

### 12.3 构建命令速查

```powershell
dotnet restore PCLCS\PCLCS.csproj
msbuild "Plain Craft Launcher 2.sln" -p:Configuration=Release -t:Rebuild
```

### 12.4 注入方案最小验证清单

- [ ] 选择注入方式（Harmony / CLR Profiler / native detour）
- [ ] 在下载页首次创建前注入
- [ ] patch `PageDownloadInstall.LoaderInit`
- [ ] 验证原版下载页出现
- [ ] patch `PageDownloadModify.LoadInstanceFinished`
- [ ] 验证版本修改页 MC 卡片出现
- [ ] 验证原版下载 + 启动
- [ ] 记录支持的 PCL 版本
- [ ] 评估杀软/合规/回滚

### 12.5 版本记录

| 版本 | 日期 | 说明 |
|---|---|---|
| v1 | 2026-10-20 | 初版：源码对比、构建验证、A/B/C 概览 |
| v2 | 2026-10-20 | 整合 JIT/注入分析；补充可行性矩阵、决策树、实施计划、测试与回滚 |
