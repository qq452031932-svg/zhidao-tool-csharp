# OTA 发布后台部署

本文只覆盖现有 `ota.fanfh.cn` HTTPS 静态 OTA 站点上的 `/admin/` 发布后台。现有 `/health`、ACME 验证、证书续期和 `releases/win-x64/stable` 静态服务均保留原配置；发布后台停止时，稳定渠道仍由 Nginx 直接下载。

## 部署边界

- 服务仅监听 `127.0.0.1:18091`，公网只通过现有 HTTPS server block 中的 `/admin/` 反向代理进入。
- 使用独立的 `superpan-ota-admin` 用户和组。配置及密钥位于 `/etc/superpan-ota-admin`，状态及暂存位于 `/var/lib/superpan-ota-admin`，二进制位于 `/opt/superpan-ota-admin`。
- Nginx 文件是 server block 内的 include 片段，不是完整虚拟主机。它不能替换现有 vhost，也不包含 `/health`、ACME、证书、站点根或静态发布规则。
- `/admin/` 强制 `Cache-Control: no-store`。现有静态配置必须继续对 `upload-manifest.sha256` 及 `upload-manifest.sha256.sig` 返回 no-cache；安装器不会修改该静态缓存规则。
- systemd unit 只使用 CentOS 7 systemd v219 支持的隔离指令。独立 mount unit 先把唯一的 OTA 发布目录 bind 到 `/var/lib/otaadmin/releases`，后台只通过该别名发布；随后服务 namespace 屏蔽整个 `/www/wwwroot`，因此看不到其他站点。这里没有使用 v219 不支持的“屏蔽父目录后再对子目录开洞”。

## 部署前记录只读基线

在修改服务器前记录以下小写 SHA-256；部署后的 `verify.sh` 要求显式传入，脚本不会写入或更新基线：

```bash
EXPECTED_CERT_SHA256="$(echo | openssl s_client -connect ota.fanfh.cn:443 -servername ota.fanfh.cn 2>/dev/null | openssl x509 -outform DER | sha256sum | awk '{print $1}')"
EXPECTED_HEALTH_SHA256="$(curl --fail --silent --show-error https://ota.fanfh.cn/health | sha256sum | awk '{print $1}')"
STABLE_PROBE_URL="https://ota.fanfh.cn/releases/win-x64/stable/RELEASES-win-x64-stable"
EXPECTED_STABLE_SHA256="$(curl --fail --silent --show-error "$STABLE_PROBE_URL" | sha256sum | awk '{print $1}')"
export EXPECTED_CERT_SHA256 EXPECTED_HEALTH_SHA256 STABLE_PROBE_URL EXPECTED_STABLE_SHA256
```

若稳定渠道尚为空，先完成经过签名验证的首个发布，再执行“后台停止时仍可下载”的最终验收；不要放入伪造的生产文件来绕过检查。可另行设置 `STATIC_ASSET_URL` 与 `EXPECTED_STATIC_CACHE_CONTROL`，证明既有不可变资产缓存头未被改变。

## 安装

### 授权服务（本地/预演阶段）

授权服务安装脚本位于 `deploy/license-service/install.sh`，只绑定
`127.0.0.1:18092`，并使用独立的 `superpan-license` 用户、配置目录和数据目录。
在本地验证阶段仅运行契约测试和离线夹具；不要把该脚本直接用于生产主机。生产或
staging 执行前必须单独确认主机、快照和回滚边界，并保留安装前后的脱敏哈希证据。

公共 Nginx 路由片段在授权服务安装和验收完成前不得加入；安装脚本不会自动修改
现有 vhost，也不会执行 Nginx reload。

授权服务安装现在是一个覆盖授权服务与既有 OTA 后台的单一事务。所有 bundle、模板、
source commit、现有身份、ACL、密钥配对状态以及升级时的维护工具都在任何修改前完成
校验。事务使用固定锁，且每次只在 `/var/backups/superpan-license/<transaction-id>`
创建一个 `root:root`、`0700` 备份。`inventory.tsv` 精确记录两个服务的 binary、config、
unit 以及授权服务 source-commit 文件原先的 present/absent、数字 owner/group、mode 和
SHA-256；`unit-state.tsv` 记录两个服务原先的 enabled/active 状态。`manifest.sha256`
覆盖备份中的每个允许文件。数据库和密钥只以配对身份及指纹写入审计记录，不作为普通
恢复载荷。旧授权 binary、source-commit 和既有 config 在备份前还必须通过从 `/` 到 leaf
的 root-owned、非 group/world-writable、无 symlink 信任链检查；binary leaf 必须是单链接
`root:root` `0755`。备份副本的 inode metadata 与 SHA-256 会再次核对，旧 binary 做 schema
probe 前还会重新验证固定备份根、leaf metadata、config metadata 与预先记录的 hash。

