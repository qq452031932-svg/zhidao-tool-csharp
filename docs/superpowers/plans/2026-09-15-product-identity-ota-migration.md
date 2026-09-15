# 知道综合工具实体改名与 OTA 迁移 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将真实 Windows 主程序、文件元数据、窗口/任务栏/安装器图标统一为“知道综合工具”，同时让线上 1.1.0 用户安全升级到 1.1.1 且保留原数据与卡密状态。

**Architecture:** 保留 `SuperPan.BaiduBatchTransfer` 包 ID、现有数据目录、单实例互斥体和卡密产品 ID作为稳定兼容身份；新增集中品牌契约，把 .NET 主程序集输出改为 `知道综合工具.exe/.dll`。1.1.1 及后续受支持版本同时打包受限的旧名兼容启动器；Velopack 主入口、快捷方式和新安装根启动器指向中文主程序，并通过 Setup/Portable、Delta/Full 的真实隔离升级验收。

**Tech Stack:** .NET 8、WPF、Velopack 1.2.0（先验证，必要时成对升级 SDK/CLI）、PowerShell、xUnit、Windows Authenticode。

---

## 文件结构与职责

- `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`：中文程序集名、PE 产品元数据、应用图标、兼容启动器发布内容。
- `src/BaiduBatchTransfer.App/Branding/ProductIdentity.cs`：运行时使用的显示名、主 EXE 名和旧兼容 EXE 名，不承载 OTA/卡密稳定身份。
- `src/BaiduBatchTransfer.App/Assets/Branding/知道综合工具.png`：规范化的原始方形品牌图。
- `src/BaiduBatchTransfer.App/Assets/Branding/知道综合工具.ico`：多分辨率 Windows 图标。
- `scripts/Convert-ProductIcon.ps1`：从仓库内规范 PNG 确定性生成 ICO 并验证层级。
- `src/BaiduBatchTransfer.LegacyLauncher/BaiduBatchTransfer.LegacyLauncher.csproj`：输出旧文件名 `BaiduBatchTransfer.App.exe` 的无业务兼容入口。
- `src/BaiduBatchTransfer.LegacyLauncher/LegacyLaunchPlan.cs`：参数、路径和等待策略的纯逻辑，便于单元测试。
- `src/BaiduBatchTransfer.LegacyLauncher/Program.cs`：验证同目录目标、启动中文主程序并按 Velopack 生命周期语义返回。
- `tests/BaiduBatchTransfer.App.Tests/Branding/ProductIdentityTests.cs`：稳定身份与可见品牌契约测试。
- `tests/BaiduBatchTransfer.App.Tests/Branding/ProductIconTests.cs`：PNG/ICO 尺寸、像素来源和项目引用测试。
- `tests/BaiduBatchTransfer.App.Tests/Branding/LegacyLaunchPlanTests.cs`：参数转发、等待条件和路径拒绝测试。
- `tests/BaiduBatchTransfer.App.Tests/Updates/ProductIdentityBuildTests.cs`：实际构建产物名和 PE 元数据测试。
- `scripts/build_local_license_acceptance.ps1`、`scripts/test_local_license_stack.ps1`、`scripts/build_production_license_clients.ps1`、`scripts/test_license_spki_rotation.ps1`：消费中文输出文件名，但不改变卡密信任配置。
- `scripts/build_ota_release.ps1`：以中文主 EXE、标题和 ICO 调用 Velopack，同时打包兼容入口。
- `scripts/Test-OtaUploadBundle.ps1`：验证中文主程序、兼容入口、元数据、签名和固定包身份。
- `scripts/Test-VelopackExecutableRenameMigration.ps1`：在隔离 Windows 环境记录根启动器、快捷方式和自动重启迁移结果。
- `tests/BaiduBatchTransfer.App.Tests/Updates/OtaReleaseScriptTests.cs`、`OtaProductionReleaseScriptTests.cs`、`ProductionLicenseBuildScriptTests.cs`：发布链脚本契约回归。
- `docs/testing/2026-09-15-product-identity-ota-migration-handoff.md`：本地验证证据、仍需用户执行的隔离/线上验收说明。

