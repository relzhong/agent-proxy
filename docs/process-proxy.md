# AgentProxy Enhanced Process Proxy (Windows x64)

`AgentProxy-Enhanced-win-x64.zip` and the portable `AgentProxy.exe` bundle the C# launcher with an optional WinDivert-based enhanced proxy for Microsoft Store `ChatGPT.exe` / `Codex.exe`, alongside `version.dll` injection for `Antigravity.exe`.

## Install and use

1. Download `AgentProxy.exe` (single portable executable) or `AgentProxy-Enhanced-win-x64.zip` and verify the published `.sha256` checksum.
2. For portable use, run `AgentProxy.exe` directly as a normal user. For fixed machine installation of the enhanced package, run `Install-Enhanced.ps1` in 64-bit Windows PowerShell (installs hash-pinned components under `C:\Program Files\AgentProxyProcessProxy\<bundle-id>`).
3. In **ChatGPT / Codex (Store)** mode, uncheck **Enable enhanced proxy** for unauthenticated `HTTP / Mixed` / system-trusted `HTTPS` launching without drivers, or check it for `HTTP` / `SOCKS5` / `HTTPS` (with optional username/password auth and proxy-hop self-signed TLS trust) kernel-level TCP 443 redirection.
4. In **Antigravity (Desktop)** mode, choose `HTTP`, `SOCKS5`, or `HTTPS` (with optional username/password auth and self-signed TLS trust) and launch without administrator elevation or WinDivert.

## Actual scope & security boundaries

- **Store ChatGPT / Codex enhanced scope**: Validates the full EXE path against the registered OpenAI Windows package plus hash-verified `node_repl.exe` and `codex.exe` runtime caches. Redirects only those executables' remote TCP port 443 connections through the selected `HTTP`, `SOCKS5`, or `HTTPS` unicast IPv4/loopback/domain upstream. Non-loopback proxy hosts, domains, and all loopback ranges are excluded from redirection. Self-signed TLS trust applies strictly to the proxy hop in SChannel, never relaxing target endpoint certificate checks.
- **WinDivert driver verification**: The bundled official WinDivert 2.2.2-A `WinDivert64.sys` driver is verified via `WinVerifyTrust` against its pinned Authenticode thumbprint (`043589F75FCE2795E7F2CC3E526D46784D5DDAB3`) before loading.
- **TLS SNI forwarding**: For TLS connections to `chatgpt.com`, `openai.com`, `oaistatic.com`, and `oaiusercontent.com`, enhanced mode peeks ClientHello SNI (bounded to 1s / 64 KiB, without decrypting or logging payloads) and forwards the hostname in HTTP `CONNECT` or SOCKS5 `ATYP_DOMAIN`.
- **Antigravity Desktop scope**: Uses vendored `antigravity-proxy` (`version.dll`) verified by SHA-256 on each launch; generates/updates `config.json` with `ui.load_notify = "none"` and upstream bypass rules.

## Build and licensing

Run the test suite in `tests/` before building:

```powershell
.\scripts\build-process-proxy.ps1
.\scripts\build-portable.ps1 -OutputDirectory .\output\portable -NativePackage .\output\enhanced\package -NativeArtifactsSource .\output\enhanced\NativeArtifacts.generated.cs
```

- `native/proxybridge`: MIT-licensed fork of `InterceptSuite/ProxyBridge` (`02703a0672a8b94011a4698368a392f7734c10dc`).
- `native/antigravity-proxy`: BSD-2-Clause-licensed `yuaotian/antigravity-proxy` (`72bb5ff59881183ce258d339100aaadba072956a`).
- WinDivert 2.2.2-A runtime: dynamically linked under LGPLv3 (full license and source archive included in `LICENSES/` and `SOURCE/`).
