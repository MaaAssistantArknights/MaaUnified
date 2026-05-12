---
order: 3
icon: mdi:shield-check-outline
---

# 兼容基线

`MAAUnified` 使用机读基线记录与 WPF 前端对齐的范围、证据、验收矩阵和平台降级预期。该基线既服务评审，也服务测试门禁。

## 事实源

| 文件 | 用途 |
| --- | --- |
| `Compat/Mapping/Baseline/baseline.freeze.v1.json` | 兼容基线的机读事实源。 |
| `Compat/Mapping/Baseline/acceptance.template.v1.json` | 验收清单模板的机读事实源。 |
| `Docs/testing/baseline.freeze.v1.md` | 由 baseline JSON 渲染出的可读投影。 |
| `Docs/testing/acceptance.checklist.template.v1.md` | 由 acceptance JSON 渲染出的可读投影。 |
| `Docs/testing/baseline-change-control.v1.md` | 基线冻结、审批和 waiver 规则。 |

`baseline.freeze.v1.md` 与 `acceptance.checklist.template.v1.md` 不是手写事实源。修改机读 JSON 后必须同步投影文档；直接手写投影文档会导致测试失败。

## 测试关系

`BaselineRenderSyncTests.GeneratedDocs_ShouldMatchMachineReadableSource` 会把 JSON 渲染出的 Markdown 与 `Docs/testing/` 下投影文件逐字比较。该测试的目的，是防止评审看到的文档与机读基线不一致。

相关测试还会检查：

- 基线条目的优先级、状态值和证据字段。
- 功能项、系统项、配置键和平台 fallback 记录的覆盖数量。
- waiver 字段是否完整、是否过期。
- 功能对齐矩阵是否声明以 `Docs/testing/baseline.freeze.v1.md` 为同步源。

## 状态与 waiver

基线中的对齐状态只允许使用：

| 状态 | 含义 |
| --- | --- |
| `Aligned` | Avalonia 行为已与基线要求对齐。 |
| `Gap` | 仍存在差距，需要继续补齐。 |
| `Waived` | 存在真实阻塞，按规则临时豁免。 |

waiver 仅在当前阶段无法完成且有替代验证方式时使用。必填字段包括 `owner`、`reason`、`expires_on` 和 `alternative_validation`。超期 waiver 应移除或续签并说明原因。

## 变更流程

基线变更应先判断类型：

- `Data-only`：仅修改状态、证据、备注或验收矩阵。
- `Schema`：修改契约字段或结构。
- `Policy`：修改 P0 口径、矩阵策略或 waiver 规则。

变更后应同步检查：

- 机读 JSON 是否符合契约测试。
- 投影 Markdown 是否重新生成或同步更新。
- 功能对齐矩阵是否仍与 baseline 状态一致。
- 相关测试和验收说明是否需要补充。

详细审批规则见 [`../../testing/baseline-change-control.v1.md`](../../testing/baseline-change-control.v1.md)。
