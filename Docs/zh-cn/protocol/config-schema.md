---
order: 1
icon: mdi:file-cog-outline
---

# 配置文件协议

`MAAUnified` 的主配置文件为 `config/avalonia.json`。该文件承载 Avalonia 前端自身的配置、配置档案、任务队列以及从旧版 GUI 配置导入时留下的迁移信息。

本文只说明 `MAAUnified` 前端配置文件的结构。MaaCore 任务参数、作业协议和回调协议仍以主仓协议文档与 MaaCore 实现为准。

## 文件位置

| 文件 | 用途 |
| --- | --- |
| `config/avalonia.json` | `MAAUnified` 当前主配置文件。 |
| `config/gui.new.json` | 旧 GUI 配置导入来源之一，只读。 |
| `config/gui.json` | 旧 GUI 配置导入来源之一，只读。 |

`avalonia.json` 存在时，启动流程会优先加载它，并跳过旧配置自动导入。旧配置文件不会被回写、覆盖或删除。

## 顶层结构

当前配置模型由 `UnifiedConfig` 定义，顶层字段如下：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `SchemaVersion` | number | 配置结构版本。当前最新版本为 `2`。 |
| `CurrentProfile` | string | 当前使用的配置档案名称，默认 `Default`。 |
| `Profiles` | object | 配置档案集合，键为档案名称。 |
| `GlobalValues` | object | 全局配置键值表。 |
| `Migration` | object | 迁移元信息，用于记录旧配置导入来源和警告。 |

配置中的具体业务键多数沿用历史 `ConfigurationKeys` 命名，以便与 WPF 配置、兼容映射和测试基线对齐。

## 配置档案

`Profiles` 中的每个配置档案包含：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Values` | object | 档案级配置键值表。 |
| `TaskQueue` | array | 当前档案下的任务队列。 |

任务队列条目包含：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Type` | string | 任务类型，例如 `StartUp`、`Fight`、`Recruit`。 |
| `Name` | string | 任务显示名称。 |
| `IsEnabled` | boolean | 是否启用该任务。 |
| `Params` | object | 任务参数。 |
| `RawTask` | object | 兼容旧 schema 的读取字段，仅在需要保留无法识别的旧任务时出现。 |

未知旧任务不会直接丢弃。导入流程会尽量保留原始内容，将任务置为禁用，并在报告中记录错误或警告，便于后续人工检查。

## 迁移元信息

`Migration` 用于记录配置来源：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `ImportedAt` | string | 导入时间。 |
| `ImportedBy` | string | 导入工具名称，通常为 `MAAUnified`。 |
| `ImportedFromGuiNew` | boolean | 是否导入过 `gui.new.json`。 |
| `ImportedFromGui` | boolean | 是否导入过 `gui.json`。 |
| `Warnings` | array | 导入时产生的非阻塞警告。 |

如果旧 schema 的 `avalonia.json` 被加载，应用只给出非阻塞提示；显式保存配置时才会备份旧文件并写入新版本结构。

## 写入策略

配置保存使用临时文件写入后替换目标文件，临时文件名形如 `avalonia.json.tmp.{pid}.{guid}`。这样可以降低进程异常退出时留下半写入配置的风险。

当旧 schema 配置被显式保存时，会先生成备份文件：

```text
config/avalonia.json.schema-v{version}.bak.{yyyyMMddHHmmss}
```

手动导入旧配置并覆盖当前配置前，也会生成备份：

```text
config/avalonia.json.bak.{yyyyMMddHHmmss}
```

## 维护要求

- 新增配置键时，应确认默认值、旧配置导入规则和 UI 绑定位置。
- 修改历史键语义时，应同步检查 WPF 映射、配置迁移测试和基线覆盖测试。
- 不应让低优先级旧配置覆盖高优先级旧配置；`gui.new.json` 的导入结果优先于 `gui.json`。
- 不应把 MaaCore 协议字段直接写成本前端配置协议的事实源。
