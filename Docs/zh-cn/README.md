# MAAUnified 中文文档

`MAAUnified` 是 MAA 的跨平台图形前端。本文档区用于整理使用说明、开发说明、协议约定、迁移材料与验收基线，便于在独立仓和 `MaaAssistantArknights` 主仓 submodule 场景下查阅。

## 文档分类

- [平台能力与降级](./platform-capabilities.md)：面向托盘、热键、自启动、Overlay、GPU OCR 的平台差异说明。
- [开发文档](./develop/)：面向本地开发、联调、构建、CI、发布与架构维护的说明。
- [协议文档](./protocol/)：面向配置、任务、桥接与外部数据格式的约定说明。

## 常用入口

- [平台能力与降级](./platform-capabilities.md)
- [配置迁移说明](../../README.md)
- [本地开发](./develop/development.md)
- [CI、发布与验收](./develop/ci-and-release.md)
- [兼容基线](./protocol/compatibility-baseline.md)

## 测试原始材料

`../testing/` 目录保留测试、评审和历史追溯使用的原始材料：

- [WPF 与 Avalonia 字段映射](../testing/wpf-avalonia-field-mapping.md)
- [功能对齐矩阵](../testing/avalonia-parity-matrix.md)
- [基线冻结说明](../testing/baseline.freeze.v1.md)
- [基线变更控制](../testing/baseline-change-control.v1.md)
- [验收清单模板](../testing/acceptance.checklist.template.v1.md)

## 阅读建议

- 普通用户优先阅读 [平台能力与降级](./platform-capabilities.md)。
- 从现有 Windows 版迁移配置时，优先阅读 [`MAAUnified` 主 README](../../README.md) 中的迁移说明。
- 需要自己本地构建、联调或打包时，先看 [本地开发](./develop/development.md)。
- 参与 `MAAUnified` 开发、构建或发布时，优先阅读 [开发文档](./develop/)。
- 涉及配置导入、任务数据、桥接边界或外部集成时，阅读 [协议文档](./protocol/) 并对照主仓协议约定。
