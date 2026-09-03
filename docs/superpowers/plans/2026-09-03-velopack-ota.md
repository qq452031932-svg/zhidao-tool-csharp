# Velopack OTA 在线更新 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为百度批量转存 Windows 客户端实现可本地完整验收的 Velopack OTA：真实设置中心、启动/手动检查、下载进度、安全关闭后更新重启，以及可重复的本地发布产物。

**Architecture:** 显式 `Main` 在 WPF 启动前初始化 Velopack，并创建由 `App` 而非 DI 容器拥有的 `VelopackUpdateRuntime`。Host 内只使用不拥有资源的 `IApplicationUpdateService` 门面；设置页和启动检查均通过该门面工作。更新下载完成后，现有关闭链路先完成偏好持久化、队列围栏、浏览器退出和 Host 释放，随后 `App` 才让进程外 updater 等待退出、替换和重启。

**Tech Stack:** .NET 8、WPF、CommunityToolkit.Mvvm、Velopack 1.2.0、xUnit、PowerShell

---

## 实施约束

- 使用现有工作树 `C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement`，不新建第二个工作树。
- 工作树已有大量用户/交接遗留修改。修改任何已有脏文件前先保存本轮起始 diff；提交时不得直接暂存整个重叠文件，必须按本轮 hunk 暂存并用 `git diff --cached` 复核。
- 不连接、不修改、不上传用户服务器。
- 不生成、保存或索取 PFX 密码、私钥、令牌。
- 普通生产 Release 不允许本地 feed 或运行时覆盖；本轮真实性方案和证书尚未落地，发布脚本一律拒绝生产 OTA 输出，只生成明确标识的 OTA 本地验收包。
- 每个行为变更遵循 @superpowers:test-driven-development：先写失败测试并确认失败原因，再写最小实现。

## 文件结构

### 新增产品文件

- `src/BaiduBatchTransfer.App/Updates/ApplicationBuildInfo.cs`：产品版本、适配器协议版本、渠道和编译期更新源。
- `src/BaiduBatchTransfer.App/Updates/ApplicationUpdateContracts.cs`：不暴露 Velopack 类型的结果、状态和接口。
- `src/BaiduBatchTransfer.App/Updates/VelopackUpdateClient.cs`：直接封装 Velopack API 的薄适配器。
- `src/BaiduBatchTransfer.App/Updates/VelopackUpdateRuntime.cs`：进程所有的检查、下载、pending 和 apply 生命周期。
- `src/BaiduBatchTransfer.App/Updates/ApplicationUpdateService.cs`：Host 内非拥有门面、并发门禁和中文错误映射。
- `src/BaiduBatchTransfer.App/Updates/UnavailableApplicationUpdateRuntime.cs`：测试/未配置时的安全空实现。
- `src/BaiduBatchTransfer.App/Updates/UpdateAwareShutdownOrchestrator.cs`：以可测试方式编排安全关闭、更新应用与退出。
- `src/BaiduBatchTransfer.App/Updates/IApplicationCloseRequester.cs`：设置页请求关闭主窗口的窄接口。
- `src/BaiduBatchTransfer.App/Program.cs`：Velopack 引导和显式 WPF 入口。
- `src/BaiduBatchTransfer.App/Features/Settings/SettingsViewModel.cs`：更新卡片状态和命令。
- `src/BaiduBatchTransfer.App/Features/Settings/SettingsView.xaml`：设置中心更新 UI。
- `src/BaiduBatchTransfer.App/Features/Settings/SettingsView.xaml.cs`：WPF 视图初始化。
- `scripts/build_ota_release.ps1`：自包含发布、vpk 打包、门禁和 SHA-256 上传清单。
- `.config/dotnet-tools.json`：锁定 `vpk` 1.2.0。

### 新增测试文件

- `tests/BaiduBatchTransfer.App.Tests/Updates/ApplicationBuildInfoTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Updates/ApplicationUpdateServiceTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Updates/VelopackUpdateRuntimeTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Features/SettingsViewModelTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Features/SettingsViewStructureTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Updates/UpdateShutdownIntegrationTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Updates/OtaBuildConfigurationTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Updates/OtaReleaseScriptTests.cs`

### 修改的已有文件