### Task 1: 锁定产品品牌与稳定身份契约

**Files:**
- Create: `src/BaiduBatchTransfer.App/Branding/ProductIdentity.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Branding/ProductIdentityTests.cs`
- Modify: `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`

- [ ] **Step 1: 写失败测试**

测试必须断言：

```csharp
Assert.Equal("知道综合工具", ProductIdentity.DisplayName);
Assert.Equal("知道综合工具.exe", ProductIdentity.MainExecutableName);
Assert.Equal("知道综合工具.dll", ProductIdentity.MainAssemblyName);
Assert.Equal("BaiduBatchTransfer.App.exe", ProductIdentity.LegacyExecutableName);
Assert.Equal("SuperPan.BaiduBatchTransfer", ProductIdentity.PackageId);
Assert.Equal("BaiduBatchTransfer", ProductIdentity.DataDirectoryName);
Assert.Equal("8270e8d7-1791-487d-ad76-0da7a0cbed12", ProductIdentity.LicenseProductId);
```

结构测试同时读取 csproj，要求 `AssemblyName`、`AssemblyTitle`、`Product`、`Description` 明确设置为中文品牌，但 `PackageId` 和 `LicenseProductId` 未变化。

- [ ] **Step 2: 验证测试先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ProductIdentityTests --no-restore`

Expected: FAIL，原因是 `ProductIdentity` 或项目元数据尚不存在。

- [ ] **Step 3: 添加最小品牌契约与项目元数据**

`ProductIdentity.cs` 使用 `internal static class` 和编译期常量。csproj 添加：

```xml
<AssemblyName>知道综合工具</AssemblyName>
<AssemblyTitle>知道综合工具</AssemblyTitle>
<Product>知道综合工具</Product>
<Description>百度网盘批量管理工作台</Description>
```

不得改变 `RootNamespace`、默认命名空间、`PackageId`、本地数据目录、互斥体或授权产品 ID。

- [ ] **Step 4: 运行定向测试**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ProductIdentityTests --no-restore`

Expected: PASS。

- [ ] **Step 5: 提交**

```powershell
git add src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj src/BaiduBatchTransfer.App/Branding/ProductIdentity.cs tests/BaiduBatchTransfer.App.Tests/Branding/ProductIdentityTests.cs
git commit -m "feat: define product identity contract"
```

### Task 2: 固化用户提供的图标资源

**Files:**
- Create: `src/BaiduBatchTransfer.App/Assets/Branding/知道综合工具.png`
- Create: `src/BaiduBatchTransfer.App/Assets/Branding/知道综合工具.ico`
- Create: `scripts/Convert-ProductIcon.ps1`
- Create: `tests/BaiduBatchTransfer.App.Tests/Branding/ProductIconTests.cs`
- Modify: `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`

- [ ] **Step 1: 写失败的资源测试**

测试读取 PNG/ICO 并断言：PNG 为 640×640；ICO 包含 16、24、32、48、64、128、256 层级；256 层与规范 PNG 按同一缩放算法得到的像素哈希一致；csproj 设置 `ApplicationIcon` 并把 PNG 作为 WPF Resource。

- [ ] **Step 2: 验证测试先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ProductIconTests --no-restore`

Expected: FAIL，原因是品牌资源不存在。

- [ ] **Step 3: 保存规范源并实现确定性转换**

从用户提供的 `C:\Users\45203\AppData\Local\Temp\codex-clipboard-178c6012-31da-4e0a-a819-dc3a12c2756c.jpg` 仅做无内容重绘的 PNG 转码；脚本对输入 SHA-256、正方形尺寸、ICO 层级和输出 SHA-256 写入控制台。生成结果提交到仓库，构建不依赖临时文件。

csproj 添加：

```xml
<ApplicationIcon>Assets\Branding\知道综合工具.ico</ApplicationIcon>
```

以及 WPF Resource 条目。

- [ ] **Step 4: 验证图标资源**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Convert-ProductIcon.ps1 -VerifyOnly`

