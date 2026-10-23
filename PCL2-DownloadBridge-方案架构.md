# PCL2 下载能力方案架构（评审稿）

> 依据：工作区中的 `PCL2-source`（2.99.0.0）与 `PCL2-source-2.14.0.0` 源码，以及本机已具备的 VS2022 / MSBuild / .NET Framework 4.8 环境。
>
> 结论先行：**优先走“源码构建版”**。2.99 的下载链路本身是完整的，只是被一个编译期常量 `RestrictMcDownload = True` 在 Release 编译时裁掉了 UI 入口；改一行源码重新编译即可恢复，不需要 IL 补丁、进程注入或重写下载引擎。若必须保留官方 PCL2 不动，则做“外置下载桥”，而不是注入。

---

## 1. 现状核验

### 1.1 两个源码版本

| 目录 | 版本 | 提交 | 说明 |
|---|---|---|---|
| `PCL2-source` | 2.99.0.0 | `6b9a50d` | 更新版本，带 MC 下载限制 |
| `PCL2-source-2.14.0.0` | 2.14.0.0 | `ade6e04` | 旧版本，MC 下载正常 |

两者源码只有 **11 个文件**不同，核心差异是 2.99 增加了：

- `Modules/ModSecret.vb:18` 的 `Public Const RestrictMcDownload As Boolean = True`
- 7 处使用该常量的入口限制
- 版本号、更新日志、关于页赞助者名单
- `MeloongCore` 子模块的回退

**下载链路本身没有删**：`ModDownload.vb`、`ModNet.vb`、`ModModpack.vb`、`PCLCS` 的下载/元数据能力在 2.99 中仍然完整。

### 1.2 本机构建环境已可用

实际验证结果：

- Visual Studio 2022 Community 已安装
- MSBuild 17.13.19 可用
- .NET Framework 4.8 Targeting Pack 已安装
- .NET SDK 9.0.203 可用
- `dotnet restore PCLCS\PCLCS.csproj` 成功
- `msbuild "Plain Craft Launcher 2.sln" -p:Configuration=Release` 成功
- 输出：`Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe`（约 5.9 MB，依赖已嵌入）
- `Resources\Help.zip` 已存在，不需要再克隆 PCL2Help

### 1.3 关键 IL 证据：官方 2.99 不能靠“翻转分支”恢复

对官方 2.99 EXE 和本地 Release 构建做了 IL 检查：

| 方法 | 2.99（`RestrictMcDownload = True`） | 2.14 | 2.99 改为 `False` 后重新编译 |
|---|---|---|---|
| `PageDownloadInstall.LoaderInit` | 94 字节，**没有 `DlClientListLoader` 引用** | 62 字节，有 `DlClientListLoader` | 51 字节，有 `DlClientListLoader` |
| `ModpackInstall()` | 19 字节，只调用 `Hint` 后返回 | 正常选择文件 | 123 字节，调用 `Dialogs.SelectFile` + `RunInThread` |
| `ModpackInstall(File)` | 保留压缩包识别，但安装分支被裁掉 | 完整 | 993 字节，`Select Case` 跳转表完整 |
| `PageDownloadModify.LoadInstanceFinished` | 只保留 `Visibility.Collapsed` | 正常三元分支 | 恢复 `Visible` |

原因：`RestrictMcDownload` 是 `Public Const Boolean = True`，VB 编译器在 Release + Optimize 下会做常量折叠和不可达代码消除。**官方 2.99 的普通下载 UI/安装分支根本不在 EXE 里**，所以：

- 把 `ldc.i4.1` 改成 `ldc.i4.0` 没有用；
- 翻转 `brtrue/brfalse` 也没有用；
- “分支目标重定向”也没有目标可重定向。

这正是 DeepSeek 方案里 S1“IL 翻转”对 2.99 不成立的地方。对 2.14 的 EXE 倒是可以补丁，但 2.14 本来就能下载，没有补丁意义。

---

## 2. 方案 A（推荐）：源码构建版 PCL2-DL

### 2.1 定位

在 2.99 源码基础上做一个**最小补丁构建**，恢复 PCL2 原有的 MC 下载页面和下载能力。产物是一个可独立运行的 PCL2 EXE，不依赖注入器、外部启动器或额外运行时。

### 2.2 分层架构