- `Directory.Packages.props`：锁定 Velopack 1.2.0。
- `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`：引用 Velopack、显式入口、版本/更新源元数据和生产/验收组合门禁。
- `src/BaiduBatchTransfer.App/packages.lock.json`：锁文件更新。
- `tests/BaiduBatchTransfer.App.Tests/packages.lock.json`：传递依赖锁文件更新；若精确 restore 实际改变其他 `packages.lock.json`，先核对原因并把实际文件加入该任务清单，不能假定只有两个。
- `src/BaiduBatchTransfer.App/App.xaml`：从自动入口改为由 `Program.Main` 初始化。
- `src/BaiduBatchTransfer.App/App.xaml.cs`：接收进程所有 runtime，安全关闭成功后应用 pending 更新。
- `src/BaiduBatchTransfer.App/AppBootstrap.cs`：注册非拥有更新门面、替换设置中心占位页、启动静默检查、使用统一版本语义。
- `src/BaiduBatchTransfer.App/StartupDataRootSelector.cs`：验收构建强制使用独立数据根。
- `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`：验收版标题标识和启动发现新版的一次轻量提示。
- `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`：DI、设置页和版本接线。
- `tests/BaiduBatchTransfer.App.Tests/ApplicationShutdownCoordinatorTests.cs`：更新应用严格晚于安全关闭。
- `tests/BaiduBatchTransfer.App.Tests/MainWindowChromeTests.cs`：显式入口和资源仍完整。
- `tests/BaiduBatchTransfer.App.Tests/StartupDataRootSelectorTests.cs`：正式与验收数据根隔离。
- `docs/testing/2026-09-01.md`：追加 OTA 本地验收交接结果。

---

### Task 0: 固化脏工作树基线与提交边界

**Files:** 不修改产品文件；只在任务记录中保存基线证据。

- [ ] **Step 1: 记录本轮基线**

在开始编辑前保存以下输出到本轮临时证据目录（不提交敏感内容）：

```text
git status --short
git diff --binary
git diff --cached --binary
git ls-files --others --exclude-standard
```

另外为“文件结构”列出的每个计划路径生成基线清单：绝对/仓库相对路径、是否存在、tracked/untracked、是否 modified/staged、文件 SHA-256。对 tracked 脏文件保存独立 binary diff；对预存 untracked 文件不能把内容当成新建文件接管，发现与计划 Create 路径冲突时立即停止并请用户处理。执行期间若同一路径的哈希或状态出现无法由本轮操作解释的变化，也停止该文件的暂存。

- [ ] **Step 2: 检查初始暂存区**

运行 `git diff --cached --quiet`。若返回非零，说明用户已有暂存内容；停止实现并先让用户决定如何隔离，不能把已有 staged 内容带入 OTA 提交。

- [ ] **Step 3: 固定逐提交规则**

每个任务提交前都必须运行 `git diff --cached --name-only` 和 `git diff --cached`，并与基线清单及该任务精确文件白名单逐项核对。基线已存在/已脏文件只使用交互式 hunk 暂存或生成最小 cached patch；只有基线明确不存在、由本轮创建的单个文件才能按完整精确路径暂存。禁止目录级 `git add`、`git add .`，禁止清理/还原用户改动。

---

### Task 1: 建立产品版本和 OTA 构建门禁

**Files:**
- Create: `src/BaiduBatchTransfer.App/Updates/ApplicationBuildInfo.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/ApplicationBuildInfoTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaBuildConfigurationTests.cs`
- Modify: `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/StartupDataRootSelector.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/StartupDataRootSelectorTests.cs`

- [ ] **Step 1: 写产品版本与更新源失败测试**

覆盖以下行为：

```csharp
[Fact]
public void ProductionBuildRejectsLocalOrMutableFeed()
{
    Assert.False(ApplicationBuildInfo.TryCreateProduction("file:///c:/feed", out _));
    Assert.False(ApplicationBuildInfo.TryCreateProduction("http://updates.example.test", out _));
    Assert.True(ApplicationBuildInfo.TryCreateProduction("https://updates.example.test/stable/", out var info));
    Assert.Equal("win-x64-stable", info.Channel);
}

[Fact]
public void ProductAndAdapterVersionsHaveSeparateSemantics()
{
    var info = ApplicationBuildInfo.CreateForTests("1.2.3", "managed-browser-v3", null);
    Assert.Equal("1.2.3", info.ProductVersion);
    Assert.Equal("managed-browser-v3", info.AdapterBuildVersion);
}

[Fact]
public void AssemblyVersionTracksValidatedProductMajor()
{
    var versions = ApplicationBuildInfo.DeriveAssemblyVersions("2.3.4");
    Assert.Equal("2.0.0.0", versions.AssemblyVersion);
    Assert.Equal("2.3.4.0", versions.FileVersion);
}
```

增加进程级 MSBuild 门禁测试：测试进程调用 `dotnet msbuild src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -t:ValidateOtaBuildConfiguration -p:...`，覆盖普通 Release 同时设置两个 feed、验收包使用生产包 ID/渠道、非三段版本、生产本地 feed 等非法组合并断言退出码非零；合法生产/验收组合断言退出码为零。不要通过递归 `dotnet build` 测门禁。

增加数据根测试：普通构建仍解析 `%LOCALAPPDATA%\BaiduBatchTransfer`；`OtaAcceptanceBuild=true` 时无条件解析到 `%LOCALAPPDATA%\BaiduBatchTransfer.OtaAcceptance`，忽略 `--data-root`，且不会调用正式数据迁移路径。测试通过注入临时 LocalAppData 根运行。

