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
