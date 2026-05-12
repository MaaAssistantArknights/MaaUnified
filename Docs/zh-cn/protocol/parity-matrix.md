---
order: 5
icon: mdi:format-list-checks
---

# 功能对齐矩阵

功能对齐矩阵用于给维护者和评审者提供一份可读的迁移状态概览。根目录当前矩阵位于：

```text
Docs/testing/avalonia-parity-matrix.md
```

该矩阵是阅读向文档，但仍受测试约束。修改矩阵状态前，应先确认 baseline、映射文档和实际实现是否一致。

## 状态定义

| 状态 | 含义 |
| --- | --- |
| `Implemented` | 当前 Avalonia 实现已覆盖基线要求。 |
| `InProgress` | 已有实现或入口，但仍存在缺口。 |
| `Pending` | 当前阶段暂未完成，或由 waiver/平台限制解释。 |

矩阵状态与 baseline 状态之间的常用对应关系为：

| Baseline 状态 | 矩阵状态 |
| --- | --- |
| `Aligned` | `Implemented` |
| `Gap` | `InProgress` |
| `Waived` | `Pending` |

## 同步规则

`ParityMatrixSyncTests` 会检查矩阵文档包含 `Docs/testing/baseline.freeze.v1.md` 这一同步源说明，并抽查部分功能状态是否与 baseline 一致。

因此，修改矩阵时需要同时检查：

- `Compat/Mapping/Baseline/baseline.freeze.v1.json`
- `Docs/testing/baseline.freeze.v1.md`
- `Docs/testing/avalonia-parity-matrix.md`
- 对应功能实现和测试

如果只改矩阵文字而没有同步事实源，测试或评审都可能发现状态不一致。

## 使用方式

矩阵适合回答以下问题：

- 当前主框架、设置页、任务页、高级模块和对话框的完成状态。
- 哪些功能已经进入 `Implemented`，哪些仍需要补齐。
- 某个状态是否有 baseline 或 waiver 支撑。

矩阵不适合作为唯一事实源。需要判断某个配置键、验收 case 或平台 fallback 是否完成时，应回到 baseline JSON、验收模板和测试结果。

原始矩阵见 [`../../testing/avalonia-parity-matrix.md`](../../testing/avalonia-parity-matrix.md)。