- [ ] **Step 2: 运行测试并确认 RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~ApplicationBuildInfoTests|FullyQualifiedName~OtaBuildConfigurationTests|FullyQualifiedName~StartupDataRootSelectorTests|FullyQualifiedName~AppBootstrapTests" --logger "console;verbosity=minimal"`

Expected: FAIL/编译失败，因为 `ApplicationBuildInfo` 和项目门禁尚不存在。

- [ ] **Step 3: 实现最小版本模型**

`ApplicationBuildInfo` 提供：

```csharp
public sealed record ApplicationBuildInfo(
    string ProductVersion,
    string AdapterBuildVersion,
    string Channel,
    string? HttpsFeedUrl,
    string? AcceptanceFeedPath,
    bool IsOtaAcceptanceBuild)
{
    public bool IsFeedConfigured => HttpsFeedUrl is not null || AcceptanceFeedPath is not null;
    public static ApplicationBuildInfo Current { get; }
}
```

验证生产 URL 为无凭据、无 query/fragment 的绝对 HTTPS；验收目录为绝对本地路径。把 `AppBootstrap.cs` 中迁移审计版本改为 `ProductVersion`，适配器/浏览器能力键改为 `AdapterBuildVersion`。

在 csproj 中定义：

```xml
<VersionPrefix Condition="'$(VersionPrefix)' == ''">1.0.0</VersionPrefix>
<Version>$(VersionPrefix)</Version>
<FileVersion>$(VersionPrefix).0</FileVersion>
<ProductMajor>$([System.Version]::Parse('$(VersionPrefix)').Major)</ProductMajor>
<AssemblyVersion>$(ProductMajor).0.0.0</AssemblyVersion>
<InformationalVersion>$(VersionPrefix)</InformationalVersion>
<UpdateChannel Condition="'$(UpdateChannel)' == ''">win-x64-stable</UpdateChannel>
<AdapterBuildVersion Condition="'$(AdapterBuildVersion)' == ''">managed-browser-v3</AdapterBuildVersion>
```

使用 `AssemblyMetadataAttribute` 注入 `UpdateFeedUrl`、`UpdateChannel`、`AdapterBuildVersion`、`OtaAcceptanceBuild` 和 `OtaAcceptanceFeedPath`。增加 MSBuild `Error` 门禁拒绝非法组合。

`StartupDataRootSelector` 使用编译期 `OtaAcceptanceBuild` 标记选择固定验收根；验收包标题标记和数据根不能由运行时参数取消。普通 Debug 的现有测试覆盖保留，普通 Release 行为不变。

- [ ] **Step 4: 运行 GREEN 测试**

Run: 同 Step 2。

Expected: PASS；所有新版本和接线用例通过。

- [ ] **Step 5: 提交 Task 1**

只暂存新文件及本轮 csproj/AppBootstrap hunk；复核 staged diff 后提交：

```bash
git commit -m "feat: define OTA build identity"
```

---

### Task 2: 引入 Velopack 并建立显式启动入口

**Files:**
- Create: `src/BaiduBatchTransfer.App/Program.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/ApplicationUpdateContracts.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/VelopackUpdateRuntime.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/UnavailableApplicationUpdateRuntime.cs`
- Create: `.config/dotnet-tools.json`
- Modify: `Directory.Packages.props`
- Modify: `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`
- Modify: `src/BaiduBatchTransfer.App/App.xaml`
- Modify: `src/BaiduBatchTransfer.App/App.xaml.cs`
- Modify: `src/BaiduBatchTransfer.App/packages.lock.json`
- Modify: `tests/BaiduBatchTransfer.App.Tests/packages.lock.json`
- Modify: `tests/BaiduBatchTransfer.App.Tests/MainWindowChromeTests.cs`

- [ ] **Step 1: 写显式入口结构测试**

断言：

```csharp
Assert.Contains("VelopackApp.Build()", program);
Assert.Contains("SetAutoApplyOnStartup(false)", program);
Assert.True(program.IndexOf("VelopackApp.Build()", StringComparison.Ordinal)
            < program.IndexOf("new App(", StringComparison.Ordinal));
Assert.Contains("<ApplicationDefinition Remove=\"App.xaml\"", project);
Assert.Contains("<Page Include=\"App.xaml\"", project);
```

- [ ] **Step 2: 运行测试并确认 RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~MainWindowChromeTests" --logger "console;verbosity=minimal"`

Expected: FAIL，因为 `Program.cs` 和 Velopack 引导尚不存在。

- [ ] **Step 3: 锁定依赖和工具**

- `Directory.Packages.props` 增加 `<PackageVersion Include="Velopack" Version="1.2.0" />`。
- App 项目增加 `<PackageReference Include="Velopack" />`。
- 使用本地 tool manifest 锁定 `vpk` 1.2.0。
- 分别执行 `dotnet restore src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj --force-evaluate -r win-x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true` 和对应的 App Tests 命令，更新直接受影响的锁文件；检查 `git status --short -- '**/packages.lock.json'`，把所有实际变化纳入复核。随后对这两个项目分别执行 `dotnet restore ... --locked-mode -r win-x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true`，确保锁文件与后续自包含 `win-x64` 发布使用完全相同的 RID/部署模式。

