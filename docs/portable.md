# AgentProxy Portable Windows Launcher

Double-click `AgentProxy.exe`. No installation wizard, shortcut creation, uninstaller, autostart, or changes to the Windows system proxy are needed. The single EXE embeds the packaged-app activation helper, Antigravity `version.dll`, ProxyBridge/WinDivert native components, licenses, and corresponding native sources. It validates its embedded file checksums before extracting into the current user's local cache (`%LOCALAPPDATA%\AgentProxy\Portable`).

## Target applications & proxy modes

1. **ChatGPT / Codex (Microsoft Store)**
   - **Standard mode (default, unchecked)**: Launches with transient registry proxy variables and `--proxy-server`. Supports `HTTP / Mixed` ports without drivers or UAC elevation. `SOCKS5` is not supported in standard mode; the UI warns and requires enabling Enhanced Proxy or switching to `HTTP`.
   - **Enhanced mode (optional checkbox)**: Uses WinDivert + ProxyBridge for remote TCP 443 redirection of the registered package, verified `node_repl.exe` browser helpers, and verified `codex.exe` core helpers. Supports both `HTTP` and `SOCKS5` with TLS SNI hostname forwarding for OpenAI domains. First use requests administrator approval to materialize an immutable protected runtime under `C:\Program Files\AgentProxyProcessProxy\<bundle-id>`.
   - Supports both loopback (`127.0.0.1`) and non-loopback unicast IPv4 addresses; non-loopback upstreams are automatically added to direct exclusion rules.
   - Tracks running Store package updates and newly spawned helpers every 15 seconds.

2. **Antigravity (Desktop)**
   - Targets `%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe` and its child processes (`language_server.exe`, `node.exe`).
   - Supports both `HTTP` and `SOCKS5` via `version.dll` without drivers or administrator elevation.
   - Verifies/restores `version.dll` before each launch and updates `config.json` with silent startup (`ui.load_notify = "none"`) and automatic direct bypass for non-loopback IPv4 upstreams.

## Diagnostics & Tray Lifetime

- Numeric launch diagnostics (stage, UI heartbeat delay, memory, handles — never credentials, headers, or payload) are stored under `%LOCALAPPDATA%\AgentProxy\Diagnostics`.
- When Enhanced Proxy is active, closing the launcher window hides it to the system tray; right-click **Exit and stop enhanced proxy** to tear down the session cleanly.
