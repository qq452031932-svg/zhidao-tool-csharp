# 2026-09-06 Web OTA 发布后台阶段性验收

## 结论

`https://ota.fanfh.cn/admin/` 的隔离发布后台已经部署并通过服务、权限、反向代理和既有站点不变性检查。生产稳定渠道尚未发布任何版本：首次管理员改密、正式 `1.0.6` 上传/确认发布、客户端更新验收和停服静态下载验收仍未执行。因此当前状态是“后台可用、发布门禁保持关闭”，不能标记为 OTA 全链路上线完成。

本记录不包含管理员一次性密码、正式密码、HMAC、会话 Cookie、私钥或其他秘密。验收期间没有读取或输出管理员密码。

## 已通过的本地与隔离验证

- Windows 更新测试：`dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~Updates` 共 `370` 项，`368` 通过、`2` 项因本机证书存储环境跳过、`0` 失败。
- Linux 服务端全包测试：在独立 CentOS 7 验证根执行 `CGO_ENABLED=0 go test -shuffle=on -count=10 ./...`，所有包通过，退出码为 `0`。
- 官方不可变 Git-object 快照构建通过了 `go test -p=1 ./...`、`go vet ./...`、静态 `linux/amd64` 交叉编译、源码快照指纹复核和原子产物发布。
- Linux 后台二进制 SHA-256：`80627c3962d8f858cfedf5432ea06751644ef5796faee830ef985a2f10c63219`。
- 可审计源码包 SHA-256：`ae1217c301ad152d301ac21cea9b5d5a71d9471de71d97509ac3c04e385f7e36`。
- 公共发布证书 SHA-256：`9db0923f308826fcd16f1147ee7843446184d7efa4c4ccd83e9650c95c625b82`；固定 SPKI SHA-256：`f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff`。
- 真实签名候选在隔离 Linux 发布根完成全部七个 durability boundary 故障注入与重启恢复验证：`file_synced`、`candidate_dir_synced`、`generation_renamed`、`generations_dir_synced`、`stable_temp_created` 在提交前失败并幂等恢复；`stable_replaced`、`win_x64_parent_synced` 在提交后恢复为已发布。验收二进制及所有隔离发布根已在取证后删除。
- 部署结构测试 `OtaDeploymentStructureTests` 为 `7/7` 通过；服务器原生 `bash -n deploy/ota-admin/install.sh` 通过。

## 部署前基线

- OTA vhost SHA-256：`3b6b8f00b9476aca0edbda97c69077acf793ae68f41cf5c8cc7f8c3ef25a0210`。
- OTA `/health` 响应 SHA-256：`31e6dc23df83ebfed036bba3fda706b8f1e8c5c7fef9ef0a602e6fac43c71cfb`。
- OTA TLS 叶证书 DER SHA-256：`a8991c8980169a67245942fd4153cb68c392a7cbf00b1b40a504d6752a444faa`。
- `f.fanfh.cn` vhost SHA-256：`1df7a78877eb37a896166c900b23a7f30c27c8dc959949f6ed6939116d09e05b`。
- `f.fanfh.cn` fullchain 文件 SHA-256：`67872e19e42938a94f371c46b8949f33161c9dbca5c7953b5c01e3c9c0a02434`。
- Windows 可信外部请求取得的 `f.fanfh.cn` 正文 SHA-256：`2d3495515de1c91186a799994d494e261efda5adeb7e4d2ac16d85bb373e2bfb`。
- `releases/win-x64/stable` 是 `www:www 0755` 的空真实目录；服务器没有后台服务、配置、数据库或已提交版本 floor；`127.0.0.1:18091` 未占用。
- CentOS 7 自带 curl/NSS 对 `f.fanfh.cn` 报旧客户端 key-usage 兼容错误，因此没有用 `-k` 伪造可信正文基线；正文基线来自 Windows 可信 TLS 请求，服务器端保留 vhost、证书文件和公开 TLS 检查。

## 生产部署与当前状态

部署前再次核对 vhost、健康页、证书、空发布目录、端口和 `nginx -t`，均未漂移。原发布目录 metadata 和原 OTA vhost 已保存到 root-only 暂存；确认目录内容和 inode 未变后，只把空的 `win-x64` 所有者从 `www:www` 调整为安装器允许接管的 `root:root`，模式仍为 `0755`，Nginx 读取权限未变。

第一次安装在二进制/证书/SPKI、`nginx -t`、systemd 服务和 loopback readiness 均通过后，暴露宝塔 CentOS 7 的真实兼容差异：`nginx.service` 是 inactive 的 SysV generator unit，`systemctl reload nginx` 返回 `Job for nginx.service invalid`。Nginx 没有 reload，公网 `/admin/` 当时仍为 `404`；精确备份保存在：

