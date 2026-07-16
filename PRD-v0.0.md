# CC Monitor 产品需求文档 PRD

## 1. 项目名称

**CC Monitor**

全称：

**Claude Code Desktop Status Monitor**

项目定位：

> 一个面向 Windows 用户的轻量级 Claude Code 状态监控工具，通过 Claude Code 官方 Hooks 获取 Session 生命周期事件，并在桌面上通过 Always-on-Top 悬浮窗口实时显示 Claude Code 当前状态。

---

# 2. 背景

用户主要在 Windows + VS Code Integrated Terminal 中使用 Claude Code。

典型环境：

* Windows 10 / Windows 11
* VS Code
* VS Code Integrated Terminal
* Git Bash / MINGW64
* Claude Code CLI

示例：

```text
user@WINDOWS-PC MINGW64 ~/Desktop/project (master)
$ claude
```

由于 Claude Code 运行在 VS Code Terminal 中，当用户切换至：

* Chrome
* PDF 阅读器
* 微信
* ChatGPT
* 其他 IDE
* 其他窗口

用户无法直观看到 Claude Code 当前是否：

1. 正在执行任务；
2. 等待权限批准；
3. 已经完成当前任务；
4. 因 API 或其他错误停止。

因此用户需要频繁切回 VS Code 检查 Terminal。

这严重影响 Claude Code 长时间 Agent Task 的使用体验。

---

# 3. 产品目标

开发一个 Windows 桌面常驻悬浮工具。

通过 Claude Code 官方 Hooks 获取 Claude Code 生命周期事件。

实时显示：

```text
RUNNING
BLOCKED
DONE
ERROR
IDLE
```

用户在任意 Windows 应用中都可以看到 Claude Code 当前状态。

目标 UI：

```text
┌──────────────────────────────┐
│  CC Monitor              ×   │
│                              │
│  🟡  RUNNING                 │
│                              │
│  fstr_img_tag_manager        │
│                              │
│  Working for 02:31           │
└──────────────────────────────┘
```

权限阻塞：

```text
┌──────────────────────────────┐
│  CC Monitor              ×   │
│                              │
│  🔴  NEEDS ATTENTION         │
│                              │
│  fstr_img_tag_manager        │
│                              │
│  Permission required         │
└──────────────────────────────┘
```

任务完成：

```text
┌──────────────────────────────┐
│  CC Monitor              ×   │
│                              │
│  🟢  DONE                    │
│                              │
│  fstr_img_tag_manager        │
│                              │
│  Finished 12 sec ago         │
└──────────────────────────────┘
```

---

# 4. 核心设计原则

## 4.1 必须使用 Claude Code 官方 Hooks

禁止：

* OCR Terminal
* 截图识别
* 读取 VS Code Terminal UI
* grep Terminal 屏幕输出
* 模拟键盘
* 轮询 VS Code DOM
* VS Code Extension API 获取 Terminal 文本
* 分析 Claude 输出中的自然语言判断状态

必须使用：

```text
Claude Code Hooks
```

架构：

```text
Claude Code
      │
      │ Official Hooks
      ▼
CC Monitor Hook Receiver
      │
      │ Local State File
      ▼
CC Monitor Desktop App
      │
      ▼
Always-on-Top Widget
```

---

## 4.2 不依赖互联网

CC Monitor 必须完全本地运行。

禁止：

* 云服务器
* SaaS backend
* WebSocket cloud service
* Firebase
* Supabase
* 远程数据库

Claude Code Hooks 与 Monitor 之间必须通过本地机制通信。

MVP 推荐：

```text
Local JSON State Files
```

路径：

```text
%USERPROFILE%\.cc-monitor\
```

目录：

```text
.cc-monitor/
├── sessions/
│   ├── abc123.json
│   ├── def456.json
│   └── ...
├── config.json
└── logs/
    └── cc-monitor.log
```

---

## 4.3 Hook 必须极轻量

Hook 不能阻塞 Claude Code。

Hook 的执行逻辑只能是：

```text
Read stdin JSON
        ↓
Parse event
        ↓
Update state JSON
        ↓
Exit 0
```

目标：

```text
Hook execution < 100 ms
```

正常情况下最好：

```text
< 30 ms
```

Hook 中禁止：

* 弹 MessageBox
* 创建 GUI
* 发网络请求
* sleep
* 长时间等待
* 调用 LLM
* 执行复杂 PowerShell
* 等待 Monitor App 响应

Hook 必须 fire-and-forget 风格。

---

# 5. 技术栈

## 5.1 推荐技术方案

使用：

```text
C#
.NET 8
WPF
```

项目结构：