- [ ] **Step 4: 实现显式入口**

`Program.Main` 必须为 `[STAThread]`，顺序固定：

```csharp
VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
var updateRuntime = VelopackUpdateRuntime.Create(ApplicationBuildInfo.Current);
var app = new App(updateRuntime);
app.InitializeComponent();
app.Run();
```

本任务同时建立能编译的最小 runtime 契约、`VelopackUpdateRuntime.Create(...)` 工厂和未配置空实现；仅负责创建/持有/释放，不接入检查、下载或应用行为。`App` 保存 runtime。保持当前资源字典和启动错误处理。

- [ ] **Step 5: 运行 GREEN 测试和构建**

Run:

```text
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~MainWindowChromeTests" --logger "console;verbosity=minimal"
dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug --no-restore
```

Expected: PASS，构建 0 errors。

- [ ] **Step 6: 提交 Task 2**

复核依赖锁文件只含 Velopack 传递依赖；按本轮 hunk 暂存重叠文件后提交：

```bash
git commit -m "feat: bootstrap Velopack before WPF"
```

---

### Task 3: 实现可测试的更新运行时与服务门面

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Updates/ApplicationUpdateContracts.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/VelopackUpdateClient.cs`
- Modify: `src/BaiduBatchTransfer.App/Updates/VelopackUpdateRuntime.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/ApplicationUpdateService.cs`
- Modify: `src/BaiduBatchTransfer.App/Updates/UnavailableApplicationUpdateRuntime.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/ApplicationUpdateServiceTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/VelopackUpdateRuntimeTests.cs`

- [ ] **Step 1: 写服务状态、并发和 pending RED 测试**

至少覆盖：

- 未配置 feed 返回 `NotConfigured`，不访问网络。
- 非 Velopack 运行返回 `Unsupported`。
- 自动检查网络错误返回静默失败；手动检查返回可见中文错误。
- 同时两次检查只触发一个底层调用。
- 检查到新版后才能下载。
- 下载进度限制在 0–100。
- 下载完成后必须从 `UpdatePendingRestart` 取得目标资产，才能 `PrepareRestart`。
- `LaunchPreparedUpdateAndRestart` 没有 prepared asset 时拒绝执行。
- 进程重新启动后从新 runtime 的 pending asset 重建状态，不序列化框架对象。
- 已安装版本优先显示 `UpdateManager.CurrentVersion`；未由 Velopack 安装的开发运行回退到 `ApplicationBuildInfo.ProductVersion`。

示例：

```csharp
[Fact]
public async Task DownloadMustProducePendingAssetBeforeRestartCanBePrepared()
{
    var client = new FakeVelopackClient { DownloadCompletesWithoutPendingAsset = true };
    var runtime = new VelopackUpdateRuntime(client);
    await runtime.CheckAsync(default);
    var result = await runtime.DownloadAsync(null, default);
    Assert.Equal(ApplicationUpdateStatus.VerificationFailed, result.Status);
    Assert.False(runtime.TryPrepareRestart());
}
```

- [ ] **Step 2: 运行 RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~ApplicationUpdateServiceTests|FullyQualifiedName~VelopackUpdateRuntimeTests" --logger "console;verbosity=minimal"`

Expected: FAIL/编译失败，新接口和实现尚不存在。

- [ ] **Step 3: 实现领域契约和 fakeable client**

核心公开契约：

```csharp
public enum ApplicationUpdateStatus
{
    Idle, Checking, UpToDate, Available, Downloading, ReadyToRestart,
    NotConfigured, Unsupported, Busy, NetworkUnavailable,
    VerificationFailed, Failed
}

public sealed record ApplicationUpdateSnapshot(
    ApplicationUpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseNotes,
    int DownloadProgress,
    string UserMessage);

public interface IApplicationUpdateService
{
    ApplicationUpdateSnapshot Snapshot { get; }
    event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;
    Task<ApplicationUpdateSnapshot> CheckAsync(bool manual, CancellationToken cancellationToken);
    Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken);
    bool TryPrepareRestart();
}
```

Velopack 类型只能出现在 `VelopackUpdateClient`/`VelopackUpdateRuntime` 内。底层 client 用不透明 token 包装 `UpdateInfo`/`VelopackAsset`，不写磁盘、不跨进程序列化。

- [ ] **Step 4: 实现实际 Velopack 适配器**

- HTTPS 配置使用 `SimpleWebSource`；验收配置使用本地目录源。
- `CheckForUpdatesAsync` 投影版本和 release notes。
- `DownloadUpdatesAsync` 报告进度。
- 下载完成后读取 `UpdatePendingRestart`。
- 当前版本优先读取 `UpdateManager.CurrentVersion`，其为空时才回退编译期产品版本。
- apply 只调用 `WaitExitThenApplyUpdates`，禁止 `UnsafeApplyUpdates` 和立即强退 API。
- 异常只映射为固定状态，不把底层异常字符串暴露给 UI。