升级已有 schema 时，安装器在恢复 trap 已武装后短暂停止旧授权服务，使用与 bundle
相同 source commit 的 `/opt/superpan-license-maintenance/license-db-snapshot` 生成一致快照，
并立即恢复原 active 状态。新 binary 只在 `127.0.0.1:28092` 针对快照副本完成迁移和
健康检查；再次 checkpoint 后再由旧 binary 对迁移副本完成健康和只读表计数验证。迁移
前已有的每个业务表计数必须在迁移后仍存在且不得减少，迁移新增表可以新增计数；摘要中
缺失、重复或非数字的 aggregate 均会拒绝，旧 binary 读到的迁移后 aggregate 必须与新
binary 一致。完整有效密钥与数据库已存在而旧 binary 缺失时按 `interrupted-initial`
恢复：只用同 commit 的新 binary 对一致快照执行 quick-check 与相同语义验证，不执行不
存在的旧 binary。Windows 本地只验证脚本合同，以上 ELF/systemd 流程仍须在获批的 Linux
staging 快照上执行。

正式提交顺序固定为：授权 binary、渲染后的 config、unit、启动与
`127.0.0.1:18092/internal/health`；随后调用 OTA installer 的受控事务模式，仅替换同一
commit 的 OTA binary/config/unit，重启并检查 `127.0.0.1:18091/admin/`。该受控路径不
创建嵌套备份，不安装或读取授权服务公共路由片段，不修改 vhost，也不执行 Nginx 检查
或 reload。最终再次记录并核对两个已安装 binary 的 SHA-256 与 source commit。

授权 unit 的 enable 与运行版本验证是两个独立步骤：安装器先 `enable`，再显式
`restart`，最多重试 30 次 loopback health。每次成功候选还必须取得非零 MainPID，确认
MainPID 在检查前后未变化，并验证 `/proc/<MainPID>/exe` 的 SHA-256 等于刚安装且已认证的
授权 binary；旧进程即使仍能返回健康也不能通过此门。

完整密钥集已经原子发布、但数据库尚未由服务创建，是中断后可安全重试的首次安装状态，
审计记录明确写作 `database absent`。部分密钥集，或数据库存在但密钥集缺失/异常，仍会
在修改前被拒绝。

事务失败会保留原退出码并调用 `deploy/license-service/rollback.sh`。rollback 在恢复前
重新校验固定备份路径、ACL、manifest、allowlist、每个 payload 的 hash 和 metadata，
按 present/absent 恢复或删除七个枚举目标，执行 daemon-reload，再恢复两个服务原先的
enabled/active 状态；数据库与密钥绝不覆盖或删除。若 rollback 自身失败，备份会保留，
两个服务保持停止，并要求人工修复。

若 unit 原先为 `not-found`，rollback 会在恢复 inventory、删除本次 unit 并执行
daemon-reload 后，显式 disable，并只对两个固定的 `multi-user.target.wants` 路径验证
symlink 目标后清理由本事务创建的链接；最后重新查询 `is-enabled`，必须恢复到记录的
`not-found`（原先为 `disabled` 时则必须仍为 `disabled`）。

为保证 enablement 可精确恢复，安装器在任何 mutation 前只接受 `enabled`、`disabled`
或 `not-found`，并同时验证两个固定 wants link 与该状态一致。`static`、`indirect`、
`generated`、`transient`、`alias`、`linked*`、runtime-only、masked 等状态均 fail closed。
rollback 对记录的 wants `present/absent` 统一执行固定路径和目标验证，需要时创建或删除
链接，再复核 `is-enabled` 与记录完全一致。

