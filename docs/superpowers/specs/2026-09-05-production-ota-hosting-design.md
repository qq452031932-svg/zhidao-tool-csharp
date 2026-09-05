# SuperPan 生产 OTA 托管设计

## 目标

在现有宝塔/Nginx 服务器上为 `ota.fanfh.cn` 建立完全独立的静态 OTA 站点，不修改 `f.fanfh.cn` 的站点目录、应用、数据库或虚拟主机配置。

## 已确认环境

- `ota.fanfh.cn` 已解析到 `106.52.168.109`。
- 服务器为 CentOS 7，使用宝塔目录结构和 `/www/server/nginx/sbin/nginx`。
- 现有业务站点为 `f.fanfh.cn`，根目录 `/www/wwwroot/f.fanfh.cn/public`。
- 当前不存在 `ota.fanfh.cn` 的独立 Nginx 虚拟主机。

## 隔离结构

- 站点根：`/www/wwwroot/ota.fanfh.cn`
- 稳定渠道：`/www/wwwroot/ota.fanfh.cn/releases/win-x64/stable`
- 独立配置：`/www/server/panel/vhost/nginx/ota.fanfh.cn.conf`
- 独立证书目录：`/www/server/panel/vhost/cert/ota.fanfh.cn`

站点只提供静态文件，不接入 PHP、数据库、Redis或现有业务反向代理。

## HTTP 与缓存

- HTTP 的 `/.well-known/acme-challenge/` 先由 OTA 独立 webroot 提供，其余请求跳转 HTTPS。
- HTTPS 支持 `GET`、`HEAD`，目录浏览关闭。
- `releases.*.json`、`RELEASES-*` 和清单文件响应 `Cache-Control: no-cache, must-revalidate`。
- 只有文件名包含精确版本或内容哈希的 `.nupkg`、Setup、Portable 资产使用长期 immutable 缓存；固定名称的 Setup/Portable 别名使用 `no-cache, must-revalidate`。
- 未匹配文件返回 404，不回落到现有站点。

## 发布边界

本地验收包 `SuperPan.BaiduBatchTransfer.OtaAcceptance` 不上传到生产稳定渠道。线上软件必须使用生产包 ID `SuperPan.BaiduBatchTransfer`、渠道 `win-x64-stable`，并内置 `https://ota.fanfh.cn/releases/win-x64/stable/`。

服务器部署先完成静态站点、TLS、目录和健康检查。没有经过生产发布门禁的资产不会写入稳定渠道，避免客户端拉取到验收包或半成品。

生产发布门禁采用 fail-closed：release JSON、asset catalog 和 RELEASES 中的 PackageId 必须精确为 `SuperPan.BaiduBatchTransfer`，channel 必须精确为 `win-x64-stable`；出现 Acceptance、其他 channel、未知顶层资产、外部引用或未引用资产时拒绝发布。所有引用文件必须存在，size 和 SHA-256 必须与元数据一致。不可变资产先上传到站点外的 staging 目录，验证完成后移动到发布目录；可变元数据最后以原子替换方式发布，并保留上一版元数据快照。

## 安全与回滚

- 修改前复制当前 Nginx 主配置及虚拟主机目录的只读备份。
- 候选 vhost 原子放到最终 include 路径后执行 `nginx -t`；失败时立即恢复/移除且不 reload，成功后才 reload。
- ACME 签发和续期只针对 `ota.fanfh.cn`，私钥仅 root 可读；续期后必须 `nginx -t` 成功才 reload，并验证证书 SNI、hostname 和到期时间。
- 部署后同时检查 `ota.fanfh.cn` 与 `f.fanfh.cn` 的证书和响应，确认现有站点未变化。
- 站点级回滚保留 OTA 专属 503/404 隔离 vhost，避免域名落入默认站点；坏版本回滚不删除站点/TLS，只恢复上一份已验证元数据快照和引用资产集合。
