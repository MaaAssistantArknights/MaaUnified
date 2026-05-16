# MAAUnified 组件目录与复用约定

本文面向 `MAAUnified` 的开发者和 agent，用于说明当前可复用的 UI 基础组件、布局骨架和样式入口。本文只覆盖共享积木与复用约定，不展开构建、测试、CI 和发布流程；这些内容请分别参考 [本地开发](./development.md)、[贡献流程说明](./contributing.md) 与 [CI、发布与验收](./ci-and-release.md)。

阅读这页时，建议先看“快速索引”定位候选组件，再按组件家族查具体条目。新增 UI 时应优先复用现有组件或补共享 variant，不应先写页面级 patch。

## 快速索引

| 我想做什么 | 优先看 |
| --- | --- |
| 放一个标准单行输入框 | `AppTextInput`、`AppInputStyles` |
| 放一个标准下拉框 | `AppSelect`、`AppInputStyles` |
| 放一个数字输入框 | `AppNumberInput`、`AppInputStyles` |
| 做“输入 + 按钮”一体控件 | `AppActionInput` |
| 做“输入 + 历史记录”一体控件 | `AppHistoryInput` |
| 做“输入 + 建议候选”一体控件 | `AppSuggestInput` |
| 做可多选的下拉面板 | `AppMultiSelect` |
| 给文本或设置项补问号提示 | `TooltipHint`、`SettingsLabel`、`AppHintedCheckBox` |
| 做带提示的布尔开关 | `AppHintedCheckBox` |
| 做三态勾选项 | `NullableCheckBox` |
| 做标准设置页标签列 + 字段列 | `SettingsLabel`、`SettingsLabelWidthCoordinator`、`SettingsShellStyles` |
| 做任务页或卡片内紧凑字段布局 | `SettingsInlineRow` |
| 做纵向表单流式排布 | `AdaptiveSpacingStackPanel`、`SettingsShellStyles` |
| 做左侧导航或对象列表 | `AppSelectionList`、`AppSelectionListStyles` |
| 做滚动区吸顶标题 | `AppStickyTitlePresenter`、`AppStickyTitleStyles` |
| 做统一窗口/对话框壳子 | `AppWindowFrame` |
| 调整颜色、卡片、输入、设置页整体语气 | `ColorTokens`、`AppFoundationStyles`、`SettingsShellStyles`、`AppInputStyles` |

## 目录地图

`MAAUnified` 的共享 UI 积木主要分布在下面几处：

- `App/Controls/`：可直接在 XAML 中复用的控件与布局骨架。
- `App/Styles/`：颜色 token、默认模板、设置页壳子、输入控件样式、列表样式和吸顶标题样式。
- `App/Features/`：真实用法示例。找示例时，优先看 `Root/`、`Settings/`、`Dialogs/` 和 `TaskQueue/`。
- `App/App.axaml`：应用级样式入口，决定哪些共享样式会被全局加载。

如果某个问题看起来像“某个控件不对”，应先判断它落在哪一层：

1. 控件本身的行为与结构；
2. 控件默认样式或模板；
3. 页面壳子与布局骨架；
4. 页面局部 variant。

## 组件家族

### 输入与选择

#### `AppTextInput`

- 简介：基于 `TextBox` 的标准文本输入控件，复用统一的输入高度、圆角、hover、focus 和 disabled 语义。
- 推荐场景：普通单行文本输入；需要和设置页其他输入控件保持一致的字段；多处复用的表单字段。
- 不推荐场景：需要立即动作按钮的输入；需要历史记录或建议候选的输入；需要明显多行编辑体验的场景。
- 示例：
  - `App/Features/Settings/PerformanceSettingsView.axaml`：自定义 GPU 描述、路径输入。
  - `App/Features/Advanced/StageManagerView.axaml`：文本编辑区和引用字段。

#### `AppSelect`

