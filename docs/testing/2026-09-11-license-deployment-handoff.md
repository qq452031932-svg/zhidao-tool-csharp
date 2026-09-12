# 授权部署工作交接（2026-09-11）

## 当前工作区

- 工作树：`.worktrees/license-production-deploy`
- 分支：`codex/license-production-deploy`
- 当前状态：工作树干净
- 最近提交：`d217cb9 fix: traverse signed client paths safely`

## 已完成并提交

- 授权服务 loopback/systemd/Nginx 模板及部署契约。
- SQLite 快照工具 `cmd/license-db-snapshot`：路径约束、WAL checkpoint、`VACUUM INTO`、`quick_check`、脱敏摘要。
- 数据库检查工具 `cmd/license-dbcheck`：只读 SQLite、`quick_check`、schema/聚合计数。
- 负载工具 `cmd/license-loadtest`：回环地址校验、固定速率/时长校验、HTTP health 请求、聚合计数和 P95。
- 本地安装/回滚脚本、v219 夹具、构建脚本及验收文档。
- 真实容量验收编排：同一生产 replay SQLite 热路径、90,000 个唯一 nonce、50/100 RPS 两阶段、健康/CPU/RSS/内存/BUSY/LOCKED/数据库完整性和失败清理。
- 生产客户端信任门禁：精确生产 origin/产品 UUID/key ID/规范 P-256 SPKI/双 TLS pin，拒绝占位、复用 pin、畸形点和配置覆盖。
- 三个独立客户端产物：SuperPan、非发布 smoke、非发布 boundary probe；只接受证书存储签名身份并验证 Authenticode、SHA-256、时间戳、完整树哈希和 CMS 清单。
- A→B→A / B→C 轮换脚本：固定 SSH 服务端挑战、已安装响应签名身份、新鲜 nonce 绑定、精确签名 smoke 执行、TLS SPKI 独立观测及离线边界 probe。

## 已实际验证

1. 本地授权栈 E2E 已通过：软件 2、授权包 1、卡密 2、隔离客户端存储 4、WPF smoke 1、策略切换 4、续期 1、解绑 1、删除卡密 1、离线边界 2。
2. Go 测试已通过：

```text
./cmd/license-db-snapshot
./cmd/license-dbcheck
./cmd/license-loadtest
./internal/deploymentcontract
```

3. 负载器使用临时 `httptest` 回环服务实际发起 HTTP 请求并统计结果，测试通过。
4. 固定 Go 工具链已成功编译 Linux 维护/测试二进制；这些 Linux ELF 不能在 Windows 直接执行。
5. Task 6 最终规格/质量审阅通过；相关 Go 定向包测试全部通过。
6. Task 7 最终规格/质量审阅通过；生产构建门禁 49 项、smoke/probe 2 项通过，两个 PowerShell 脚本 AST 解析及路径遍历实测通过。

## 尚未完成（不要误判为已验收）

- 尚未在获授权的 Linux staging `MainPID` 上执行 20 分钟完整容量负载。
- 尚未在 Linux/systemd v219 主机执行安装器或回滚器。
- 尚未连接 staging 或生产服务器。
- 尚未启用公网 Nginx 授权路由。
- 尚未从获授权的已安装服务读取正式 key ID/P-256 SPKI，也未取得 staging TLS A/B/C 和签名客户端的真实轮换证据。

## 下一步建议

1. 由用户明确指定 disposable Linux staging 主机、快照/恢复边界和维护窗口。
2. 获得对该主机 SSH、上传同提交 bundle、启动临时 systemd unit、运行容量验收和切换 staging TLS A/B/A 的明确授权。
3. 只在获授权 staging 上记录脱敏容量、数据库、签名身份和轮换证据；生产公网路由与 Nginx 继续保持不变。

## 服务器边界

任何 SSH、staging、systemd、Nginx 或生产主机操作前，必须先通知用户并取得明确确认。当前没有执行过服务器操作。
