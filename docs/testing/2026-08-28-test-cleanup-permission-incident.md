# 2026-08-28 测试清理权限意外：只读排查记录

## 事件与已知范围

Task 8 的测试清理命令对三个精确测试路径运行了 `icacls /reset /T /C /Q`，随后以 `Remove-Item -LiteralPath` 删除这些路径。其中一个目标实际上是普通文件。原工具输出（chunk `eace99`）出现 20 条目标范围外的 Temp 子目录访问拒绝信息，表明权限命令的实际遍历范围超出预期。

确认删除的测试残留只有：

- `%LOCALAPPDATA%/Temp/managed-helper-acl-owner-1ac6a65f403f4e6c8ec13455673327a9`（目录）
- `%LOCALAPPDATA%/Temp/managed-helper-type-owner-ec4f4d9c4b704d8a8b3dc3c4fcf048ce`（普通文件）
- `%LOCALAPPDATA%/BaiduBatchTransfer/verified-helper-snapshots/snapshot-ea5c4a10d38e48ee8b96fa3571233d17`（目录）

权限命令的成功输出被 `/Q` 和 `Out-Null` 抑制，执行前未保存 ACL 基线。因此不能确认哪些范围外 ACL 成功改变、也不能据退出码 0 宣称无影响。没有发现无关内容删除或改写的证据；这不等于已经证明不存在影响。以上测试残留已删除，未保留用于恢复的副本。

## 本轮只读检查

用户同意继续排查后，主代理仅对下列四个明确路径读取属性与 ACL，没有再次运行权限重置、恢复未知 ACL 或递归清理：

- `%LOCALAPPDATA%/Temp`
- `%LOCALAPPDATA%/Temp/codex-skill-validate-deps`
- `%LOCALAPPDATA%/BaiduBatchTransfer`
- `%LOCALAPPDATA%/BaiduBatchTransfer/verified-helper-snapshots`

四个路径均为普通目录且可读取 ACL；当前规则保留对应所有者控制权限，受管快照父目录仍使用受保护的所有者 ACL。这只能说明检查时这些明确目录的当前状态，不能还原历史变化，也不能推断 Temp 下全部文件或其他应用正常。

没有足够证据支持批量恢复，因此未进行恢复。若以后出现具体访问异常，应针对确切对象读取错误、核对应用要求与可用备份，再决定修复，禁止猜测或对整个 Temp 重置。

## 后续验证约束

1. 禁止再次使用递归 `icacls /reset /T` 进行测试清理，也不进行未知 ACL 的批量修复。
2. 本轮验证进程的 `TEMP`、`TMP` 指向工作树 `.artifacts/task8-verify-20260828`；不改变系统级环境变量。已有产品默认快照仍只使用其明确的应用专用目录。
3. 没有必要清理的测试残留暂时保留。任何必要清理必须先检查目标类型、边界和 reparse 状态，不混用普通文件与递归目录处理。
4. 不隐藏安全相关操作的错误输出。失败时停止并记录，不以退出码替代逐项验证。
5. 此事件来自代理执行的清理命令；不是产品验证器自动运行了 `icacls`。代码修复仍需独立规范与质量复审，不能把事故排查替代代码验收。

## 开发边界

事件发生时 HEAD 为 `1f9a1ab`，验证器与其测试各有一个未提交修改集（阻止预开写句柄、确定性释放根句柄）。Task 8 尚未终审通过；Task 9 客户端、Task 10 浏览器进程管理、Task 11 登录 UI 接入和 Task 12 基础验收仍待继续。这里不宣称本地浏览器转存或分享已经可用。