```text
┌─────────────────────────────────────────────────────┐
│ UI 层（PCL2 现有）                                   │
│  PageDownloadInstall / PageDownloadLeft              │
│  PageDownloadModify / PageDownloadCompDetail         │
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

### 2.3 改动清单

| 文件 | 改动 | 必要性 |
|---|---|---|
| `Plain Craft Launcher 2/Modules/ModSecret.vb:18` | `RestrictMcDownload = False` | **必须** |
| `Plain Craft Launcher 2/FormMain.xaml.vb:14-16` | 删除/改写“删除 MC 下载功能”的更新日志条目 | 建议，否则日志与实际功能不符 |
| `Plain Craft Launcher 2/FormMain.xaml.vb:106-108` | 同上（Snapshot 分支） | 建议 |
| `Plain Craft Launcher 2/Modules/Base/ModBase.vb` | 可选：版本名加 `-DL` 后缀，避免和官方 2.99 混淆 | 可选 |
| `Plain Craft Launcher 2/My Project/AssemblyInfo.vb` | 可选：同步程序集版本 | 可选 |
| `ModSecret.vb` 的 OAuth/CurseForge 字段 | 如需微软登录/CurseForge，设置环境变量或填入自己的 Key | 按需 |

> 建议保留 7 处 `If RestrictMcDownload Then` 守卫，只把常量改为 `False`。这样补丁最小，未来切换/对比也方便。

### 2.4 构建流程

```powershell
# 1. 还原 NuGet
dotnet restore PCLCS\PCLCS.csproj

# 2. Release 重建
msbuild "Plain Craft Launcher 2.sln" -p:Configuration=Release -t:Rebuild

