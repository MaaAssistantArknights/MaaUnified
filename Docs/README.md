# MAAUnified 文档索引

本目录收录 `MAAUnified` 的用户说明、开发说明、协议约定与迁移基线材料。`MAAUnified` 作为 `MaaAssistantArknights` 的 submodule 维护，文档应同时兼顾独立仓阅读与主仓联调场景。

## 语言目录

- [`zh-cn/`](./zh-cn/)：简体中文文档，当前主要维护语言。

未来如需新增语言目录，目录名应采用小写语言标签，例如 `en-us`、`ja-jp`，并在对应语言文档具备实际内容后再创建目录；本索引不预置空目录。

## 中文文档

- [`zh-cn/README.md`](./zh-cn/README.md)：中文文档入口。
- [`zh-cn/platform-capabilities.md`](./zh-cn/platform-capabilities.md)：平台能力与降级说明。
- [`zh-cn/develop/README.md`](./zh-cn/develop/README.md)：开发文档索引。
- [`zh-cn/develop/development.md`](./zh-cn/develop/development.md)：位于中文开发文档 `develop/` 下的本地构建、联调与运行说明。
- [`zh-cn/protocol/README.md`](./zh-cn/protocol/README.md)：协议与数据约定索引。
- [`testing/README.md`](./testing/README.md)：测试专用文档入口。

## 测试专用文档

- [`testing/wpf-avalonia-field-mapping.md`](./testing/wpf-avalonia-field-mapping.md)：WPF 与 Avalonia 页面、模块、设置项映射。
- [`testing/avalonia-parity-matrix.md`](./testing/avalonia-parity-matrix.md)：面向阅读的功能对齐概览。
- [`testing/baseline.freeze.v1.md`](./testing/baseline.freeze.v1.md)：当前冻结基线的可读投影。
- [`testing/baseline-change-control.v1.md`](./testing/baseline-change-control.v1.md)：基线变更控制规则。
- [`testing/acceptance.checklist.template.v1.md`](./testing/acceptance.checklist.template.v1.md)：验收清单模板。

机读源位于 `../Compat/Mapping/Baseline/`。`testing/` 下的基线文档中部分内容由机读基线生成或与之保持同步。

## 示例与参考

- [`config-import-report.example.json`](./config-import-report.example.json)：配置导入报告示例。

## 维护约定

- 新增文档应优先补充到相应语言目录索引，并说明用途。
- 阶段性记录、临时验收结果与已关闭问题不在本目录长期保留。
- 发布、打包与 CI 细节集中维护在中文开发文档中，根 README 仅保留入口链接。
