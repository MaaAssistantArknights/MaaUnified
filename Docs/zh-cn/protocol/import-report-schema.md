---
order: 2
icon: mdi:file-chart-outline
---

# 配置导入报告协议

配置导入报告用于说明 `MAAUnified` 从旧 GUI 配置导入了哪些内容、跳过了哪些文件、出现了哪些冲突或损坏项。报告默认写入：

```text
debug/config-import-report.json
```

示例文件位于 [`../../config-import-report.example.json`](../../config-import-report.example.json)。

## 生成时机

以下场景会生成或更新导入报告：

- 首次启动时没有 `config/avalonia.json`，应用尝试从旧配置自动导入。
- 在配置管理界面手动导入 `gui.new.json`、`gui.json` 或两者组合。
- 导入过程中发现缺失、损坏、冲突或只能部分应用的内容。

报告用于诊断和回归测试，不应作为用户配置事实源。

## 字段说明

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `source` | string | 导入来源，通常为 `Auto`、`GuiNewOnly`、`GuiOnly`。 |
| `startedAt` | string | 导入开始时间。 |
| `finishedAt` | string | 导入结束时间。 |
| `success` | boolean | 导入过程是否整体成功。部分导入可应用但仍可能为 `false`。 |
| `appliedConfig` | boolean | 是否已将导入结果写入当前配置。 |
| `createdDefaultConfig` | boolean | 是否因无法导入而创建默认配置。 |
| `importedGuiNew` | boolean | 是否读取并应用了 `gui.new.json`。 |
| `importedGui` | boolean | 是否读取并应用了 `gui.json`。 |
| `mappedFieldCount` | number | 成功映射的旧配置字段数量。 |
| `defaultFallbackCount` | number | 使用默认值兜底的字段数量。 |
| `conflictCount` | number | 旧配置来源之间出现冲突的次数。 |
| `importedFiles` | array | 成功参与导入的文件名。 |
| `missingFiles` | array | 缺失的候选文件名。 |
| `damagedFiles` | array | 解析失败或结构损坏的文件名。 |
| `warnings` | array | 非阻塞警告。 |
| `errors` | array | 阻塞错误或需要人工检查的问题。 |
| `outputConfigPath` | string | 输出配置路径。 |
| `reportPath` | string | 报告路径。 |
| `summary` | string | 简短统计摘要，形如 `mapped=312, fallback=5, conflicts=8`。 |

## 诊断口径

`success=false` 不一定表示没有生成可用配置。手动导入允许部分导入时，应用可以写入可用部分，同时在报告中记录损坏文件和错误原因。

排查导入问题时建议同时查看：

- `debug/config-import-report.json`
- `debug/avalonia-ui-errors.log`
- 当前 `config/avalonia.json`
- 旧配置源文件是否仍位于 `config/` 目录

报告中的路径可因运行目录不同而变化，文档和测试不应依赖其中的个人绝对路径。

## 维护要求

- 新增报告字段时，应保持旧字段含义稳定。
- 新字段如果用于诊断，应说明生成条件和失败时的表现。
- 不应把报告中的 `summary` 作为唯一判断依据；结构化字段才是更稳定的读取入口。