- 简介：基于 `ComboBox` 的标准下拉控件，统一了选择框、箭头热区、popup 和选中态样式。
- 推荐场景：枚举项选择；需要统一下拉外观的设置项；任务页中等宽度的选择字段。
- 不推荐场景：需要多选；需要允许自由输入；需要历史记录删除或建议列表行为。
- 示例：
  - `App/Features/Settings/ConnectSettingsView.axaml`：连接配置、截图方式、输入方式。
  - `App/Features/TaskQueue/FightSettingsView.axaml`：关卡、系列和模式选择。

#### `AppNumberInput`

- 简介：标准数字输入控件，收敛了数字框的高度、边框、focus 表达和上下调节行为。
- 推荐场景：数值型设置；次数、分钟、百分比、超时、计数等字段；任务页中的短数字输入。
- 不推荐场景：非数值文本；需要复杂公式或自由文本表达；只读数字展示。
- 示例：
  - `App/Features/Settings/BackgroundSettingsView.axaml`：透明度、模糊半径。
  - `App/Features/TaskQueue/FightSettingsView.axaml`：理智药、源石、次数等紧凑计数字段。

#### `AppActionInput`

- 简介：将文本输入与右侧动作按钮做成一体的组合控件，保证高度、圆角和交互区一致。
- 推荐场景：路径选择；令牌、身份串、CDK 等“输入后立刻执行次级动作”的场景；输入框旁边带“选择”“另存为”“生成”等按钮的字段。
- 不推荐场景：按钮与输入语义无关；一个输入框后面需要并排多个复杂按钮；只是普通文本输入。
- 示例：
  - `App/Features/Settings/ConnectSettingsView.axaml`：`AdbPath` 选择按钮。
  - `App/Features/Settings/ConfigurationManagerView.axaml`：另存为新配置。

#### `AppHistoryInput`

- 简介：带历史记录 popup 的输入控件，支持历史项选择、编辑提交和删除历史项。
- 推荐场景：ADB 地址、路径、命令等会重复输入的字段；需要“最近使用”而不是固定候选的输入。
- 不推荐场景：候选项应实时随文本过滤的自动补全；普通无历史语义的文本框；需要标准单选下拉即可表达的配置。
- 示例：
  - `App/Features/Settings/ConnectSettingsView.axaml`：ADB 地址输入与历史地址选择。

#### `AppSuggestInput`

- 简介：带建议候选列表的输入控件，适合“可自由输入，但也希望从候选中快速选”的场景。
- 推荐场景：干员名、路径名、关键字等有建议列表的字段；需要键盘高亮与候选回填的输入。
- 不推荐场景：候选集合固定且不允许自由输入；只有历史记录没有实时建议；只是普通文本框。
- 示例：
  - `App/Features/TaskQueue/RoguelikeSettingsView.axaml`：核心干员名称建议输入。

#### `AppMultiSelect`

- 简介：带自定义下拉内容的多选外壳控件，闭合态展示摘要，展开态承载 `ToggleButton` 或任意可组合内容。
- 推荐场景：多标签、多奖励、多选项摘要；需要在弹出面板中承载一组可勾选项；希望闭合态仍保持输入框风格。
- 不推荐场景：单选；选项很多且需要虚拟化的大列表；只是普通 checkbox 列表且不需要折叠 popup。
- 示例：
  - `App/Features/TaskQueue/RecruitSettingsView.axaml`：公招标签多选。
  - `App/Features/TaskQueue/RoguelikeSettingsView.axaml`：肉鸽开局奖励选项。

### 提示与勾选

#### `TooltipHint`

- 简介：问号提示控件，统一了延时显示、点击打开、浮层卡片样式和热区大小。
- 推荐场景：解释某个标签、勾选项或局部按钮；需要轻量补充说明但不想占页面正文空间。
- 不推荐场景：长篇帮助文档；必须一直可见的说明文案；把错误反馈或关键操作结果塞进 tooltip。
- 示例：
  - `App/Features/Settings/IssueReportView.axaml`：清理图片缓存提示。
  - `App/Features/Advanced/CopilotView.axaml`：多个字段旁的补充说明。

#### `AppHintedCheckBox`

