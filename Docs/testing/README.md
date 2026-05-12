# MAAUnified 测试专用文档

本目录收录 `MAAUnified` 的测试、评审和基线契约材料。这里的文档主要服务 baseline 门禁、验收同步、功能对齐追踪和历史映射核对，不作为普通用户或日常开发的首选阅读入口。

## 当前内容

- [`baseline.freeze.v1.md`](./baseline.freeze.v1.md)：当前冻结 baseline 的可读投影。
- [`acceptance.checklist.template.v1.md`](./acceptance.checklist.template.v1.md)：验收清单模板投影。
- [`baseline-change-control.v1.md`](./baseline-change-control.v1.md)：baseline 变更控制和 waiver 规则。
- [`avalonia-parity-matrix.md`](./avalonia-parity-matrix.md)：功能对齐矩阵。
- [`wpf-avalonia-field-mapping.md`](./wpf-avalonia-field-mapping.md)：WPF 与 Avalonia 的冻结映射表。

## 维护约定

- 机读事实源位于 `../Compat/Mapping/Baseline/`。
- `baseline.freeze.v1.md` 与 `acceptance.checklist.template.v1.md` 是机读源的投影，不应直接手写修改。
- 修改 baseline、acceptance 或 parity 状态后，应同步运行 `MAAUnified.Tests` 中的 baseline 相关测试。
- 普通说明性内容优先放在语言目录下；仅服务测试契约或评审门禁的文档保留在本目录。
