---
order: 4
icon: mdi:arrow-left-right-bold-outline
---

# WPF 与 Avalonia 映射

WPF 与 Avalonia 映射用于说明旧前端页面、设置段、对话框、配置键和命令在 `MAAUnified` 中对应到哪里。当前冻结映射文档位于：

```text
Docs/testing/wpf-avalonia-field-mapping.md
```

该文件是迁移评审和对齐测试的重要参考，不应在没有 baseline 和测试证据的情况下随意改动。

## 阅读顺序

建议按以下顺序阅读映射：

1. 页面映射：确认 WPF 入口和 Avalonia 视图的对应关系。
2. 任务模块映射：确认任务配置页是否有明确 Avalonia 实现。
3. 设置段映射：确认设置页分组和视图归属。
4. 对话框映射：确认弹窗能力是否完成迁移。
5. 配置键映射：确认历史配置键导入后的落点。
6. 命令映射：确认关键用户操作对应的 ViewModel 或服务入口。

## 与配置迁移的关系

配置迁移依赖历史配置键与 Avalonia 落点之间的稳定关系。新增或调整映射时，应同步检查：

- `Compat/Constants/LegacyConfigurationKeys.cs`
- `Application/Configuration/*ConfigImporter.cs`
- `README.md` 中的配置迁移说明
- `Compat/Mapping/Baseline/baseline.freeze.v1.json`
- 配置迁移和 baseline 覆盖测试

如果某个旧配置键暂时没有 Avalonia 落点，应明确是使用默认值、保留原始内容，还是通过 waiver 记录为暂缓项。

## 与功能对齐的关系

映射文档回答“旧功能对应到哪里”，功能对齐矩阵回答“当前状态如何”。两者应保持口径一致：

- 映射存在但功能未完成时，矩阵状态不应写成 `Implemented`。
- 矩阵标记已完成时，应能在映射文档、视图文件或测试中找到可定位证据。
- 对用户可见行为有差异时，应在平台能力或验收文档中说明降级方式。

原始映射表见 [`../../testing/wpf-avalonia-field-mapping.md`](../../testing/wpf-avalonia-field-mapping.md)。