1. 在现有 `ota.fanfh.cn` HTTPS `server {}` 内只加入一次下列 include 行。先备份该 vhost，除此之外不复制或改写其他站点配置。片段本身也对非 HTTPS 请求 fail closed，但仍必须只放在 HTTPS block。

   ```nginx
   include /etc/superpan-ota-admin/ota.fanfh.cn.admin.nginx.conf;
   ```

   安装器会在写入文件后执行 `nginx -t`，只有成功才 reload。由于 include 文件尚未安装，此时不要提前 reload。

2. 在可信构建机计算 Linux 二进制和公开发布证书的 SHA-256，并根据已审核证书计算 SPKI pin：

   ```bash
   sha256sum ./ota-admin ./publisher.cer
   openssl x509 -inform DER -in ./publisher.cer -pubkey -noout | openssl pkey -pubin -outform DER | sha256sum
   ```

   计算结果必须等于编译进服务和配置模板的生产 pin `f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff`；安装器会拒绝其他 SPKI。

3. 手工加入 include 后审核完整 vhost，并记录当前文件的小写 SHA-256。安装器会在任何修改前验证它，防止部署窗口内配置被替换：

   ```bash
   ACTIVE_VHOST_SHA256="$(sha256sum /www/server/panel/vhost/nginx/ota.fanfh.cn.conf | awk '{print $1}')"
   ```

4. 将整个 `deploy/ota-admin` 目录、二进制和公开证书放入 root-only 暂存目录，然后运行：

   ```bash
   sudo bash deploy/ota-admin/install.sh \
     --binary ./ota-admin \
     --binary-sha256 '<审核过的二进制 SHA-256>' \
     --certificate ./publisher.cer \
     --certificate-sha256 '<审核过的证书 SHA-256>' \
     --publisher-spki-sha256 '<审核过的证书 SPKI SHA-256>' \
     --active-vhost-sha256 "$ACTIVE_VHOST_SHA256"
   ```

安装器在任何持久化修改前校验二进制、证书及 SPKI；重复执行会保留已有 HMAC 密钥和数据库。配置为 `root:superpan-ota-admin`、`0640`，状态目录为服务用户独占的 `0700`。32 字节 source HMAC 密钥只写入 root 管理的配置目录，不输出。管理员初始化以数据库中的管理员记录为准，而不是以数据库文件是否存在为准；幂等重试可修复“数据库已创建但管理员尚未写入”的中断状态。一次性密码只写入 root-only 的 `/root/superpan-ota-admin.initial-password`，不会经过 systemd 或 journald；安全的中断临时文件会恢复或清空后重试。

每次执行只逐个备份管理后台的二进制、service/mount unit、include、配置、公开证书、HMAC、一次性密码文件，以及本次手工加入 include 后的 OTA vhost 和只读哈希；两个 unit 原先的 enabled 与 active 状态分别写入 `unit-enabled-state.txt` 和 `unit-active-state.txt`。数据库不会回滚，因此 HMAC 和一次性密码无论原先存在还是本次新建，都使用独立的 `persistent-existing-targets.txt` / `persistent-new-targets.txt` 审计账本，绝不进入 present/absent 恢复清单；回滚保留它们的安装后值，使其与保留的数据库同生命周期。`.new` 密码临时文件只在 `transient-targets.txt` 记录是否存在，不备份也不恢复，避免旧临时密码与保留的新管理员哈希错配。备份固定存放在校验为 `root:root`、`0700` 的 `/var/backups/superpan-ota-admin`，不接受任意备份根参数。成功与备份后发生的任何失败都会显示精确备份目录和回滚提示。安装器不会改写 vhost 的任何内容。发布代、stable 链接和静态站点不进入安装备份。`releases/win-x64` 与 `generations` 由专用服务用户拥有并保持 `0755`：只有 owner 可发布，Nginx 仍可只读下载，且满足服务启动时对 group/other 不可写的校验。

安装器重启服务后先等待 `systemctl is-active` 和 loopback `/admin/` 同时就绪，最多 30 秒；只有就绪且 `nginx -t` 已通过，才通过宝塔 Nginx 的绝对路径执行 `nginx -s reload`。安装器在 reload 前后绑定并检查真实 master PID，并等待本机 HTTPS `/admin/` 生效；服务未就绪、master 异常或代理未生效时会失败并保留精确备份/回滚信息。它不依赖 CentOS 7 上可能仅由 SysV generator 生成且不支持 reload 的 `nginx.service`。