```text
CCMonitor/
├── src/
│   ├── CCMonitor.App/
│   ├── CCMonitor.Hook/
│   └── CCMonitor.Core/
├── tests/
│   ├── CCMonitor.Core.Tests/
│   └── CCMonitor.Hook.Tests/
├── scripts/
│   ├── install-hooks.ps1
│   └── uninstall-hooks.ps1
├── docs/
│   └── architecture.md
├── README.md
└── CCMonitor.sln
```

职责：

### CCMonitor.App

Windows WPF GUI。

负责：

* Always-on-Top Window
* Session 状态展示
* FileSystemWatcher
* 状态切换
* Windows 通知
* 悬浮窗
* 设置
* 多 Session UI

### CCMonitor.Hook

Console Application。

负责：

```text
stdin JSON
    ↓
Parse Hook Event
    ↓
State Mapping
    ↓
Atomic JSON Write
    ↓
Exit
```

禁止依赖 WPF。

必须能够单独执行：

```powershell
Get-Content event.json | CCMonitor.Hook.exe
```

### CCMonitor.Core

共享业务逻辑。

包括：

* Models
* State Machine
* Event Parser
* State Mapper
* Storage
* Path Utilities

---

# 6. Claude Code Hooks 事件设计

需要监听以下事件。

## 6.1 SessionStart

状态：

```text
IDLE
```

作用：

创建 Session。

事件：

```text
SessionStart
```

行为：

```text
Create session state
status = IDLE
```

---

## 6.2 UserPromptSubmit

状态：

```text
RUNNING
```

事件：

```text
UserPromptSubmit
```

状态转换：

```text
IDLE → RUNNING
DONE → RUNNING
ERROR → RUNNING
BLOCKED → RUNNING
```

记录：

```text
startedAt = current timestamp
updatedAt = current timestamp
```

可选：

从 Hook JSON 中读取 prompt。

MVP 不允许在悬浮窗展示完整 prompt。

只允许保存：

```text
promptPreview
```

最大：

```text
100 characters
```

用户配置中允许关闭 prompt 保存。

默认：

```text
savePromptPreview = false
```

隐私优先。

---

## 6.3 PermissionRequest

状态：

```text
BLOCKED
```

显示文案：

```text
NEEDS ATTENTION
Permission required
```

状态转换：

```text
RUNNING → BLOCKED
```

记录：

```text
blockedAt
updatedAt
```

如果 Hook JSON 提供：

```text
tool_name
```

保存：

```text
blockedReason = "Permission required: Bash"
```

或者：

```text
blockedReason = "Permission required: Edit"
```

UI 示例：

```text
🔴 NEEDS ATTENTION

fstr_img_tag_manager

Permission required: Bash
```

---

## 6.4 Notification / permission_prompt

作为 PermissionRequest 的 fallback。

Matcher：

```text
permission_prompt
```

状态：

```text
BLOCKED
```

如果当前 Session 已经是 BLOCKED：

不要重复产生通知。

要求实现：

```text
notification deduplication
```

例如：

```text
same session
same status
within 3 seconds
```

只发送一次通知。

---

## 6.5 Notification / idle_prompt

状态：

```text
DONE
```

显示：

```text
DONE
Waiting for your input
```

该事件可作为 Stop 状态确认。

注意：

不要因为收到 idle_prompt 创建新的 Session。

只更新现有 Session。

---

## 6.6 Stop

状态：

```text
DONE
```

Stop 表示 Claude 已完成当前 response。

状态转换：

```text
RUNNING → DONE
BLOCKED → DONE
```

记录：

```text
finishedAt
updatedAt
```

计算：

```text
duration = finishedAt - startedAt
```

UI：

```text
🟢 DONE

fstr_img_tag_manager

Finished in 04:21
```

Windows Notification：

```text
Claude Code finished
fstr_img_tag_manager completed in 4m 21s
```

---

## 6.7 StopFailure

状态：

```text
ERROR
```

StopFailure 表示当前 turn 因 API error 结束。

状态转换：

```text
RUNNING → ERROR
BLOCKED → ERROR
```

记录：

```text
failedAt
updatedAt
```

UI：

```text
❌ ERROR

fstr_img_tag_manager

Claude Code stopped because of an API error
```

Windows Notification：

```text
Claude Code error

fstr_img_tag_manager stopped unexpectedly
```

---

## 6.8 SessionEnd

状态：

```text
CLOSED
```

Session 从 Active Session List 移除。

但状态文件不立即删除。

设置：

```text
sessionRetentionHours = 24
```

默认：

```text
24 hours
```

超过时间后自动清理。

---

# 7. 状态机

必须实现明确的 State Machine。

状态：

```csharp
public enum ClaudeSessionStatus
{
    Idle,
    Running,
    Blocked,
    Done,
    Error,
    Closed
}
```

主要流程：

