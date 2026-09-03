# Velopack OTA 在线更新设计

## 目标

为百度批量转存 Windows 客户端加入可投入使用的 OTA 基础能力：

- 软件进入主界面后静默检查一次更新，不阻塞启动。
- 设置中心展示当前版本、检查状态、最新版和更新说明。
- 用户可以手动检查，并在发现新版后点击“立即更新并重启”。
- 下载完成后必须先走现有安全关闭流程，确认账号偏好、任务队列和受控浏览器均已安全结束，再由 Velopack 替换程序并重启。
- 当前不接入真实服务器。更新源在正式发布时通过构建参数注入；后续获得 HTTPS 地址后，设置页交互和更新服务接口保持不变，但生产启用前仍可能需要补充独立真实性校验实现。
- 提供本地目录更新源和发布脚本，用于在没有域名、服务器和签名证书时完整验证检查、下载、替换、重启链路。

## 非目标

- 本轮不配置服务器、域名、TLS 证书、SSH/SFTP 或对象存储账号。
- 本轮不开发后台管理网页和上传服务。
- 本轮不自动下载或强制安装更新。
- 本轮不支持 beta、多租户或用户自行切换更新渠道。
- 本轮不声称产物已经完成正式代码签名；正式签名需要后续提供代码签名证书或签名服务。
- 不重构批量转存、分享数据、账号或数据库业务。

## 技术选择

采用 Velopack `1.2.0`，客户端包与 `vpk` 工具锁定相同版本。渠道固定为 `win-x64-stable`，包 ID 固定为 `SuperPan.BaiduBatchTransfer`。

选择 Velopack 而不是自建更新器，是因为当前优先级是尽快投入使用。Velopack 已提供 Windows 每用户安装结构、完整包和差分包、基于 release feed 的 SHA-256 包校验、并发更新锁、断点临时文件、退出后替换与重启。这里的 SHA-256 只能证明下载包与同次获取的 feed 一致，不能独立证明发布者身份。自建更新器的原子切换、断电恢复、路径穿越和回滚测试量明显更大；MSIX 又会把应用身份和证书部署引入当前工程。

Windows 正式分发使用 Velopack `Setup.exe`，默认安装到当前用户目录，不要求管理员权限。可同时输出 Portable ZIP 供开发验收，但面向普通用户以安装版为主。

## 版本模型

### 产品版本

- 首版产品版本只接受三段数字稳定版 SemVer，例如 `1.0.0`、`1.0.1`；不接受预发布标签或构建元数据。
- `Version` 和 `InformationalVersion` 使用完整三段版本；`FileVersion` 映射为 `major.minor.patch.0`；`AssemblyVersion` 映射为稳定兼容主版本 `major.0.0.0`。
- 发布脚本必须显式接收版本号，并拒绝空值、非三段数字稳定版和相对于已提供历史资产的重复版本。
- 设置页优先显示 Velopack 已安装版本；开发运行时回退到程序集产品版本。

### 兼容性和审计版本

当前适配器能力和迁移记录多处读取程序集版本，但这两类数据需要不同语义：

- `ApplicationBuildInfo.ProductVersion` 使用发布脚本注入的完整产品版本，供 OTA 展示、诊断和数据库迁移审计记录使用。
- `ApplicationBuildInfo.AdapterBuildVersion` 使用独立、显式的适配器协议版本；只有适配器行为或验收契约变化时才更新，避免普通界面小版本使既有能力记录无故失配。
- 现有直接读取 `Assembly.GetExecutingAssembly().GetName().Version` 的调用点按用途迁移到上述两个属性。
- `AssemblyVersion` 使用稳定的兼容性主版本；它不再承担产品发布审计语义。

## 更新源配置

生产更新地址使用 MSBuild 属性 `UpdateFeedUrl` 注入为程序集元数据，默认值为空。

- 空值：开发构建可正常运行；设置中心显示“更新服务尚未配置”；启动静默检查直接结束。
- HTTPS 值：生产客户端创建 Velopack `SimpleWebSource`。
- 本地验收：使用专用 `OtaAcceptanceBuild=true` 的 Release 构建，在编译时注入绝对本地目录并使用独立包 ID `SuperPan.BaiduBatchTransfer.OtaAcceptance` 和独立渠道 `win-x64-ota-acceptance`。普通生产 Release 不接受命令行或本地路径覆盖。
- 生产地址只允许绝对 HTTPS URL，禁止用户名、密码、查询字符串和片段。
- 服务器最终只需静态提供 `releases.win-x64-stable.json` 及其引用的 `.nupkg` 文件。

