# MAAUnified 测试与验收指南

本文说明本地测试命令、CI 门禁口径、baseline 与 acceptance 的关系，以及基线投影文档的维护要求。除特别说明外，命令均从主仓根目录 `MaaAssistantArknights` 执行。

## 日常测试命令

快速模式适合日常迭代，优先使用已构建产物：

```bash
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-build --no-restore --logger "console;verbosity=minimal"
```

稳定模式适合快速模式在 testhost 收尾阶段偶发卡住时使用：

```bash
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-build -s src/MAAUnified/Tests/stable.runsettings --logger "console;verbosity=minimal"
```

首次运行、依赖变更或清理过构建产物后，先构建再测试：

```bash
dotnet restore src/MAAUnified/Tests/MAAUnified.Tests.csproj
dotnet build src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-restore
dotnet test src/MAAUnified/Tests/MAAUnified.Tests.csproj -c Release --no-build --logger "console;verbosity=minimal"
```

若明确从 `src/MAAUnified` 执行，命令可写为：

```bash
cd src/MAAUnified
dotnet test Tests/MAAUnified.Tests.csproj -c Release --no-build --no-restore --logger "console;verbosity=minimal"
```

不要通过提高并行度处理卡住问题；稳定模式已通过 `Tests/stable.runsettings` 限制并行，以牺牲少量速度换取收尾稳定性。

## 推荐测试范围

根据变更类型选择测试范围：

- 纯 ViewModel 或功能服务变更：运行相关测试类，并在提交前运行快速模式完整测试。
- 配置迁移、任务参数、远程控制、更新流程或多语言变更：运行对应功能测试和完整 `MAAUnified.Tests`。
- 平台能力、GPU 探测、托盘、热键、Overlay 或启动流程变更：运行平台能力契约、启动守卫和相关回归测试。
- baseline、acceptance、parity matrix 或字段映射变更：运行基线门禁测试和完整测试。
- 打包布局、runtime/resource 合并或启动脚本变更：在目标平台执行本地打包验证，或触发主仓 `ci-avalonia`。

CI 中 Linux 会运行 baseline consistency gate 和完整 `MAAUnified.Tests`，Windows 会运行平台能力契约与 native smoke gate；macOS 主要承担打包、签名可用性判断和 dmg 产物验证。

## Baseline 与 Acceptance 的关系

baseline 是冻结的功能、配置键和平台 fallback 事实源；acceptance 是基于 baseline 生成的验收矩阵和案例集合。当前机读源位于：

- `src/MAAUnified/Compat/Mapping/Baseline/baseline.freeze.v1.json`
- `src/MAAUnified/Compat/Mapping/Baseline/acceptance.template.v1.json`
- `src/MAAUnified/Compat/Mapping/Baseline/baseline.schema.v1.json`
- `src/MAAUnified/Compat/Mapping/Baseline/acceptance.schema.v1.json`

可读投影位于：

- `src/MAAUnified/Docs/testing/baseline.freeze.v1.md`
- `src/MAAUnified/Docs/testing/acceptance.checklist.template.v1.md`
- `src/MAAUnified/Docs/testing/avalonia-parity-matrix.md`

维护关系如下：

1. baseline 定义功能项、系统入口、配置键映射、fallback 记录、证据路径与状态。
2. acceptance 引用 baseline，按平台、主题、语言和 tier 生成验收案例。
3. 可读 Markdown 仅用于评审和阅读，是机读源的投影。
4. 测试负责保证机读源与投影一致；若 JSON 已变更但投影未同步，相关同步测试必须失败。

## 禁止手写投影文档

禁止直接手写修改 `baseline.freeze.v1.md`、`acceptance.checklist.template.v1.md` 或由机读源投影出的 parity 内容来“修正”测试结果。正确流程是：

1. 修改 `Compat/Mapping/Baseline/` 下的机读 JSON。
2. 运行仓库既有同步或生成流程，使 Markdown 投影由机读源更新。
3. 运行基线门禁测试，确认投影与机读源一致。
4. 在 PR 中说明变更类型：`Data-only`、`Schema` 或 `Policy`。

`Data-only` 变更通常只调整状态、备注、证据或案例矩阵。`Schema` 或 `Policy` 变更会影响契约字段、P0 口径、Waiver 规则或矩阵策略，需更严格评审。

## 验收要求

Package A 冻结后，baseline 条目默认按 P0 处理。任意失败路径必须满足：

- 应用进程不崩溃。
- UI 有用户可见反馈。
- `debug/` 下有可定位日志。
- 日志或证据能够关联到 scope 与 case id。

平台能力不可用时，应进入可见降级状态并记录诊断，而不是静默失败。Windows GPU 探测遇到虚拟、间接或 IDD adapter 时，应跳过异常 adapter 并继续枚举真实显卡。

## Waiver 规则

只有存在真实阻塞且无法在当前包完成时，才允许将条目标记为 `Waived`。Waiver 必须包含：

- `owner`
- `reason`
- `expires_on`
- `alternative_validation`

`expires_on` 超期后应视为门禁失败。不得使用 Waiver 掩盖缺失测试、未同步文档或未完成的常规实现。
