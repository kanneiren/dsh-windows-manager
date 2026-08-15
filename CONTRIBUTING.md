# 贡献指南

[**中文**](CONTRIBUTING.md) | [English](CONTRIBUTING.en.md)

## 环境要求

- Windows 11，或兼容且支持 .NET Framework 4.8 的 Windows 环境。
- Node.js 18 或更高版本，以及 npm。
- 真实集成测试需要 `plugins/deepseek-harness-web/plugin.json` 中声明的 DSH 版本。

本应用有意使用 .NET Framework C# 5 编译器进行编译。除非已经明确决定迁移运行时和工具链，否则请勿引入更新版本的 C# 语法。

## 构建与测试

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
```

`Test.ps1` 是 Windows CI 使用的权威测试入口。它会编译应用程序和测试、运行 C# 覆盖测试场景、在临时端口启动真实的 DSH 实例、验证优雅关闭、测试版本化 Node 命名管道桥接，并在隔离目录中测试 npm CLI 安装。

真实启动、指纹、兼容性冒烟和优雅关闭测试使用清单所声明且已全局安装的 DSH。源码运行时解析和回滚测试使用测试专用的伪 `pnpm.cmd` 和伪造的检出目录；这些测试不会执行真实的 pnpm 源代码构建。npx 适配器会在解析和事务层面进行测试。

对于空闲性能相关改动，请安装候选构建，等待启动工作稳定后运行：

```powershell
.\scripts\Measure-Performance.ps1 -DurationSeconds 30
```

请在相同的 DSH 状态和机器条件下，对比工作集、专用内存、句柄数和线程数的中位数，以及平均 CPU 使用率。

请单独验证可分发包：

```powershell
npm pack
node .\tests\npm-package.test.mjs .\dsh-windows-manager-0.2.0.tgz
```

## 变更规则

- 优先选择能够保持 `AGENTS.md` 和 `SECURITY.md` 中安全不变量的最小改动。
- 对可观察行为的变更，应新增或更新测试。
- 在同一变更中更新面向用户的文档。
- 确保英文和简体中文区域设置的键集合完全一致。
- 请勿提交 `dist/`、npm tarball、日志、本地配置或用户数据。
- 请勿捆绑机密信息、npm 令牌、签名证书或特定于机器的路径。

## 版本管理

`package.json` 中的 npm 版本、锁文件版本、`AssemblyInformationalVersion`、`AssemblyFileVersion` 和发布标签必须一致。DSH 的 `BundledVersion` 独立于这些版本，只有在完成兼容性测试后才应更改。

## 拉取请求

拉取请求应说明用户可见的变更、安全影响、已执行的测试及已更新的文档。合并前 Windows CI 必须通过。

## 发布检查清单

1. 确认 npm 包名称和 GitHub 仓库元数据。
2. 在 Windows 上运行完整的测试套件。
3. 运行 `npm pack` 并检查文件允许列表。
4. 从生成的 tarball 安装到隔离目录。
5. 执行一次真实的按用户升级，并验证 `config.json` 未被更改。
6. 验证两个区域设置文件、插件载荷、桌面快捷方式、托盘进程及 DSH Web 指纹。
7. 对 0.2.0 事件驱动候选重新运行 `Measure-Performance.ps1`，并更新性能文档中的基线。
8. 扫描已跟踪文件中的凭据和私有数据。
9. 创建 GitHub Release 产物和校验和。
10. 仅在 GitHub 标签和 Release 最终确定后发布 npm 包。

该软件包通过 `publishConfig` 将发布目标固定为 `https://registry.npmjs.org/`。这不会改变用户为安装而选择的 registry，并可防止维护者的下载镜像意外成为发布目标。