MSBuild 发布门禁禁止以下组合：生产包使用本地 feed、验收包使用正式包 ID、同时设置生产 URL 与本地目录、或普通 Release 启用运行时 feed 覆盖。验收包在产品标题和设置页中明确显示“OTA 本地验收版”，不作为用户发布物。

验收包还必须强制使用独立数据根 `%LOCALAPPDATA%\BaiduBatchTransfer.OtaAcceptance`，不得读取、迁移或写入正式 `%LOCALAPPDATA%\BaiduBatchTransfer`。独立包 ID/渠道只隔离安装文件，不能替代数据目录隔离。验收构建忽略普通 Release 不允许的运行时 `--data-root` 覆盖，并由编译期标记选择固定验收根；测试可以通过显式注入临时 LocalAppData 根验证该规则。

更新地址不会放到普通用户可编辑的设置项里，防止误操作把客户端切到不受信任的更新源。后续拿到服务器地址后，通过发布参数生成第一版正式安装包；以后发布新版本只上传发布目录中的更新资产。

## 客户端组件

### 1. Velopack 启动引导

新增显式 `Main` 入口，在创建 WPF `Application` 之前调用 `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()`。`App.xaml` 从 `ApplicationDefinition` 改为 `Page`。入口随后创建由应用进程直接拥有的 `VelopackUpdateRuntime`，把它传给 `App`，再运行 WPF 应用。

这样安装、更新和卸载钩子能在 WPF 初始化之前执行，避免更新阶段加载完整应用。

### 2. 应用构建信息

`ApplicationBuildInfo` 只负责读取并格式化：

- 当前产品版本；
- 是否为 Velopack 安装/便携包；
- 渠道；
- 更新源是否已配置。

它不访问网络，也不参与下载。

### 3. 更新服务

`IApplicationUpdateService` 为设置页提供稳定的领域结果，不把 Velopack 类型泄漏给 ViewModel：

- `CheckAsync(manual, cancellationToken)`：串行检查更新；重复调用共享或拒绝同一在途操作。
- `DownloadAsync(progress, cancellationToken)`：只下载当前检查到的目标版本。
- `PrepareRestart()`：仅在下载完成且 `UpdateManager.UpdatePendingRestart` 返回目标资产后登记不可变的待应用句柄。
- `HasPendingUpdate`：读取已经下载但尚未应用的更新。

`VelopackApplicationUpdateService` 负责把 Velopack 异常映射成少量中文状态：

- 已是最新版本；
- 发现新版本；
- 更新服务尚未配置；
- 当前运行方式不支持在线更新；
- 网络暂时不可用；
- 更新文件校验失败；
- 更新操作正在进行；
- 更新失败，请稍后重试。

原始 URL、本机路径、堆栈和底层异常不会显示给用户。

### 4. 设置中心

用真实 `SettingsView` / `SettingsViewModel` 替换当前“功能建设中”占位页。首版只做“软件更新”卡片：

- 当前版本；
- 更新状态；
- 最新版本（发现更新后显示）；
- 更新说明；
- 下载进度；
- “检查更新”按钮；
- “立即更新并重启”按钮。

状态规则：

- 检查期间禁用重复检查。
- 没有新版时只显示“已是最新版本”。
- 有新版时显示版本和说明，不自动下载。
- 点击“立即更新并重启”后先确认；用户确认后才下载，按钮文字和进度实时变化。下载并登记待应用更新成功后自动请求安全关闭，不再增加第二次确认。
- 如果安全关闭失败，或软件在下载后、应用前重新启动，设置页直接显示“重启完成更新”；再次确认后复用 `UpdatePendingRestart`，不重新检查或下载。
- 下载或校验失败时保持当前版本可用，并允许重试。
- 当前不是 Velopack 包或更新源为空时，按钮禁用并给出简短原因。

### 5. 启动静默检查

`AppShellFactory` 在主窗口 Loaded、现有工作区初始化完成后触发一次 `CheckAsync(manual: false)`：

- 不阻塞主窗口显示；
- 无更新、无网络、未配置或开发运行时不弹窗；
- 发现新版后更新设置页状态，并显示一次轻量提示；
- 不自动下载，不启动定时轮询。

手动“检查更新”始终发起一次新检查，但与启动检查共享并发门禁。

## 安全关闭、应用与重启