```text
SessionStart
     │
     ▼
   IDLE
     │
     │ UserPromptSubmit
     ▼
  RUNNING
     │
     ├──────── PermissionRequest ────────┐
     │                                   ▼
     │                                BLOCKED
     │                                   │
     │                                   │ UserPromptSubmit
     │                                   ▼
     │                                RUNNING
     │
     ├──────── Stop ─────────────────────► DONE
     │
     └──────── StopFailure ──────────────► ERROR
```

下一轮：

```text
DONE
  │
  │ UserPromptSubmit
  ▼
RUNNING
```

错误后：

```text
ERROR
  │
  │ UserPromptSubmit
  ▼
RUNNING
```

SessionEnd：

```text
ANY STATE
    │
    ▼
  CLOSED
```

---

# 8. Session 数据结构

定义：

```csharp
public sealed class ClaudeSessionState
{
    public string SessionId { get; set; }
    public string ProjectName { get; set; }
    public string WorkingDirectory { get; set; }

    public ClaudeSessionStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }

    public string? BlockedReason { get; set; }
    public string? PromptPreview { get; set; }

    public string? LastHookEvent { get; set; }
}
```

JSON Example：

```json
{
  "sessionId": "abc123",
  "projectName": "fstr_img_tag_manager",
  "workingDirectory": "C:\\Users\\ExampleUser\\Desktop\\project",
  "status": "Blocked",
  "createdAt": "2026-07-09T13:01:23+08:00",
  "updatedAt": "2026-07-09T13:06:12+08:00",
  "startedAt": "2026-07-09T13:01:40+08:00",
  "blockedAt": "2026-07-09T13:06:12+08:00",
  "finishedAt": null,
  "failedAt": null,
  "blockedReason": "Permission required: Bash",
  "promptPreview": null,
  "lastHookEvent": "PermissionRequest"
}
```

---

# 9. 项目名获取逻辑

优先使用 Hook JSON：

```text
cwd
```

例如：

```text
C:\Users\ExampleUser\Desktop\project
```

提取：

```text
fstr_img_tag_manager
```

算法：

```csharp
Path.GetFileName(
    workingDirectory.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
    )
)
```

必须同时处理：

Windows Path：

```text
C:\Users\ExampleUser\Desktop\project
```

Git Bash / MSYS 风格路径：

```text
/c/Users/ExampleUser/Desktop/project
```

WSL Path：

```text
/home/admin/project
```

MVP 主要支持 Windows Claude Code。

但 Path Parser 不应 hardcode：

```text
C:\
```

---

# 10. 本地 State Storage

Session 文件：

```text
%USERPROFILE%\.cc-monitor\sessions\<session_id>.json
```

例如：

```text
C:\Users\ExampleUser\.cc-monitor\sessions\abc123.json
```

要求：

必须 Atomic Write。

禁止：

```text
open
truncate
write slowly
```

推荐：

```text
write temp file
        ↓
flush
        ↓
File.Move / File.Replace
```

流程：

```text
abc123.json.tmp
        ↓
write complete JSON
        ↓
replace
        ↓
abc123.json
```

原因：

Monitor 使用 FileSystemWatcher。

不能读取 half-written JSON。

---

# 11. Hook Receiver

Executable：

```text
CCMonitor.Hook.exe
```

调用方式：

```text
Claude Code Hook
       │
       ▼
CCMonitor.Hook.exe
```

Claude Code 将 JSON 传入 stdin。

伪代码：

```csharp
static async Task<int> Main()
{
    try
    {
        string input = await Console.In.ReadToEndAsync();

        HookEvent hookEvent = HookEventParser.Parse(input);

        ClaudeSessionState state =
            await stateStore.GetOrCreateAsync(hookEvent.SessionId);

        stateMachine.Apply(state, hookEvent);

        await stateStore.SaveAtomicAsync(state);

        return 0;
    }
    catch (Exception ex)
    {
        LogError(ex);

        // Monitor failure must never block Claude Code.
        return 0;
    }
}
```

非常重要：

> CC Monitor 的任何错误都不能影响 Claude Code 正常运行。

因此 Hook Receiver 即使内部异常：

```text
exit 0
```

错误写日志。

不要：

```text
exit 2
```

不要 block Claude Code。

---

# 12. Hooks 安装脚本

提供：

```text
scripts/install-hooks.ps1
```

功能：

安全修改：

```text
%USERPROFILE%\.claude\settings.json
```

要求：

## 12.1 保留现有配置

禁止直接覆盖：

```json
{
  "hooks": {}
}
```

必须：

1. 读取现有 JSON；
2. Parse；
3. 保留所有已有字段；
4. 保留已有 Hooks；
5. 添加 CC Monitor Hooks；
6. 避免重复安装；
7. 写回。

例如原文件：

```json
{
  "model": "opus",
  "hooks": {
    "PostToolUse": []
  }
}
```

安装后：

