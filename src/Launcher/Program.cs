using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgentProxy
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            PortableRuntime.ReadArguments(args);
            Application.Run(new LauncherForm());
        }
    }

    internal sealed class LauncherForm : Form
    {
        private readonly AgentProxy.ProcessProxy.ProxyController processProxy = new AgentProxy.ProcessProxy.ProxyController();
        private readonly LauncherDiagnostics diagnostics;
        private CuaProxyForwardingWatch browserForwardingWatch;
        private string lastBrowserForwardingError;
        private readonly System.Windows.Forms.Timer processProxyHealth = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox processProxyCheckBox = new CheckBox { AutoSize = true, Checked = false };
        private readonly Label antigravityHintLabel = new Label { AutoSize = false };
        private readonly TextBox hostBox = new TextBox { Text = "127.0.0.1" };
        private readonly NumericUpDown portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 10808 };
        private readonly ComboBox protocolBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button launchButton = new Button { Height = 44 };
        private readonly Button browserProxyButton = new Button { Height = 32 };
        private readonly Button removePatchButton = new Button { Height = 32, Visible = false };
        private readonly Button languageButton = new Button { Height = 28, Width = 48 };
        private readonly Label titleLabel = new Label();
        private readonly Label headerDescription = new Label();
        private readonly Label targetLabel = new Label();
        private readonly Label hostLabel = new Label();
        private readonly Label portLabel = new Label();
        private readonly Label protocolLabel = new Label();
        private readonly Label footnote = new Label();
        private readonly Label statusLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label statusMark = new Label { AutoSize = false, Text = "●", TextAlign = ContentAlignment.MiddleCenter };
        private readonly Panel statusPanel = new Panel();
        private bool english;
        private readonly NotifyIcon sessionTray = new NotifyIcon();
        private bool explicitExit;
        private bool trayNoticeShown;


        public LauncherForm() : this(new LauncherDiagnostics()) { }
        internal LauncherForm(LauncherDiagnostics trace)
        {
            diagnostics=trace;
            Text = "Agent Proxy";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            sessionTray.Icon = Icon;
            sessionTray.Text = "AgentProxy";
            sessionTray.DoubleClick += delegate { RestoreFromTray(); };
            var trayMenu = new ContextMenuStrip();
            var openTrayItem = trayMenu.Items.Add("打开启动器 / Open launcher");
            openTrayItem.Click += delegate { RestoreFromTray(); };
            var exitTrayItem = trayMenu.Items.Add("退出并停止增强代理 / Exit and stop enhanced proxy");
            exitTrayItem.Click += delegate { explicitExit = true; Close(); };
            sessionTray.ContextMenuStrip = trayMenu;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 520);
            MinimumSize = new Size(496, 559);
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(244, 246, 248);
            ForeColor = Color.FromArgb(23, 23, 23);

            var header = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Color.FromArgb(23, 23, 23) };
            var badge = new Label
            {
                Text = "LOCAL PROXY",
                Font = new Font("Segoe UI Semibold", 8F),
                ForeColor = Color.FromArgb(212, 175, 55),
                Location = new Point(28, 19),
                AutoSize = true
            };
            languageButton.Location = new Point(402, 16);
            languageButton.FlatStyle = FlatStyle.Flat;
            languageButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            languageButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 55, 55);
            languageButton.BackColor = Color.FromArgb(23, 23, 23);
            languageButton.ForeColor = Color.FromArgb(212, 175, 55);
            languageButton.Cursor = Cursors.Hand;
            languageButton.TabStop = true;
            languageButton.Click += delegate { english = !english; ApplyLanguage(); };
            titleLabel.Text = "Agent Proxy";
            titleLabel.Font = new Font("Segoe UI Semibold", 20F);
            titleLabel.ForeColor = Color.White;
            titleLabel.Location = new Point(26, 38);
            titleLabel.AutoSize = true;
            headerDescription.ForeColor = Color.FromArgb(190, 190, 190);
            headerDescription.Location = new Point(29, 82);
            headerDescription.Size = new Size(423, 22);
            header.Controls.Add(badge);
            header.Controls.Add(languageButton);
            header.Controls.Add(titleLabel);
            header.Controls.Add(headerDescription);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 22) };
            targetLabel.AutoSize = true;
            targetLabel.ForeColor = Color.FromArgb(64, 64, 64);
            targetLabel.Location = new Point(28, 14);
            targetBox.Location = new Point(28, 36);
            targetBox.Size = new Size(424, 28);
            targetBox.Items.AddRange(new object[] {
                "ChatGPT / Codex (Store)",
                "Antigravity (Desktop)"
            });
            targetBox.SelectedIndex = 0;
            targetBox.SelectedIndexChanged += delegate { UpdateTargetMode(); };

            var fields = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, Location = new Point(28, 72), Size = new Size(424, 60) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            hostLabel.AutoSize = true;
            hostLabel.ForeColor = Color.FromArgb(64, 64, 64);
            portLabel.AutoSize = true;
            portLabel.ForeColor = Color.FromArgb(64, 64, 64);
            protocolLabel.AutoSize = true;
            protocolLabel.ForeColor = Color.FromArgb(64, 64, 64);
            fields.Controls.Add(hostLabel, 0, 0);
            fields.Controls.Add(portLabel, 1, 0);
            fields.Controls.Add(protocolLabel, 2, 0);
            hostBox.Dock = DockStyle.Fill;
            hostBox.Margin = new Padding(0, 6, 10, 0);
            portBox.Dock = DockStyle.Fill;
            portBox.Margin = new Padding(0, 6, 10, 0);
            protocolBox.Dock = DockStyle.Fill;
            protocolBox.Margin = new Padding(0, 6, 0, 0);
            protocolBox.Items.AddRange(new object[] { "HTTP", "SOCKS5" });
            protocolBox.SelectedIndex = 0;
            protocolBox.SelectedIndexChanged += delegate {
                if (launchButton.Enabled) UpdateSocks5PromptAndStatus();
            };
            fields.Controls.Add(hostBox, 0, 1);
            fields.Controls.Add(portBox, 1, 1);
            fields.Controls.Add(protocolBox, 2, 1);

            processProxyCheckBox.Location = new Point(30, 142);
            processProxyCheckBox.ForeColor = Color.FromArgb(51, 65, 85);
            processProxyCheckBox.Cursor = Cursors.Hand;

            antigravityHintLabel.Location = new Point(29, 142);
            antigravityHintLabel.Size = new Size(423, 22);
            antigravityHintLabel.ForeColor = Color.FromArgb(51, 65, 85);
            antigravityHintLabel.Visible = false;

            launchButton.Location = new Point(28, 172);
            launchButton.Size = new Size(424, 44);
            launchButton.BackColor = Color.FromArgb(23, 23, 23);
            launchButton.ForeColor = Color.White;
            launchButton.FlatStyle = FlatStyle.Flat;
            launchButton.FlatAppearance.BorderSize = 0;
            launchButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 55, 55);
            launchButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 0, 0);
            launchButton.Cursor = Cursors.Hand;
            launchButton.Click += LaunchButtonClick;

            statusPanel.Location = new Point(28, 270);
            statusPanel.Size = new Size(424, 48);
            statusPanel.BackColor = Color.White;
            statusPanel.BorderStyle = BorderStyle.FixedSingle;
            statusMark.Location = new Point(8, 7);
            statusMark.Size = new Size(24, 26);
            statusMark.ForeColor = Color.FromArgb(100, 116, 139);
            statusLabel.Location = new Point(35, 4);
            statusLabel.Size = new Size(378, 38);
            statusLabel.ForeColor = Color.FromArgb(71, 85, 105);
            statusPanel.Controls.Add(statusMark);
            statusPanel.Controls.Add(statusLabel);

            footnote.ForeColor = Color.FromArgb(100, 116, 139);
            footnote.Font = new Font("Segoe UI", 9F);
            footnote.Location = new Point(29, 326);
            footnote.Size = new Size(423, 54);
            body.Controls.Add(targetLabel);
            body.Controls.Add(targetBox);
            body.Controls.Add(fields);
            body.Controls.Add(processProxyCheckBox);
            body.Controls.Add(antigravityHintLabel);
            body.Controls.Add(launchButton);
            body.Controls.Add(statusPanel);
            body.Controls.Add(footnote);
            browserProxyButton.Location = new Point(28, 226);
            browserProxyButton.Size = new Size(424, 32);
            browserProxyButton.FlatStyle = FlatStyle.Flat;
            browserProxyButton.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            browserProxyButton.BackColor = Color.White;
            browserProxyButton.Cursor = Cursors.Hand;
            browserProxyButton.Click += delegate {
                try { using (var dialog = new CuaProxyMaintenanceDialog(english)) dialog.ShowDialog(this); }
                catch (Exception ex) { SetStatus(ex.Message, false); }
            };
            body.Controls.Add(browserProxyButton);

            removePatchButton.Location = new Point(28, 226);
            removePatchButton.Size = new Size(424, 32);
            removePatchButton.FlatStyle = FlatStyle.Flat;
            removePatchButton.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            removePatchButton.BackColor = Color.White;
            removePatchButton.Cursor = Cursors.Hand;
            removePatchButton.Click += RemovePatchButtonClick;
            body.Controls.Add(removePatchButton);

            processProxyCheckBox.CheckedChanged += async delegate {
                if (!processProxyCheckBox.Checked && processProxy.State == AgentProxy.ProcessProxy.ProxyState.Ready) {
                    try { await Task.Run(() => processProxy.Dispose()); SetStatus(L("增强代理已关闭；基础代理在客户端重启前仍保持", "Enhanced proxy stopped; basic client proxy stays until restart"), null); }
                    catch (Exception ex) { SetStatus(ex.Message, false); }
                } else if (processProxy.State == AgentProxy.ProcessProxy.ProxyState.Off && !IsAntigravitySelected) {
                    UpdateSocks5PromptAndStatus();
                }
            };
            processProxyHealth.Tick += delegate {
                diagnostics.Heartbeat();
                try { processProxy.PollHealth(); }
                catch (Exception ex) { processProxyCheckBox.Checked = false; SetStatus(ex.Message, false); return; }
                string error=browserForwardingWatch==null?null:browserForwardingWatch.LastError;
                if(error!=lastBrowserForwardingError) {
                    if(error!=null) SetStatus(L("浏览器代理配置检查失败：", "Browser proxy configuration check failed: ")+error,false);
                    else if(lastBrowserForwardingError!=null) SetStatus(L("浏览器代理配置已恢复；已运行的工具可能需要重新连接", "Browser proxy configuration restored; existing tools may need reconnecting"),null);
                    lastBrowserForwardingError=error;
                }
            };
            processProxyHealth.Start();
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                // Closing the window must not silently cut an active client's network.
                // Windows shutdown and the explicit tray exit still perform cleanup.
                if(e.CloseReason == CloseReason.UserClosing && !explicitExit &&
                    processProxy.State == AgentProxy.ProcessProxy.ProxyState.Ready) {
                    e.Cancel = true;
                    sessionTray.Visible = true;
                    Hide();
                    if(!trayNoticeShown) {
                        trayNoticeShown = true;
                        sessionTray.ShowBalloonTip(4000, "AgentProxy",
                            L("增强代理继续运行。双击托盘图标打开；右键可退出并停止代理。",
                              "Enhanced proxy remains active. Double-click to open; right-click to exit and stop it."), ToolTipIcon.Info);
                    }
                    return;
                }
                processProxyHealth.Stop();
                if(browserForwardingWatch!=null) browserForwardingWatch.Dispose();
                try { processProxy.Dispose(); } catch (Exception ex) { MessageBox.Show(ex.Message); }
            };
            FormClosed += delegate { diagnostics.Dispose(); sessionTray.Dispose(); trayMenu.Dispose(); };
            Controls.Add(body);
            Controls.Add(header);
            AcceptButton = launchButton;
            ApplyLanguage();
            Shown += async delegate {
                var resume = PortableRuntime.Resume;
                if (resume != null) {
                    targetBox.SelectedIndex = 0;
                    hostBox.Text = resume.Host; portBox.Value = resume.Port;
                    protocolBox.SelectedIndex = String.Equals(resume.Protocol, "socks5", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    processProxyCheckBox.Checked = resume.Enhanced;
                    english = resume.English; ApplyLanguage();
                    LaunchButtonClick(this, EventArgs.Empty);
                } else {
                    // Reuse a listening local proxy instead of resetting this machine
                    // to a port where no proxy is running.
                    string probeHost = String.IsNullOrWhiteSpace(hostBox.Text) ? "127.0.0.1" : hostBox.Text.Trim();
                    foreach (int candidate in new[] { 7890, 10808, 1080 })
                        if (await Task.Run(() => CanConnect(probeHost, candidate))) { portBox.Value = candidate; break; }
                }
            };
        }

        private bool IsAntigravitySelected { get { return targetBox.SelectedIndex == 1; } }

        private string SelectedProtocol { get { return protocolBox.SelectedIndex == 1 ? "socks5" : "http"; } }

        private string L(string chinese, string englishText)
        {
            return english ? englishText : chinese;
        }

        private void UpdateTargetMode()
        {
            if (!IsAntigravitySelected)
            {
                processProxyCheckBox.Visible = true;
                antigravityHintLabel.Visible = false;
                browserProxyButton.Visible = true;
                removePatchButton.Visible = false;
            }
            else
            {
                processProxyCheckBox.Visible = false;
                antigravityHintLabel.Visible = true;
                browserProxyButton.Visible = false;
                removePatchButton.Visible = true;
            }
            ApplyLanguage();
        }

        private void UpdateSocks5PromptAndStatus()
        {
            ApplyLanguage();
        }

        private void ApplyTargetStatus()
        {
            if (IsAntigravitySelected)
            {
                SetStatus(L("准备就绪 · Antigravity 进程级代理（支持 HTTP / SOCKS5）",
                    "Ready · Antigravity process proxy (HTTP / SOCKS5)"), null);
                return;
            }
            if (SelectedProtocol == "socks5" && !processProxyCheckBox.Checked)
            {
                SetStatus(L("注意：SOCKS5 不支持普通模式 Codex / ChatGPT，请勾选「启用增强代理」或改用 HTTP",
                    "Note: SOCKS5 is not supported in standard Codex / ChatGPT mode; enable Enhanced Proxy or switch to HTTP"), false);
                return;
            }
            if (processProxyCheckBox.Checked)
            {
                SetStatus(SelectedProtocol == "socks5"
                    ? L("准备就绪 · 已勾选增强代理（SOCKS5 驱动级 443 转发，需驱动授权）", "Ready · Enhanced proxy selected (SOCKS5 driver 443 routing, approval required)")
                    : L("准备就绪 · 已勾选增强代理（HTTP 驱动级 443 转发，需驱动授权）", "Ready · Enhanced proxy selected (HTTP driver 443 routing, approval required)"), null);
                return;
            }
            SetStatus(L("准备就绪 · 普通代理模式（无需驱动，仅支持 HTTP）", "Ready · Standard proxy mode (no driver needed, HTTP only)"), null);
        }

        private void ApplyLanguage()
        {
            languageButton.Text = english ? "中文" : "EN";
            languageButton.AccessibleName = english ? "切换到中文" : "Switch to English";
            headerDescription.Text = L(
                "无需开启全局代理，为 ChatGPT / Codex 或 Antigravity 单独代理",
                "Proxy ChatGPT / Codex or Antigravity without enabling a global proxy");
            targetLabel.Text = L("启动目标", "Target app");
            hostLabel.Text = L("代理主机（支持本机/局域网 IP）", "Proxy host (local / LAN IP)");
            portLabel.Text = L("代理端口", "Proxy port");
            protocolLabel.Text = L("代理协议", "Protocol");
            processProxyCheckBox.Text = (!IsAntigravitySelected && SelectedProtocol == "socks5")
                ? L("启用增强代理（SOCKS5 不支持普通 Codex，必须勾选）", "Enable enhanced proxy (required for SOCKS5 on Codex)")
                : L("启用增强代理（WinDivert 驱动级 443 转发，可选）", "Enable enhanced proxy (WinDivert 443 routing, optional)");
            antigravityHintLabel.Text = L(
                "通过 version.dll 注入进程与子进程，无需驱动与管理员权限",
                "Injects via version.dll into host & child processes; no driver/admin");
            launchButton.Text = IsAntigravitySelected
                ? L("部署代理并启动 Antigravity", "Apply proxy & launch Antigravity")
                : L("启动 ChatGPT / Codex", "Launch ChatGPT / Codex");
            footnote.Text = IsAntigravitySelected
                ? L("每次启动自动校验并恢复 version.dll，并将主机/端口/协议写入 config.json（自动静默与排除上游 IP）。",
                    "Verifies/restores version.dll on launch and writes host/port/protocol to config.json (silent load, auto-bypasses upstream IP).")
                : L("普通模式仅支持 HTTP/Mixed；SOCKS5 不支持普通 Codex，需勾选增强代理。勾选增强代理时非回环 IP 自动直连排除，关闭窗口收至托盘。",
                    "Standard mode supports HTTP/Mixed only; SOCKS5 requires Enhanced Proxy. Enhanced mode auto-excludes non-loopback IPs and stays in the tray.");
            browserProxyButton.Text = L("检查 / 修复浏览器", "Check / repair browser");
            browserProxyButton.AccessibleName = browserProxyButton.Text;
            removePatchButton.Text = L("移除 Antigravity 代理补丁 (version.dll)", "Remove Antigravity proxy patch (version.dll)");
            removePatchButton.AccessibleName = removePatchButton.Text;
            protocolBox.Enabled = launchButton.Enabled;
            ApplyTargetStatus();
        }

        private async void RemovePatchButtonClick(object sender, EventArgs e)
        {
            SetInputsEnabled(false);
            UseWaitCursor = true;
            try
            {
                string exe = await Task.Run(() => AntigravityLauncher.ResolveExecutable());
                if (await Task.Run(() => AntigravityLauncher.IsRunning(exe)))
                    throw new Exception(L("Antigravity 正在运行，请先完全退出后再移除补丁。", "Antigravity is running. Fully quit it before removing the patch."));
                bool existed = await Task.Run(() => AntigravityLauncher.IsPatched(exe));
                await Task.Run(() => AntigravityLauncher.RemovePatch(exe));
                SetStatus(existed
                    ? L("已移除 Antigravity 目录下的 version.dll 代理补丁", "Removed version.dll patch from Antigravity directory")
                    : L("Antigravity 目录当前未安装 version.dll 补丁", "No version.dll patch found in Antigravity directory"), true);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, false);
            }
            finally
            {
                UseWaitCursor = false;
                SetInputsEnabled(true);
            }
        }

        private void SetInputsEnabled(bool enabled)
        {
            launchButton.Enabled = enabled;
            browserProxyButton.Enabled = enabled;
            removePatchButton.Enabled = enabled;
            processProxyCheckBox.Enabled = enabled;
            targetBox.Enabled = enabled;
            hostBox.Enabled = enabled;
            portBox.Enabled = enabled;
            protocolBox.Enabled = enabled;
        }

        private async void LaunchButtonClick(object sender, EventArgs e)
        {
            SetInputsEnabled(false);
            UseWaitCursor = true;
            Exception launchFailure=null;
            try
            {
              try {
                string host = hostBox.Text.Trim();
                int port = (int)portBox.Value;
                bool isAntigravity = IsAntigravitySelected;
                string protocol = SelectedProtocol;
                diagnostics.Stage("checking-proxy");
                if (host.Length == 0) throw new Exception(L("请输入代理主机。", "Enter a proxy host."));
                if (!isAntigravity && protocol == "socks5" && !processProxyCheckBox.Checked)
                    throw new Exception(L("SOCKS5 不支持普通模式 Codex / ChatGPT，请勾选「启用增强代理」或改用 HTTP。",
                        "SOCKS5 is not supported in standard Codex / ChatGPT mode. Enable Enhanced Proxy or switch to HTTP."));
                SetStatus(L("正在检查代理…", "Checking proxy…"), null);

                bool reachable = await Task.Run(() => CanConnect(host, port));
                if (!reachable) throw new Exception(String.Format(L("无法连接代理 {0}:{1}。", "Cannot reach proxy {0}:{1}."), host, port));

                if (isAntigravity)
                {
                    SetStatus(L("正在查找 Antigravity…", "Finding Antigravity…"), null);
                    string agExe = await Task.Run(() => AntigravityLauncher.ResolveExecutable());
                    if (await Task.Run(() => AntigravityLauncher.IsRunning(agExe)))
                        throw new Exception(L("Antigravity 已在运行，请完全退出后再试。", "Antigravity is already running. Fully quit it and try again."));
                    SetStatus(L("正在写入配置并校验 version.dll…", "Writing config and verifying version.dll…"), null);
                    await Task.Run(() => AntigravityLauncher.DeployAndWriteConfig(agExe, host, port, protocol));
                    await Task.Run(() => AntigravityLauncher.Launch(agExe));
                    diagnostics.Stage("running");
                    SetStatus(String.Format(L("已通过 {0}://{1}:{2} 启动 Antigravity", "Antigravity started via {0}://{1}:{2}"), protocol, host, port), true);
                    return;
                }

                if (await Task.Run(()=>IsDesktopAppRunning())) throw new Exception(L("ChatGPT 已在运行，请完全退出后再试。", "ChatGPT is already running. Fully quit it and try again."));

                string proxyUrl = String.Format("{0}://{1}:{2}", protocol, host, port);
                if (processProxyCheckBox.Checked)
                {
                    SetStatus(L("正在准备增强代理组件…", "Preparing enhanced proxy components…"), null);
                    diagnostics.Stage("preparing-runtime");
                    var resumeSettings = new PortableResume {
                        Host=host, Port=port, Protocol=protocol, Enhanced=true, English=english
                    };
                    bool handedOff = await Task.Run(() => PortableRuntime.Handoff(resumeSettings));
                    if (handedOff) { Close(); return; }
                    // Verify the protected, pinned bundle before requesting administrator approval.
                    SetStatus(L("增强代理：等待引擎与驱动授权", "Enhanced proxy: waiting for engine and driver approval"), null);
                    diagnostics.Stage("preparing-proxy-host");
                    await Task.Run(()=>processProxy.Prepare());

                    await Task.Run(() => {
                        string executable=AgentProxy.ProcessProxy.ProxyController.ResolveExecutable();
                        processProxy.Start(new AgentProxy.ProcessProxy.ProxyPlan {
                            ExecutablePath=executable,
                            BrowserHelperPaths=AgentProxy.ProcessProxy.BrowserHelperScope.Resolve(executable),
                            CoreHelperPaths=AgentProxy.ProcessProxy.CoreHelperScope.Resolve(executable),
                            ProxyHost=host, ProxyPort=port, ProxyType=protocol, Process443Approved=true, DriverApproved=true
                        });
                    });
                    SetStatus(L("增强代理已就绪", "Enhanced proxy ready"), true);
                }
                diagnostics.Stage("checking-browser-forwarding");
                if(browserForwardingWatch==null) browserForwardingWatch=await Task.Run(()=>CuaProxyForwardingWatch.CreateDefault());
                {
                    SetStatus(L("正在查找 ChatGPT…", "Finding ChatGPT…"), null);
                    diagnostics.Stage("activating-package");
                    await StartPackagedChatGPT(proxyUrl, null, processProxy.State == AgentProxy.ProcessProxy.ProxyState.Ready ? processProxy.TargetExecutable : null);
                    diagnostics.Stage("running");
                    SetStatus(L("已通过 " + proxyUrl + " 启动 ChatGPT", "ChatGPT started through " + proxyUrl), true);
                }
            }
              catch (Exception error) { launchFailure=error; }
              if(launchFailure!=null) {
                var ex=launchFailure;
                diagnostics.Stage("launch-error");
                // Keep an approved session for its live client, including when
                // Launch is pressed again while that client is already running.
                bool keepProxy=false;
                try { keepProxy=!IsAntigravitySelected && processProxy.State==AgentProxy.ProcessProxy.ProxyState.Ready && await Task.Run(()=>IsDesktopAppRunning()); } catch { }
                if(keepProxy) SetStatus(L("客户端已启动，增强代理保持运行；后续步骤失败：", "Client started; enhanced proxy remains active. A later step failed: ")+ex.Message,false);
                else {
                    string cleanup="";
                    if (!IsAntigravitySelected) {
                        try { await Task.Run(()=>processProxy.Dispose()); } catch(Exception stopError) { cleanup=" "+stopError.Message; }
                    }
                    SetStatus(ex.Message+cleanup,ex is OperationCanceledException?(bool?)null:false);
                }
              }
            }
            finally { UseWaitCursor = false; SetInputsEnabled(true); }
        }

        private void SetStatus(string text, bool? success)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = success == true ? Color.FromArgb(21, 128, 61) : success == false ? Color.FromArgb(185, 28, 28) : Color.FromArgb(71, 85, 105);
            statusMark.ForeColor = statusLabel.ForeColor;
            statusPanel.BackColor = success == true ? Color.FromArgb(240, 253, 244) : success == false ? Color.FromArgb(254, 242, 242) : Color.White;
        }

        private static bool CanConnect(string host, int port)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    var result = client.BeginConnect(host, port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(1800)) return false;
                    client.EndConnect(result);
                    return client.Connected;
                }
                catch { return false; }
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            sessionTray.Visible = false;
        }

        private static bool IsDesktopAppRunning()
        {
            return Process.GetProcessesByName("ChatGPT").Any(p => IsAppExecutable(p, "ChatGPT.exe")) ||
                   Process.GetProcessesByName("Codex").Any(p => IsAppExecutable(p, "Codex.exe"));
        }

        private static bool IsAppExecutable(Process process, string fileName)
        {
            try { return process.MainModule.FileName.EndsWith("\\app\\" + fileName, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
            finally { process.Dispose(); }
        }

        private static async Task StartPackagedChatGPT(string proxyUrl, int? cdpPort, string expectedExecutable)
        {
            string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PackagedChatGPTLauncher.exe");
            if (!File.Exists(helper))
                throw new FileNotFoundException("PackagedChatGPTLauncher.exe is missing.", helper);
            string arguments = QuoteArgument(proxyUrl) + (expectedExecutable != null ? " " + (cdpPort ?? 0) + " " + QuoteArgument(expectedExecutable) : (cdpPort.HasValue ? " " + cdpPort.Value : ""));
            var info = new ProcessStartInfo(helper, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(info))
            {
                var outputRead = process.StandardOutput.ReadToEndAsync();
                var errorRead = process.StandardError.ReadToEndAsync();
                await Task.Run(()=>process.WaitForExit());
                string output = (await outputRead).Trim();
                string error = (await errorRead).Trim();
                if (process.ExitCode != 0 || output.Length == 0)
                    throw new InvalidOperationException(error.Length == 0 ? "ChatGPT package activation failed." : error);
            }
        }

    }
}