Expected: exit 0，列出全部 7 个尺寸且哈希与仓库资源一致。

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ProductIconTests --no-restore`

Expected: PASS。

- [ ] **Step 5: 提交**

```powershell
git add src/BaiduBatchTransfer.App/Assets/Branding src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj scripts/Convert-ProductIcon.ps1 tests/BaiduBatchTransfer.App.Tests/Branding/ProductIconTests.cs
git commit -m "feat: add product icon assets"
```

### Task 3: 统一 WPF 窗口名称与图标

**Files:**
- Modify: `src/BaiduBatchTransfer.App/App.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Accounts/BaiduLoginWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Acceptance/BaiduAcceptanceProbeWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceConfirmationWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebAutomationWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/ExcelColumnMappingWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/ExistingShareDecisionWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserLoginWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferFolderNameWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryWindow.xaml`
- Create: `tests/BaiduBatchTransfer.App.Tests/Branding/WindowBrandingTests.cs`

- [ ] **Step 1: 写失败的 XAML 结构测试**

枚举全部 `<Window>` 文件，要求每个窗口通过共享资源引用 `/Assets/Branding/知道综合工具.png`；主窗口标题必须为“知道综合工具”。禁止重新引入“百度批量转存”作为产品标题。

- [ ] **Step 2: 验证测试先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~WindowBrandingTests --no-restore`

Expected: FAIL，列出尚未使用共享图标的窗口。

- [ ] **Step 3: 添加共享 BitmapImage 并应用到所有窗口**

在 `App.xaml` 创建冻结的 `BitmapImage` 资源；每个窗口设置 `Icon="{StaticResource ProductWindowIcon}"`。不改变窗口业务布局。

- [ ] **Step 4: 运行测试与构建**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~WindowBrandingTests --no-restore`

Expected: PASS。

Run: `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug --no-restore`

Expected: 0 errors，输出 `知道综合工具.exe`。

- [ ] **Step 5: 提交**

```powershell
git add src/BaiduBatchTransfer.App tests/BaiduBatchTransfer.App.Tests/Branding/WindowBrandingTests.cs
git commit -m "feat: apply product branding to windows"
```

### Task 4: 实现受限的旧名兼容启动器

**Files:**
- Create: `src/BaiduBatchTransfer.LegacyLauncher/BaiduBatchTransfer.LegacyLauncher.csproj`
- Create: `src/BaiduBatchTransfer.LegacyLauncher/LegacyLaunchPlan.cs`
- Create: `src/BaiduBatchTransfer.LegacyLauncher/Program.cs`
- Modify: `BaiduBatchTransfer.sln`
- Modify: `tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj`
- Create: `tests/BaiduBatchTransfer.App.Tests/Branding/LegacyLaunchPlanTests.cs`

- [ ] **Step 1: 写失败的启动计划测试**

覆盖：普通参数不等待；任一 `--veloapp-` 生命周期参数要求等待并传播退出码；参数保持元素边界；目标只能是同一真实目录内的 `知道综合工具.exe`；缺失文件、目录逃逸和重解析点目标均失败关闭。

- [ ] **Step 2: 验证测试先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~LegacyLaunchPlanTests --no-restore`

Expected: FAIL，兼容启动器项目尚不存在。

- [ ] **Step 3: 实现最小兼容入口**

兼容项目必须：

```xml
<AssemblyName>BaiduBatchTransfer.App</AssemblyName>
<OutputType>WinExe</OutputType>
<TargetFramework>net8.0-windows</TargetFramework>
<SelfContained>true</SelfContained>
```

`Program` 使用 `Environment.ProcessPath` 确定入口目录，解析中文目标的最终路径，拒绝越界/重解析点，通过 `ProcessStartInfo.ArgumentList` 转发参数。生命周期调用 `WaitForExit()` 并设置 `Environment.ExitCode`；普通启动创建进程后立即返回。