```json
{
  "model": "opus",
  "hooks": {
    "PostToolUse": [],
    "SessionStart": [
      ...
    ],
    "UserPromptSubmit": [
      ...
    ]
  }
}
```

禁止删除：

```text
model
permissions
env
existing hooks
plugins
other settings
```

---

## 12.2 配置事件

安装：

```text
SessionStart
UserPromptSubmit
PermissionRequest
Stop
StopFailure
SessionEnd
```

以及：

```text
Notification
matcher = permission_prompt
```

和：

```text
Notification
matcher = idle_prompt
```

所有事件调用：

```text
CCMonitor.Hook.exe
```

Hook Receiver 根据：

```text
hook_event_name
```

和事件特有字段决定状态。

Notification 根据 notification type 决定具体状态。

---

## 12.3 Hook 配置示意

注意：实际实现应由安装器根据 Claude Code 当前 settings schema 正确生成。

示意：

```json
{
  "hooks": {
    "SessionStart": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\CCMonitor.Hook.exe"
          }
        ]
      }
    ],
    "UserPromptSubmit": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\CCMonitor.Hook.exe"
          }
        ]
      }
    ],
    "PermissionRequest": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\CCMonitor.Hook.exe"
          }
        ]
      }
    ],
    "Notification": [
      {
        "matcher": "permission_prompt",
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\CCMonitor.Hook.exe"
          }
        ]
      },
      {
        "matcher": "idle_prompt",
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\CCMonitor.Hook.exe"
          }
        ]
      }
    ],
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\CCMonitor.Hook.exe"
          }
        ]
      }
    ],
    "StopFailure": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\CCMonitor.Hook.exe"
          }
        ]
      }
    ],
    "SessionEnd": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "C:\\Path\\CCMonitor.Hook.exe"
          }
        ]
      }
    ]
  }
}
```

实现时不要盲目复制示意 JSON。

应根据实际 Claude Code Hook schema 编写并测试。

---

# 13. Hook 卸载

提供：

```text
scripts/uninstall-hooks.ps1
```

功能：

只删除 CC Monitor 注册的 Hook Handler。

禁止删除：

* 其他 Hooks
* hooks object
* settings.json
* 用户其他配置

推荐安装时为 handler command 使用固定 executable path。

卸载时只删除：

```text
command == CCMonitor.Hook.exe path
```

匹配的 handlers。

---

# 14. Desktop App UI

使用 WPF。

窗口属性：

```text
Topmost = true
ShowInTaskbar = false
ResizeMode = NoResize
WindowStyle = None
```

默认位置：

```text
Screen Right Top
```

距离：

```text
right = 20 px
top = 60 px
```

宽度：

```text
300 px
```

单 Session 高度：

```text
130 px
```

---

# 15. UI 状态颜色

状态视觉规范：

```text
IDLE
⚪ Gray

RUNNING
🟡 Yellow / Amber

BLOCKED
🔴 Red

DONE
🟢 Green

ERROR
❌ Red / Dark Red
```

颜色应定义为 Resource。

禁止散落 hard-coded color。

例如：

```xml
<SolidColorBrush x:Key="RunningBrush" Color="#F59E0B" />
```

---

# 16. 主窗口内容

Single Session：

```text
┌──────────────────────────────┐
│ CC Monitor               ─ × │
│                              │
│ ● RUNNING                    │
│                              │
│ fstr_img_tag_manager         │
│                              │
│ Working for 02:31            │
└──────────────────────────────┘
```

字段：

1. Status Icon
2. Status Text
3. Project Name
4. Status Detail
5. Duration

RUNNING：

```text
Working for 02:31
```

BLOCKED：

```text
Waiting for 00:42
Permission required: Bash
```

DONE：

```text
Finished 12 sec ago
Duration 04:21
```

ERROR：

```text
Stopped 8 sec ago
API error
```

IDLE：

```text
Waiting for task
```

---

# 17. Always-on-Top

窗口默认：

```text
Topmost = true
```

用户可以关闭。

设置：

```text
Always on top
[✓]
```

关闭后：

```text
Topmost = false
```

必须持久化。

---

# 18. 窗口拖拽

用户按住窗口任意空白区域：

```text
MouseLeftButtonDown
```

调用：

```csharp
DragMove();
```

记录窗口：

```text
Left
Top
```

保存至：

```text
config.json
```

应用重启恢复位置。

---

# 19. 多 Claude Code Session 支持

必须支持多个 Claude Code Session。

典型场景：

```text
VS Code Window 1
project-a
claude

VS Code Window 2
project-b
claude
```

Monitor：

```text
┌──────────────────────────────┐
│ CC Monitor                   │
├──────────────────────────────┤
│ 🔴 project-a                 │
│ NEEDS ATTENTION              │
│ Permission required          │
├──────────────────────────────┤
│ 🟡 project-b                 │
│ RUNNING                      │
│ Working for 05:12            │
└──────────────────────────────┘
```

