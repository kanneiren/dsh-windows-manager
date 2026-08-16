# 安全政策与设计
[**中文**](SECURITY.md) | [English](SECURITY.en.md)

## 漏洞报告

请通过 https://github.com/kanneiren/dsh-windows-manager/security/advisories/new 私下报告疑似漏洞。请勿在公开议题中包含令牌、非公开日志、个人路径或漏洞利用细节。

请提供管理器版本、Windows 版本、运行时类型、复现步骤、预期行为，以及诊断问题所需且经过脱敏的最小日志片段。

## 信任边界

管理器信任其安装文件、用户自有配置、已声明的插件清单，以及用户明确选择的命令。它不会仅因某个进程监听预期端口或以 `node.exe` 运行，就信任该进程。

DSH Web UI 使用 `--host 127.0.0.1` 启动。生命周期桥接机制使用本地 Windows 命名管道，而不是网络端口。

## 进程识别

仅当运行中实例的 HTTP 响应包含已声明的 DSH 标记，且该实例所属进程的命令行匹配已声明的 DSH 模式时，才会将其认定为 DSH。正常接管时，仅有一个信号匹配并不足够。

当 DSH 内部的版本化 IPC 桥已通过每次启动随机生成的 256 位令牌认证时，桥上报的 PID/端口会再与端口占用者和进程身份（启动时间、映像路径、会话）进行核验，之后才作为权威运行状态。桥不可用时，外部接管仍保留 HTTP + 进程命令行双指纹要求。

在终止任何进程之前，管理器会再次查询端口占用进程，并核验其 PID、进程启动时间、可执行文件路径、Windows 会话、是否位于系统目录，以及所承载的 Windows 服务。系统进程、管理器自身、其他会话中的进程、路径无法核验的进程、Windows 目录中的进程，以及服务宿主进程均受保护。

未知进程绝不会被自动终止。用户必须主动请求终止；只有在正常关闭尝试失败并经过第二次确认后，才会强制终止。

外部启动的 DSH 进程若未进入 Web 就绪状态，不会因就绪等待超时而被自动终止。启动清理仅限当前管理器操作所启动的进程。

## Manager Control 协议

Manager 对 CLI 和第三方前端暴露一个独立的本地命名管道：

```text
\\.\pipe\dsh-windows-manager-control-{user-sid}
```

它只允许当前 Windows 用户读写，不监听 TCP，不暴露到局域网或互联网。协议 v1 仅包含 `getVersion`、`getStatus`、`listInstances`、`start`、`stop`、`restart`、`open`、`exit`，所有响应都带 `protocolVersion`。该协议不提供任意命令执行、PowerShell、npm 代理或任意文件读写。它只允许一次请求一个 JSON 行，输入上限 64 KiB。

## WSL 适配安全边界

未来的 WSL2 支持遵循以下边界：Manager 只运行在 Windows，不在 WSL 内安装 Manager/daemon；WSL 内只有 DSH 和生成的 Runtime Bridge `--patch`。WSL 默认关闭，检测只由用户显式触发（`wsl detect/enable/disable/status`）。只对用户配置的 distro 执行内部命令白名单；使用 `wsl.exe` 进程作为存活句柄，Linux PID 只接受 WSL 内 Runtime Bridge 上报值，不通过 WMI 猜测；Runtime Bridge transport 使用 loopback TCP 和与 Windows 命名管道相同的 256 位 token，不暴露到局域网；默认停止方式是桥内 `shutdown`，不会默认执行 `wsl.exe --terminate <distro>`。

## 优雅关闭与 IPC

每个由管理器启动的 DSH 实例都会通过生成的本地补丁，获得唯一的命名管道名称和随机生成的 256 位十六进制令牌。命名管道只允许已认证的新版 `ping`、`getStatus`、`getRuntimeInfo` 和 `shutdown` 命令；DSH 端插件验证令牌后才会调用 `ctx.appExit(0)`。

协议为每行一个 JSON 消息，并区分 command、response、event。插件拒绝未知命令、格式错误消息和不支持的协议版本，且不提供任意命令执行能力。

管道名称和令牌均仅用于对应的单次启动。它们并非监听中的 TCP 端点。桥接失败或不可用时，不会静默回退为直接终止进程。

## 安装与权限

应用程序清单采用 `asInvoker`。安装位置为当前用户的 LocalAppData 和桌面。项目不会注册 Windows 服务、修改防火墙规则、创建管理员任务或请求提升权限。

npm 软件包不包含安装生命周期钩子。下载该软件包不会安装或启动 Windows 应用程序；用户必须明确调用 CLI 的 `install` 命令。

配置和日志存放在可替换的应用程序目录之外。升级和默认卸载都会保留它们；如需彻底删除，必须使用 `--purge-data` 或 `-PurgeData`。

## 更新与供应链

自动更新检查绝不会安装代码。每次更新都必须得到明确确认。npm 更新会选择确切版本，npx 实例会保留固定版本；若 Git 检出目录不干净，源码更新将拒绝执行。

更新后，由管理器管理的冒烟测试进程会使用隔离的 DSH 主目录，在随机回环端口上运行。只有两个指纹均通过检查，并且经过身份验证的优雅关闭已释放端口后，才会接受此次更新。如果失败，则恢复上一版本并重新测试。只有在检出目录仍然干净时，源码回滚才会使用已记录的提交；否则会保留用户更改并留下恢复日志，而不会强制重置。

npm 软件包采用文件允许列表和 `prepack` 验证器。Windows CI 会执行干净构建、完整测试套件、tarball 安装和制品生成。当 npm 发布迁移至 GitHub Actions 时，应启用发布来源证明（provenance）。

软件包发布固定使用官方 `https://registry.npmjs.org/` 端点。最终用户仍可通过自己的 npm 配置选择可信的下载镜像。

可执行文件目前未签名。Windows SmartScreen 或端点安全产品可能会对下载的构建版本发出警告。可复现的本地构建可减少不确定性，但不能替代可信的代码签名证书。

## 已知残余风险

- DSH 是处于开发者预览阶段的上游依赖，其 CLI、Web 标记或 Cordis 生命周期 API 可能会变化。相关兼容代码集中在 DSH 端插件和 Manager 的 IPC 客户端中。
- 读取进程命令行和端口归属依赖 Windows API，在非标准权限或端点安全控制下可能失败。
- npm 和 Git 的可用性取决于用户的网络，以及已配置的软件包注册源或代理。
- 能够替换用户应用程序安装目录内文件的恶意行为者，已拥有等同的用户级访问权限。
- 产品名称和上游鲸鱼图标可能受其所有者的商标权约束；本管理器并不是 DeepSeek 的独立官方产品。
