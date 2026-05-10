# MAAUnified 模块边界与禁止事项

本文定义 `src/MAAUnified` 内各模块的边界。新增代码应优先放入已有层级，不应通过跨层引用、复制逻辑或临时全局状态绕过边界。

## 总体原则

- UI 只表达交互和状态投影，业务语义进入 `Application`。
- MaaCore 原生细节只进入 `CoreBridge`。
- 操作系统能力只进入 `Platform`。
- 历史兼容、基线和配置映射只进入 `Compat`。
- 测试替身只进入 `Tests` 或测试专用 fixture。
- 依赖方向保持单向，不引入循环引用。

## App 边界

`App` 可以包含 Avalonia 视图、样式、窗口、ViewModel、UI service、启动入口和 UI 诊断辅助。

禁止事项：

- 禁止直接 P/Invoke MaaCore 或访问 MaaCore 原生句柄。
- 禁止直接读写 `config/avalonia.json`、`gui.new.json` 或 `gui.json`。
- 禁止在 View 或 code-behind 中复制应用层业务规则。
- 禁止在 UI 层吞掉失败而不向 `UiDiagnosticsService` 或用户可见错误通道反馈。
- 禁止让主窗口可交互性依赖长耗时平台初始化。

## Application 边界

`Application` 可以包含配置服务、会话编排、任务参数编译、功能服务、日志诊断、多语言协调、远程控制和更新流程。

禁止事项：

- 禁止引用 Avalonia 控件、窗口或平台 UI 类型。
- 禁止直接写平台专用实现，例如注册表、launch agent、DBus 或 Windows overlay 细节。
- 禁止绕过 `IMaaCoreBridge` 直接调用 MaaCore。
- 禁止在多个服务中分散维护同一份任务状态；状态变更应通过会话服务或明确的功能服务归口。

## CoreBridge 边界

`CoreBridge` 可以包含 MaaCore native bridge、stub、接口、回调模型和 DTO。

禁止事项：

- 禁止引用 `App`、ViewModel 或 UI 文案资源。
- 禁止把原生句柄暴露给 `Application` 或 `App`。
- 禁止让平台能力判断进入 MaaCore bridge。
- 禁止在桥接层保存 UI 配置语义；配置解释应在 `Application` 或 `Compat` 完成。

## Platform 边界

`Platform` 可以包含托盘、通知、热键、自启动、Overlay、文件选择、GPU 能力、后置动作执行和平台 fallback。

禁止事项：

- 禁止在平台层调用 Avalonia 页面或 ViewModel。
- 禁止把平台不可用视为默认致命错误；应返回降级结果并记录诊断。
- 禁止让单个坏 adapter、坏窗口句柄或权限失败中断全部平台能力枚举。
- 禁止把业务任务参数、配置迁移规则或 MaaCore 协议放入平台实现。

## Compat 边界

`Compat` 可以包含历史配置键、任务类型目录、WPF 功能基线、baseline/acceptance 机读源、runtime 布局兼容和迁移辅助。

禁止事项：

- 禁止在 `Compat` 中新增 UI 状态或 ViewModel。
- 禁止将 `Compat` 作为任意共享工具箱使用。
- 禁止手写修改由机读 baseline 或 acceptance 生成的投影文档。
- 禁止把旧配置文件作为主写目标；旧文件只能作为导入来源。

## Tests 边界

`Tests` 可以引用生产层进行单元测试、契约测试、回归测试和基线门禁。

禁止事项：

- 禁止让测试依赖开发者本地绝对路径、个人化远端、个人化分支或本地机器专有资源。
- 禁止通过修改生产代码降低测试断言强度来绕过回归。
- 禁止用手工改投影文档替代基线机读源更新。
- 禁止把测试 fixture、fake bridge 或 fake platform service 放入生产装配路径。

## 文档与生成物边界

文档应说明稳定约定、维护流程和命令入口，不应记录个人工作目录、个人远端、临时分支或一次性排障命令。生成物、发布包、`bin/`、`obj/`、`staging/`、`publish/`、`release/`、`debug/` 日志通常不应随开发改动提交，除非仓库已有明确规则要求纳入。

## 常见跨层调整位置

- 新增设置项：`Compat` 增加历史键或映射，`Application` 增加配置模型和服务语义，`App` 增加 UI 投影，`Tests` 增加迁移与绑定测试。
- 新增 MaaCore 能力：`CoreBridge` 扩展接口和 DTO，`Application` 编排调用，`App` 展示状态，`Tests` 使用 fake bridge 覆盖成功与失败路径。
- 新增平台能力：`Platform` 定义接口和默认实现，`Application` 增加能力服务或诊断语义，`App` 展示可用性和降级结果，`Tests` 增加契约测试。
- 修改 baseline 或 acceptance：只改 `Compat/Mapping/Baseline/` 下机读源，运行同步生成与门禁测试，再提交生成后的投影变更。