- [ ] **Step 5: 运行 GREEN 和重复并发测试**

Run: 同 Step 2，连续运行 3 次。

Expected: 每轮 PASS，无随机并发失败。

- [ ] **Step 6: 提交 Task 3**

按 Task 0 基线逐个暂存本任务列出的 7 个精确文件：Task 2 已存在且本任务继续修改的契约/runtime 文件只暂存本轮 hunk；基线不存在的新增 client/service/测试文件才可整文件暂存。禁止暂存 `Updates` 目录。

```bash
git diff --cached --name-only
git diff --cached
git commit -m "feat: add application update runtime"
```

---

### Task 4: 建立真实设置中心更新卡片

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Settings/SettingsViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Settings/SettingsView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Settings/SettingsView.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/IApplicationCloseRequester.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/SettingsViewModelTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/SettingsViewStructureTests.cs`

- [ ] **Step 1: 写 ViewModel 与 XAML RED 测试**

覆盖：

- 初始显示当前版本和更新状态。
- `CheckForUpdatesCommand` 在检查期间禁用，完成后恢复。
- 有更新才启用“立即更新并重启”。
- 点击“立即更新并重启”时先确认；确认后才下载，下载状态显示 0–100%，完成后登记重启并请求安全关闭。
- 用户拒绝确认不下载。
- 初始状态已是 `ReadyToRestart`（包括安全关闭失败后重试或进程重启后恢复 pending）时，按钮显示“重启完成更新”；确认后跳过检查和下载，直接重新 `TryPrepareRestart()` 并请求安全关闭。
- 自动检查发现更新只更新状态，不自动下载。
- XAML 包含当前版本、更新状态、最新版本、更新说明、进度条和两个按钮；不再显示“功能建设中”。

- [ ] **Step 2: 运行 RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~SettingsViewStructureTests" --logger "console;verbosity=minimal"`

Expected: FAIL/编译失败，因为 Settings 组件不存在。

- [ ] **Step 3: 实现 SettingsViewModel**

使用 `ObservableObject`、`AsyncRelayCommand`、现有 `IDialogService` 和 `IApplicationCloseRequester`。只暴露 UI 所需属性；订阅 `SnapshotChanged` 时切回 UI 同步上下文；实现 `Dispose` 取消订阅。普通新版路径先用固定文案“下载完成后软件将安全关闭、安装更新并自动重启，是否继续？”确认，随后下载、登记 pending 并请求关闭。若启动时已经 `ReadyToRestart`，或上次安全关闭失败后仍有 pending，则使用“软件将安全关闭、安装已下载的更新并自动重启，是否继续？”；确认后不重新检查/下载，直接从当前 pending 重新登记并调用关闭请求接口。

- [ ] **Step 4: 实现 SettingsView**

沿用 `BorderedPanelStyle`、`SecondaryButtonStyle`、`BrandBrush`、`MutedBrush`，布局参考用户截图但不复刻：顶部“软件更新（OTA 在线更新）”，中间版本和状态，黄色更新说明卡片，底部两个按钮和下载进度。设置页不增加服务器地址输入框。

- [ ] **Step 5: 运行 GREEN**

Run: 同 Step 2。

Expected: PASS。

- [ ] **Step 6: 提交 Task 4**

按 Task 0 基线逐个暂存本任务列出的 6 个精确文件；只有基线不存在且确认由本轮创建的文件才可整文件暂存，禁止暂存 `Features/Settings` 目录。

```bash
git diff --cached --name-only
git diff --cached
git commit -m "feat: add OTA settings workspace"
```

---

### Task 5: 接入设置导航与启动静默检查

**Files:**
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Settings/SettingsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Settings/SettingsView.xaml`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/SettingsViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/SettingsViewStructureTests.cs`

- [ ] **Step 1: 写组合与启动检查 RED 测试**

断言：

- `IApplicationUpdateService` 解析到非拥有门面。
- 设置工作区为 `SettingsView`，不再是 `DeferredWorkspaceView`。
- Shell Loaded 完成现有初始化后调用一次 `CheckAsync(manual: false)`。
- 静默检查异常被观察但不阻断主窗口。
- 导航到其他模块不重复自动检查；手动检查仍由设置 VM 发起。
- 启动检查发现新版时，只把一次轻量提示写入 Shell `StatusMessage`，不弹窗、不下载、不重复提示。
- 验收构建的产品标题/设置页显式包含“OTA 本地验收版”，普通构建标题不变。

- [ ] **Step 2: 运行 RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~AppBootstrapTests|FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~SettingsViewStructureTests" --logger "console;verbosity=minimal"`

Expected: 新增用例 FAIL。

- [ ] **Step 3: 最小接线**

