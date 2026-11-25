# AgentProxy Portable Windows Launcher

Double-click `AgentProxy.exe`. No installation wizard, shortcut creation, uninstaller, autostart, or changes to the Windows system proxy are needed. The single EXE embeds the packaged-app activation helper, Antigravity `version.dll`, ProxyBridge/WinDivert native components, licenses, and corresponding native sources. It validates its embedded file checksums before extracting into the current user's local cache (`%LOCALAPPDATA%\AgentProxy\Portable`).

## Target applications & proxy modes

1. **ChatGPT / Codex (Microsoft Store)**
   - **Standard mode (default, unchecked)**: Launches with transient registry proxy variables and `--proxy-server`. Supports unauthenticated `HTTP / Mixed` and system-trusted `HTTPS` without drivers or UAC elevation. `SOCKS5`, proxy username/password authentication, and self-signed HTTPS proxy trust are not supported in standard mode; the UI warns and requires enabling Enhanced Proxy.
   - **Enhanced mode (optional checkbox)**: Uses WinDivert + ProxyBridge for remote TCP 443 redirection of the registered package, verified `node_repl.exe` browser helpers, and verified `codex.exe` core helpers. Supports `HTTP`, `SOCKS5`, and `HTTPS` (Windows SChannel + optional proxy-hop self-signed cert trust) plus optional username/password auth with TLS SNI hostname forwarding for OpenAI domains. First use requests administrator approval to materialize an immutable protected runtime under `C:\Program Files\AgentProxyProcessProxy\<bundle-id>`.
   - Supports loopback (`127.0.0.1`), non-loopback unicast IPv4 addresses, and domain hostnames; upstream IPs and domains are automatically added to direct exclusion rules.
   - Tracks running Store package updates and newly spawned helpers every 15 seconds.

2. **Antigravity (Desktop)**
   - Targets `%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe` and its child processes (`language_server.exe`, `node.exe`).
   - Supports `HTTP`, `SOCKS5`, and `HTTPS` (SChannel TLS + optional self-signed trust) via `version.dll` without drivers or administrator elevation.
   - Verifies/restores `version.dll` before each launch and updates `config.json` with silent startup (`ui.load_notify = "none"`) and automatic direct bypass for non-loopback IPv4 upstreams.

## Diagnostics & Tray Lifetime

- Launcher settings (target app, proxy host, port, protocol, Enhanced Proxy, UI language, username, and DPAPI-encrypted password) are stored locally in `%LOCALAPPDATA%\AgentProxy\settings.json`; credentials are never passed on CLI arguments or recorded in diagnostics.
- Numeric launch diagnostics (stage, UI heartbeat delay, memory, handles — never credentials, headers, or payload) are stored under `%LOCALAPPDATA%\AgentProxy\Diagnostics`.
- When Enhanced Proxy is active, closing the launcher window hides it to the system tray; right-click **Exit and stop enhanced proxy** to tear down the session cleanly.