- [ ] **Step 4: 运行定向测试和发布构建**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~LegacyLaunchPlanTests --no-restore`

Expected: PASS。

Run: `dotnet publish src/BaiduBatchTransfer.LegacyLauncher/BaiduBatchTransfer.LegacyLauncher.csproj -c Release -r win-x64 --self-contained true -o .artifacts/legacy-launcher-check`

Expected: 生成 `BaiduBatchTransfer.App.exe`，不包含主程序业务程序集。

- [ ] **Step 5: 提交**

```powershell
git add BaiduBatchTransfer.sln src/BaiduBatchTransfer.LegacyLauncher tests/BaiduBatchTransfer.App.Tests
git commit -m "feat: add legacy executable migration launcher"
```

### Task 5: 迁移本地卡密构建链中的产物名

**Files:**
- Modify: `scripts/build_local_license_acceptance.ps1`
- Modify: `scripts/test_local_license_stack.ps1`
- Modify: `scripts/build_production_license_clients.ps1`
- Modify: `scripts/test_license_spki_rotation.ps1`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Updates/ProductionLicenseBuildScriptTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Updates/LicenseLocalStackScriptTests.cs`

- [ ] **Step 1: 更新测试预期并确认失败**

测试要求脚本加载/检查 `知道综合工具.dll/.exe`，但仍验证相同的生产服务 URL、产品 ID、签名公钥和 SPKI pin。

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~ProductionLicenseBuildScriptTests|FullyQualifiedName~LicenseLocalStackScriptTests" --no-restore`

Expected: FAIL，脚本仍引用旧主程序集产物。

- [ ] **Step 2: 只替换产物路径，不改变信任参数**

使用脚本局部常量 `$mainExecutableName`、`$mainAssemblyName`；边界验证继续覆盖中文主程序，不把兼容入口当作授权主程序。

- [ ] **Step 3: 运行脚本契约测试**

Run: 同 Step 1。

Expected: PASS。

- [ ] **Step 4: 提交**

```powershell
git add scripts/build_local_license_acceptance.ps1 scripts/test_local_license_stack.ps1 scripts/build_production_license_clients.ps1 scripts/test_license_spki_rotation.ps1 tests/BaiduBatchTransfer.App.Tests/Updates
git commit -m "build: migrate license client artifact names"
```

### Task 6: 迁移 OTA 打包参数并加入兼容入口

**Files:**
- Modify: `scripts/build_ota_release.ps1`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaReleaseScriptTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaProductionReleaseScriptTests.cs`

- [ ] **Step 1: 写失败的发布脚本测试**

测试要求生产与 acceptance 路径都向 vpk 传入：

```text
--mainExe 知道综合工具.exe
--packTitle 知道综合工具
--icon <仓库规范 ICO>
```

并断言 publish 目录同时含中文 EXE/DLL 与 `BaiduBatchTransfer.App.exe` 兼容入口；固定包 ID、通道、Setup/Portable 别名保持原值。

- [ ] **Step 2: 验证测试先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~OtaReleaseScriptTests|FullyQualifiedName~OtaProductionReleaseScriptTests" --no-restore`

Expected: FAIL，当前脚本仍把旧 EXE 作为 `mainExe`。

- [ ] **Step 3: 修改生产和 acceptance 打包流水线**

先分别发布主程序与兼容启动器，再仅复制兼容 EXE 到主 publish 目录。元数据反射从 `知道综合工具.dll` 读取。所有 vpk 参数继续以数组传递；保留 `SuperPan.BaiduBatchTransfer`、`SuperPan.BaiduBatchTransfer.OtaAcceptance` 及固定别名。

- [ ] **Step 4: 运行发布脚本测试**

Run: 同 Step 2。

Expected: PASS，包括 fake-toolchain 完整路径。

- [ ] **Step 5: 提交**

```powershell
git add scripts/build_ota_release.ps1 tests/BaiduBatchTransfer.App.Tests/Updates/OtaReleaseScriptTests.cs tests/BaiduBatchTransfer.App.Tests/Updates/OtaProductionReleaseScriptTests.cs
git commit -m "build: package renamed application entry point"
```

