<h1 align="center">Agent Proxy (agent-proxy)</h1>

<p align="center">
  <strong>简体中文</strong> · <a href="README.en.md">English</a>
</p>

<p align="center">
  <a href="https://github.com/relzhong/agent-proxy/actions"><img src="https://img.shields.io/github/actions/workflow/status/relzhong/agent-proxy/ci-release.yml?style=flat-square" alt="CI Status"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0b7668?style=flat-square" alt="Windows 10 / 11 x64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/relzhong/agent-proxy?style=flat-square" alt="License"></a>
</p>

<p align="center">
  面向 Windows 桌面端 AI 客户端的统一免全局代理启动器，同时支持 <strong>ChatGPT / Codex（Microsoft Store 版）</strong> 与 <strong>Antigravity（桌面版）</strong>，无需开启系统代理或 TUN 模式。
</p>

---

## 核心特性

- **双客户端统一入口**
  - **ChatGPT / Codex (Store)**：保留应用包身份激活与注册表环境变量瞬时注入/恢复，内置浏览器（CUA）转发与读取兼容修复。
  - **Antigravity (Desktop)**：内置 `version.dll` 进程级代理注入，自动生成并同步 `%LOCALAPPDATA%\Programs\Antigravity\config.json`（静默加载、自动覆盖 `Antigravity.exe` 与 `language_server.exe`），每次启动校验并自动补回被更新清除的 `version.dll`，支持一键移除补丁。
- **支持本机与局域网代理地址**
  - 放宽回环地址限制，支持填写 `127.0.0.1` 或局域网/单播 IPv4（如 `192.168.x.x`）。
  - 非回环上游 IP 会自动加入直连排除规则与 `NO_PROXY` / `bypass-list`，防止代理流量回环。
- **HTTP 与 SOCKS5 双协议支持**
  - **Antigravity (Desktop)**：原生支持 `HTTP` 与 `SOCKS5`（无需驱动与管理员权限）。
  - **ChatGPT / Codex (Store)**：
    - **普通模式（默认不勾选增强代理）**：支持 `HTTP / Mixed` 端口，无需驱动、无需管理员 UAC 授权。*注意：SOCKS5 不支持普通模式 Codex / ChatGPT，界面会明确提示需勾选增强代理或改用 HTTP。*
    - **增强模式（可选勾选，WinDivert + ProxyBridge）**：同时支持 `HTTP` 与 `SOCKS5`（含 OpenAI 域名 TLS SNI 主机名透传），自动追踪运行中的 Store 版本更新与延迟生成的 `codex.exe` / `node_repl.exe`。

---

## 快速开始

1. 从 [Releases](https://github.com/relzhong/agent-proxy/releases) 下载单文件免安装版 **`AgentProxy.exe`**（或从 CI 构建产物获取）。
2. 完全退出目标客户端（ChatGPT / Codex 或 Antigravity）。
3. 双击运行 `AgentProxy.exe`：
   - 选择 **启动目标**：`ChatGPT / Codex (Store)` 或 `Antigravity (Desktop)`。
   - 填写 **代理主机**（本机 `127.0.0.1` 或局域网 IP）、**代理端口** 及 **代理协议**（`HTTP` / `SOCKS5`）。
   - 若启动 **ChatGPT / Codex** 且仅使用 `HTTP / Mixed` 端口，可直接点击 **启动 ChatGPT / Codex**；若使用纯 `SOCKS5` 或需要驱动级 443 兜底，请勾选 **启用增强代理**。
   - 若启动 **Antigravity**，选择 `HTTP` 或 `SOCKS5` 后点击 **部署代理并启动 Antigravity**（不需要管理员权限）。

> 详细安全边界与组件校验机制参见 [免安装说明](docs/portable.md) 与 [增强代理技术文档](docs/process-proxy.md)。

---

## 目录结构

```text
agent-proxy/
├── .github/workflows/ci-release.yml   # GitHub Actions 测试、构建与 Release 发布
├── assets/                            # 应用图标
├── docs/                              # 免安装与增强代理说明文档
├── native/
│   ├── antigravity-proxy/             # Antigravity version.dll C/C++ 源码 (BSD-2-Clause)
│   └── proxybridge/                   # ProxyBridge.dll C 源码 (MIT)
├── scripts/                           # 原生编译、增强包与单文件便携包构建脚本
├── src/
│   ├── Antigravity/                   # Antigravity 启动、config.json 与 version.dll 管理
│   ├── Codex/                         # Store 版 ChatGPT/Codex 激活与 CUA 浏览器维护
│   ├── Launcher/                      # WinForms 主界面、便携引导与诊断日志
│   └── ProcessProxy/                  # WinDivert 进程代理控制、权限校验与 Broker
├── tests/                             # C# 与原生 C/C++ 自动化回归测试
└── build.ps1                          # 本地快速编译独立运行目录 (output/dist)
```

---

## 本地编译与测试

前置条件：Windows 10/11 x64、.NET Framework 4.x（系统自带 `csc.exe`）、Visual Studio 2022（C++ x64 工具链 `cl.exe`）、Python 3。

### 1. 运行自动化测试

```powershell
.\tests\test-antigravity-launcher.ps1
.\tests\test-process-proxy.ps1
.\tests\test-native-process-proxy.ps1
.\tests\test-tls-proxy.ps1
.\tests\test-cua-forwarding.ps1
.\tests\test-browser-launch-policy.ps1
.\tests\test-activation-handoff.ps1
```

### 2. 编译独立运行目录（含普通模式 + Antigravity `version.dll`，不拉取 WinDivert）

```powershell
.\build.ps1 -OutputDirectory .\output\dist
.\tests\test-launcher-lifetime.ps1 -LauncherExecutable .\output\dist\AgentProxyLauncher.exe
```

### 3. 编译完整单文件免安装版 `AgentProxy.exe`（含 WinDivert 增强模式）

```powershell
.\scripts\build-process-proxy.ps1 -OutputRoot .\output\enhanced
.\scripts\build-portable.ps1 -OutputDirectory .\output\portable -NativePackage .\output\enhanced\package -NativeArtifactsSource .\output\enhanced\NativeArtifacts.generated.cs
```

> 提示：由于 `WinDivert` 属于底层网络拦截驱动库，Windows Defender 可能会在编译或解包时误报，按需将工作区或发布目录加入排除项即可。

---

## 上游项目与致谢 (Citations & Acknowledgments)

本项目整合与基于以下开源项目构建，特此引用并致谢：

- **[gaopengbin/codex-launch-deck](https://github.com/gaopengbin/codex-launch-deck)**（`v2.4.5-portable`，MIT License）
  提供 Microsoft Store 版 ChatGPT / Codex 激活、环境变量瞬时注入与恢复、CUA 浏览器代理/读取修复、以及基于 WinDivert + ProxyBridge 的增强进程代理架构与安全校验体系。
- **[yuaotian/antigravity-proxy](https://github.com/yuaotian/antigravity-proxy)**（BSD-2-Clause License）
  提供针对 Antigravity 的 `version.dll`（MinHook + FakeIP + 子进程注入）网络代理实现，支持 HTTP / SOCKS5 与 `language_server.exe` 透明代理。
- **底层原生组件**：
  - [InterceptSuite/ProxyBridge](https://github.com/InterceptSuite/ProxyBridge)（MIT License）
  - [basil00/WinDivert](https://github.com/basil00/WinDivert)（LGPLv3 / GPLv2）

---

## 许可证

启动器托管代码与修改部分采用 [MIT License](LICENSE)；内嵌及整合的第三方组件保留其各自开源许可证（MIT / BSD-2-Clause / LGPLv3）。