- 简介：把 `CheckBox` 和 `TooltipHint` 组合在一起的设置项控件，适合“勾选项 + 问号解释”这一高频模式。
- 推荐场景：设置页里的布尔开关；任务页中的可选策略项；需要让问号提示与勾选热区解耦的场景。
- 不推荐场景：没有提示文案；需要三态勾选；需要更复杂的右侧附加字段或按钮。
- 示例：
  - `App/Features/Settings/ConnectSettingsView.axaml`：自动检测、断线重试等开关。
  - `App/Features/TaskQueue/RecruitSettingsView.axaml`：`NotChooseLevel1` 带说明的勾选项。

#### `NullableCheckBox`

- 简介：三态勾选控件，可表达 `true / false / null`，并可附带问号提示。
- 推荐场景：任务页中的“启用 / 禁用 / 跟随默认值”类字段；需要显式表达未设置状态的布尔项。
- 不推荐场景：普通二态开关；用户无法理解第三态语义的设置页；需要联动复杂附属字段但没有明确默认值语义。
- 示例：
  - `App/Features/TaskQueue/FightSettingsView.axaml`：理智药、源石、次数限制等三态策略项。
  - `App/Features/Root/TaskQueueView.axaml`：任务配置面板里的紧凑三态行。

### 设置页布局与对齐骨架

#### `SettingsLabel`

- 简介：设置页字段标签控件，可在标签文本后稳定挂接 `TooltipHint`，并统一标签与提示的对齐关系。
- 推荐场景：设置页的左列标签；需要标签与问号提示一起参与宽度协调的字段；任务页中需要复用“标签 + 提示”的行。
- 不推荐场景：纯正文标题；单独一个问号提示，不需要标签文本；页面私有、完全不走标准字段栅格的展示。
- 示例：
  - `App/Features/Settings/ConnectSettingsView.axaml`：字段标签与提示。
  - `App/Features/TaskQueue/FightSettingsView.axaml`：任务卡片中的字段名。

#### `SettingsInlineRow`

- 简介：面向任务页和紧凑卡片的行布局控件，固定“标签列 / 间隔列 / 字段列”的结构，并自动给字段控件分配合理宽度。
- 推荐场景：任务配置卡片；一行内只有一组标签和字段；需要把数字输入、下拉、勾选紧凑排到统一节奏里的场景。
- 不推荐场景：设置页的大型两列布局；一行内有多组复杂栅格且不适合共享同一字段列；页面只需要普通 `Grid` 就能表达的简单结构。
- 示例：
  - `App/Features/TaskQueue/FightSettingsView.axaml`：战斗设置卡片。
  - `App/Features/TaskQueue/RecruitSettingsView.axaml`：公招卡片中的字段行。

#### `AdaptiveSpacingStackPanel`

- 简介：纵向自适应间距面板，会按子项角色自动调整段落间隔，减少表单和卡片里到处手调 `Spacing` 的情况。
- 推荐场景：设置页纵向表单；任务页卡片内容堆叠；带说明块、提示块、开关组和字段组混排的区域。
- 不推荐场景：必须严格逐像素控制的复杂网格；纯横向排列；页面局部只需要简单 `StackPanel` 且无需动态节奏。
- 示例：
  - `App/Features/Settings/ConnectSettingsView.axaml`：整页结构与分区节奏。
  - `App/Features/TaskQueue/FightSettingsView.axaml`：卡片内容和阶段计划列表。

#### `SettingsLabelWidthCoordinator`

- 简介：为同一组字段行协调共享标签宽度，避免不同标签长度导致字段起点忽左忽右。
- 推荐场景：设置页 `Grid.settings-page-labeled-row`；任务页多个 `SettingsInlineRow` 需要共享标签宽度的区域；同一逻辑组内字段需要稳定左对齐。
- 不推荐场景：单独一行；不同视觉区域之间强行共用同一 `GroupKey`；标签本就不需要对齐的自由排版。
- 示例：
  - `App/Features/Settings/ConnectSettingsView.axaml`：`Connect.AdbCore`、`Connect.AttachWindow` 等分组。
  - `App/Features/TaskQueue/FightSettingsView.axaml`：`TaskQueue.Fields` 分组。

### 导航、列表与切换

