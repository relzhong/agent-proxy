<h1 align="center">Agent Proxy (agent-proxy)</h1>

<p align="center">
  <a href="README.md">简体中文</a> · <strong>English</strong>
</p>

<p align="center">
  <a href="https://github.com/relzhong/agent-proxy/actions"><img src="https://img.shields.io/github/actions/workflow/status/relzhong/agent-proxy/ci-release.yml?style=flat-square" alt="CI Status"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0b7668?style=flat-square" alt="Windows 10 / 11 x64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/relzhong/agent-proxy?style=flat-square" alt="License"></a>
</p>

<p align="center">
  A unified per-app Windows proxy launcher for <strong>ChatGPT / Codex (Microsoft Store)</strong> and <strong>Antigravity (Desktop)</strong> without changing system proxy settings or enabling TUN mode.
</p>

---

## Features

- **Unified Launcher for Both Clients**
  - **ChatGPT / Codex (Store)**: Preserves Store AUMID activation, transient registry environment backup/restore, and CUA browser forwarding / read compatibility fixes.
  - **Antigravity (Desktop)**: Embeds `version.dll` (MinHook + Winsock redirection + child injection), automatically writes `%LOCALAPPDATA%\Programs\Antigravity\config.json`, verifies/restores `version.dll` across client updates, covers `Antigravity.exe` and `language_server.exe`, and supports one-click patch removal.
- **Local, LAN IP & Domain Proxy Support**
  - Supports loopback (`127.0.0.1`), non-loopback unicast IPv4 addresses (e.g. `192.168.x.x`), and domain hostnames (e.g. `proxy.example.com`).
  - Non-loopback proxy IPs and domains are automatically added to direct bypass rules (`NO_PROXY`, `--proxy-bypass-list`, WinDivert direct rule, and Antigravity routing bypass) to avoid proxy loops.
- **HTTP / SOCKS5 / HTTPS, Auth & Self-Signed Cert Trust**
  - **Antigravity (Desktop)**: Natively supports `HTTP`, `SOCKS5`, and `HTTPS` (Windows SChannel TLS handshake) with optional username/password authentication and per-proxy self-signed certificate trust (no driver or admin elevation required).
  - **ChatGPT / Codex (Store)**:
    - **Standard mode (Enhanced Proxy unchecked)**: Supports unauthenticated `HTTP / Mixed` and system-trusted `HTTPS` without drivers or UAC elevation. *Note: SOCKS5, username/password authentication, and self-signed HTTPS proxy certs are not supported in standard Codex / ChatGPT mode; the UI warns and requires checking Enhanced Proxy.*
    - **Enhanced mode (optional checkbox, WinDivert + ProxyBridge)**: Supports `HTTP`, `SOCKS5`, and `HTTPS` (SChannel TLS + optional proxy-hop self-signed cert trust), HTTP Basic / SOCKS5 RFC 1929 authentication (including TLS SNI hostname forwarding for OpenAI domains), and live Store update / helper tracking.
- **Persistent Settings & Encrypted Credentials**
  - Remembers last used target app, proxy host, port, protocol, Enhanced Proxy checkbox, UI language, username, and password across restarts (`%LOCALAPPDATA%\AgentProxy\settings.json`, with passwords encrypted via Windows per-user DPAPI and never exposed on command lines or in diagnostics).

---

## Quick Start

1. Download **`AgentProxy.exe`** from [Releases](https://github.com/relzhong/agent-proxy/releases) (or CI build artifacts).
2. Fully quit the target desktop client (ChatGPT / Codex or Antigravity).
3. Run `AgentProxy.exe`:
   - Select **Target app**: `ChatGPT / Codex (Store)` or `Antigravity (Desktop)`.
   - Enter **Proxy host** (`127.0.0.1`, LAN IP, or domain), **Proxy port**, **Protocol** (`HTTP` / `SOCKS5` / `HTTPS`), optional **Username / Password**, and (for `HTTPS`) optional **Trust self-signed proxy cert** (all remembered across restarts).
   - For **ChatGPT / Codex**: keep **Enable enhanced proxy** unchecked for driver-free unauthenticated `HTTP` or system-trusted `HTTPS`, or check it for `SOCKS5`, authenticated proxies, self-signed HTTPS proxy certs, or kernel-level TCP 443 coverage.
   - For **Antigravity**: pick `HTTP`, `SOCKS5`, or `HTTPS` and click **Apply proxy & launch Antigravity** (no admin prompt required).

See [portable documentation](docs/portable.md) and [process-proxy technical notes](docs/process-proxy.md) for security boundaries and component verification details.

---

## Build & Test

Prerequisites: Windows 10/11 x64, .NET Framework 4.x (`csc.exe`), Visual Studio 2022 C++ x64 build tools (`cl.exe`), Python 3.

```powershell
# Run automated tests
.\tests\test-launcher-settings.ps1
.\tests\test-antigravity-launcher.ps1
.\tests\test-process-proxy.ps1
.\tests\test-native-process-proxy.ps1
.\tests\test-tls-proxy.ps1
.\tests\test-cua-forwarding.ps1
.\tests\test-browser-launch-policy.ps1
.\tests\test-activation-handoff.ps1

# Build standalone directory (standard mode + Antigravity version.dll, no WinDivert)
.\build.ps1 -OutputDirectory .\output\dist
.\tests\test-launcher-lifetime.ps1 -LauncherExecutable .\output\dist\AgentProxyLauncher.exe

# Build full enhanced ZIP & single-file portable AgentProxy.exe
.\scripts\build-process-proxy.ps1 -OutputRoot .\output\enhanced
.\scripts\build-portable.ps1 -OutputDirectory .\output\portable -NativePackage .\output\enhanced\package -NativeArtifactsSource .\output\enhanced\NativeArtifacts.generated.cs
```

---

## Upstream Citations & Acknowledgments

This project integrates and builds upon the following open-source repositories:

- **[gaopengbin/codex-launch-deck](https://github.com/gaopengbin/codex-launch-deck)** (`v2.4.5-portable`, MIT License)
  Store package activation, transient environment backup/restore, CUA browser forwarding/read compatibility, and the WinDivert + ProxyBridge enhanced process-proxy architecture and verification pipeline.
- **[yuaotian/antigravity-proxy](https://github.com/yuaotian/antigravity-proxy)** (BSD-2-Clause License)
  `version.dll` (MinHook + FakeIP + child-process injection) Winsock proxy layer supporting HTTP / SOCKS5 and `language_server.exe` for Antigravity Desktop.
- **Underlying native dependencies**:
  - [InterceptSuite/ProxyBridge](https://github.com/InterceptSuite/ProxyBridge) (MIT License)
  - [basil00/WinDivert](https://github.com/basil00/WinDivert) (LGPLv3 / GPLv2)

---

## License

Launcher code and modifications are licensed under the [MIT License](LICENSE). Bundled third-party components retain their respective licenses (MIT / BSD-2-Clause / LGPLv3).