Session 唯一标识：

```text
session_id
```

禁止使用：

```text
project name
```

作为唯一标识。

因为：

```text
同一个 project
```

可能存在：

```text
两个 Claude Session
```

---

# 20. Session 排序

Priority：

```text
BLOCKED
ERROR
RUNNING
DONE
IDLE
```

同 Priority：

```text
updatedAt DESC
```

例如：

```text
BLOCKED session
ERROR session
RUNNING session
DONE session
```

确保最需要用户注意的 Session 在顶部。

---

# 21. Windows Notifications

使用 Windows 原生通知机制。

触发：

## BLOCKED

Title：

```text
Claude Code needs attention
```

Body：

```text
fstr_img_tag_manager requires permission
```

## DONE

Title：

```text
Claude Code finished
```

Body：

```text
fstr_img_tag_manager completed in 4m 21s
```

## ERROR

Title：

```text
Claude Code stopped
```

Body：

```text
fstr_img_tag_manager encountered an error
```

RUNNING 不发送通知。

IDLE 不发送通知。

---

# 22. Notification Deduplication

必须防止：

```text
PermissionRequest
+
Notification(permission_prompt)
```

造成两次通知。

Dedup key：

```text
sessionId
+
targetStatus
```

窗口：

```text
3 seconds
```

如果：

```text
same session
same status
less than 3 seconds
```

不要再次发送通知。

状态文件仍可更新：

```text
updatedAt
```

但禁止重复 Toast。

---

# 23. 声音

默认：

```text
BLOCKED = Sound enabled
DONE = Sound enabled
ERROR = Sound enabled
```

配置：

```text
[✓] Blocked sound
[✓] Done sound
[✓] Error sound
```

使用 Windows System Sounds。

MVP 不需要自定义 mp3。

---

# 24. File Watching

CCMonitor.App 使用：

```csharp
FileSystemWatcher
```

监听：

```text
%USERPROFILE%\.cc-monitor\sessions\
```

事件：

```text
Created
Changed
Deleted
Renamed
```

注意：

FileSystemWatcher 事件可能：

* 重复触发
* 快速连续触发
* 文件暂时被占用

因此实现：

```text
50-100 ms debounce
```

然后读取 Session State。

JSON parse failure：

```text
retry 3 times
```

间隔：

```text
50 ms
```

仍失败：

```text
log
ignore event
```

禁止 App Crash。

---

# 25. App Startup

启动流程：

```text
Start App
    ↓
Create required directories
    ↓
Load config
    ↓
Load existing session JSON
    ↓
Remove expired sessions
    ↓
Render sessions
    ↓
Start FileSystemWatcher
    ↓
Show Widget
```

---

# 26. App Single Instance

CC Monitor Desktop App 必须 Single Instance。

使用：

```text
Mutex
```

例如：

```text
Global\CCMonitor.App
```

第二次打开：

```text
detect existing process
        ↓
bring existing window to front
        ↓
exit
```

不得同时运行两个 Monitor App。

注意：

```text
CCMonitor.Hook.exe
```

不是 Single Instance。

多个 Hook Receiver 必须允许并发。

---

# 27. 并发安全

可能出现：

```text
UserPromptSubmit
PermissionRequest
Notification
Stop
```

短时间连续执行。

甚至多个 Hook Process 并发。

StateStore 必须考虑并发。

至少实现：

```text
Named Mutex per session
```

格式：

```text
CCMonitor.Session.<hash(sessionId)>
```

流程：

```text
Acquire session mutex
        ↓
Read current state
        ↓
Apply event
        ↓
Atomic write
        ↓
Release mutex
```

Timeout：

```text
500 ms
```

获取失败：

```text
log
exit 0
```

禁止阻塞 Claude Code。

---

# 28. 日志

使用 rolling log。

路径：

```text
%USERPROFILE%\.cc-monitor\logs\
```

日志：

```text
cc-monitor-app.log
cc-monitor-hook.log
```

要求：

```text
max 5 MB per file
max 3 files
```

禁止默认记录：

* 完整 prompt
* Claude response
* API Key
* environment variables
* transcript content

允许：

```text
timestamp
session_id shortened
hook_event_name
old status
new status
execution duration
error stack trace
```

例如：

```text
2026-07-09 13:04:12
session=abc123
event=PermissionRequest
Running -> Blocked
duration=8ms
```

---

# 29. 隐私要求

默认不读取：

```text
transcript_path
```

禁止读取 Claude Conversation JSONL。

虽然 Hook Input 可能提供 transcript path：

MVP 不允许打开 transcript。

禁止：

```text
ReadAllText(transcript_path)
```

