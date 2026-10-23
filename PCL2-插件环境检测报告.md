# PCL2 下载插件 · 环境检测报告

> 检测时间：2026-10-20　检测对象：当前开发机（Windows 11）
> 结论：**开发环境已就绪，无需补装关键组件**；网络类验证已确认可用；唯一可选缺口是 Java 8（旧版本 MC / 旧版 Forge 用）。

---

## 1. 检测结果

| 项目 | 值 | 状态 |
|---|---|---|
| 操作系统 | Windows NT 10.0.26200（Windows 11） | ✅ |
| 架构 | AMD64 | ✅ |
| .NET Framework 运行时 | Release 533509（4.8.1） | ✅ |
| .NET Framework 4.8 目标包 | `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8` | ✅ |
| Visual Studio | 2022 Community（`C:\Program Files\Microsoft Visual Studio\2022\Community`） | ✅ |
| MSBuild | `...\MSBuild\Current\Bin\MSBuild.exe` | ✅ |
| .NET SDK | 9.0.203 | ✅ |
| Java | Microsoft OpenJDK 17.0.14（`C:\Program Files\Microsoft\jdk-17.0.14.7-hotspot`） | ✅ 用于 1.18+ 与 Forge processors |
| git | 2.55.0.windows.5 | ✅ |
| winget | 可用 | ✅ |
| 磁盘 C: | 8.2 GB 空闲 / 200 GB | ⚠️ 偏紧 |
| 磁盘 G: | 21.2 GB 空闲 / 931 GB | ⚠️ 偏紧（构建与测试目录放这里） |

---

## 2. 已完成的补充

| 动作 | 结果 |
|---|---|
| 下载并内置 Newtonsoft.Json 13.0.3 | `libs\Newtonsoft.Json.dll`（711,952 字节），**不走 NuGet，避免还原依赖** |
| 新建环境验证工程 | `_envtest\`（net48 WPF 类库 + net48 控制台） |

### 验证 A：net48 WPF 编译（dotnet SDK 9 + 4.8 目标包）

```text
dotnet build App\App.csproj -c Release
→ App.dll 4,608 字节，0 警告 0 错误
```

要点：SDK 式工程目标 `net48` + `UseWPF` 可直接编译；**`System.Net.Http`、`System.IO.Compression` 必须显式 `<Reference>`**，否则报 CS0234。

### 验证 B：net48 进程内的 HTTPS 访问

控制台程序在真实用户会话中运行，结果：

```text
OK  200 277821  https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json
OK  200 278138  https://launchermeta.mojang.com/mc/game/version_manifest_v2.json
OK  200  39184  https://meta.fabricmc.net/v2/versions/loader
```

即：**引擎所需的三个关键端点全部可用，TLS 正常**。

---

## 3. 重要环境约束（会影响开发流程）

1. **DSH 沙箱内 SChannel 不可用**：git / curl / PowerShell 直连 HTTPS 会报
   `SEC_E_NO_CREDENTIALS (0x8009030e)`。git 可用 `-c http.sslBackend=openssl` 绕过，
   但 .NET 程序无法绕过。
   → **所有联网操作（还原、下载、验证）统一用 host_exec 在宿主会话执行**，沙箱只做编译与离线测试。
   这条约束仅存在于沙箱，真实用户运行 PCL 插件时不受影响（验证 B 已证明）。

2. **C 盘空间偏紧（8.2 GB）**：所有工程、产物、测试用 `.minecraft` 一律放 G 盘。

3. **PowerShell 为 5.1**（无 `pwsh`）：脚本不要用 `if()` 表达式等 PS7 语法。

4. **WMI/CIM 在沙箱内被拒**：OS 版本、磁盘容量类查询走 host_exec。

---

## 4. 缺口评估

| 缺口 | 是否阻塞 | 处理 |
|---|---|---|
| Java 8（MC ≤ 1.16.5、旧版 Forge） | 不阻塞 MVP | 需要测旧版本时用 winget 装 Microsoft OpenJDK 8 或 Adoptium 8 |
| nuget.exe | 不阻塞 | 不需要，已改为内置 DLL + SDK 式工程 |
| 7-Zip | 不阻塞 | 不需要，`System.IO.Compression` 覆盖 zip（Forge installer jar 也是 zip） |
| .NET Framework 4.8 Developer Pack | 不阻塞 | 目标包已存在，能编译 |
| 代码签名证书 | 后期 | 仅公开分发时需要 |

---

## 5. 测试用 PCL 实例

| 版本 | 路径 | 状态 |
|---|---|---|
| 2.99.0.0（受限） | `PCL2-source\Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe`（5.63 MB） | ✅ 已构建 |
| 2.14.0.0（正常） | `PCL2-source-2.14.0.0\...` | ❌ 未构建（验证 D0 判定时需要） |

---

## 6. 下一步

1. 建 `src\` 骨架（`PclDl.Core` / `PclDl.Plugin` / `PclDl.Bootstrap` / `PclDl.Launcher`），
   统一 SDK 式工程、目标 `net48`、产物输出到 `dist\`。
2. M1：`Catalog` + `Resolver` + `Planner` + `Fetcher`，控制台跑通 1.20.1 完整安装到 G 盘测试目录。
3. 需要时再补 Java 8，并构建 2.14 版 EXE 作为 D0 对照。