# 3. 产物
# Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe
```

部署：把 EXE 放到一个有写权限的目录即可。PCL 会在同级创建 `PCL\` 和 `.minecraft\`，不需要安装器。

### 2.5 测试矩阵

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

### 2.6 风险

| 风险 | 说明 | 应对 |
|---|---|---|
| 合规风险 | 上游把限制描述为“为遵守相关法律法规”；恢复下载可能违背其合规意图 | 仅本地研究/自用；公开分发前做合规评估，不要默认可以公开传播 |
| 密钥缺失 | 开源版 `OAuthClientId`、`CurseForgeAPIKey` 为空 | MC 下载不需要；微软登录/CurseForge 需自行配置环境变量 |
| 上游漂移 | 2.99 是特殊版本，后续可能变化 | 补丁保持最小，记录为 `patches/0001-unrestrict.patch`，便于 rebase |
| 版本混淆 | 版本号仍是 2.99.0.0 | 显示名加 `-DL` 后缀；`RegFolder = PCLDebug` 已与官方隔离 |
| 无自动更新 | 开源版没有更新器 | 手动重建/替换；安全更新需重新构建 |
| 分发 | 无签名，SmartScreen 可能拦截 | 如公开分发，使用代码签名证书 |

---

## 3. 方案 B（不改 PCL2）：外置下载桥

### 3.1 定位

如果目标是“插件”而不是“改造版启动器”，做一个独立下载器：它把原版/加载器/整合包安装到 PCL2 的 `.minecraft` 目录，PCL2 只负责识别和启动。官方 PCL2 完全不动。

### 3.2 架构

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

### 3.3 数据流

1. 定位 PCL2 与游戏目录。
2. 拉取版本清单，用户选择 MC 版本和加载器。
3. 解析版本 JSON，生成文件清单（客户端 JAR、libraries、assets、natives）。
4. 下载到临时目录，校验 SHA1/大小，原子移动。
5. 按标准布局写入 `.minecraft`：
   - `versions/<id>/<id>.json`
   - `versions/<id>/<id>.jar`
   - `libraries/...`
   - `assets/indexes/...`、`assets/objects/...`
6. 可选写入 `versions/<id>/PCL/Setup.ini`（Logo、Info、VersionVanilla 等展示信息）。
7. 启动 PCL2，PCL2 扫描 `versions/` 后即可看到并启动新版本。

### 3.4 复用与实现建议

- 复用 PCL2 的 `DlSource*` 规则：Mojang → BMCLAPI 的 URL 映射、`FileChecker` 的大小/SHA1 语义。
- 原版安装可考虑 `CmlLib.Core`，减少自己处理 `inheritsFrom`、natives、asset index 的工作量。
- Forge/NeoForge/OptiFine 安装器复杂，建议先做原版 + Fabric，再逐步扩展。
- 整合包安装是最大工作量；如果只需要“下载游戏”，不要一开始就做整合包。

### 3.5 优缺点

| 优点 | 缺点 |
|---|---|
| 官方 PCL2 不动，合规风险相对低 | UI 在 PCL2 外面，不是真正的“内置页面” |
| 可作为独立工具分发 | 需要自己实现/封装下载与安装逻辑 |
| 可支持多个启动器 | 加载器/整合包安装复杂度高 |
| 不受 PCL2 版本升级影响 | 用户需要运行两个程序 |

---

## 4. 方案 C（不推荐）：二进制补丁 / 进程注入

### 4.1 对官方 2.99 做 IL 补丁：不可行

如 1.3 所述，Release 编译已经把普通下载分支裁掉。官方 2.99 的 `PageDownloadInstall.LoaderInit` 只有 94 字节，没有 `DlClientListLoader`；`ModpackInstall()` 只有 19 字节。没有代码可以“翻转”。

### 4.2 对 2.14 做补丁：没必要

2.14 的 EXE 本身就有完整下载代码，直接用 2.14 即可；补丁只会增加风险。

### 4.3 对官方 2.99 做注入：成本最高

只有在“必须保留官方 PCL2 EXE 且必须把下载 UI 放进 PCL2 内部”时才考虑。需要：

- 把托管程序集注入 WPF 进程；
- 在 UI 线程创建窗口/页面；
- 通过反射访问 `PageDownloadInstall` 的控件（很多控件是 `Friend`）；
- 调用 `ModDownload` / `ModNet` 的公开 API，或自己实现下载；
- 处理单文件 EXE 的嵌入程序集、AppDomain 解析、版本差异、杀软误报。

这是一个研究型项目，不适合作为第一版产品架构。

---

## 5. 方案对比

| 维度 | A 源码构建版 | B 外置下载桥 | C 补丁/注入 |
|---|---|---|---|
| 恢复 MC 下载 | 完整恢复 | 完整（独立工具） | 2.99 补丁不可行；注入可行但复杂 |
| 是否改 PCL2 | 是（一行 + 构建） | 否 | 是（运行时） |
| UI 是否在 PCL2 内 | 是 | 否 | 是 |
| 开发成本 | **最低** | 中 | 最高 |
| 稳定性 | **最高**（复用原逻辑） | 中 | 低 |
| 维护成本 | 低（补丁小） | 中（跟随 API） | 高（跟随版本/UI） |
| 合规风险 | 中（修改启动器） | 相对低 | 中 |
| 适合场景 | 自用/可控分发 | 插件式产品/多启动器 | 官方 EXE 不可替换且必须内嵌 UI |

---

## 6. 建议的推进顺序

1. **先做 A 的 M1**：改一行、构建、验证原版下载与启动。这一步可以直接确认“下载链路是否完整”。
2. **再做 A 的 M2**：验证 Forge/Fabric/NeoForge/OptiFine、整合包、镜像源、取消/续传。
3. **如果产品要求“不能改 PCL2”**：并行启动 B，先做原版下载桥，再扩展加载器。
4. **C 只作为最后手段**：先做注入可行性验证（单独窗口 + 调用公开下载 API），不要一上来做 UI 接管。

### 里程碑（A）

| 阶段 | 内容 | 预估 |
|---|---|---|
| M1 | 改常量、构建、原版下载 + 启动 | 0.5 天 |
| M2 | 加载器、整合包、镜像源、异常路径 | 1-2 天 |
| M3 | 品牌/版本号、打包、签名、回滚说明 | 0.5 天 |

### 里程碑（B）

| 阶段 | 内容 | 预估 |
|---|---|---|
| M1 | PCL 定位 + 版本清单 + 原版安装 | 1-2 天 |
| M2 | Fabric/Forge/NeoForge | 3-5 天 |
| M3 | 整合包 + UI | 5-10 天 |

---

## 7. 需要确认的问题

1. 目标是**本地自用/研究**，还是要**公开分发**？
2. 是否允许替换 PCL2 的 EXE？还是必须保留官方 EXE？
3. 下载 UI 必须在 PCL2 内部，还是独立工具也可以？
4. 首版范围是只做**原版下载**，还是必须包含 Forge/Fabric/整合包？
5. 是否需要微软登录、CurseForge 搜索等依赖密钥的功能？

确认这 5 点后，就可以在 A/B/C 中选定一条，把上面的架构落成具体的项目目录、接口定义和任务拆解。
