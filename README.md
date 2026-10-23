# PclDl · 给 PCL2 的下载插件（含 HMCL 适配工具链）

> 非官方项目。与 Plain Craft Launcher 2（作者 龙腾猫跃）、HMCL（作者 huangyuhui）均无关联，
> 未获得其作者授权或背书。本项目不改动启动器本体文件。

## 它是什么

一个**自带下载内核**的 PCL2 插件：装进 PCL2 目录后，下载页换成插件自带的页面与引擎。
不调用、不依赖 PCL 自带的下载代码；PCL 上游把这些代码删掉，插件照样工作。

同时附带一套与启动器无关的引擎/命令行，可给 **HMCL** 或任何标准 `.minecraft` 目录装版本、装整合包、校验修复。

## 快速开始（PCL2）

1. 下载 `PclDl.Setup.exe`，放进 PCL2 所在文件夹（与 `Plain Craft Launcher 2.exe` 同级）。
2. 双击运行一次。它会自动识别 PCL 与版本，写入 `PCL2Plugins\\`，并创建
   `启动 PCL2（插件版）.lnk`。
3. 以后从该快捷方式启动即可，**不需要再运行安装器**（重复运行也是安全的）。

## 快速开始（HMCL）

HMCL 是 Java 程序，无法进程内挂载 .NET 插件。安装器会在目录下生成 `PclDl\\` 工具链：

```bat
PclDl.Cli.exe install 1.20.1 --dir <HMCL 的 .minecraft> --loader fabric
PclDl.Cli.exe install-modpack 包.mrpack --dir <HMCL 的 .minecraft>
PclDl.Cli.exe verify / repair <版本ID> --dir <目录>
```

写出来的是标准布局（`versions/ libraries/ assets/`），HMCL 直接识别。

## 功能

- 原版安装：1.0 ~ 最新，多源竞速（官方 + BMCLAPI）、断点续传、sha1 全量校验、原子落盘
- 加载器：Fabric / Forge / NeoForge / OptiFine（自动选 Java 8/16/17/21）
- 整合包：Modrinth `.mrpack`、CurseForge `manifest.json`（后者需自备 API Key）
- 校验修复：`verify` / `repair`，界面里也有「校验修复」按钮
- 启动自检：`launch-plan` 拼出完整命令行并检查 classpath/natives/assets，`launch` 可直接启动
- 页面：版本列表（正式/快照/远古分组、搜索、已安装标记）、安装选项（加载器/安装路径）、
  进度（速度/剩余时间/当前文件）、任务队列（重试/清理）
- 挂载策略：E1 接管下载页 / E2 自建入口 / E3 独立窗口；**社区资源页不接管**

## 构建

需要 .NET SDK（net48 目标）与 VS2022 的 MSVC（编译 C++/CLI 桥接 DLL）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1      # 产出 dist\ 与单文件 PclDl.Setup.exe
powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -PclDir <PCL目录> -Action install
dotnet run --project src\PclDl.Tests                     # 单元测试
```

## 目录

```
src\PclDl.Core       下载内核（Catalog/Resolver/Planner/Fetcher/Verifier/Loaders/Modpack/Launch）
src\PclDl.Plugin     注入 PCL 后运行的插件（装配、挂载、页面）
src\PclDl.Injector   启动 PCL 并注入桥接 DLL
native\PclDl.Bridge  C++/CLI 桥接（DllMain → 反射调用 PluginEntry.Bootstrap）
src\PclDl.Cli        命令行（install / install-modpack / verify / repair / launch-plan / launch）
src\PclDl.Setup      单文件一键安装器（自动识别 PCL / HMCL）
src\PclDl.Tests      单元测试
```

## 声明与免责

见 [声明.md](声明.md)。要点：非官方、不修改启动器本体、按现状提供、风险自担、
不含任何启动器源码、不绕过正版验证。

## 许可

MIT（见 [LICENSE](LICENSE)）。