不能直接调用会立即结束进程的更新 API。使用 Velopack `WaitExitThenApplyUpdates`，并把调用时机放在现有安全关闭成功之后。

流程如下：

1. 用户在下载前确认一次；设置页完成下载后重新读取 `UpdateManager.UpdatePendingRestart`，向 `PendingUpdateRestartCoordinator` 登记由当前 `UpdateManager` 和 `VelopackAsset` 组成的进程内待应用句柄。
2. 设置页请求关闭主窗口。
3. `App.OnMainWindowClosing` 沿用现有逻辑：冻结 UI、持久化分享数据账号偏好、关闭任务队列、确认受控浏览器退出、停止并释放 Host。
4. 任一步失败，取消关闭并恢复 UI；不启动 Velopack updater，待应用计划保留，可再次点击重启。
5. 全部安全关闭成功后，`App` 使用进程入口创建、由 `App` 直接拥有的同一 `VelopackUpdateRuntime`，调用 `UpdateManager.WaitExitThenApplyUpdates(pendingAsset, silent: false, restart: true)`。
6. updater 等待当前进程退出、切换到新版本并重启。
7. updater 启动失败时，显示安全错误并退出；当前已安装版本不被业务代码手工覆盖。

更新应用组件必须独立于已 Dispose 的 Host：`VelopackUpdateRuntime`、其 `UpdateManager` 和进程内 `VelopackAsset` 由显式入口创建并归 `App` 所有，不由 DI 容器创建、跟踪或释放。Host 中只注册一个不拥有资源的更新服务门面；门面随 Host 释放只取消 UI 操作，不释放 runtime。`App` 在 updater 启动调用完成或普通退出时负责释放 runtime 中需要释放的资源。Velopack 启动引导关闭默认自动应用，避免一次意外退出绕过用户确认。若进程在下载后、应用前意外退出，下次启动不反序列化任何 Velopack 对象，而是由新建 runtime 从新的 `UpdateManager.UpdatePendingRestart` 重建“已下载，等待重启”状态；用户再次点击后登记新的进程内句柄。

## 完整性和签名

### 本轮可完成

- Velopack release feed 包含完整包和差分包的 SHA-256，下载时由 Velopack校验包是否与该 feed 一致。feed 和包来自同一服务器，因此该校验只检测传输损坏或 feed/包不一致，不能抵御服务器控制者同时替换二者。
- `vpk` 与客户端库锁定同一稳定版本。
- 发布脚本生成 SHA-256 清单，供上传前和部署后核对。
- 本轮发布脚本只允许专用 OTA 验收构建，并且必须显式 `-OtaAcceptanceBuild -AllowUnsignedLocal`；任何生产模式调用都在生成资产前失败。
- 更新只从编译时固定的 HTTPS 地址获取。

### 后续外部条件

正式数字签名需要代码签名证书、Azure Artifact Signing 或兼容的签名服务。后续生产脚本通过 Velopack `--signParams`/签名模板传入，不在仓库保存 PFX、密码、令牌或私钥。

Authenticode 代码签名用于标识发布者并改善 Windows SmartScreen 体验，但本设计不假设 Velopack `1.2.0` 会自动拒绝错误发布者。真实服务器接入阶段必须先做一次黑盒验证：用另一证书或无签名的测试更新包确认客户端是否拒绝。如果框架不强制发布者一致，则在启用生产 OTA 前增加“固定发布者证书指纹的显式 Authenticode 校验”或“内置公钥验证的签名 release feed”。未来脚本只有在显式选择 `PinnedPublisher` 或 `SignedFeed` 真实性模式、实际校验产物通过后才允许生成正式资产；非空 `SignParams` 本身不构成通过条件。这可能增加客户端校验流程和发布资产格式，但不改变设置页交互和 `IApplicationUpdateService` 接口。完成这项门禁之前，生产 OTA 资产生成被机器门禁禁止。

服务器若被攻破，攻击者可以同时替换 release feed 和包；单独的 feed SHA-256 不能阻止恶意包注入。发布目录仍应使用最小写权限、不可变版本文件和最后覆盖 release feed 的上传顺序，但这些运维措施不能替代独立签名验证。

## 发布脚本

新增 PowerShell 发布脚本，参数包括：

- `Version`（必填）；
- `AcceptanceFeedPath`（必填绝对本地目录）；
- `ReleaseNotesPath`；
- `OutputDirectory`；
- 必须同时显式指定 `OtaAcceptanceBuild` 与 `AllowUnsignedLocal`。

