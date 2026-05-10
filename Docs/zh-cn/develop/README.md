# 开发文档

本目录收录 `MAAUnified` 的开发、联调、测试、发布和维护说明。

## 当前索引

- [本地开发](./development.md)：本地构建、运行、测试、诊断和提交前检查。
- [贡献流程说明](./contributing.md)：说明普通贡献者如何向子仓 `dev` 提交改动，以及维护者如何把结果推进到主仓 `dev-v2`。
- [架构说明](./architecture.md)：说明 `App`、`Application`、`CoreBridge`、`Platform`、`Compat` 等层次职责。
- [组件目录与复用约定](./component-directory.md)：说明共享组件、布局骨架、样式入口与复用边界。
- [模块边界与禁止事项](./module-boundaries.md)：说明跨层依赖、配置、平台能力和测试边界。
- [CI、发布与验收](./ci-and-release.md)：说明 workflow、产物约定、基线门禁和维护者发布流程。

## 基线与验收

- [基线冻结说明](../../testing/baseline.freeze.v1.md)
- [基线变更控制](../../testing/baseline-change-control.v1.md)
- [验收清单模板](../../testing/acceptance.checklist.template.v1.md)

机读基线位于 `Compat/Mapping/Baseline/`。修改配置语义、任务映射、默认值或 WPF 对齐行为时，应同步评估基线与验收清单。

## 本地开发

本地开发建议始终在 `MaaAssistantArknights` 主仓里联调，这样才能一起验证 MaaCore runtime、`resource/`、平台启动入口和打包布局。

入口文档：

- [本地开发](./development.md)：构建、运行、测试与诊断。
- [贡献流程说明](./contributing.md)：子仓 `dev` / `main` 分工、主仓 `dev-v2` 指针更新流程。
- [CI、发布与验收](./ci-and-release.md)：主仓 workflow、调试包、正式包与验收门禁。

## 维护约定

- 发布与打包细节集中维护在 [CI、发布与验收](./ci-and-release.md)。
- 涉及平台能力的变更应说明探测失败、降级路径与日志位置。
- 涉及用户可见行为的变更，应同步更新 [中文文档入口](../README.md) 或相关说明页面。