- `/var/backups/superpan-ota-admin/20260906T122110Z.gyZATC`

安装器随后改为在 `nginx -t` 之后通过绝对路径执行 `/www/server/nginx/sbin/nginx -s reload`，并校验 reload 前后真实 master PID 不变且本机 HTTPS `/admin/` 已生效。修复提交 `58359cb` 已通过独立复审；幂等重试成功，第二份精确备份为：

- `/var/backups/superpan-ota-admin/20260906T122927Z.AftBAe`

最终只读核对结果：

- `superpan-ota-admin.service` 与 `var-lib-otaadmin-releases.mount` 均为 `active`、`enabled`。
- 发布目录通过 bind mount 暴露给后台；服务状态/暂存目录为专用用户 `0700`，配置、公开证书和 HMAC 为 `root:superpan-ota-admin 0640`，一次性密码文件为 `root:root 0600`。
- 后台只监听 `127.0.0.1:18091`；未发现 `0.0.0.0:18091` 或 `[::]:18091` 监听。
- `/www/server/nginx/sbin/nginx -t` 通过；reload 前后的真实 master PID 均为 `9117`，进程名为 `nginx`。
- `https://ota.fanfh.cn/admin/` 返回 `200`，同时具有 `Cache-Control: no-store, max-age=0`、CSP 和 `X-Frame-Options: DENY`。
- 已在用户可见的 Codex 应用内浏览器打开真实 `https://ota.fanfh.cn/admin/` 页面并读取无障碍树：页面标题为“SuperPan OTA 发布后台”，连接状态为“已连接”，登录表单包含管理员账号、密码和登录按钮；不是截图或本地 mock。
- OTA vhost 仅增加一次 `/etc/superpan-ota-admin/ota.fanfh.cn.admin.nginx.conf` include；部署后 SHA-256 为 `23046876bdd83b91eb64156c6b8a1ec7eaa321bcb42f14db3dcedbab58971786`。
- OTA `/health`、TLS 叶证书、`f.fanfh.cn` vhost 和 fullchain 哈希与部署前完全一致。
- 旧空 `stable` 目录已按迁移逻辑移除，当前发布树只有空的 `generations` 目录；尚无 `stable` 相对链接、release JSON、RELEASES、Setup、Portable 或任何正式版本。
- 服务日志只有安装/幂等重试产生的正常 stop/start 和 loopback listen 记录，没有发布或恢复错误。

## 已准备但未上传的正式候选

本机已生成正式签名候选：

- 版本：`1.0.6`
- 文件：`.artifacts/ota-production-20260906-task5-v21-final/SuperPan.BaiduBatchTransfer-1.0.6-win-x64-stable-upload.zip`
- 大小：`762190421` 字节
- SHA-256：`83d4e8723f201499793d218bc3512439c00e56403a0eac5174bf5c05f46ec592`

该文件没有上传到服务器，服务器发布树也没有写入其任何内容。

## 尚未完成的生产验收

以下步骤必须后续显式完成，当前不得写成已通过：

1. 在真实 `/admin/` 页面使用一次性凭据登录，由用户完成首次密码修改；不得把密码写入 Git、文档、日志或聊天记录。
2. 从真实网页选择上述 `1.0.6` ZIP，等待七项固定 validation gate 全部显示明确证据，再执行一次明确发布确认。
3. 发布后核验 release JSON、manifest/signature、asset catalog、RELEASES、Setup/Portable、哈希/大小/引用闭包、相对 `stable` 链接和全部缓存头。
4. 用客户端固定 SPKI 的 SignedFeed 链路验证接受正式 feed，并验证错钥、坏签名、元数据/包篡改和已签名 replay 均在 updater 启动前被拒绝。
5. 完成检查更新、下载、pending-update 的不重启 dry run；不要在未确认前强制用户应用重启。
6. 停止后台服务，证明 Nginx 静态 stable 仍可下载；随后启动服务，验证恢复、旧会话失效和 read-only 状态清除。
7. 在发布存在后执行完整 `deploy/ota-admin/verify.sh`，复核 stable 下载哈希、元数据 no-cache、版本资产 immutable、端口隔离、停服恢复 trap 和本机/公网 TLS。
8. 重新取得部署后的 Windows 可信 `f.fanfh.cn` 正文哈希，并执行既有证书续期 dry-run，补齐三项不可变基线与续期健康的最终证据。
完成以上步骤前，最终门禁仍是关闭状态；不得把生产 OTA 标记为完整上线。

