# CC Monitor

[简体中文](README.md) | [English](README.en.md)

CC Monitor 是一个轻量的 Windows 桌面悬浮窗，用来集中查看多个 Claude Code 会话当前是在工作、等待权限、已经完成，还是因错误停止。

![CC Monitor 会话监控界面](docs/images/cc-monitor.png)

## 主要功能

- 实时汇总多个 Claude Code 会话的状态
- 高亮显示等待权限等需要注意的会话
- 会话完成时闪烁提醒，并支持 Windows 通知和提示音
- 按状态分组或平铺显示会话
- 自定义会话名称，隐藏或移除历史会话
- 点击会话切换到对应的 VS Code 窗口或终端
- Usage Dashboard 展示可读取的本地用量数据
- 支持置顶、最小化、拖动和调整窗口大小

## 下载

从 [Releases](https://gitea.lan.fasteurai.com/linruyue/claude-code-monitor-desktop/releases) 下载最新的 `CCMonitor-*-win-x64.zip`，解压后运行 `CCMonitor.App.exe`。

发布包为 Windows x64 自包含版本，无需另外安装 .NET Runtime。

## 使用要求

- Windows 10 或 Windows 11（x64）
- Claude Code
- VS Code（仅精确切换终端功能需要）

## 快速开始

1. 解压 Release 压缩包，并运行 `CCMonitor.App.exe`。
2. 打开应用中的 **Settings**，点击 **Reinstall Hooks**。
3. 启动或继续使用 Claude Code；会话会自动出现在 CC Monitor 中。
4. 移动发布目录后，请重新安装 Hooks，让 Claude Code 使用新的程序路径。

如果 PowerShell 阻止运行安装脚本，可以使用应用内的 **Reinstall Hooks**，或者执行：

```powershell
PowerShell -ExecutionPolicy Bypass -File .\install-hooks.ps1 `
  -AppPath "C:\Path\To\CCMonitor.App.exe"
```

卸载 Hooks：

```powershell
PowerShell -ExecutionPolicy Bypass -File .\uninstall-hooks.ps1 `
  -AppPath "C:\Path\To\CCMonitor.App.exe"
```

安装程序会合并 `%USERPROFILE%\.claude\settings.json` 中的现有字段和 Hooks，避免重复添加 CC Monitor 项。修改前会创建备份；如果 JSON 无效，安装会停止且不会覆盖原文件。已有的 `statusLine` 会在卸载 CC Monitor 时恢复。

## VS Code 终端联动

Release 包内包含 `cc-monitor-terminal-bridge-*.vsix`。在 VS Code 中安装该扩展并重新加载窗口后，点击 CC Monitor 中的会话可切换到相应的 VS Code 窗口，并尽可能定位对应的终端。

## 状态说明

| 状态 | 含义 |
| --- | --- |
| `IDLE` | 等待任务 |
| `RUNNING` | Claude 正在工作 |
| `NEEDS ATTENTION` | 等待权限或需要用户处理 |
| `DONE` | 本轮工作已完成，等待新输入 |
| `ERROR` | Claude 意外停止 |

## 架构

```text
Claude Code Hooks
      -> CCMonitor.Hook.exe
      -> 本地 JSON 状态
      -> CCMonitor.App.exe
```

Hook 程序从标准输入读取 Claude Code Hook JSON，经状态机处理后，以原子方式写入 `%USERPROFILE%\.cc-monitor\sessions\<session_id>.json`。即使内部发生错误，Hook 也会以退出码 `0` 结束，避免阻塞 Claude Code。

## 从源码构建

开发环境需要 .NET 8 SDK。

```powershell
dotnet test
PowerShell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

自包含发布包会生成到 `artifacts`，其中包括桌面应用、Hook 程序、安装脚本和 VS Code Terminal Bridge 扩展。

## 隐私

- CC Monitor 可能在本机读取近期 Claude Code transcript 的元数据以计算用量；消息内容不会由 CC Monitor 上传或保存。
- CC Monitor 不会向服务器发送数据。
- CC Monitor 不需要 Anthropic API Key。
- Prompt Preview 默认关闭；启用后最多保留 100 个字符。
- 设置 `CCMONITOR_DEBUG_HOOKS=1` 后会将原始 Hook payload 保存到 `%USERPROFILE%\.cc-monitor\debug-hooks`，仅应在本地调试时启用。

## 故障排查

- 状态没有变化：在 Claude Code 中检查 `/hooks`，并查看 `%USERPROFILE%\.cc-monitor\logs\cc-monitor-hook.log`。
- `/hooks` 中没有 CC Monitor：从 Settings 或安装脚本重新安装 Hooks。
- `settings.json` 无效：修复 `%USERPROFILE%\.claude\settings.json` 后重新安装 Hooks。
- 移动了程序目录：移动后重新安装 Hooks。
- Windows 通知不显示：在 Settings 中启用通知，并检查 Windows 专注助手。
- 同一项目出现多个相同名称：CC Monitor 默认根据 Hook 的 `cwd` 生成项目名，可以通过会话菜单自定义名称。

## License

[MIT](LICENSE)
