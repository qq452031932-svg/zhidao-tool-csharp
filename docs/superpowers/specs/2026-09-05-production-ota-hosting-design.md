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

- HTTP 仅用于 ACME 验证并跳转 HTTPS。
- HTTPS 支持 `GET`、`HEAD`，目录浏览关闭。
- `releases.*.json`、`RELEASES-*` 和清单文件响应 `Cache-Control: no-cache, must-revalidate`。
- `.nupkg`、Setup 和 Portable 等版本化资产响应长期 immutable 缓存。
- 未匹配文件返回 404，不回落到现有站点。

## 发布边界

本地验收包 `SuperPan.BaiduBatchTransfer.OtaAcceptance` 不上传到生产稳定渠道。线上软件必须使用生产包 ID `SuperPan.BaiduBatchTransfer`、渠道 `win-x64-stable`，并内置 `https://ota.fanfh.cn/releases/win-x64/stable/`。

服务器部署先完成静态站点、TLS、目录和健康检查。没有经过生产发布门禁的资产不会写入稳定渠道，避免客户端拉取到验收包或半成品。

## 安全与回滚

- 修改前复制当前 Nginx 主配置及虚拟主机目录的只读备份。
- 新配置先写临时文件，执行 `nginx -t` 成功后再原子放置并 reload。
- 部署后同时检查 `ota.fanfh.cn` 和 `f.fanfh.cn`。
- 回滚只移除新增的 OTA 虚拟主机配置并 reload；现有站点配置不参与回滚。
