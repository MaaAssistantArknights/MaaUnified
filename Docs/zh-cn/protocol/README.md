---
title: 协议文档
icon: basil:document-solid
index: false
dir:
  order: 3
---

# 协议文档

本目录收录 `MAAUnified` 涉及的配置、导入报告、兼容基线与 WPF 对齐说明。`MAAUnified` 不重新定义 MaaCore 协议；与核心任务相关的协议仍以 `MaaAssistantArknights` 主仓协议文档和 MaaCore 实现为准。

## 当前索引

- [配置文件协议](./config-schema.md)：说明 `config/avalonia.json` 的顶层结构和维护口径。
- [配置导入报告协议](./import-report-schema.md)：说明 `debug/config-import-report.json` 的字段和诊断用途。
- [兼容基线](./compatibility-baseline.md)：说明 baseline、验收模板、投影文档和测试门禁之间的关系。
- [WPF 与 Avalonia 映射](./wpf-avalonia-mapping.md)：说明如何阅读现有字段、页面和命令映射。
- [功能对齐矩阵](./parity-matrix.md)：说明功能对齐状态、同步规则和根目录矩阵文档的维护边界。

## 适用范围

- `config/avalonia.json` 及其兼容导入规则。
- 配置导入报告、诊断日志与测试样例中使用的稳定数据格式。
- 与 WPF 前端对齐所需的字段映射、默认值和验收基线。
- 面向评审与测试的功能对齐状态说明。

## 维护约定

- 新增或修改协议字段时，应同时说明默认值、兼容策略和失败诊断方式。
- 与 MaaCore 通用协议重复的内容不在本目录复制维护，应链接到主仓协议文档或桥接实现。
- 会影响历史配置导入的变更，应同步更新配置迁移说明、字段映射和相关测试。
- 根目录下的基线投影文档是测试契约的一部分，除非同步修改机读源和测试，否则不要手写重构。