不保存：

* Claude 完整回答
* 用户完整 prompt
* 代码内容
* Tool output

CC Monitor 是：

> Status Monitor

不是：

> Claude conversation logger

---

# 30. Config

路径：

```text
%USERPROFILE%\.cc-monitor\config.json
```

Schema：

```json
{
  "alwaysOnTop": true,
  "showWindowsNotifications": true,
  "blockedSound": true,
  "doneSound": true,
  "errorSound": true,
  "savePromptPreview": false,
  "sessionRetentionHours": 24,
  "windowLeft": null,
  "windowTop": null
}
```

如果 config 不存在：

使用 Default Config。

如果 JSON 损坏：

1. rename：

```text
config.json.corrupted.<timestamp>
```

2. 创建默认 config。

禁止 App Crash。

---

# 31. 设置 UI

点击：

```text
⚙
```

显示简单设置窗口。

项目：

```text
General

[✓] Always on top
[✓] Windows notifications

Notifications

[✓] Sound when permission is required
[✓] Sound when task finishes
[✓] Sound when task fails

Privacy

[ ] Save prompt preview

Sessions

Keep closed sessions for:
[24] hours

Claude Code Integration

Hooks status: Installed

[Reinstall Hooks]
[Uninstall Hooks]
```

---

# 32. Hook 安装状态检测

App 启动后检查：

```text
~/.claude/settings.json
```

判断 CC Monitor Handler 是否注册。

UI：

```text
Hooks status: Installed
```

或者：

```text
Hooks status: Not installed
```

未安装：

显示：

```text
Claude Code integration is not configured.

[Install Hooks]
```

用户点击：

```text
Install Hooks
```

执行安装逻辑。

推荐：

不要仅调用 PowerShell script。

将核心 JSON merge logic 实现在：

```text
CCMonitor.Core
```

PowerShell script 可以调用 CLI。

例如：

```text
CCMonitor.App.exe --install-hooks
```

或者单独：

```text
CCMonitor.Setup.exe
```

由开发者选择合理设计。

---

# 33. MVP 范围

必须实现：

* Windows 10 / 11
* Claude Code Hooks
* SessionStart
* UserPromptSubmit
* PermissionRequest
* Notification permission_prompt fallback
* Notification idle_prompt
* Stop
* StopFailure
* SessionEnd
* RUNNING 状态
* BLOCKED 状态
* DONE 状态
* ERROR 状态
* IDLE 状态
* Always-on-Top WPF Widget
* Project Name
* Running Duration
* 多 Session
* Windows Notifications
* Sound
* Atomic JSON State
* FileSystemWatcher
* Hook Installer
* Hook Uninstaller
* Existing settings.json merge
* Notification Deduplication
* Basic Settings
* Logging
* README

---

# 34. 非 MVP 范围

第一版禁止实现：

* VS Code Extension
* Electron
* React
* Web UI
* Cloud Sync
* Account system
* Claude API
* Anthropic API Key
* LLM 状态分类
* Terminal OCR
* Terminal parsing
* Transcript parsing
* Prompt History
* Claude Conversation Viewer
* Mobile App
* macOS
* Linux GUI
* WSL GUI
* Remote Claude monitoring
* Team dashboard
* Database
* SQLite

不要过度设计。

---

# 35. UX 要求

核心原则：

> User should understand Claude Code status in less than one second.

因此禁止：

```text
Session status: execution_in_progress
```

使用：

```text
RUNNING
```

禁止：

```text
awaiting_permission_request
```

使用：

```text
NEEDS ATTENTION
```

完整用户文案：

```text
IDLE
Waiting for task

RUNNING
Claude is working

NEEDS ATTENTION
Permission required

DONE
Waiting for your input

ERROR
Claude stopped unexpectedly
```

---

# 36. 性能要求

CCMonitor.App：

Idle CPU：

```text
< 1%
```

Memory target：

```text
< 100 MB
```

Hook Receiver：

Execution target：

```text
< 30 ms typical
< 100 ms acceptable
```

Hook Receiver 不允许常驻。

执行后立即退出。

Monitor 状态更新延迟：

```text
Hook Event → UI Update
< 500 ms
```

目标：

```text
< 200 ms
```

---

# 37. 可靠性要求

以下情况不能 Crash：

* sessions folder 不存在
* config.json 不存在
* state JSON 损坏
* settings.json 不存在
* settings.json 已有 Hooks
* settings.json 有用户自定义字段
* Hook stdin 为空
* Hook stdin JSON 无效
* Unknown hook event
* session state 文件被锁
* 两个 Hooks 同时触发
* App 没有运行
* Claude Code 没有运行
* Project directory 被删除
* FileSystemWatcher 重复事件
* Windows Notification 失败

原则：

> Monitor failure must never affect Claude Code.