### Task 7: 强化上传包验证器

**Files:**
- Modify: `scripts/Test-OtaUploadBundle.ps1`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaProductionReleaseScriptTests.cs`

- [ ] **Step 1: 添加失败夹具与断言**

覆盖缺少中文 EXE/DLL/deps/runtimeconfig、mainExe 仍为旧名、缺少兼容入口、品牌元数据错误、图标缺失、任一 EXE 签名/发布者/架构不一致、Delta 未表达旧名删除与中文名新增等情况。

- [ ] **Step 2: 验证新增测试先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~OtaProductionReleaseScriptTests --no-restore`

Expected: FAIL，当前验证器接受旧布局或未验证兼容入口。

- [ ] **Step 3: 更新验证器**

Portable 检查 `current/知道综合工具.*` 和旧兼容 EXE；Full 检查 `lib/app/知道综合工具.*` 和旧兼容 EXE；nuspec `mainExe` 必须为中文名。签名验证对两个 EXE 使用相同允许发布者 SPKI；根启动器检查包含中文入口，固定外层资产名保持不变。

- [ ] **Step 4: 运行验证器测试**

Run: 同 Step 2。

Expected: PASS。

- [ ] **Step 5: 提交**

```powershell
git add scripts/Test-OtaUploadBundle.ps1 tests/BaiduBatchTransfer.App.Tests/Updates/OtaProductionReleaseScriptTests.cs
git commit -m "test: enforce renamed OTA package layout"
```

### Task 8: 建立 Velopack 主程序改名的真实迁移门

**Files:**
- Create: `scripts/Test-VelopackExecutableRenameMigration.ps1`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/VelopackExecutableRenameMigrationScriptTests.cs`
- Conditional modify only if the pinned pair fails: `.config/dotnet-tools.json`
- Conditional modify only if the pinned pair fails: `Directory.Packages.props`
- Conditional modify only if the pinned pair fails: `src/BaiduBatchTransfer.App/packages.lock.json`

- [ ] **Step 1: 写失败的脚本结构测试**

测试要求脚本拒绝非隔离用户环境，记录安装根目录、`current`、桌面/开始菜单快捷方式、卸载项与重启进程；分别支持 `-Path Delta`、`-Path Full`、`-AssetKind Setup|Portable`；默认不访问生产源。

- [ ] **Step 2: 验证结构测试先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~VelopackExecutableRenameMigrationScriptTests --no-restore`

Expected: FAIL，脚本尚不存在。

- [ ] **Step 3: 实现隔离验收脚本**

脚本参数必须显式提供旧资产目录、新候选目录、证据输出目录和隔离确认开关。它只安装/展开传入资产，不连接生产服务，不删除非自身创建目录，并输出 JSON 证据与文件哈希。

- [ ] **Step 4: 用当前固定 Velopack 1.2.0 运行最小双版本样例**

Run（必须在一次性 Windows Sandbox/VM 内）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-VelopackExecutableRenameMigration.ps1 -OldAssets <1.1.0-assets> -NewAssets <1.1.1-candidate> -AssetKind Setup -Path Delta -EvidenceDirectory <temp-evidence> -IUnderstandThisIsAnIsolatedWindowsProfile
```

Expected: 中文根启动器、`current/知道综合工具.exe`、中文快捷方式、中文重启进程全部存在，旧入口可转发。

- [ ] **Step 5: 若 1.2.0 失败，成对升级并重新锁定**

只在 Step 4 有证据失败时执行：选择最小的、已验证修复改名启动器问题的 Velopack 版本；同时更新 CLI 与 SDK 包版本，执行 `dotnet restore --force-evaluate` 更新 lock file，再运行全部 OTA 测试。不得只升级 CLI 或只升级 SDK。

- [ ] **Step 6: 分别完成 Setup Delta、Setup Full、Portable 升级**

Expected: 三条路径均通过；Portable 结果不得替代 Setup 快捷方式证据。

- [ ] **Step 7: 提交脚本、测试与必要的版本锁定**

```powershell
git add scripts/Test-VelopackExecutableRenameMigration.ps1 tests/BaiduBatchTransfer.App.Tests/Updates/VelopackExecutableRenameMigrationScriptTests.cs .config/dotnet-tools.json Directory.Packages.props src/BaiduBatchTransfer.App/packages.lock.json
git commit -m "test: gate executable rename migration"
```

### Task 9: 构建并验证 1.1.1 本地候选包

**Files:**
- Create: `docs/testing/2026-09-15-product-identity-ota-migration-handoff.md`
- Modify as evidence requires: `docs/testing/2026-09-15-product-identity-ota-migration-handoff.md`

- [ ] **Step 1: 运行格式与全量测试**

Run: `git diff --check`

Expected: no output。

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore`