本轮脚本不接受生产 feed 或签名参数，也不生成待上传的正式资产。后续服务器与真实性方案到位时再扩展生产参数；届时必须把 `PinnedPublisher` 或 `SignedFeed` 的实际校验作为可执行门禁。

步骤：

1. 校验干净的输出目录、版本和参数；重复版本检查只相对于传入的历史 release feed/资产和当前输出目录生效。
2. `dotnet publish` 为 `win-x64` 自包含 Release 注入产品版本和更新地址。
3. 运行现有 managed-browser-helper Release 完整性门禁。
4. 调用锁定版本的 `vpk pack`，生成 Setup、Portable、full/delta nupkg 和 release feed。
5. 重新读取 release feed，验证包名、版本、大小和 SHA-256。
6. 生成上传清单和简短中文说明。
7. 不自动连接或上传服务器；本轮产物仅供本机验收，不能上传为正式更新。

为了生成差分包，发布脚本允许把上一版 release 资产复制到工作输出目录；没有上一版时生成首个完整包。

## 错误处理

- 启动检查的网络错误保持静默，不影响软件使用。
- 手动检查展示稳定中文错误，可重试。
- 校验失败不进入待安装状态。
- 下载取消或应用关闭时不删除 Velopack 管理的有效缓存；无效 partial 由框架处理。
- 更新期间软件的其他功能仍可使用，只有最后应用更新时才请求关闭。
- 没有活动更新时，普通关闭流程与当前行为完全一致。
- 数据库迁移继续由现有启动恢复和备份逻辑保护。本集成不提供“新版本应用成功但启动失败”后的自动二进制回滚，也不承诺跨越不兼容数据库迁移回滚；发布要求仍应保持至少一个版本的数据库向后兼容。若新版无法启动，用户需要重新运行上一版安装包或修复包，程序数据保持在独立本地数据目录中。

## 测试策略

### 单元和结构测试

- 产品版本、开发回退和空更新源显示。
- 更新源只接受合规 HTTPS；生产 Release 拒绝命令行/本地目录覆盖；专用验收包 ID 和渠道不能与生产组合。
- 设置页各状态、按钮可用性、进度和重复点击。
- 启动静默检查不弹窗，手动检查显示结果。
- 并发检查和下载只有一个在途操作。
- 未下载、校验失败或不支持更新时不能登记重启。
- 设置中心替换占位页，导航保持不变。
- WPF 显式入口在应用初始化前调用 Velopack 引导。

### 关闭顺序测试

- 账号偏好持久化失败时不启动 updater。
- 队列或浏览器安全关闭失败时不启动 updater。
- 安全关闭成功且存在 pending 更新时只启动一次 updater。
- updater 启动失败不会手工覆盖或删除当前程序。
- 普通关闭不触发更新。

### 本地端到端验收

1. 生成 `1.0.0` 本地安装包并安装到当前用户目录。
2. 生成 `1.0.1` 到本地 feed。
3. 从 `1.0.0` 启动，确认自动检查不阻塞且设置中心发现 `1.0.1`。
4. 点击立即更新，确认进度、安全关闭、替换、重启和版本显示。
5. 断开/破坏 feed、修改包字节，确认继续运行旧版本且出现安全错误。
6. 构造一个可以被应用但无法正常启动的验收版本，记录 Velopack 实际行为并确认不会把“自动回滚”作为产品承诺；使用上一版安装包完成恢复演练。
7. 确认全过程只使用 `BaiduBatchTransfer.OtaAcceptance` 数据根，正式数据库、账号会话、缓存和任务记录的时间戳与哈希均未变化。

真实服务器地址到位后，客户端交互和更新服务抽象保持不变；需要增加 HTTPS 连通性、缓存头、Range 请求和部署后 SHA-256 核对，并完成上述发布者一致性黑盒验证。若 Velopack 不强制发布者一致，还必须实现并验收独立真实性校验，才能启用生产 OTA。

## 验收标准

- 设置中心不再是占位页，更新状态清晰且与现有界面风格一致。
- 未配置服务器时，普通开发和 Release 构建、启动均不受影响。
- 本地 Velopack 安装版可完成 `1.0.0 → 1.0.1` 检查、下载、退出、替换、重启。
- 更新操作不会绕过现有安全关闭门禁。
- 发布脚本能可重复地产出本地验收资产，并在独立真实性方案尚未实现时拒绝一切生产 OTA 输出。
- 相关聚焦测试和 Release 构建通过，已有无关脏工作树改动不被覆盖或提交。
