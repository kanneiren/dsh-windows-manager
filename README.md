# DeepSeek Harness Manager

[**中文**](README.md) | [English](README.en.md)

[![Windows CI](https://github.com/kanneiren/dsh-windows-manager/actions/workflows/windows-ci.yml/badge.svg?branch=main)](https://github.com/kanneiren/dsh-windows-manager/actions/workflows/windows-ci.yml)

`DeepSeek Harness Manager` 是面向 Windows 11 的原生托盘 Supervisor：在本机安装、启动、打开、停止、重启和更新 DeepSeek Harness（DSH），并显示端口、进程、版本和运行状态。

DSH（npm 包 `@deepseek-ai/dsh`）是实际提供 Web UI 与 Agent 能力的程序；本项目只负责在 Windows 上安装和控制 DSH，不包含也不替代 DSH 本体。安装后创建 `DSH Manager` 桌面快捷方式，双击即可启动或打开 DSH Web UI。本项目是独立的非官方第三方管理器，不隶属于 DeepSeek。

架构上，原生托盘进程是进程外 Supervisor（.NET Framework 4.8 WinForms，EXE 约 136 kB，不依赖 PowerShell 7、Electron 或第三方托盘框架），负责启动、崩溃恢复、更新回滚、进程接管和托盘 UI；DSH 进程内部通过 Cordis Runtime Bridge 插件，经认证命名管道向 Manager 提供权威状态与生命周期事件，正常运行时零轮询（平均 CPU 0%）。Manager 以当前用户权限运行，只让 DSH 监听 `127.0.0.1`，不注册 Windows 服务，也不自动结束未知进程。

## 项目文档

- [功能与边界](docs/FEATURES.md)
- [项目架构](docs/ARCHITECTURE.md)
- [安全方案与漏洞报告](SECURITY.md)
- [Web UI 故障排查](docs/TROUBLESHOOTING.md)
- [性能基准与复测](docs/PERFORMANCE.md)
- [贡献与发布流程](CONTRIBUTING.md)
- [编码 Agent 指引](AGENTS.md)

## 功能

- 双击桌面快捷方式：发现已运行的 DSH 时直接打开 Web UI；未运行时启动并等待就绪后打开。
- 关闭浏览器不会结束 DSH，托盘图标继续显示真实进程状态。
- 右键托盘：打开、启动、停止、重启、查看状态、查看版本、检查更新、打开日志和退出管理器。
- 同时验证 HTTP 页面和进程命令，不会仅凭 `node.exe` 或端口号认定它是 DSH。
- 3080 被其他程序占用时，可选择空闲端口、查看占用详情或明确确认后结束未知进程。
- 通过 DSH Runtime Bridge 插件和认证命名管道执行优雅关闭，并从 DSH 内部获取权威 PID、端口、版本与生命周期事件。
- 支持 npm 全局安装、固定版本 npx 和 Git 源码检出。
- 配置模型支持多个 profile/实例，默认只创建一个 Web 实例。

## 安装与卸载

### 交给 Agent 的安装与卸载提示词

安装时，将下面这句话发给具有终端权限的编码 Agent：

```text
请为当前 Windows 用户安装最新版 DeepSeek Harness Manager：确认 Node.js 18+ 和 npm 可用后，执行 npx --yes dsh-windows-manager install；官方源失败时，可在记录原 registry 后临时切换至 https://registry.npmmirror.com。安装后确认桌面快捷方式存在，再运行 npx --yes dsh-windows-manager status --json，等待 managerRunning 和默认实例的 webUiVerified 为 true。不要请求管理员权限、覆盖已有 config.json 或删除用户数据，最后报告安装结果和 registry 变更。
```

卸载时，将下面这句话发给 Agent：

```text
请卸载当前用户的 DeepSeek Harness Manager：执行 npx --yes dsh-windows-manager uninstall，删除应用和桌面快捷方式，保留配置、日志及正在运行的 DSH；若安装过全局 CLI，再执行 npm uninstall --global dsh-windows-manager。未经我明确确认，不要使用 --purge-data 或结束 DSH，最后报告删除项和保留项。
```

项目不提供额外的 MSI、NSIS 或 Setup 安装器。DSH 本身依赖 Node.js/npm，而管理器只需执行当前用户目录复制、首次配置和快捷方式创建；使用 npm CLI、Agent 或源码中的 `Install.cmd` 可以保持发布体积和维护面最小。

从源码双击：

```text
Install.cmd
```

安装位置：

```text
%LOCALAPPDATA%\DeepSeekHarnessManager\app
```

配置与日志：

```text
%LOCALAPPDATA%\DeepSeekHarnessManager
```

桌面快捷方式直接启动 `DeepSeekHarnessManager.exe`，正常使用不会显示终端窗口。

安装目录只包含运行软件所需的 EXE、语言包、运行适配器、图标和文档，不包含 `src`、`tests` 或构建脚本。配置、状态和日志放在应用目录之外，因此覆盖安装默认不会删除用户数据。

### npm 命令行安装

可直接运行：

```text
npx --yes dsh-windows-manager install
```

也可以先全局安装命令：

```text
npm install --global dsh-windows-manager
dsh-windows-manager install
```

安装 npm 包本身不会通过 `postinstall` 修改系统。只有明确执行 `install` 子命令时才会复制应用和创建桌面快捷方式。

常用命令：

```text
dsh-windows-manager install --no-launch
dsh-windows-manager install --port 4000
dsh-windows-manager open
dsh-windows-manager start
dsh-windows-manager stop
dsh-windows-manager restart
dsh-windows-manager status
dsh-windows-manager diagnostics
dsh-windows-manager configure
dsh-windows-manager uninstall
dsh-windows-manager uninstall --purge-data
```

`start` 只启动 DSH，`open` 会启动并打开 Web UI。`uninstall` 默认保留配置和日志；`--purge-data` 才会完全清理。

首次运行采用 zero-config：只有一个 Windows DSH 和 Web frontend 时会直接使用默认值。需要高级配置时运行：

```text
dsh-windows-manager configure
```

非交互模式：

```text
dsh-windows-manager configure --runtime windows --frontend web --tray true --shortcut false --autostart true
```

`--runtime` 设置 `RuntimeType`（`windows`，`wsl` 预留），`--frontend` 设置 `web`/`oh-dsh`/`custom`，`--tray`、`--shortcut`、`--autostart` 设置托盘、桌面快捷方式和开机自启。不会显示重量级 GUI Wizard。

WSL 是可选项，默认关闭。启用前先检测：

```text
dsh-windows-manager wsl detect
dsh-windows-manager wsl enable --distro <实际 distro 名称>
dsh-windows-manager wsl status --json
dsh-windows-manager wsl disable
```

`configure --runtime wsl --wsl-distro <name>` 也会自动启用 WSL 支持并写入默认 distro。不在 WSL 内安装 Manager。

`3080` 只是新实例的默认端口，并非写死。新安装可通过 `--port 4000` 指定；已有安装不会因再次执行安装命令而覆盖配置，应通过托盘菜单打开管理器配置文件，修改 `config.json` 中实例的 `PreferredPort`，然后退出并重新启动管理器。管理器会显式向 DSH 传递 `--port`，外部手动启动的 DSH 也只有在端口与实例配置一致时才会被安全接管。

Manager 运行期间，CLI 的 `status` / `open` / `start` / `stop` / `restart` / `exit` 会通过本机命名管道 `\\.\pipe\dsh-windows-manager-control-{user}` 交给 Primary，而不是启动第二个 Supervisor。该 Manager Control Protocol v1 提供 `getVersion`、`getStatus`、`listInstances`、`start`、`stop`、`restart`、`open`、`exit`，仅允许当前 Windows 用户访问，不监听 TCP，也不提供任意命令执行。

`open` 遵循实例的 `Frontend` 配置：当前 `web` 打开本地 Web UI；`oh-dsh` 和 `custom` 已预留，但未配置实现时会明确报错，不会静默退回 Web。

把 `config.json` 中的 `TrayEnabled` 设为 `false` 后，同一个 `DeepSeekHarnessManager.exe` 会以无托盘模式运行：Manager Core、Supervisor、Runtime Bridge 和 Manager Control API 继续工作，通过 CLI 控制。

`RuntimeType` 已支持 WSL2 基本路径。Manager 只运行在 Windows，不在 WSL 内安装任何 Manager/daemon；WSL 内只有 DSH 进程和按启动注入的 Runtime Bridge `--patch`。`WslRuntimeAdapter` 通过 `wsl.exe` 管理进程句柄，用 WSL 内 Runtime Bridge 提供权威 PID/状态，transport 使用带 256 位 token 的 loopback TCP，不使用 WMI 猜测 Linux PID。需要 Windows 与 WSL2 之间启用 localhost/loopback 转发。



### 卸载

使用 npm 或 npx 安装时运行：

```text
npx --yes dsh-windows-manager uninstall
```

从源码安装时，可双击源码仓库中的 `Uninstall.cmd`。

默认卸载会删除应用目录和桌面快捷方式，但保留整个数据目录（配置、状态、日志、运行时状态和更新记录），也不会结束正在运行的 DSH。

完全清理数据：

```text
npx --yes dsh-windows-manager uninstall --purge-data
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Uninstall.ps1 -PurgeData
```

第二条命令仅用于源码仓库。若曾全局安装 CLI，还可运行 `npm uninstall --global dsh-windows-manager` 删除全局命令。

### 中国大陆网络

npm 官方源在中国大陆并非一定不可用，但可能出现超时、连接重置或下载缓慢。可以先检查：

```text
npm ping --registry=https://registry.npmjs.org
```

如果官方源不可用，可将当前用户的 npm 源切换到 npmmirror：

```text
npm config set registry https://registry.npmmirror.com
npx --yes dsh-windows-manager install
```

这里建议使用用户级 npm 配置，而不是只给单次 `npx` 命令增加 `--registry`：管理器在没有全局 DSH 时还会通过 npx 下载 `@deepseek-ai/dsh`，后续用户确认的 npm 更新也需要可用的 registry。镜像同步新版本可能有短暂延迟；找不到刚发布的版本时，应稍后重试或临时切回官方源。

## 打开方式

- 双击桌面快捷方式 `DSH Manager`：DSH 已运行时直接打开 Web UI；未运行时先启动，等待就绪后再打开。
- 双击托盘图标：打开默认实例的 Web UI。
- 命令行运行 `npx --yes dsh-windows-manager open`：执行与桌面快捷方式相同的操作。

关闭浏览器不会结束 DSH。只需启动服务而不打开浏览器时，运行 `npx --yes dsh-windows-manager start`。

## DSH 运行时选择

### 自动选择

默认 `Runtime` 为 `auto`，按以下顺序做本地检测：

1. npm 全局 `dsh.cmd`。
2. 已配置的 Git 源码目录。
3. `npx.cmd`。

本地检测只检查文件和 PATH，不运行 npm、Git，也不访问网络。

### 固定版本 npx

npx 模式使用：

```text
npx --yes @deepseek-ai/dsh@<PinnedVersion> ...
```

它不会在每次启动时静默切换到最新版。只有用户确认更新后才改变 `PinnedVersion`。

### Git 源码

源码用户可运行：

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Install.ps1 `
  -Runtime source `
  -SourceRoot C:\path\to\deepseek-harness
```

源码适配器验证 `.git`、`package.json`、`pnpm-lock.yaml` 和 `apps\cli`，通过 `pnpm dsh` 启动。源码必须先完成官方要求的 `pnpm install` 和 `pnpm run build`。

## 托盘菜单

- `Status`：运行、启动、停止、端口冲突、更新或错误状态。
- `Version`：当前 DSH 版本及可用新版。
- `Open Web UI`：打开当前实例地址。
- `Start Harness`：启动实例，不自动打开页面。
- `Stop Harness`：优先通过 Cordis 桥优雅关闭。
- `Restart Harness`：优雅关闭后重新启动。
- `Check for updates`：忽略缓存并立即检查。
- `Install available update`：仅发现新版时出现，必须再次确认。
- `Status details`：显示端口、PID、路径、指纹、工作区和日志。
- `Open workspace`：打开实例工作目录。
- `打开 DSH 配置文件`：打开该实例 `settings.yaml` 所在的 `DSH_HOME` 目录，不直接启动 YAML 编辑器。
- `DSH plugin marketplace`：打开 GitHub 插件发现页。
- `打开管理器配置文件`：打开管理器的 `%LOCALAPPDATA%\DeepSeekHarnessManager\config.json`。
- `Open logs`：打开日志目录。
- `Language / 语言`：在跟随 Windows、简体中文和 English 之间切换。
- `About`：显示管理器版本和 .NET 运行时。
- `Exit manager (leave DSH running)`：只退出托盘，保持 DSH 服务运行。

多个实例时，每个实例拥有独立子菜单。

## 更新策略

- 管理器启动时会判断自动检查是否到期。
- 管理器持续运行期间，到达上次自动尝试后的 24 小时也会检查一次。
- 计时基准是上次实际自动检查尝试；手动检查会把下一次自动检查顺延到 24 小时后。
- 距上次自动尝试不足 24 小时，只读取本地缓存。
- npm Registry HTTPS 请求超时 6 秒。
- Git 源码 `ls-remote` 超时 15 秒。
- 自动检查不重试，失败也进入 24 小时冷却，避免网络异常时反复请求。
- 手动 `Check for updates` 会绕过缓存。
- 更新绝不静默执行，必须由用户确认。
- npm 全局版执行固定目标版本的 `npm install --global`。
- npx 版只更新配置中的固定版本。
- 源码版仅在 Git 工作区干净时执行 `git pull --ff-only`、`pnpm install --frozen-lockfile` 和 `pnpm run build`。
- 更新后会使用随机本地端口和隔离的 `DSH_HOME`，按真实运行参数启动 DSH，验证 HTTP/进程双指纹，再通过 Cordis 优雅关闭。
- 兼容性测试失败会触发回滚并再次验证：全局 npm 恢复精确旧版本，npx 恢复旧固定版本，源码仅在工作区仍干净时恢复旧提交并重建。
- 更新事务写入 `%LOCALAPPDATA%\DeepSeekHarnessManager\updates`；只有更新或已恢复版本验证成功后才删除日志，回滚失败时保留供排查。

## 常驻性能

Manager Control Pipe 请求即时响应。由管理器启动并已连接 DSH IPC 桥的实例不再进行周期性的 WMI、进程枚举、端口或 HTTP 轮询：进程存活由 Windows 进程句柄事件负责，运行状态和生命周期事件由认证命名管道推送。后备探测仅用于外部接管、插件不可用、协议不兼容、启动阶段和诊断场景。

在 32 逻辑处理器的当前测试机上，0.2.0 稳定运行（桥已连接、settle 后 60 秒采样）中位数约为 `109.81 MB` 工作集、`62.65 MB` 私有内存、`846` 句柄、`20` 线程，60 秒平均 CPU 为 `0.000%`（单核等效）；同一机器 0.1.0 对照为 `59.07 MB`、`31.72 MB`、`473` 句柄、`12` 线程、`0.103%` CPU。事件驱动重构以小幅内存/句柄增加换取 CPU 归零：资源差异主要来自 CLR/WinForms/native 运行时基础设施（线程池、IO 完成端口、GC 段、持久认证管道与 WinForms 资源），业务托管堆占比很小（几 MB），复测数值稳定无泄漏；运行时使用默认 Workstation GC。内存不是本项目的主要优化目标。复现方法见 [性能文档](docs/PERFORMANCE.md)。进程没有分配 GPU 上下文。

## 优雅关闭

管理器启动 DSH 时追加一个动态 `--patch`，加载 `windows-lifecycle.mjs`：

当前桥已从单用途关闭通道升级为版本化运行时协议：管理器保持一条认证 IPC 连接，可调用 `ping`、`getStatus`、`getRuntimeInfo` 和 `shutdown`，并接收 `ready`、`stopping`、`exiting` 事件。`getStatus`/`getRuntimeInfo` 返回 DSH 进程内部可获得的 PID、实际监听端口、DSH 版本、profile 和 DSH home，不会用外部猜测值伪造状态。


1. 插件创建仅限本机的随机命名管道。
2. 管理器发送带 256 位随机令牌的关闭请求。
3. 插件调用 DSH 提供的 `ctx.appExit(0)`。
4. DSH 最多等待 5 秒执行整个 Cordis 插件树的 `dispose`。
5. 会话、文件监听器、终端和 HTTP 服务完成清理后退出。

命名管道不是网络端口，不暴露到局域网或互联网。外部启动且未加载配套插件的 DSH 仍可被接管和打开，但停止时会明确提示是否使用强制结束作为备用方案。


`plugins/deepseek-harness-web` 目录同时声明了 `dsh.bundle.patch`，可作为正式 DSH 插件包安装；未配置 `pipeName`/`token` 时插件保持 inert，不会开放未认证管道。



## 端口安全

未知端口占用进程会显示 PID、名称、路径、启动时间和关联 Windows 服务。

结束进程前会重新验证：

- 端口所有者仍是同一个 PID。
- PID 的启动时间和映像路径没有改变。
- 不是系统 PID、管理器自身、其他 Windows 会话或 Windows 系统目录进程。
- 不是承载 Windows 服务的进程。

管理器先尝试普通窗口关闭，失败后才二次确认强制结束。它不会自动结束未知进程，也不会自动申请管理员权限。

## 多实例

`config.example.json` 展示了 npm 日常实例和源码开发实例同时运行的配置。每个实例应使用独立端口。

需要强隔离时，为每个实例配置不同的 `DshHome`。这样可以避免并行进程共享活动会话状态；留空表示使用环境变量 `DSH_HOME`，环境变量也为空时使用默认 `~/.dsh`。实例菜单中的“打开 DSH 配置文件”会按同一规则打开 `settings.yaml` 所在目录，不会直接打开 YAML 或包含密钥的 `.credentials.yaml`。

管理器只创建一个托盘图标。配置一个实例时，操作项直接显示；配置多个实例时，每个实例按 `Name` 显示为独立子菜单，并拥有自己的状态、版本、打开、启动、停止、重启、更新、详情、工作区和 DSH 设置文件操作。

当前没有添加实例的图形界面。通过托盘菜单打开管理器配置文件，在 `config.json` 的 `Instances` 中加入具有唯一 `Id` 和 `PreferredPort` 的配置，然后退出并重新启动管理器。桌面快捷方式、托盘双击以及 CLI 的 `open`、`start`、`stop`、`restart` 默认只操作 `DefaultInstanceId`；`dsh-windows-manager status` 会列出全部实例。

## 权限和安全软件

- 每次启动都使用应用清单中的 `asInvoker` 当前用户权限，不会请求 UAC 管理员授权。
- 应用清单固定为 `asInvoker`，日常运行不请求 UAC。
- 安装和配置均位于当前用户目录。
- 不注册 Windows 服务；只有用户明确执行 `configure --autostart true` 时才写入当前用户的 Run 键。不修改防火墙、不监听 `0.0.0.0`。
- 若另一台电脑的 npm 全局目录需要管理员权限，更新会失败并显示错误，不会自动提权。
- 程序目前没有商业代码签名证书。360、Defender SmartScreen 等软件可能对首次运行、启动 Node、创建命名管道或用户主动结束进程进行启发式提示。
- 复制源码后在本机执行 `Build.cmd` 可得到可复现的本地构建；正式消除“未知发布者”需要受信任的代码签名证书。

SmartScreen 或安全软件的首次运行警告不等于 UAC 提权。若用户要求结束系统/其他会话进程，或 npm 全局目录需要管理员写权限，管理器不会自动提权：前者会被安全策略拒绝，后者会显示更新失败。

## 构建与测试

```text
Build.cmd
Test.cmd
```

测试覆盖：

- C# 5 / .NET Framework 4.8 构建。
- JSON 插件与配置。
- SemVer 与 24 小时更新缓存。
- IPv4/IPv6 端口到 PID 映射。
- HTTP 与进程双指纹。
- npm、npx、源码运行适配。
- 已运行 3080 实例接管。
- 命名管道版本化协议、认证、状态查询和 `ctx.appExit`。
- 真实 DSH 随机端口启动与 Cordis 优雅关闭。
- 更新后随机端口兼容性冒烟测试及全局 npm、npx、源码回滚事务。
- npm CLI 隔离安装、覆盖升级、状态查询、保留配置和卸载。

## GitHub Actions

`.github/workflows/windows-ci.yml` 会在每次推送、Pull Request 和手动触发时申请一台临时 `windows-latest` GitHub 托管虚拟机。工作流安装固定测试版本的 DSH，执行 `scripts\Test.ps1`，检查 npm 发布包内容，并保留七天的 Windows 构建产物。

它只影响 GitHub 上的自动验证，不会常驻用户电脑，也不会改变本地安装。公开仓库可直接使用 GitHub Actions；实际额度和并发限制以 GitHub 当前账户政策为准。

## 图标来源

黑色鲸鱼 SVG 来自 DeepSeek Harness 官方仓库：

`https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/web/public/favicon.svg`

上游仓库许可证：MIT。EXE 和托盘状态图标沿用该图案；`DSH Manager` 桌面快捷方式使用基于鲸鱼图案生成并叠加管理器徽标的独立图标。仓库和发布包直接提供全部预生成图标，用户无需生成。

## 开源协议

[MIT](LICENSE)