Expected: 全部通过；仅允许已有、注明环境原因的 skip。

- [ ] **Step 2: 运行 Debug 构建并检查实际产物**

Run: `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug --no-restore`

Expected: `bin/Debug/net8.0-windows/知道综合工具.exe` 和同名 DLL/deps/runtimeconfig 存在，旧业务 EXE 不存在。

- [ ] **Step 3: 构建 1.1.1 生产等价候选**

使用现有生产构建入口、已有签名发布者和 1.1.0 previous release ZIP；版本严格为 `1.1.1`，输出到新的 `.artifacts` 子目录。不得覆盖 1.1.0 归档，不得上传服务器。

Expected: Setup、Portable、1.1.1 Full、1.1.1 Delta、签名 feed/manifest 全部生成并通过 `Test-OtaUploadBundle.ps1`。

- [ ] **Step 4: 验证 PE 元数据、图标与签名**

检查主 EXE 的 `ProductName/FileDescription/ProductVersion/FileVersion`，提取 16/32/48/256 图标做哈希对比；检查主 EXE 与兼容入口使用同一受信发布者签名。

- [ ] **Step 5: 回归卡密到期时间**

运行现有授权 UI/状态测试；在隔离 Release 验收环境使用生产信任链签发的专用测试卡，记录未激活、有限期、永久及升级后不要求重新激活四种结果。卡的创建/消耗写入单独审计记录。

- [ ] **Step 6: 写交接文档**

记录 commit、命令、测试计数、候选资产 SHA-256、迁移证据路径、卡密回归结果、生产用户目录未被触碰的证据，以及“尚未上传生产”的明确状态。

- [ ] **Step 7: 提交验证记录**

```powershell
git add docs/testing/2026-09-15-product-identity-ota-migration-handoff.md
git commit -m "docs: record product identity migration acceptance"
```

### Task 10: 生产发布准备与用户在线更新指引

**Files:**
- Modify: `docs/testing/2026-09-15-product-identity-ota-migration-handoff.md`

- [ ] **Step 1: 生成但不执行生产发布清单**

清单列出 staging 目录、允许替换的 OTA 资产、旧/新 SHA-256、原子切换顺序和回退 feed/别名；明确禁止递归删除服务器目录。

- [ ] **Step 2: 记录用户验收路径**

用户实际在线测试步骤必须为：1.1.0 检查更新 → 发现 1.1.1 → 下载/应用 → 自动重启为 `知道综合工具.exe` → 核对图标、账号、任务、卡密到期时间 → 固定 Setup 链接全新安装。

- [ ] **Step 3: 最终实现审阅**

使用 `superpowers:requesting-code-review` 审阅完整 diff；修复所有阻断项后重新执行 Task 9 的测试和包验证。

- [ ] **Step 4: 停在生产变更边界**

交付本地候选、证据和清单。只有用户随后明确要求上线时，才在服务器 OTA 目录执行 staging 上传和原子切换；上线后再按 Task 10 Step 2 完成真实在线更新验收。

- [ ] **Step 5: 提交最终文档**

```powershell
git add docs/testing/2026-09-15-product-identity-ota-migration-handoff.md
git commit -m "docs: finalize 1.1.1 release handoff"
```
