# 授权部署工作交接（2026-09-11）

## 当前工作区

- 工作树：`.worktrees/license-production-deploy`
- 分支：`codex/license-production-deploy`
- 当前状态：工作树干净
- 最近提交：`c92629c feat: run loopback license workload`

## 已完成并提交

- 授权服务 loopback/systemd/Nginx 模板及部署契约。
- SQLite 快照工具 `cmd/license-db-snapshot`：路径约束、WAL checkpoint、`VACUUM INTO`、`quick_check`、脱敏摘要。
- 数据库检查工具 `cmd/license-dbcheck`：只读 SQLite、`quick_check`、schema/聚合计数。
- 负载工具 `cmd/license-loadtest`：回环地址校验、固定速率/时长校验、HTTP health 请求、聚合计数和 P95。
- 本地安装/回滚脚本、v219 夹具、构建脚本及验收文档。

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

## 尚未完成（不要误判为已验收）

- 尚未在真实授权服务进程上执行完整容量负载。
- 尚未在 Linux/systemd v219 主机执行安装器或回滚器。
- 尚未连接 staging 或生产服务器。
- 尚未启用公网 Nginx 授权路由。
- 尚未完成生产 Release 信任材料（正式 HTTPS 地址、P-256 签名公钥、TLS SPKI pins）。

## 下一步建议

1. 在本地先启动真实授权服务进程，使用 disposable 数据目录运行 `license-loadtest`，记录仅脱敏的聚合指标。
2. 完善并验证 `BUSY/LOCKED`、健康连续性、CPU/RSS/内存阈值采集。
3. 检查安装器的密钥生命周期、事务回滚和 OTA 配置替换边界。
4. 所有本地测试和提交完成后，再准备 staging 演练。

## 服务器边界

任何 SSH、staging、systemd、Nginx 或生产主机操作前，必须先通知用户并取得明确确认。当前没有执行过服务器操作。