#### `AppSelectionList`

- 简介：自带选中指示器、视觉模式和可重排能力的列表控件，用于导航列表、对象列表和卡片列表。
- 推荐场景：左侧导航 rail；任务列表；弹窗中的对象选择列表；需要轻量选中指示器而不是整项重背景的列表。
- 不推荐场景：只有 2-3 个简单选项且更适合按钮组；需要复杂表格列的场景；完全不需要选中状态的静态展示列表。
- 示例：
  - `App/Features/Root/SettingsView.axaml`：设置页左侧 section 导航。
  - `App/Features/Dialogs/AnnouncementDialogView.axaml`：公告列表 rail。

### 吸顶标题与结构性容器

#### `AppStickyTitlePresenter`

- 简介：滚动区吸顶标题控件，负责当前标题、过渡标题和顶替动画的表达。
- 推荐场景：长滚动区域需要当前 section 标题始终可见；页面或弹窗存在明显分节且需要随滚动顶替。
- 不推荐场景：只有一屏内容、没有分节；标题应始终固定而不是随滚动切换；只想做普通页头。
- 示例：
  - `App/Features/Root/SettingsView.axaml`：设置页右侧 section 吸顶标题。
  - `App/Features/Dialogs/AnnouncementDialogView.axaml`：公告正文滚动区标题吸顶。

#### `AppWindowFrame`

- 简介：统一窗口和对话框外壳，负责标题区、动作区、关闭/缩放按钮、拖拽与可调整窗口边缘。
- 推荐场景：自定义窗口边框的主窗口；弹窗、错误框、选择器、预览窗口；需要统一头部和底部动作区的对话框。
- 不推荐场景：只是页面内局部卡片；无需自定义窗口 chrome 的普通控件；页面内二次嵌套“伪弹窗”。
- 示例：
  - `App/Views/MainWindow.axaml`：主窗口壳子。
  - `App/Features/Dialogs/ErrorDialogView.axaml`：错误对话框壳子。

### 样式入口与 token

#### `ColorTokens`

- 简介：颜色与画刷 token 入口，集中维护亮色/暗色模式下的基础色、文本色、卡片色、输入色和状态色。
- 推荐场景：需要新增共享颜色语义；多个组件应共享同一状态色或表面色；调整主题层而不是单个页面补色。
- 不推荐场景：页面级临时改色；把一次性局部颜色直接塞进全局 token；只为修一个局部 selector 就扩充语义层。
- 示例：
  - `App/App.axaml`：全局样式加载入口。
  - `App/Styles/ControlStyles.axaml`、`App/Styles/AppFoundationStyles.axaml`：大量共享样式都从这里取色。

#### `AppFoundationStyles`

- 简介：基础表面、卡片、边框、按钮热区和通用视觉语义的共享样式入口。
- 推荐场景：新增共享卡片、分组容器、轻量操作热区；想让多个页面共用同一基础表面语气。
- 不推荐场景：只修单个页面一处 margin；在页面里复制一套“看起来像卡片”的局部样式；绕过全局卡片语义自行发明一套基础表面。
- 备注：`Button.app-button.locked` 是全局软禁用态，适合“视觉上置灰，但仍保留点击以执行停止/提示等次级动作”的场景；需要灰态时优先复用这个通用语义，不要在单页重复 patch 模板。
- 示例：
  - `App/Features/Dialogs/AnnouncementDialogView.axaml`：`app-surface`、`app-card` 组合。
  - `App/Features/Settings/AchievementSettingsView.axaml`：`grouped-card-frame` 风格的轻卡片。

#### `SettingsShellStyles`

- 简介：设置页专用的壳子样式入口，统一 section rail、页面流式间距、标签列、字段行、提示间距和分区节奏。
- 推荐场景：新增设置页 section；复用标准设置页栅格和缩进关系；让表单遵守已有设置页节奏。
- 不推荐场景：任务页卡片直接硬套设置页壳子；为了局部排版差异就绕开共享类名；在页面内重复定义同一套设置行结构。
- 示例：
  - `App/Features/Root/SettingsView.axaml`：左 rail 与右滚动区壳子。
  - `App/Features/Settings/ConnectSettingsView.axaml`：`settings-page-flow`、`settings-page-labeled-row` 等类。