- `CreateHost` 增加一个保留旧重载的 runtime 参数路径，现有测试调用不破坏。
- 默认测试/旧重载使用 `UnavailableApplicationUpdateRuntime`。
- 正式 `App` 传入进程拥有 runtime；DI 注册不拥有的 `ApplicationUpdateService`。
- `AppShellFactory` 构造 `SettingsViewModel`/`SettingsView`。
- `HandleLoadedAsync` 在主窗口已显示、工作区初始化之后 Observe 一次静默检查。
- 自动检查返回 `Available` 时调用 `MainWindowViewModel.ReportUpdateAvailable(version)`，仅显示一次“发现新版本 …，可到设置中心更新”。
- 根据 `ApplicationBuildInfo.IsOtaAcceptanceBuild` 给窗口标题加标识，并让 `SettingsViewModel.BuildLabel` 暴露“OTA 本地验收版”、由 `SettingsView.xaml` 绑定显示；普通构建该属性为空，避免验收包被当成正式软件。
- Shell 关闭时 Dispose `SettingsViewModel`，不释放 runtime。

- [ ] **Step 4: 运行 GREEN**

Run: 同 Step 2。

Expected: PASS。

- [ ] **Step 5: 提交 Task 5**

只按本轮 hunk 暂存 `AppBootstrap.cs`、`MainWindowViewModel.cs`、Settings VM/XAML 和四个对应测试文件，复核 staged 白名单与内容后提交。所有本轮修改文件都使用 `git add -p -- <明确文件列表>` 或生成只含本轮 hunk 的 cached patch；尤其禁止对基线已脏的 `AppBootstrap.cs`、`MainWindowViewModel.cs`、`MainWindowViewModelTests.cs` 使用整文件 `git add`：

```bash
git diff --cached --name-only
git diff --cached
git commit -m "feat: connect OTA settings and startup check"
```

---

### Task 6: 把更新应用放到安全关闭之后

**Files:**
- Create: `src/BaiduBatchTransfer.App/Updates/UpdateAwareShutdownOrchestrator.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/UpdateShutdownIntegrationTests.cs`
- Modify: `src/BaiduBatchTransfer.App/App.xaml.cs`
- Modify: `src/BaiduBatchTransfer.App/Updates/VelopackUpdateRuntime.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Settings/SettingsViewModel.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/ApplicationShutdownCoordinatorTests.cs`

- [ ] **Step 1: 写关闭顺序 RED 测试**

用 `IApplicationCloseRequester` fake 和可注入委托/接口驱动的 `UpdateAwareShutdownOrchestrator` 覆盖，不依赖真实 WPF `Application` 或 `Window`：

```text
preference-flushed
queue-disposed
browser-stopped
host-stopped
host-disposed
velopack-wait-exit-then-apply
application-exit
```

并验证：

- 偏好刷新失败：没有 apply，窗口恢复。
- 队列超时：没有 apply，窗口恢复。
- 浏览器退出未确认：没有 apply，窗口恢复。
- 普通关闭：没有 apply。
- 并发关闭：apply 最多一次。
- apply 启动异常：不覆盖/删除当前应用，不重复调用。
- 安全关闭前失败可恢复窗口；Host 已释放后 apply 启动失败只显示安全错误并退出，不尝试恢复已释放的 UI/DI。

- [ ] **Step 2: 运行 RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UpdateShutdownIntegrationTests|FullyQualifiedName~ApplicationShutdownCoordinatorTests" --logger "console;verbosity=minimal"`

Expected: 新用例 FAIL。

- [ ] **Step 3: 实现关闭请求和 apply**

- WPF 实现的 `IApplicationCloseRequester` 只调度 `MainWindow.Close()`；`SettingsViewModel` 不直接退出进程。
- `UpdateAwareShutdownOrchestrator` 接收安全关闭操作、pending/apply 操作和退出/错误回调；它精确保证先完成现有 `ApplicationShutdownCoordinator.ShutdownAsync`，成功后才调用一次 apply。
- `App.OnMainWindowClosing` 仅负责冻结/恢复 UI 并调用该协调器，现有偏好、队列、浏览器、Host 顺序不变。
- 安全关闭失败时取消关闭并恢复 UI，pending 保留以便再次重试。
- 安全关闭成功且 Host 已释放后，调用 `LaunchPreparedUpdateAndRestart()`；无论启动成功或抛错都不恢复已释放的 UI。异常只显示“更新程序未能启动，本机数据和当前版本未被删除。”并退出，等待用户下次打开后由 Velopack pending 状态恢复。
- apply 调用后解绑 Closing、设置 `allowShutdown` 并 `Shutdown()`；不得调用不安全更新 API。
- 普通退出由 `App.OnExit` 释放进程拥有 runtime。

- [ ] **Step 4: 运行 GREEN 和现有关闭回归**

Run: 同 Step 2。

Expected: PASS，事件顺序精确一致。

- [ ] **Step 5: 提交 Task 6**

按本轮 hunk 暂存重叠文件后提交：

```bash
git commit -m "feat: apply updates after safe shutdown"
```

---

### Task 7: 建立可重复的 OTA 发布脚本