---

# 38. Unit Tests

必须覆盖：

## State Machine Tests

```text
SessionStart -> Idle
UserPromptSubmit -> Running
Running + PermissionRequest -> Blocked
Running + Stop -> Done
Blocked + Stop -> Done
Running + StopFailure -> Error
Done + UserPromptSubmit -> Running
Error + UserPromptSubmit -> Running
Any + SessionEnd -> Closed
```

## Event Parser Tests

测试：

```text
SessionStart JSON
UserPromptSubmit JSON
PermissionRequest JSON
Notification permission_prompt
Notification idle_prompt
Stop JSON
StopFailure JSON
SessionEnd JSON
Unknown Event
Invalid JSON
Empty Input
```

## StateStore Tests

测试：

```text
Create
Read
Atomic Write
Concurrent Write
Corrupted JSON
Missing Directory
```

## Settings Merger Tests

输入：

```json
{}
```

输入：

```json
{
  "model": "opus"
}
```

输入：

```json
{
  "hooks": {}
}
```

输入：

```json
{
  "hooks": {
    "PostToolUse": []
  }
}
```

输入：

```json
{
  "hooks": {
    "Notification": [
      {
        "matcher": "custom",
        "hooks": []
      }
    ]
  }
}
```

验证：

* existing config preserved
* existing hooks preserved
* CC Monitor hooks added
* install twice does not duplicate
* uninstall removes only CC Monitor

---

# 39. Manual Test Checklist

Test 1：

```text
Start Claude Code
```

Expected：

```text
CC Monitor shows IDLE
```

Test 2：

输入：

```text
Explain this project
```

Expected：

```text
RUNNING
```

Test 3：

让 Claude 执行需要批准的 Bash command。

Expected：

```text
BLOCKED
Windows Notification
Sound
```

Test 4：

批准。

Claude 继续工作。

Expected：

```text
RUNNING
```

如果 Claude Code 没有直接发送能恢复 RUNNING 的 Hook：

需要评估使用 `PreToolUse` 或 `PostToolUse` 作为 BLOCKED → RUNNING 的补充事件。

注意：

不要在未验证真实 Hook 顺序前猜测。

记录 Hook Event Sequence 并根据真实 Claude Code 行为修正 State Machine。

Test 5：

Claude 完成 response。

Expected：

```text
DONE
```

Test 6：

提交下一个 prompt。

Expected：

```text
RUNNING
```

Test 7：

同时打开两个 Claude Code Session。

Expected：

```text
two session cards
```

Test 8：

关闭 Claude Code。

Expected：

Session becomes CLOSED and is removed from active UI.

---

# 40. Hook Event Debug Mode

开发阶段提供：

```text
CCMONITOR_DEBUG_HOOKS=1
```

开启时保存 Hook 原始事件。

路径：

```text
%USERPROFILE%\.cc-monitor\debug-hooks\
```

文件：

```text
20260709-130412-PermissionRequest.json
```

注意：

该模式默认关闭。

README 明确说明：

> Debug Hook Mode may store Claude Code hook metadata and must only be enabled for local debugging.

不允许生产默认开启。

主要用途：

确认真实事件顺序：

```text
UserPromptSubmit
PreToolUse
PermissionRequest
Notification
PostToolUse
Stop
```

然后调整 State Machine。

---

# 41. README 要求

README 必须包括：

## What is CC Monitor?

一句话：

```text
CC Monitor is a lightweight Windows desktop widget that shows whether Claude Code is working, waiting for permission, finished, or stopped with an error.
```

## Screenshot

提供 UI screenshot placeholder。

## Requirements

```text
Windows 10/11
Claude Code
.NET runtime only if not self-contained
```

## Installation

```text
Download
Run CCMonitor.App.exe
Click Install Hooks
Restart or reopen Claude Code if required
Run /hooks to verify integration
```

## Status Meaning

```text
⚪ IDLE
🟡 RUNNING
🔴 NEEDS ATTENTION
🟢 DONE
❌ ERROR
```

## Architecture

```text
Claude Code Hooks
      ↓
CCMonitor.Hook.exe
      ↓
Local JSON State
      ↓
CCMonitor.App.exe
```

## Privacy

明确：

```text
CC Monitor does not read Claude Code transcripts by default.
CC Monitor does not send data to a server.
CC Monitor does not require an Anthropic API key.
```

## Troubleshooting

至少包含：

```text
Monitor does not change status
/hooks does not show CC Monitor
settings.json is invalid
Hook executable moved
Notifications do not appear
Multiple sessions show the same project name
```

---

# 42. Build 和发布

目标：

```text
win-x64
```

Publish：

```powershell
dotnet publish src/CCMonitor.App/CCMonitor.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true
```

Hook：

```powershell
dotnet publish src/CCMonitor.Hook/CCMonitor.Hook.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true
```

