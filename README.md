# DeepSeek Harness Manager

[**中文**](README.md) | [English](README.en.md)

[![Windows CI](https://github.com/kanneiren/dsh-windows-manager/actions/workflows/windows-ci.yml/badge.svg?branch=main)](https://github.com/kanneiren/dsh-windows-manager/actions/workflows/windows-ci.yml)

**Windows 与 WSL2 都能用的 DeepSeek Harness（DSH）托盘管理器。**

`DeepSeek Harness Manager` 是 Windows 上的原生托盘 Supervisor，可在 Windows 本机或 WSL2 Linux 发行版中安装、启动、打开、停止、重启和更新 DSH，并显示端口、进程、版本与运行状态。本项目是独立的非官方第三方管理器，不包含也不替代 DSH 本体（npm 包 `@deepseek-ai/dsh`）。

## 使用场景

- **Windows 原生**：支持 npm 全局安装、固定版本 npx 和 Git 源码三种运行方式；开始菜单/桌面快捷方式只打开托盘，由托盘菜单或 CLI 启动并打开 DSH Web UI。
- **WSL2 Linux**：Manager 仍运行在 Windows，DSH 运行在 WSL2 发行版中；自动忽略 Docker Desktop / Rancher Desktop / Podman 等辅助发行版，并选择 Ubuntu、Debian 等普通发行版，无需在 WSL 内安装 Manager。
- **托盘常驻**：关闭浏览器不会结束 DSH；托盘菜单提供打开、启停、状态、更新、日志和语言切换。
- **多实例**：Windows 与 WSL 实例可同时配置，每个实例拥有独立端口、状态与生命周期。

## 快速开始

Windows（PowerShell）：

```powershell
npm install --global dsh-windows-manager
dsh-windows-manager install
dsh-windows-manager open
```

安装默认只创建**开始菜单快捷方式**（Win 键可搜索到）并打开托盘，不会自动启动 DSH；需要桌面快捷方式时加 `--desktop-shortcut`，完全不要快捷方式时加 `--no-shortcut`。

WSL2（在 Windows 终端执行）：

```powershell
dsh-windows-manager wsl detect
dsh-windows-manager wsl enable --distro Ubuntu-24.04
dsh-windows-manager wsl open
```

`wsl enable --distro` 可以省略：管理器会自动忽略 Docker Desktop 等辅助发行版并选择普通发行版；也可直接点击托盘菜单中的“启动并打开WSL DSH”。中国大陆网络请参考 [安装与使用指南](docs/USAGE.zh-CN.md) 中的 npmmirror 说明。

## 项目文档

| 文档 | 内容 |
| --- | --- |
| [安装与使用指南](docs/USAGE.zh-CN.md) | 安装卸载、CLI、运行时选择、托盘菜单、更新与运维详情 |
| [功能与边界](docs/FEATURES.md) | 当前能力、入口与刻意排除项 |
| [项目架构](docs/ARCHITECTURE.md) | 组件、配置模型、Runtime Bridge、WSL 适配与打包 |
| [安全方案与漏洞报告](SECURITY.md) | 威胁模型、边界与报告流程 |
| [Web UI 故障排查](docs/TROUBLESHOOTING.md) | 常见问题、日志与恢复步骤 |
| [性能基准与复测](docs/PERFORMANCE.md) | 稳定态资源占用与复现方法 |
| [贡献与发布流程](CONTRIBUTING.md) | 构建、测试、发版与社区约定 |
| [编码 Agent 指引](AGENTS.md) | 面向自动化编码的硬性约束 |

## 开源协议

[MIT](LICENSE)