**Files:**
- Create: `scripts/build_ota_release.ps1`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaReleaseScriptTests.cs`
- Modify: `.gitignore`（仅在新产物目录尚未覆盖时）

- [ ] **Step 1: 写脚本门禁 RED 测试**

测试读取脚本并在临时目录执行纯参数校验，覆盖：

- 非 `major.minor.patch` 拒绝。
- 未同时指定 `OtaAcceptanceBuild` 与 `AllowUnsignedLocal` 时，无论是否传入看似有效的 HTTPS feed/签名信息，都在 publish/vpk 前拒绝且不产生资产。
- 本轮脚本不接受生产 feed、`SignParams` 或“仅签名即生产就绪”的路径；真实性模式未实现前，生产输出始终拒绝。
- `AllowUnsignedLocal` 只能与 `OtaAcceptanceBuild` 一起使用。
- 验收包 ID/渠道固定且不能等于生产值。
- 传入历史 feed 已存在相同版本时拒绝。
- 输出目录不是预期子目录或非空时拒绝覆盖。
- 脚本不包含上传、SSH、SFTP、对象存储写入命令。

- [ ] **Step 2: 运行 RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~OtaReleaseScriptTests" --logger "console;verbosity=minimal"`

Expected: FAIL，因为脚本不存在。

- [ ] **Step 3: 实现脚本**

参数：

```powershell
param(
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][string]$AcceptanceFeedPath,
  [Parameter(Mandatory)][string]$OutputDirectory,
  [string]$PreviousReleaseDirectory,
  [string]$ReleaseNotesPath,
  [switch]$OtaAcceptanceBuild,
  [switch]$AllowUnsignedLocal
)
```

参数校验的第一道机器门禁要求两个 switch 同时为真，否则立即以非零退出并保持输出目录不存在/为空。脚本不提供生产模式旁路；待服务器、证书与 `PinnedPublisher`/`SignedFeed` 方案落地后另行扩展并测试。

步骤固定为：验证路径和参数 → `dotnet tool restore` → `dotnet restore ... --locked-mode -r win-x64` → self-contained `win-x64` publish → helper gate → 从上一版复制 feed/包 → `dotnet tool run vpk pack` → 验证 `releases.<channel>.json` → 对全部上传资产生成 `upload-manifest.sha256` → 输出目录说明。不得自动上传。

`dotnet publish` 同样使用参数数组，必须显式传入并记录：

```text
dotnet publish src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj
  -c Release
  -r win-x64
  --self-contained true
  --no-restore
  -o <publish-directory>
  -p:VersionPrefix=<major.minor.patch>
  -p:RuntimeIdentifier=win-x64
  -p:SelfContained=true
  -p:UpdateFeedUrl=
  -p:OtaAcceptanceBuild=true
  -p:OtaAcceptanceFeedPath=<acceptance-absolute-path>
  -p:UpdateChannel=win-x64-ota-acceptance
```

脚本先用完全相同的 RID、自包含和 OTA 属性执行 locked restore；publish 后读取生成程序集元数据，反向验证版本、feed、渠道和验收标记确实与参数一致，不能只相信命令行传值。

`vpk` 调用使用 PowerShell 参数数组，不做字符串拼接或 `Invoke-Expression`，核心参数明确为：

```text
dotnet tool run vpk pack
  --packId SuperPan.BaiduBatchTransfer.OtaAcceptance
  --packVersion <major.minor.patch>
  --packDir <publish-directory>
  --mainExe BaiduBatchTransfer.App.exe
  --channel win-x64-ota-acceptance
  --runtime win-x64
  --outputDir <release-directory>
  --releaseNotes <release-notes-path>
```

本轮不追加 `--signParams`，产物明确为未签名本地验收包；只有显式 `-OtaAcceptanceBuild -AllowUnsignedLocal` 才允许执行。执行前先用锁定的 `vpk 1.2.0 --help` 核对参数名，若 CLI 实际名称不同，同步调整脚本和测试，不能静默猜测。

- [ ] **Step 4: 运行脚本测试 GREEN**

Run: 同 Step 2。

Expected: PASS。

- [ ] **Step 5: 生成首个本地验收包**

用专用临时目录和 `-OtaAcceptanceBuild -AllowUnsignedLocal` 生成 `1.0.0`；确认：

- 包 ID 为 acceptance ID；
- 渠道为 acceptance channel；
- Setup、Portable、full nupkg、release JSON 和 SHA-256 清单齐全；
- 省略任一验收 switch，或尝试生产输出，均在产物生成前失败。

- [ ] **Step 6: 提交 Task 7**

先与 Task 0 基线核对下面三个精确路径；只有基线不存在且由本轮创建的文件才整文件暂存，任何预存/并发产生的未跟踪冲突都停止处理。

```bash
git diff --cached --name-only
git diff --cached
git commit -m "build: add guarded OTA release pipeline"
```

---

### Task 8: 本地 1.0.0 → 1.0.1 端到端验收与最终回归

**Files:**
- Modify: `docs/testing/2026-09-01.md`