最终目录：

```text
CCMonitor/
├── CCMonitor.App.exe
├── CCMonitor.Hook.exe
└── README.txt
```

可选后续：

```text
MSIX
WiX Installer
Inno Setup
```

MVP 不要求安装包。

优先实现可运行程序。

---

# 43. 开发顺序

必须按照以下顺序开发。

## Phase 1

实现：

```text
Core Models
Hook Event Parser
State Machine
State Store
Unit Tests
```

不要写 UI。

## Phase 2

实现：

```text
CCMonitor.Hook.exe
```

手工输入 mock JSON。

验证：

```text
stdin → state JSON
```

## Phase 3

注册 Claude Code Hooks。

开启：

```text
CCMONITOR_DEBUG_HOOKS=1
```

真实运行 Claude Code。

记录真实 Event Sequence。

根据实际事件修正 State Machine。

不要凭假设修改状态机。

## Phase 4

实现：

```text
WPF Monitor App
FileSystemWatcher
Session Cards
Always-on-Top
```

## Phase 5

实现：

```text
Windows Notifications
Sound
Deduplication
```

## Phase 6

实现：

```text
Hook Installer
Hook Uninstaller
Settings UI
```

## Phase 7

执行：

```text
Unit Test
Manual Integration Test
Release Build
README
```

---

# 44. Acceptance Criteria

项目完成必须满足以下条件。

### AC-01

用户在 VS Code Git Bash 中执行：

```text
claude
```

Monitor 能识别 Session。

### AC-02

用户提交 prompt 后：

```text
< 500 ms
```

Monitor 显示：

```text
RUNNING
```

### AC-03

Claude Code 请求权限：

```text
< 500 ms
```

Monitor 显示：

```text
NEEDS ATTENTION
```

并发送 Windows Notification。

### AC-04

Claude 完成 response：

Monitor 显示：

```text
DONE
```

并发送 Windows Notification。

### AC-05

Claude 因 API error 结束：

Monitor 显示：

```text
ERROR
```

### AC-06

用户切换到 Chrome 后：

Monitor 仍显示在屏幕最上层。

### AC-07

两个 Claude Session 同时运行：

Monitor 分别显示两个状态。

### AC-08

安装 Hooks 两次：

settings.json 不产生重复 CC Monitor Hook。

### AC-09

卸载 Hooks：

只删除 CC Monitor Hook。

### AC-10

CC Monitor App 未启动：

Claude Code 仍正常工作。

### AC-11

CCMonitor.Hook.exe 内部异常：

Claude Code 仍正常工作。

### AC-12

默认情况下：

CC Monitor 不读取 Claude transcript，不上传任何数据。

---

# 45. 关键工程约束

开发 Agent 必须遵循：

1. Do not build a VS Code extension.
2. Do not parse terminal text.
3. Do not use OCR.
4. Do not use Electron.
5. Do not use a cloud backend.
6. Do not require an Anthropic API key.
7. Do not read Claude transcripts in MVP.
8. Do not overwrite `~/.claude/settings.json`.
9. Do not remove existing Claude Code hooks.
10. Hook failures must never block Claude Code.
11. Use `session_id` as the session identity.
12. Use `cwd` to derive the project name.
13. Use atomic writes for session state.
14. Support concurrent Hook processes.
15. Verify real Claude Code Hook event sequences before finalizing BLOCKED → RUNNING behavior.
16. Keep the MVP small and production-usable.

---

# 46. 开发 Agent 最终交付要求

完成开发后必须输出：

1. Project architecture summary
2. Final file tree
3. State machine explanation
4. Claude Code Hook configuration explanation
5. How existing settings.json is preserved
6. Build command
7. Run command
8. Hook installation steps
9. Manual test steps
10. Known limitations

同时：

```text
dotnet test
```

必须通过。

执行 Release Build。

不要只输出代码片段。

必须在当前 repository 中创建完整可运行项目。

最终确认：

```text
CCMonitor.App.exe
CCMonitor.Hook.exe
```

能够构建成功。

---

# 47. 给开发 Agent 的执行指令

Read this PRD completely before writing code.

Implement the project in the current repository.

Do not simplify the architecture by parsing VS Code terminal output.

Claude Code official Hooks are the only source of session lifecycle events.

Start with the Core state machine, event parser, atomic state storage, and tests. Then implement the Hook executable. Before finalizing the UI state transitions, use real Claude Code Hook events or mock fixtures matching the official hook schema to validate the event mapping.

Preserve all existing Claude Code settings and hooks when installing the integration.

Do not stop after scaffolding. Build, test, and fix the project until the Release build succeeds.

When implementation is complete, provide a concise engineering report containing the final architecture, changed files, tests executed, build result, installation steps, and known limitations.
