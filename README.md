# SeeowKiller

**让教室电脑不再被「希沃集控」锁屏的小工具。**

默认只在**真的被锁屏时**才出手 —— 希沃的白板、课件、巡视等日常功能完全不受影响。

- 平台：Windows 10 / 11（x64），C# / .NET 8，带托盘图标，支持开机自启
- 默认策略：只在检测到锁屏窗口时解除锁屏（也可切换为"彻底不让它跑"）

> ⚠️ 请只用于你**有权管理**的电脑。学校资产请先确认不违反校规，详见 [注意事项](#注意事项请务必读)。

---

## 它解决什么问题

学校开通希沃集控后，管理员可以在后台一键把教室电脑**锁屏**：屏幕上盖一块全屏窗口、限制热键切不出去，解锁要扫码或找希沃客服要激活码。

SeeowKiller 常驻在你的电脑上，一旦发现屏幕上出现了那块锁屏窗口，就立刻让它消失（默认直接把产生它的进程结束掉）。它**不是**无差别地砍掉希沃的所有功能，只针对"锁屏"这一个动作。

---

## 3 分钟开始用

### 第 1 步：拿到程序

```powershell
git clone <本仓库地址>
cd SeeowKiller
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish
```

产物在 `dist\SeeowKiller.exe`。做了自包含单文件版（目标机免装 .NET 运行时）见 [从源码构建](#从源码构建)。

### 第 2 步：先试一下（不改任何系统设置）

```powershell
cd dist
.\SeeowKiller.exe --status      # 看状态：策略、锁屏判据、是否装了自启、硬拦截状态
.\SeeowKiller.exe --lockscan    # 看现在有没有"像锁屏"的窗口
```

### 第 3 步：装成开机自启

```powershell
.\SeeowKiller.exe --install              # 登录后自动静默启动（推荐）
.\SeeowKiller.exe --install --boot       # 想连开机阶段也一起压，再加这个
```

装好后右下角会出现一个红色圆叉图标，右键有菜单。之后正常用电脑即可，被锁屏时会自动解除并在日志留痕。

> 想先看效果又不动系统？直接运行 `.\SeeowKiller.exe`（不带参数）就是托盘模式，退出图标即结束。

---

## 默认行为：只在锁屏时动手

程序每 0.5 秒扫一次窗口，**四个条件同时满足**才判定为"锁屏"：

| 条件 | 默认值 |
| --- | --- |
| 窗口标题 | 精确等于 `希沃管家` |
| 窗口类名 | 含 `Chrome_WidgetWin` |
| 属主进程 | 含 `SeewoServiceAssistant` |
| 窗口状态 | **可见 + 置顶 + 全屏** |

命中后默认 **kill**：结束那个进程，锁屏窗口随即消失。

也可以换成更温和的 **hide**（把窗口藏起来、进程不动）：`--on-lock=hide`；隐藏失败（权限不够）会自动退回 kill。

> 判据可能随希沃版本变化。拿不准先跑 `--lockscan`：它会列出当前所有「可见+置顶+全屏」窗口的标题、类名、进程名，并标出哪些命中了判据。发现特征不一样，用 `--lock-title` / `--lock-class` / `--lock-process` 校正。

---

## 我该选哪种模式

| 我想要 | 用这个 | 说明 |
| --- | --- | --- |
| 别锁我屏，其它功能照常用（**默认**） | `--policy=lock` | 只在锁屏窗口出现时动手 |
| 上课期间别让希沃管家运行 | `--policy=kill` | 见一个杀一个，不看窗口（希沃其它功能也会一起没） |
| 连"启动"都不允许，最狠 | `--block on` | 给目标 exe 挂假调试器，从此它启动不起来 |

三种可以叠加。**硬拦截**是独立的手动开关：

```powershell
.\SeeowKiller.exe --block status          # 看当前挂的是什么
.\SeeowKiller.exe --block on              # 开：目标 exe 从此启动即失败（静默，不弹报错）
.\SeeowKiller.exe --block on --with-acl   # 更狠：再加一层"禁止执行"
.\SeeowKiller.exe --block off             # 撤掉，并还原被覆盖的原值
```

硬拦截的代价：希沃管家自己的修复/升级也会一起失败，而且**所有用户**都起不来（`--with-acl` 尤其会挡住自修复）。程序会定期自检，发现拦截被抹掉就重新挂上（日志里会写"检测到硬拦截被移除"）。

---

## 命令速查

```powershell
# 日常
.\SeeowKiller.exe                          # 托盘常驻（默认：只在锁屏时动手）
.\SeeowKiller.exe --status                 # 看状态
.\SeeowKiller.exe --lockscan               # 看有没有"像锁屏"的窗口（只读诊断）
.\SeeowKiller.exe --once                   # 立刻结束一次目标进程后退出

# 策略
.\SeeowKiller.exe --policy=lock            # 默认：只认锁屏
.\SeeowKiller.exe --policy=kill            # 无差别结束目标进程
.\SeeowKiller.exe --on-lock=hide           # 命中锁屏时改为"藏窗口"，藏不住才杀
.\SeeowKiller.exe --policy=kill --save     # 写进配置，以后默认就是它

# 自启
.\SeeowKiller.exe --install                # 登录自启（静默、最高权限，不弹 UAC）
.\SeeowKiller.exe --install --boot         # 再加一个开机 SYSTEM 任务
.\SeeowKiller.exe --install-run            # 兜底：写注册表 Run 项（每次登录弹一次 UAC）
.\SeeowKiller.exe --uninstall              # 一键全清：任务 + Run 项 + 撤销硬拦截

# 硬拦截
.\SeeowKiller.exe --block on|off|status
.\SeeowKiller.exe --block-acl on|off|status
.\SeeowKiller.exe --block-path="C:\...\SeewoServiceAssistant.exe"   # 手动指定目标 exe
.\SeeowKiller.exe --no-discover            # 只用上面指定的路径，不做自动探测

# 其它
.\SeeowKiller.exe --interval=300           # 扫描间隔（50~60000 毫秒，默认 500）
.\SeeowKiller.exe --never-kill=xxx.exe     # 保险丝：永不结束这个进程（可重复给）
.\SeeowKiller.exe --no-elevate             # 不提权运行
.\SeeowKiller.exe --help                   # 全部参数
```

> 本程序是 GUI 程序，在 PowerShell 里直接跑不会等它、也捕获不到输出。要看输出请：
> `.\SeeowKiller.exe --status > out.txt; Get-Content out.txt`

---

## 托盘菜单

| 菜单项 | 作用 |
| --- | --- |
| 立即结束一次 | 手动触发一次 |
| 暂停拦截 / 恢复 | 临时停手，其它设置不变 |
| 硬拦截：禁止启动（IFEO） | 勾选即开/关，等价于 `--block on/off` |
| 硬拦截：禁止执行（ACL） | 勾选即开/关，等价于 `--block-acl on/off` |
| 随登录启动 / 随开机启动 | 勾选即装/卸自启任务 |
| 打开日志 / 打开配置目录 | 直接定位到文件 |
| 退出 | 结束程序 |

双击托盘图标可看当前模式、累计拦截次数、最近一次拦截时间。

---

## 配置文件

位置 `%LOCALAPPDATA%\SeeowKiller\config.json`（首次运行自动生成；命令行参数优先级更高，加 `--save` 可把参数写回）

```json
{
  "Targets": ["SeewoServiceAssistant.exe"],
  "IntervalMs": 500,
  "LogPath": "C:\\Users\\you\\AppData\\Local\\SeeowKiller\\SeeowKiller.log",
  "NeverKill": [],
  "ReapplyBlock": true,
  "BlockPaths": [],
  "Policy": "lock",
  "OnLock": "kill",
  "LockTitle": "希沃管家",
  "LockClass": "Chrome_WidgetWin",
  "LockProcess": "SeewoServiceAssistant"
}
```

| 字段 | 说明 |
| --- | --- |
| `Policy` | `lock`（默认，只在锁屏时动手）/ `kill`（无差别） |
| `OnLock` | `kill`（默认）/ `hide`（先藏窗口） |
| `LockTitle` / `LockClass` / `LockProcess` | 锁屏判据三要素，判据失效时改这里 |
| `Targets` | 无差别模式 / 硬拦截的目标进程名 |
| `IntervalMs` | 扫描间隔（毫秒） |
| `NeverKill` | 保险丝名单，永不结束 |
| `ReapplyBlock` | 是否定期把被抹掉的硬拦截重新挂上 |
| `BlockPaths` | 硬拦截要处理的 exe 路径（自动探测不到时手填） |

日志：`%LOCALAPPDATA%\SeeowKiller\SeeowKiller.log`

---

## 常见问题

**装了但一点反应都没有？**
先 `--status` 看"管理员权限"是不是 `False`。结束希沃进程需要管理员；程序会自己弹 UAC 提权，如果你点了"否"就会以低权限运行（日志里会出现"拒绝访问"）。也可以右键"以管理员身份运行"。

**怎么知道它有没有在干活？**
看托盘提示（累计拦截次数）或日志。每次命中都会记录窗口标题、类名、PID、exe 路径。

**被锁屏了但它没反应？**
跑 `--lockscan`。如果窗口根本没列出来（候选 0 个），说明这版希沃换了实现，把输出里的标题/类名/进程名记下来，用 `--lock-title` / `--lock-class` / `--lock-process` 校正。如果列出来了但标"候选"、没标"命中锁屏"，看哪一项打了 `×` 就调哪一项。

**想更温和 / 更狠？**
更温和：`--on-lock=hide`（只藏窗口，不杀进程）。更狠：`--policy=kill`，或上硬拦截 `--block on --with-acl`。

**会影响老师正常上课用希沃白板吗？**
默认策略下不会 —— 它只对"锁屏窗口"动手。只有切到 `--policy=kill` 或开硬拦截才会影响希沃本身的运行。

**杀掉了又被拉起来，来回拉锯？**
可能出现。程序每 0.5 秒会再处理一次；想彻底解决就用硬拦截让它根本起不来。日志里反复出现"检测到锁屏"就是这个情况。

**开机自启装在哪？**
计划任务 `SeeowKiller-Logon`（登录后；最高权限、静默）与可选的 `SeeowKiller-Boot`（开机 20 秒后；SYSTEM）。`--print-task-xml` 可看将要写入的完整定义。

**日志/配置想删掉？**
整个 `%LOCALAPPDATA%\SeeowKiller\` 目录删掉即可。

---

## 不想用了：怎么还原

```powershell
.\SeeowKiller.exe --block off      # 若开过硬拦截，先撤（会还原被覆盖的原值）
.\SeeowKiller.exe --uninstall      # 删计划任务 + Run 项 + 撤销硬拦截
# 再从托盘右键退出，删除程序目录即可
```

程序本体已经找不到了的话：

1. 「任务计划程序」里删除 `SeeowKiller-Logon` 和 `SeeowKiller-Boot`
2. 删除 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `SeeowKiller` 项
3. 若开过 IFEO 硬拦截：删除
   `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\SeewoServiceAssistant.exe`
   下的 `Debugger` 值（64 位与 `WOW6432Node` 两处都要看）

---

## 注意事项（请务必读）

- **需要管理员权限**：装自启会写计划任务，硬拦截会写 HKLM 注册表 / 文件 ACL。请确认你了解这些改动。
- **可能被学校发现**：集控后台会看到设备"离线"或指令执行异常（锁屏、喊话、巡视失效）。据公开资料，2026-03 起集控还加入了针对此类插件的检测逻辑，并可能上报学校运维。
- **学校资产请谨慎**：如果这台电脑属于学校，使用本工具可能违反校规或设备使用协议，后果自负。
- **会挡住的正常功能**：`--policy=kill` 与硬拦截会让希沃管家整体不可用（含冰点还原、文件保护、远程巡视等）。只有默认的锁屏触发模式是"外科手术式"的。
- **只对应用层锁屏有效**：希沃的锁屏是它自己进程画的一块全屏置顶窗口，所以"关窗口/杀进程"有效；遇到驱动级或系统安全桌面级的限制，本工具无能为力。
- **已知未验证项**：`--on-lock=hide` 在真实环境（对方进程权限更高）可能隐藏失败，失败会自动退回杀进程；`--block on` 的 IFEO 注册需要管理员，首次在真机使用时请用 `--block status` 复核，并记住 `--block off` 怎么撤。

---

## 原理（30 秒版）

希沃管家的锁屏**不是 Windows 自带的锁屏**，而是前端进程 `SeewoServiceAssistant.exe`（Electron/Chromium 应用）弹出的一个**全屏 + 置顶窗口**（标题 `希沃管家`，窗口类 `Chrome_WidgetWin_0`），再配一套键盘热键限制。所以：

- 让这个窗口消失（或让这个进程不存在）→ 锁屏就没了 → 默认策略
- 让这个 exe 根本启动不起来 → 硬拦截（IFEO / ACL）

机制调研、真机二进制取证、实现细节、踩过的工程坑与测试记录：见 [`docs/技术细节.md`](docs/技术细节.md)。

---

## 从源码构建

需要 .NET 8 SDK（或更高）。

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish                  # 框架依赖，产物在 dist\
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish -SelfContained   # 单文件自包含（约 150MB，目标机免装运行时）
```

> `-SelfContained` 第一次构建需要联网下载 runtime pack；只想自己用的话，框架依赖版就够（目标机装一个
> [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 即可）。

目录结构：

```
SeeowKiller/             主程序（C# / .NET 8 / WinForms 托盘）
tests/FakeLockWindow/    造一个"假锁屏窗口"，用于验证识别是否有效（默认全透明，不挡屏幕）
build.ps1                一键 build / publish
global.json / NuGet.config   构建环境钉版（SDK 版本、清空包源，离线可编译）
docs/技术细节.md          原理、取证、实现与测试记录（面向开发者）
```

`build.ps1` 会把 dotnet 的家目录与包目录放在仓库内的 `.dotnet-home` / `.nuget-packages`，不污染用户目录。

### 自测识别是否有效

```powershell
cd tests\FakeLockWindow
dotnet build -c Release
.\bin\Release\net8.0\FakeLockWindow.exe --seconds=20   # 造一个"像锁屏"的窗口（全透明）
# 另开一个终端：
.\dist\SeeowKiller.exe --lockscan                      # 应当看到 [命中锁屏]
```

---

## 许可证

[MIT](LICENSE) © 2026 GETESy1 —— 可自由使用、修改、再发布，保留版权声明即可。

本仓库为独立实现（未包含任何第三方项目代码），机制调研参考了下方列出的公开项目与文档。

---

## 致谢

机制调研参考了以下公开项目与文档（仅参考思路，本仓库为独立实现）：

- [HugoWidget/HugoProgs](https://github.com/HugoWidget/HugoProgs)、[HugoDlls](https://github.com/HugoWidget/HugoDlls) —— 希沃功能增强工具集（锁屏窗口判据、IFEO 禁用思路）
- [HugoAura](https://github.com/HugoAura/Seewo-HugoAura) —— 希沃管家插件注入方案
- [eClassKiller/ECTools](https://github.com/eClassKiller/ECTools) —— 多种电子教室的限制解除
- [希沃集控帮助文档](https://help.seewo.com/hugo/seUwokPXWT) —— 锁屏指令与解锁方式说明