## 2026-09-07 八位密码最低长度更新

本次仅更新隔离的 `ota.fanfh.cn` OTA 管理后台，使管理员密码最低长度由十六位调整为八个 Unicode 字符；1024 字节上限及其他安全控制均未改变。没有上传或发布 `1.0.6`，没有创建 `stable`，没有修改 `f.fanfh.cn` 业务文件，也没有读取、输出、哈希或修改管理员密码、初始密码、HMAC、Cookie 或私钥。

部署前的本地全量 `go test ./... -count=1` 覆盖 `15` 个 Go 包并全部通过，`go vet ./...` 通过。部署使用的正式 Linux/amd64 二进制 SHA-256 为 `1e76d2e504ccef00bd6b43ad8e1d0fa0a8fe82ca07b26686535318cd4224cd73`，可审计源码包 SHA-256 为 `888a383fd45cb74a9f4e3d5b9d32507afa0653a8b3db7ed59ab365075f1596b9`；公共发布证书 SHA-256 仍为 `9db0923f308826fcd16f1147ee7843446184d7efa4c4ccd83e9650c95c625b82`，固定 SPKI SHA-256 仍为 `f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff`。

生产变更前重新建立并核对基线：服务与发布目录挂载均为 `active`、`enabled`，后台仅监听 `127.0.0.1:18091`；OTA vhost SHA-256 为 `23046876bdd83b91eb64156c6b8a1ec7eaa321bcb42f14db3dcedbab58971786`，公网 `/health` SHA-256 为 `31e6dc23df83ebfed036bba3fda706b8f1e8c5c7fef9ef0a602e6fac43c71cfb`，TLS 叶证书 DER SHA-256 为 `a8991c8980169a67245942fd4153cb68c392a7cbf00b1b40a504d6752a444faa`。`f.fanfh.cn` vhost、fullchain 和 Windows 可信正文基线分别仍为 `1df7a78877eb37a896166c900b23a7f30c27c8dc959949f6ed6939116d09e05b`、`67872e19e42938a94f371c46b8949f33161c9dbca5c7953b5c01e3c9c0a02434`、`2d3495515de1c91186a799994d494e261efda5adeb7e4d2ac16d85bb373e2bfb`；可信正文为 `200`、`60838` 字节。

发布树基线包含空的 `generations`，以及部署前已存在且内容为空的 `.Recycle_bin`。后者以目录类型、`root:root`、模式 `0600`、大小、修改时间和空内容作为明确允许基线；安装器执行前后完整发布树逐行一致，两目录均保持为空，没有新增版本资产。

生产数据库通过 SQLite Online Backup API 分别取得变更前后的一致快照，并以项目锁定的现代 SQLite 驱动完成 `PRAGMA quick_check`。除允许因服务重启变化的 `sessions`、`audit_events` 和 `sqlite_sequence` 外，数据库 schema 与全部表行完全一致；管理员密码哈希、`must_change_password`、永久版本 floor、releases/assets、candidates、operations、leases、cleanup 和 failure 状态均未改变。HMAC、初始密码文件和管理配置仅使用 `cmp` 做等同性判断，三项均通过且未输出内容或哈希。

幂等安装器成功完成更新，保留的 root-only 回滚备份为：

- `/var/backups/superpan-ota-admin/20260907T045239Z.tjZX1A`

更新后 `superpan-ota-admin.service` 与 `var-lib-otaadmin-releases.mount` 仍为 `active`、`enabled`，端口仍仅绑定 loopback；部署二进制与预期 SHA-256 一致，Nginx 配置检查通过。真实公网 `https://ota.fanfh.cn/admin/` 返回 `200`，保留 `Cache-Control: no-store`、CSP 和 `X-Frame-Options: DENY`，页面包含“至少 8 位”且不包含“至少 16 位”。OTA `/health`、TLS 证书、两个 vhost、`f.fanfh.cn` fullchain、可信正文及完整业务目录 metadata 均与变更前一致。更新时间窗内服务日志的秘密关键词扫描为零。

远端部署暂存目录在确认绝对路径、`0700 root:root`、无符号链接且无挂载后已删除；临时只读数据库校验器也已从本机忽略目录删除。安装器备份继续保留。生产稳定渠道仍未发布版本，上一节列出的首次登录改密、正式候选上传发布及客户端验收步骤仍未完成。

## 2026-09-07 首次 `1.0.6` 生产发布与 WAL 锁修复