## 授权公网路由

授权服务安装、回滚、容量、客户端签名和双 SPKI 信任检查全部通过后，才允许执行公网路由事务。先捕获固定目录下的只读基线，再以审核过的片段哈希启用：

```bash
BASELINE="$(sudo bash deploy/license-service/capture-baseline.sh | sed -n 's/^LICENSE_ROUTE_BASELINE=//p')"
SNIPPET_SHA256="$(sha256sum deploy/license-service/ota.fanfh.cn.license-api.nginx.conf | awk '{print $1}')"
sudo bash deploy/license-service/enable-public-route.sh \
  --mode enable --baseline "$BASELINE" \
  --snippet deploy/license-service/ota.fanfh.cn.license-api.nginx.conf \
  --snippet-sha256 "$SNIPPET_SHA256"
sudo bash deploy/license-service/verify.sh --baseline "$BASELINE"
```

脚本只在现有 HTTPS server block 的管理后台 include 前加入一次固定授权 include 行。它先校验原 vhost、基线清单和片段哈希，再执行 `nginx -t` 和 reload，并确认 Nginx master PID 不变。任何失败都会恢复本次事务开始时的 vhost/include；不会修改其他站点、静态发布内容、数据库或证书。

回退公网路由时使用相同基线：

```bash
sudo bash deploy/license-service/enable-public-route.sh --mode restore --baseline "$BASELINE"
```

恢复模式只接受“当前 vhost 与基线相比恰好多出受管 include 行”的状态；发现其他并发改动会停止，不会覆盖它们。

## 验证

确认环境中仍有部署前导出的三个基线值，再执行：

```bash
sudo --preserve-env=EXPECTED_CERT_SHA256,EXPECTED_HEALTH_SHA256,EXPECTED_STABLE_SHA256,STABLE_PROBE_URL \
  bash deploy/ota-admin/verify.sh
```

验证脚本检查：systemd 服务状态；端口只在 loopback 监听且公网不可达；HTTP `/admin/` 只能跳转到 HTTPS；HTTPS `/admin/` 为 no-store；两份签名元数据不可缓存；证书、`/health` 和稳定文件与部署前哈希一致。站内 HTTP/HTTPS 检查通过 `curl --resolve` 固定到 `127.0.0.1`，验证当前机器的 Nginx，不受公网 DNS/CDN 影响；证书基线同时检查本机 `127.0.0.1:443`（保留 SNI/主机名验证）和公网 TLS。脚本随后先武装恢复 trap 再停止后台服务、再次下载稳定文件及检查健康页，并无论中途成功或失败都恢复后台服务；恢复失败会让验证明确失败。它只读取基线，不修改证书、健康页或静态缓存配置。

## 回滚

安装器会打印本次精确备份目录和恢复命令。发生问题时：

1. 停止 `superpan-ota-admin.service`。
2. 只恢复 `present-targets.txt` 中列出的精确文件，并只删除 `absent-targets.txt` 中本次新建的精确目标。
3. 根据 `release-directory-metadata.txt` 中记录的数字 uid、gid、mode 恢复两个发布目录的元数据。若记录显示 `generations` 原先不存在但现在含有发布内容，不得删除它。
4. 根据 `unit-enabled-state.txt` 恢复 service 和 mount unit 原先的 enabled/disabled 状态，并根据 `unit-active-state.txt` 恢复原先的 active/inactive 状态；首次安装前不存在的 unit 应 disable 并移除其 wants 链接，不得无条件启动。不要用备份覆盖 `persistent-existing-targets.txt` 或删除 `persistent-new-targets.txt` 中的 HMAC/一次性密码；必须保留安装后的凭据，因为数据库会保留。
5. 对现有 OTA vhost 移除本次增加的 include 行；不要替换整个 vhost。
6. 执行 `/www/server/nginx/sbin/nginx -t`，成功后才执行 `/www/server/nginx/sbin/nginx -s reload`；不要依赖宝塔环境中可能不支持 reload 的 `nginx.service`。
7. 执行 `systemctl daemon-reload`。需要回到旧后台版本时再启动服务。

回滚后台不得删除或回滚 `releases/win-x64` 下已经发布的 generation 或 stable 链接。若配置检查失败，保持 Nginx 不 reload，并先恢复 include 片段或移除 include 行。