- [ ] **Step 1: 生成两个验收版本**

- 在隔离的 `.artifacts/ota-acceptance-<timestamp>` 下生成并保留 `1.0.0`、`1.0.1`。
- 先建立固定 feed 目录 `<acceptance-root>/feed`；`1.0.0` 编译时把这个绝对目录固化为 acceptance feed，但先把自身产物生成到 `<acceptance-root>/v1.0.0`。
- `1.0.1` 使用相同 acceptance 包 ID/渠道，在 `<acceptance-root>/v1.0.1` 合并上一版资产并生成差分包。
- 安装并启动 `1.0.0` 后，再把 `1.0.1` 的 `releases.win-x64-ota-acceptance.json` 及其引用的 nupkg 复制到固定 `<acceptance-root>/feed`，确保运行中的 `1.0.0` 检查的是它编译时固化的同一路径。

- [ ] **Step 2: 安装基线并确认数据隔离**

验收开始前记录正式 `%LOCALAPPDATA%\BaiduBatchTransfer` 内数据库、账号会话、缓存和任务文件的存在性、时间戳与 SHA-256。安装 `1.0.0` 到当前用户目录，确认标题明确显示“OTA 本地验收版”、设置中心显示 `1.0.0`，且验收版只使用 `%LOCALAPPDATA%\BaiduBatchTransfer.OtaAcceptance`。

- [ ] **Step 3: 在成功升级前逐个验证失败场景**

每个场景都从重新安装/修复 `1.0.0` 开始，恢复固定 feed；如需清理下载缓存，只能在解析并验证确属 acceptance 包 ID 的安装/缓存目录后进行，绝不触碰生产安装或 `%LOCALAPPDATA%\BaiduBatchTransfer`：

- 移走 feed：启动和手动检查不影响当前版本。
- 恢复 feed 后修改 nupkg 一个字节：下载/校验失败，旧版本继续可用。
- 恢复原始包，下载后取消关闭或制造安全关闭失败：updater 不启动；随后重新启动 `1.0.0` 并确认 pending 状态可恢复。
- 使用单独的“可应用但无法正常启动”验收版本记录 Velopack 实际行为，再用 `1.0.0` 安装包完成恢复演练；不宣称自动回滚。

每个场景结束后重新核对正式数据根的时间戳与 SHA-256 未变化。

- [ ] **Step 4: 最后验证 1.0.0 → 1.0.1 成功链路**

重新安装/修复到干净的 `1.0.0`，恢复未经修改的固定 feed 和 `1.0.1` 资产，然后：

- 确认主窗口先显示，静默检查不阻塞。
- 确认设置中心显示 `1.0.1` 和 release notes。
- 点击“立即更新并重启”，确认一次后观察下载进度。
- 确认原进程安全退出、新版启动、版本显示 `1.0.1`。
- 确认验收根内状态可正常保留；再次核对正式根文件的时间戳与 SHA-256 完全未变化，验收版未读取、迁移或写入正式数据。

- [ ] **Step 5: 运行聚焦回归**

Run:

```text
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~ApplicationBuildInfoTests|FullyQualifiedName~OtaBuildConfigurationTests|FullyQualifiedName~StartupDataRootSelectorTests|FullyQualifiedName~ApplicationUpdateServiceTests|FullyQualifiedName~VelopackUpdateRuntimeTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~SettingsViewStructureTests|FullyQualifiedName~UpdateShutdownIntegrationTests|FullyQualifiedName~OtaReleaseScriptTests|FullyQualifiedName~ApplicationShutdownCoordinatorTests|FullyQualifiedName~AppBootstrapTests|FullyQualifiedName~MainWindowChromeTests|FullyQualifiedName~MainWindowViewModelTests" --logger "console;verbosity=minimal"
dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Release --no-restore -warnaserror
git diff --check
```

Expected: 所有 OTA/受影响聚焦测试通过；Release 构建 0 warnings、0 errors；diff check 无错误。若既有 `AppBootstrapTests` 的 Debug-only Release 断言再次失败，单独记录且不得误称全套通过。

- [ ] **Step 6: 更新交接文档**

追加：

- 实现范围和已知限制；
- 本地验收包路径、版本、渠道和 SHA-256；
- 测试/构建命令和实际计数；
- 明确“真实服务器 URL、TLS/缓存、发布者一致性验证和正式签名仍待外部条件”；
- 生产 OTA 未达到真实性门禁前不得标记上线。

- [ ] **Step 7: 最终代码复审并提交**

使用 @superpowers:requesting-code-review 对规格、计划、全部本轮提交和实际测试证据做复审。修复所有 Critical/Important 后，按本轮 hunk 暂存交接文档并提交：

```bash
git commit -m "test: verify local OTA upgrade flow"
```

- [ ] **Step 8: 完成分支交接**

使用 @superpowers:verification-before-completion 提供新鲜证据。项目仍在持续开发，默认保留当前分支和工作树，不自动合并、推送、清理或上传服务器。