用户在真实 `https://ota.fanfh.cn/admin/` 登录后，上传并明确确认发布重新从当前审核源码构建的 `1.0.6`。上传包为 `287037100` 字节，SHA-256 为 `805018e169f2ff2561b6fa73ee2c137b0898cfb8ef5cedf954a7f2eb5e8ffb53`；后台七项校验全部通过。生产 `stable` 当前指向 `generations/release-d32d1b26-f69c-4664-9c17-d93b8e7b4130`，包含三个 feed/catalog 文件、Setup、Portable、full nupkg、manifest 与 manifest 签名共八个常规文件。`upload-manifest.sha256` 引用的六项资产均通过服务器端 `sha256sum -c`，公网逐文件哈希与本地上传包一致，RSA 签名验证通过。

发布后验收发现服务进程仍持有已 unlink 的非空 `state.db-wal`，路径上的 WAL 则为另一 inode 的空文件；外部只读连接因此暂时看不到已经提交的发布账本。根因定位为 Linux `store.Open` 在 SQLite 打开后执行安全校验时，对 DB/WAL/SHM 使用裸 `open/close`，触发 POSIX“同进程关闭同 inode 任一 fd 会释放该进程全部 record locks”的语义。修复提交 `815faac` 将 post-open 校验改为 `Fstatat(AT_SYMLINK_NOFOLLOW)`，保留 owner、类型、模式、链接数及主库 dev/inode 校验但不再打开目标 fd；生产 unit、安装器、验收程序和 CentOS probe 统一使用 `umask 077`，安装器也会在任何外部 `init-admin` SQLite 打开之前先停止原 active 服务，并在安装失败时恢复原状态。

恢复前在服务短暂 `SIGSTOP` 的一致窗口内，从 `/proc/27373/fd/6` 与仍存活的 deleted WAL fd10 取得主库和 WAL 字节快照并逐一 fsync；原始证据保存在 root-only `/root/ota-recovery-evidence-20260907T0618Z`，早期对照证据保存在 `/root/ota-postpublish-evidence-20260907T0606Z`。不复制 SHM 作为恢复输入。两个独立 clone 均由现代 SQLite 完成 WAL replay/checkpoint；`integrity_check=ok`、schema v6、floor/current release `1.0.6`、六项 release asset、published candidate、upload/publish 两项 succeeded/completed operation、零 pending/lease 均通过，关闭后仅靠主库再次打开仍得到相同业务状态。

在恢复副本验证完成后执行受控 graceful stop；SQLite 成功把 orphan WAL 落回主库并移除 sidecar，因此没有执行数据库替换。停服后的主库复制品再次得到 `integrity_check=ok`、schema v6、floor/current `1.0.6`、六项 asset、published candidate、全部 operation terminal、零 pending。随后部署 `815faac` 构建的 Linux/amd64 修复二进制，SHA-256 为 `27ac208e19a60c3db1c11532522c1e9d2264211d6e659ebdab0086cbffba4e25`；安装器备份为 `/var/backups/superpan-ota-admin/20260907T070515Z.JT1qku`。

修复门禁包括：不可变 Git archive 源码快照的 `go test ./... -count=1` 和 `go vet ./...` 全通过；Windows `OtaDeploymentStructureTests` 为 `7/7`；Linux store 回归二进制在真实 CentOS 7 上对 POSIX 锁保持和 sibling SQLite open/close 后 WAL/SHM inode 不变连续运行十轮并通过。重启后的生产进程二进制哈希与预期一致，unit 为 `UMask=0077`，DB/WAL/SHM 均为服务用户 `0600`、nlink 1，三个 fd 均指向有名字的当前文件；再执行一次外部 PHP SQLite read-only open/close 后 WAL/SHM inode 保持不变。服务和 bind mount 均为 active/enabled，后台只监听 loopback，loopback 与公网 `/admin/` 均返回 `200`，Nginx 配置检查通过，staging 已为空。

`f.fanfh.cn` vhost SHA-256 仍为 `1df7a78877eb37a896166c900b23a7f30c27c8dc959949f6ed6939116d09e05b`，可信正文 SHA-256 仍为 `2d3495515de1c91186a799994d494e261efda5adeb7e4d2ac16d85bb373e2bfb`；OTA vhost SHA-256 仍为 `23046876bdd83b91eb64156c6b8a1ec7eaa321bcb42f14db3dcedbab58971786`。本次只删除了三个本任务创建并已核实为 root-only 的临时测试/部署目录；两份恢复证据与安装器回滚备份继续保留。服务重启按设计撤销旧后台会话，浏览器需要重新登录一次。

客户端固定 SPKI 的真实生产 feed 读取测试已接受 `1.0.6` 并识别 canonical full nupkg；尚未执行真实已安装客户端的下载、应用更新和自动重启，因此这三项仍属于后续客户端验收，不应写成已经通过。
