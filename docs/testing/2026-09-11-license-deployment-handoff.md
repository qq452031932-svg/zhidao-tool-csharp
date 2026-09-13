# 授权部署工作交接（2026-09-11）

## 当前工作区（2026-09-13 更新）

- 工作树：`.worktrees/license-production-deploy`
- 分支：`codex/license-production-deploy`
- 当前状态：生产部署与验收完成；待提交最终脱敏证据
- 已安装二进制源提交：`63c72139a02ece768a82bcc1b2bc8fa15c0b6f90`

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

## 生产完成项

- 已在获授权的 CentOS 7/systemd 219 主机安装同提交 OTA 与授权服务，未清理任何
  原有项目、站点、发布代或稳定链接。
- 50/100 RPS 各 10 分钟容量验收通过，数据库 `quick_check` 和聚合不变量通过。
- 正式响应签名身份、A/B/C TLS SPKI、Windows 自签名 Authenticode 身份均已校验。
- A→B→A、B→C、30 分钟离线边界、真实多产品管理流程均通过。
- 授权公网路由已完成“启用→完整恢复→重新启用”的事务演练和最终验证。
- 验收卡已删除；两条软件记录按计划停用保留，验收套餐带
  `DEPLOYMENT-SMOKE-RETAINED` 标记保留。

## 后续运维

1. 分发 A/B 双 pin 的正式 SuperPan 客户端；B/C 构建仅作为下一次证书轮换准备。
2. Windows 自签名证书不会自动获得 SmartScreen 信誉，也不会静默安装到用户信任库。
3. SSH 密钥化、限制 root 密码登录与防扫描属于单独变更，尚未执行，避免在本次部署中
   扩大服务器修改范围。

## 服务器边界

任何 SSH、staging、systemd、Nginx 或生产主机操作前，必须先通知用户并取得明确确认。当前没有执行过服务器操作。