#### `AppInputStyles`

- 简介：输入、下拉、数字框、popup、箭头热区等输入家族的共享样式入口。
- 推荐场景：统一输入控件的 hover、focus、disabled 表达；补输入家族共享 variant；修正箭头热区、popup、边框层级等共性问题。
- 不推荐场景：把单页局部输入差异都堆进全局；只看到某个页面不对就不查真实模板链路；在页面上重复覆写一整套默认输入样式。
- 示例：
  - `App/App.axaml`：全局样式加载入口。
  - `App/Controls/AppMultiSelect.axaml`、`App/Controls/AppHistoryInput.axaml`：自定义输入控件与共享输入语义对齐。

#### `AppSelectionListStyles`

- 简介：`AppSelectionList` 的视觉模式、选中指示器、卡片外观和重排反馈样式入口。
- 推荐场景：需要给列表切换 `Rail / Surface / None` 风格；统一对象列表的轻卡片语气；调选中指示器而不是整项重背景。
- 不推荐场景：单页局部直接复制一套列表视觉；只修一个页面 hover 色就破坏其他列表模式；把复杂表格也硬塞进 `AppSelectionList` 语义里。
- 示例：
  - `App/Features/Root/SettingsView.axaml`：导航 rail 样式。
  - `App/Features/Root/TaskQueueView.axaml`：任务列表卡片样式。

#### `AppStickyTitleStyles`

- 简介：`AppStickyTitlePresenter` 的过渡动画、标题间距和不同宿主场景 variant 的样式入口。
- 推荐场景：公告弹窗这类标准吸顶标题；需要补充新的吸顶标题 variant；统一滚动顶替动画节奏。
- 不推荐场景：不使用 `AppStickyTitlePresenter` 却想直接复用局部 selector；只想做普通固定标题；为单个页面局部动画问题直接复制一份吸顶样式。
- 示例：
  - `App/Features/Dialogs/AnnouncementDialogView.axaml`：`announcement-dialog-sticky-title` variant。

## 复用决策规则

新增 UI 时，默认按下面顺序决策：

1. 先找现有组件或布局骨架。
2. 现有组件差一点时，优先补共享 variant 或补共享样式入口。
3. 只有当抽象已经稳定、且至少能服务多个页面或组件族时，才新增组件。

几个常见约束需要一并遵守：

- 不应把共享问题长期压成页面级 patch。
- 不应在多个页面里复制同一套输入、列表或设置行结构。
- 不应绕开 `SettingsShellStyles`、`AppInputStyles`、`AppSelectionListStyles` 直接各页各写一套默认语义。
- 需要说明文字时，优先判断是正文说明、状态反馈，还是 `TooltipHint`；不要把错误反馈塞进 tooltip。

## 常见坑与验证

### 常见坑

- 看起来像“控件有问题”，实际问题在模板承载层或共享样式入口。
- 输入框、下拉框、数字框的 hover 或 focus 异常时，只改外层 `Background` 往往不够，应先确认真实模板链路。
- 多语言文本会影响导航宽度、标签列宽度和 tooltip 换行；不要默认中文短文本场景就能代表全部语言。
- `SettingsLabelWidthCoordinator` 的 `GroupKey` 过大或过小都会出问题：过大会把无关区域绑在一起，过小则无法对齐。
- `AppSelectionList` 的选中态应优先靠指示器和轻量状态表达，不要随手改成重背景整项高亮。

### 验证建议

- 新增或调整共享组件后，应至少检查一个设置页场景、一个任务页场景和一个弹窗场景。
- 涉及输入家族时，应确认默认态、hover、focus、disabled 和 popup 表现。
- 涉及标签和提示时，应确认问号热区、tooltip 延时显示、换行和多语言宽度。
- 涉及列表或吸顶标题时，应确认滚动、选中态和指示器几何是否稳定。
- 结构测试可以锁住“用了什么组件”，但视觉与交互问题仍应做真实界面确认。
