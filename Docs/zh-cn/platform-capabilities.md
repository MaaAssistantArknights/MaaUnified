# 平台能力与降级

MAAUnified 在 Windows、macOS、Linux 上使用同一套界面，但部分能力依赖操作系统、桌面环境和运行时权限。程序会优先使用平台原生实现；不可用时，尽量降级到可见、可测试的替代模式，而不是阻塞主流程。

## 能力概览

| 能力 | Windows | macOS | Linux | 降级方式 |
| --- | --- | --- | --- | --- |
| 托盘 | 原生通知区域图标 | Avalonia 托盘能力可用时启用 | Avalonia 托盘能力可用时启用 | 窗口内菜单 |
| 通知 | 桌面通知，失败时回退 | 桌面通知，失败时回退 | 桌面通知，失败时回退 | 应用内通知或命令式通知回退 |
| 全局热键 | SharpHook 原生热键，失败时回退 | Carbon `RegisterEventHotKey`，失败时回退 | Wayland 优先 xdg-desktop-portal，其他场景可用 SharpHook | 窗口内热键 |
| 自启动 | HKCU Run 注册表项 | LaunchAgents plist | XDG autostart desktop 文件 | 失败时提示，不阻塞启动 |
| Overlay | Win32 窗口附着 | CoreGraphics 目标发现，附着降级为预览与日志 | X11 目标发现，附着失败时预览与日志 | preview-and-log |
| GPU OCR | Windows DirectML GPU 探测与选择 | 不支持 | 不支持 | CPU OCR |

可通过环境变量 `MAA_PLATFORM_FORCE_FALLBACK=1` 强制使用降级路径，便于测试托盘、通知、热键、Overlay 等能力的 fallback 行为。

## macOS Gatekeeper 与发布包签名

macOS 发布包优先使用 Developer ID 签名和 Apple notarization。签名材料缺失或签名失败时，CI 会输出 warning 并降级生成 ad-hoc/unsigned `.dmg`；该包未公证，用户可能需要在“隐私与安全性”中手动允许，或确认来源后执行 `xattr -dr com.apple.quarantine /Applications/MAAUnified.app`。

## 托盘与通知

Windows 优先使用原生通知区域图标。Linux 和 macOS 在 Avalonia 托盘能力可用时启用系统托盘；不可用时，托盘菜单会降级到窗口内菜单。托盘菜单仍应提供开始、停止、显示窗口、隐藏托盘、切换 Overlay、重启和退出等命令。

通知优先使用系统桌面通知。若系统通知不可用、权限不足或发送失败，程序会回退到应用内通知或命令式通知服务。测试时应关注 `debug/avalonia-platform-events.log` 中的 provider、operationId、usedFallback 和 errorCode。

## 热键

全局热键优先使用系统级能力：

- Windows：优先使用 SharpHook；
- macOS：优先使用 Carbon `RegisterEventHotKey`；
- Linux Wayland：优先使用 `xdg-desktop-portal` GlobalShortcuts，要求门户接口版本满足程序要求；
- Linux X11 或门户不可用时：尝试其他可用原生路径。

macOS 的 Carbon 全局热键不依赖 Accessibility API 权限；注册失败时会回退为窗口内热键。

原生全局热键不可用时，程序会降级为窗口内热键。窗口内热键只在应用窗口获得焦点时响应，这是预期限制。热键冲突、权限拒绝、门户取消授权等情况不会阻止主窗口使用。

## 自启动

自启动使用各平台原生机制：

- Windows：当前用户的 `Software\\Microsoft\\Windows\\CurrentVersion\\Run`；
- macOS：`~/Library/LaunchAgents/io.maa.unified.autostart.plist`；
- Linux：XDG autostart 目录中的 `maa-unified.desktop`。

查询或写入失败时，设置页应显示失败原因；主流程不应因自启动失败而退出。

## Overlay

Windows 提供完整的 Win32 窗口枚举、目标选择、置顶附着、透明度与点击穿透控制。若目标窗口不可用、宿主窗口无效或连续同步失败，Overlay 会降级到预览与日志模式。

macOS 使用 CoreGraphics 枚举屏幕上的普通应用窗口，因此目标选择器可以显示可见窗口；受平台窗口附着能力限制，选中目标后仍会进入预览与日志模式。

Linux 仅在存在 `DISPLAY` 的 X11 会话中启用 Overlay 目标发现；Wayland 或无 `DISPLAY` 环境会进入预览与日志模式。Linux 下即使能发现目标窗口，仍可能因窗口管理器限制而无法稳定附着，此时应记录降级事件。

macOS 测试者应确认目标列表能列出可见应用窗口、提示明确、选中目标后降级到预览与日志模式且主流程仍可继续。

## GPU OCR

GPU OCR 当前仅在 Windows 上提供可编辑选择。程序通过 DXGI 枚举候选显卡，并检查 D3D12 / DirectML 相关支持。可选项通常包括：

- 禁用 GPU，使用 CPU OCR；
- 使用系统默认 GPU；
- 指定某个可用 GPU。

部分旧显卡或驱动日期早于 DirectML 支持要求的设备会被标记为 deprecated；默认不展示或不优先使用，用户允许旧设备后才可选择。GPU 选择变更通常需要重启后才影响 MaaCore 初始化。

Windows GPU 探测会跳过明显不适合 OCR 的虚拟或间接显示适配器，包括描述或实例路径中包含 `Virtual`、`Indirect`、`IDD`、`Remote`、`Basic Render`、`ROOT\\`、`INDIRECT` 等特征的 adapter。单个坏 adapter 不应导致整次 GPU 枚举失败；程序会继续探测后续真实显卡。若探测整体失败或超时，本次会话回退到 CPU OCR。

非 Windows 平台 GPU OCR 选择不可编辑，显示为不支持，并使用 CPU OCR。

## 诊断日志

平台能力相关日志位于运行目录的 `debug/`：

- `debug/avalonia-platform-events.log`：托盘、通知、热键、Overlay 等平台操作事件；
- `debug/avalonia-ui-startup.log`：启动阶段诊断；
- `debug/avalonia-ui-errors.log`：启动失败、Linux 无图形会话等错误；
- `debug/windows-gpu-probe.log`：Windows GPU 探测结果、候选显卡、跳过原因、异常或超时信息。

Windows 发布包排查“打开后不出 GUI”或“GPU 探测失败”时，优先查看 `publish/publish-win-x64/debug/windows-gpu-probe.log` 与 `publish/publish-win-x64/debug/avalonia-ui-startup.log`。若实际运行目录不是该路径，请以当前包的运行目录为准。
