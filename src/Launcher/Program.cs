using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AgentProxy
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            PortableRuntime.ReadArguments(args);
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new LauncherWindow();
            app.SessionEnding += delegate { window.MarkSystemShutdown(); };
            app.Run(window);
        }
    }

    internal sealed class LauncherWindow : Window
    {
        private readonly ProcessProxy.ProxyController processProxy = new ProcessProxy.ProxyController();
        private readonly LauncherDiagnostics diagnostics;
        private readonly LauncherSettingsStore settingsStore;
        private readonly bool persistSettings;
        private CuaProxyForwardingWatch browserForwardingWatch;
        private string lastBrowserForwardingError;
        private readonly DispatcherTimer processProxyHealth = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        private readonly ComboBox targetBox = new ComboBox();
        private readonly TextBox hostBox = new TextBox { Text = "127.0.0.1" };
        private readonly TextBox portBox = new TextBox { Text = "10808" };
        private readonly ComboBox protocolBox = new ComboBox();
        private readonly TextBox usernameBox = new TextBox();
        private readonly PasswordBox passwordBox = new PasswordBox();
        private readonly CheckBox processProxyCheckBox = new CheckBox { IsChecked = false };
        private readonly TextBlock antigravityHintLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly Button launchButton = new Button { Height = 42, IsDefault = true };
        private readonly Button browserProxyButton = new Button { Height = 34 };
        private readonly Button removePatchButton = new Button { Height = 34, Visibility = Visibility.Collapsed };
        private readonly Button languageButton = new Button { Height = 28, MinWidth = 52 };

        private readonly TextBlock titleLabel = new TextBlock();
        private readonly TextBlock headerDescription = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock targetLabel = new TextBlock();
        private readonly TextBlock hostLabel = new TextBlock();
        private readonly TextBlock portLabel = new TextBlock();
        private readonly TextBlock protocolLabel = new TextBlock();
        private readonly TextBlock usernameLabel = new TextBlock();
        private readonly TextBlock passwordLabel = new TextBlock();
        private readonly TextBlock footnote = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock statusLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock statusMark = new TextBlock { Text = "●", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        private readonly Border statusPanel = new Border();

        private bool english;
        private bool isBusy;
        private bool shuttingDown;
        private readonly Forms.NotifyIcon sessionTray = new Forms.NotifyIcon();
        private readonly Forms.ContextMenuStrip trayMenu = new Forms.ContextMenuStrip();
        private bool explicitExit;
        private bool trayNoticeShown;

        public LauncherWindow() : this(new LauncherDiagnostics(), new LauncherSettingsStore(), true) { }

        internal LauncherWindow(LauncherDiagnostics trace)
            : this(trace, new LauncherSettingsStore(Path.Combine(Path.GetTempPath(), "agentproxy-test-settings-" + Guid.NewGuid().ToString("N") + ".json")), false) { }

        internal LauncherWindow(LauncherDiagnostics trace, LauncherSettingsStore store, bool persist)
        {
            diagnostics = trace;
            settingsStore = store ?? new LauncherSettingsStore();
            persistSettings = persist;
            Title = "Agent Proxy";
            Width = 548;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            FontSize = 13;
            Background = BrushFromRgb(243, 248, 252);
            Foreground = BrushFromRgb(15, 23, 42);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

            var logoBitmap = LoadEmbeddedLogo();
            if (logoBitmap != null) Icon = logoBitmap;

            try
            {
                var appExe = Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().Location : Process.GetCurrentProcess().MainModule.FileName;
                sessionTray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(appExe);
            }
            catch { }
            sessionTray.Text = "AgentProxy";
            sessionTray.DoubleClick += delegate { RestoreFromTray(); };
            var openTrayItem = trayMenu.Items.Add("打开启动器 / Open launcher");
            openTrayItem.Click += delegate { RestoreFromTray(); };
            var exitTrayItem = trayMenu.Items.Add("退出并停止增强代理 / Exit and stop enhanced proxy");
            exitTrayItem.Click += delegate { explicitExit = true; Close(); };
            sessionTray.ContextMenuStrip = trayMenu;

            ApplyControlStyles();

            var rootStack = new StackPanel { Orientation = Orientation.Vertical };

            // Header (light, fresh, crisp)
            var headerBorder = new Border
            {
                Padding = new Thickness(24, 18, 24, 18),
                BorderBrush = BrushFromRgb(220, 232, 242),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = new LinearGradientBrush(
                    Color.FromRgb(255, 255, 255),
                    Color.FromRgb(235, 246, 251),
                    new Point(0, 0), new Point(1, 1))
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (logoBitmap != null)
            {
                var logoImg = new Image
                {
                    Source = logoBitmap,
                    Width = 44,
                    Height = 44,
                    Margin = new Thickness(0, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(logoImg, 0);
                headerGrid.Children.Add(logoImg);
            }

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleLabel.Text = "Agent Proxy";
            titleLabel.FontSize = 20;
            titleLabel.FontWeight = FontWeights.SemiBold;
            titleLabel.Foreground = BrushFromRgb(15, 23, 42);
            headerDescription.FontSize = 12;
            headerDescription.Foreground = BrushFromRgb(71, 85, 105);
            headerDescription.Margin = new Thickness(0, 3, 10, 0);
            titleStack.Children.Add(titleLabel);
            titleStack.Children.Add(headerDescription);
            Grid.SetColumn(titleStack, 1);
            headerGrid.Children.Add(titleStack);

            languageButton.VerticalAlignment = VerticalAlignment.Top;
            languageButton.Padding = new Thickness(12, 0, 12, 0);
            languageButton.FontSize = 12;
            languageButton.FontWeight = FontWeights.SemiBold;
            languageButton.Background = BrushFromRgb(240, 249, 255);
            languageButton.Foreground = BrushFromRgb(2, 132, 199);
            languageButton.BorderBrush = BrushFromRgb(186, 230, 253);
            languageButton.BorderThickness = new Thickness(1);
            languageButton.Cursor = Cursors.Hand;
            languageButton.Click += delegate { english = !english; ApplyLanguage(); };
            Grid.SetColumn(languageButton, 2);
            headerGrid.Children.Add(languageButton);

            headerBorder.Child = headerGrid;
            rootStack.Children.Add(headerBorder);

            // Body container
            var bodyPanel = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };

            // Main rounded settings card
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = BrushFromRgb(221, 232, 240),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18, 16, 18, 18)
            };
            var cardStack = new StackPanel();

            targetLabel.FontSize = 12;
            targetLabel.FontWeight = FontWeights.SemiBold;
            targetLabel.Foreground = BrushFromRgb(71, 85, 105);
            targetLabel.Margin = new Thickness(0, 0, 0, 6);
            cardStack.Children.Add(targetLabel);

            targetBox.Height = 36;
            targetBox.Margin = new Thickness(0, 0, 0, 14);
            targetBox.Items.Add("ChatGPT / Codex (Store)");
            targetBox.Items.Add("Antigravity (Desktop)");
            targetBox.SelectedIndex = 0;
            targetBox.SelectionChanged += delegate { if (!isBusy) UpdateTargetMode(); };
            cardStack.Children.Add(targetBox);

            var fieldsGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.0, GridUnitType.Star) });
            fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            fieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            fieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            foreach (var lbl in new[] { hostLabel, portLabel, protocolLabel })
            {
                lbl.FontSize = 12;
                lbl.FontWeight = FontWeights.SemiBold;
                lbl.Foreground = BrushFromRgb(71, 85, 105);
                lbl.Margin = new Thickness(0, 0, 0, 6);
            }
            Grid.SetColumn(hostLabel, 0); Grid.SetRow(hostLabel, 0); fieldsGrid.Children.Add(hostLabel);
            Grid.SetColumn(portLabel, 2); Grid.SetRow(portLabel, 0); fieldsGrid.Children.Add(portLabel);
            Grid.SetColumn(protocolLabel, 4); Grid.SetRow(protocolLabel, 0); fieldsGrid.Children.Add(protocolLabel);

            hostBox.Height = 36;
            hostBox.VerticalContentAlignment = VerticalAlignment.Center;
            portBox.Height = 36;
            portBox.VerticalContentAlignment = VerticalAlignment.Center;
            portBox.PreviewTextInput += delegate(object s, TextCompositionEventArgs args)
            {
                foreach (char ch in args.Text)
                {
                    if (!char.IsDigit(ch)) { args.Handled = true; return; }
                }
            };
            protocolBox.Height = 36;
            protocolBox.Items.Add("HTTP");
            protocolBox.Items.Add("SOCKS5");
            protocolBox.SelectedIndex = 0;
            protocolBox.SelectionChanged += delegate { if (!isBusy) ApplyLanguage(); };

            Grid.SetColumn(hostBox, 0); Grid.SetRow(hostBox, 1); fieldsGrid.Children.Add(hostBox);
            Grid.SetColumn(portBox, 2); Grid.SetRow(portBox, 1); fieldsGrid.Children.Add(portBox);
            Grid.SetColumn(protocolBox, 4); Grid.SetRow(protocolBox, 1); fieldsGrid.Children.Add(protocolBox);
            cardStack.Children.Add(fieldsGrid);

            var authGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            authGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            authGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            authGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            authGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            authGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            foreach (var lbl in new[] { usernameLabel, passwordLabel })
            {
                lbl.FontSize = 12;
                lbl.FontWeight = FontWeights.SemiBold;
                lbl.Foreground = BrushFromRgb(71, 85, 105);
                lbl.Margin = new Thickness(0, 0, 0, 6);
            }
            Grid.SetColumn(usernameLabel, 0); Grid.SetRow(usernameLabel, 0); authGrid.Children.Add(usernameLabel);
            Grid.SetColumn(passwordLabel, 2); Grid.SetRow(passwordLabel, 0); authGrid.Children.Add(passwordLabel);

            usernameBox.Height = 36;
            usernameBox.VerticalContentAlignment = VerticalAlignment.Center;
            usernameBox.TextChanged += delegate { if (!isBusy && processProxy.State == ProcessProxy.ProxyState.Off) ApplyTargetStatus(); };
            passwordBox.Height = 36;
            passwordBox.VerticalContentAlignment = VerticalAlignment.Center;
            passwordBox.PasswordChanged += delegate { if (!isBusy && processProxy.State == ProcessProxy.ProxyState.Off) ApplyTargetStatus(); };

            Grid.SetColumn(usernameBox, 0); Grid.SetRow(usernameBox, 1); authGrid.Children.Add(usernameBox);
            Grid.SetColumn(passwordBox, 2); Grid.SetRow(passwordBox, 1); authGrid.Children.Add(passwordBox);
            cardStack.Children.Add(authGrid);

            processProxyCheckBox.Margin = new Thickness(0, 0, 0, 14);
            processProxyCheckBox.Foreground = BrushFromRgb(51, 65, 85);
            processProxyCheckBox.FontSize = 12.5;
            processProxyCheckBox.Cursor = Cursors.Hand;
            processProxyCheckBox.Checked += OnEnhancedCheckChanged;
            processProxyCheckBox.Unchecked += OnEnhancedCheckChanged;
            cardStack.Children.Add(processProxyCheckBox);

            antigravityHintLabel.Margin = new Thickness(0, 0, 0, 14);
            antigravityHintLabel.Foreground = BrushFromRgb(15, 118, 110);
            antigravityHintLabel.FontSize = 12.5;
            antigravityHintLabel.Visibility = Visibility.Collapsed;
            cardStack.Children.Add(antigravityHintLabel);

            launchButton.FontSize = 14;
            launchButton.FontWeight = FontWeights.SemiBold;
            launchButton.Foreground = Brushes.White;
            launchButton.BorderThickness = new Thickness(0);
            launchButton.Cursor = Cursors.Hand;
            launchButton.Background = new LinearGradientBrush(
                Color.FromRgb(2, 132, 199),
                Color.FromRgb(13, 148, 136),
                new Point(0, 0), new Point(1, 0));
            launchButton.Margin = new Thickness(0, 0, 0, 10);
            launchButton.Click += LaunchButtonClick;
            cardStack.Children.Add(launchButton);

            foreach (var secBtn in new[] { browserProxyButton, removePatchButton })
            {
                secBtn.FontSize = 12.5;
                secBtn.Background = BrushFromRgb(248, 250, 252);
                secBtn.Foreground = BrushFromRgb(51, 65, 85);
                secBtn.BorderBrush = BrushFromRgb(203, 213, 225);
                secBtn.BorderThickness = new Thickness(1);
                secBtn.Cursor = Cursors.Hand;
            }
            browserProxyButton.Click += delegate
            {
                try { var dialog = new CuaProxyMaintenanceDialog(english) { Owner = this }; dialog.ShowDialog(); }
                catch (Exception ex) { SetStatus(ex.Message, false); }
            };
            removePatchButton.Click += RemovePatchButtonClick;
            cardStack.Children.Add(browserProxyButton);
            cardStack.Children.Add(removePatchButton);

            card.Child = cardStack;
            bodyPanel.Children.Add(card);

            // Status banner
            statusPanel.CornerRadius = new CornerRadius(10);
            statusPanel.BorderThickness = new Thickness(1);
            statusPanel.Padding = new Thickness(14, 10, 14, 10);
            statusPanel.Margin = new Thickness(0, 12, 0, 10);
            var statusGrid = new Grid();
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusLabel.FontSize = 12.5;
            Grid.SetColumn(statusMark, 0);
            Grid.SetColumn(statusLabel, 1);
            statusGrid.Children.Add(statusMark);
            statusGrid.Children.Add(statusLabel);
            statusPanel.Child = statusGrid;
            bodyPanel.Children.Add(statusPanel);

            footnote.FontSize = 11.5;
            footnote.Foreground = BrushFromRgb(100, 116, 139);
            footnote.LineHeight = 17;
            footnote.Margin = new Thickness(2, 0, 2, 0);
            bodyPanel.Children.Add(footnote);

            rootStack.Children.Add(bodyPanel);
            Content = rootStack;

            processProxyHealth.Tick += delegate
            {
                diagnostics.Heartbeat();
                try { processProxy.PollHealth(); }
                catch (Exception ex) { processProxyCheckBox.IsChecked = false; SetStatus(ex.Message, false); return; }
                string error = browserForwardingWatch == null ? null : browserForwardingWatch.LastError;
                if (error != lastBrowserForwardingError)
                {
                    if (error != null) SetStatus(L("浏览器代理配置检查失败：", "Browser proxy configuration check failed: ") + error, false);
                    else if (lastBrowserForwardingError != null) SetStatus(L("浏览器代理配置已恢复；已运行的工具可能需要重新连接", "Browser proxy configuration restored; existing tools may need reconnecting"), null);
                    lastBrowserForwardingError = error;
                }
            };
            processProxyHealth.Start();

            ApplyLanguage();
            Loaded += async delegate
            {
                var saved = persistSettings ? settingsStore.Load() : null;
                var resume = PortableRuntime.Resume;
                if (resume != null)
                {
                    if (saved != null)
                    {
                        usernameBox.Text = saved.Username ?? String.Empty;
                        passwordBox.Password = saved.Password ?? String.Empty;
                    }
                    targetBox.SelectedIndex = 0;
                    hostBox.Text = resume.Host;
                    portBox.Text = resume.Port.ToString();
                    protocolBox.SelectedIndex = String.Equals(resume.Protocol, "socks5", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    processProxyCheckBox.IsChecked = resume.Enhanced;
                    english = resume.English;
                    UpdateTargetMode();
                    LaunchButtonClick(this, new RoutedEventArgs());
                }
                else if (saved != null)
                {
                    ApplySavedSettings(saved);
                    UpdateTargetMode();
                }
                else
                {
                    string probeHost = String.IsNullOrWhiteSpace(hostBox.Text) ? "127.0.0.1" : hostBox.Text.Trim();
                    foreach (int candidate in new[] { 7890, 10808, 1080 })
                    {
                        if (await Task.Run(() => CanConnect(probeHost, candidate)))
                        {
                            portBox.Text = candidate.ToString();
                            break;
                        }
                    }
                }
            };
        }

        private void ApplySavedSettings(LauncherSettings saved)
        {
            if (saved == null) return;
            targetBox.SelectedIndex = String.Equals(saved.Target, "antigravity", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (!String.IsNullOrWhiteSpace(saved.Host)) hostBox.Text = saved.Host.Trim();
            if (!String.IsNullOrWhiteSpace(saved.Port)) portBox.Text = saved.Port.Trim();
            protocolBox.SelectedIndex = String.Equals(saved.Protocol, "socks5", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            processProxyCheckBox.IsChecked = saved.Enhanced;
            english = saved.English;
            usernameBox.Text = saved.Username ?? String.Empty;
            passwordBox.Password = saved.Password ?? String.Empty;
        }

        private void SaveCurrentSettings()
        {
            if (!persistSettings) return;
            settingsStore.Save(new LauncherSettings
            {
                Target = IsAntigravitySelected ? "antigravity" : "codex",
                Host = hostBox.Text.Trim(),
                Port = portBox.Text.Trim(),
                Protocol = SelectedProtocol,
                Enhanced = processProxyCheckBox.IsChecked == true,
                English = english,
                Username = usernameBox.Text,
                Password = passwordBox.Password
            });
        }

        internal void MarkSystemShutdown()
        {
            shuttingDown = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!shuttingDown && !explicitExit &&
                processProxy.State == ProcessProxy.ProxyState.Ready)
            {
                e.Cancel = true;
                sessionTray.Visible = true;
                Hide();
                if (!trayNoticeShown)
                {
                    trayNoticeShown = true;
                    sessionTray.ShowBalloonTip(4000, "AgentProxy",
                        L("增强代理继续运行。双击托盘图标打开；右键可退出并停止代理。",
                          "Enhanced proxy remains active. Double-click to open; right-click to exit and stop it."),
                        Forms.ToolTipIcon.Info);
                }
                base.OnClosing(e);
                return;
            }
            SaveCurrentSettings();
            processProxyHealth.Stop();
            if (browserForwardingWatch != null) browserForwardingWatch.Dispose();
            try { processProxy.Dispose(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Agent Proxy"); }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            diagnostics.Dispose();
            sessionTray.Dispose();
            trayMenu.Dispose();
            base.OnClosed(e);
        }

        private async void OnEnhancedCheckChanged(object sender, RoutedEventArgs e)
        {
            if (isBusy) return;
            bool checkedNow = processProxyCheckBox.IsChecked == true;
            if (!checkedNow && processProxy.State == ProcessProxy.ProxyState.Ready)
            {
                try
                {
                    await Task.Run(() => processProxy.Dispose());
                    SetStatus(L("增强代理已关闭；基础代理在客户端重启前仍保持", "Enhanced proxy stopped; basic client proxy stays until restart"), null);
                }
                catch (Exception ex) { SetStatus(ex.Message, false); }
            }
            else if (processProxy.State == ProcessProxy.ProxyState.Off && !IsAntigravitySelected)
            {
                ApplyTargetStatus();
            }
        }

        private bool IsAntigravitySelected { get { return targetBox.SelectedIndex == 1; } }
        private string SelectedProtocol { get { return protocolBox.SelectedIndex == 1 ? "socks5" : "http"; } }
        private bool HasCredentials { get { return !String.IsNullOrEmpty(usernameBox.Text) || !String.IsNullOrEmpty(passwordBox.Password); } }
        private string L(string chinese, string englishText) { return english ? englishText : chinese; }

        private void UpdateTargetMode()
        {
            if (!IsAntigravitySelected)
            {
                processProxyCheckBox.Visibility = Visibility.Visible;
                antigravityHintLabel.Visibility = Visibility.Collapsed;
                browserProxyButton.Visibility = Visibility.Visible;
                removePatchButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                processProxyCheckBox.Visibility = Visibility.Collapsed;
                antigravityHintLabel.Visibility = Visibility.Visible;
                browserProxyButton.Visibility = Visibility.Collapsed;
                removePatchButton.Visibility = Visibility.Visible;
            }
            ApplyLanguage();
        }

        private void ApplyTargetStatus()
        {
            if (IsAntigravitySelected)
            {
                SetStatus(L("准备就绪 · Antigravity 进程级代理（支持 HTTP / SOCKS5 与账密认证）",
                    "Ready · Antigravity process proxy (HTTP / SOCKS5 + auth)"), null);
                return;
            }
            if (HasCredentials && processProxyCheckBox.IsChecked != true)
            {
                SetStatus(L("注意：带用户名/密码的代理不支持普通模式 Codex / ChatGPT，请勾选「启用增强代理」或清空凭据",
                    "Note: Authenticated proxies are not supported in standard Codex / ChatGPT mode; enable Enhanced Proxy or clear credentials"), false);
                return;
            }
            if (SelectedProtocol == "socks5" && processProxyCheckBox.IsChecked != true)
            {
                SetStatus(L("注意：SOCKS5 不支持普通模式 Codex / ChatGPT，请勾选「启用增强代理」或改用 HTTP",
                    "Note: SOCKS5 is not supported in standard Codex / ChatGPT mode; enable Enhanced Proxy or switch to HTTP"), false);
                return;
            }
            if (processProxyCheckBox.IsChecked == true)
            {
                SetStatus(SelectedProtocol == "socks5"
                    ? L("准备就绪 · 已勾选增强代理（SOCKS5 驱动级 443 转发，需驱动授权）", "Ready · Enhanced proxy selected (SOCKS5 driver 443 routing, approval required)")
                    : L("准备就绪 · 已勾选增强代理（HTTP 驱动级 443 转发，需驱动授权）", "Ready · Enhanced proxy selected (HTTP driver 443 routing, approval required)"), null);
                return;
            }
            SetStatus(L("准备就绪 · 普通代理模式（无需驱动，仅支持无认证 HTTP）", "Ready · Standard proxy mode (no driver needed, unauthenticated HTTP only)"), null);
        }

        private void ApplyLanguage()
        {
            languageButton.Content = english ? "中文" : "EN";
            headerDescription.Text = L(
                "无需开启全局代理，为 ChatGPT / Codex 或 Antigravity 单独代理",
                "Proxy ChatGPT / Codex or Antigravity without enabling a global proxy");
            targetLabel.Text = L("启动目标", "Target app");
            hostLabel.Text = L("代理主机（本机 / 局域网 IP）", "Proxy host (local / LAN IP)");
            portLabel.Text = L("代理端口", "Proxy port");
            protocolLabel.Text = L("代理协议", "Protocol");
            usernameLabel.Text = L("用户名（可选）", "Username (optional)");
            passwordLabel.Text = L("密码（可选）", "Password (optional)");
            processProxyCheckBox.Content = (!IsAntigravitySelected && (SelectedProtocol == "socks5" || HasCredentials))
                ? L("启用增强代理（SOCKS5 或账密认证不支持普通 Codex，必须勾选）", "Enable enhanced proxy (required for SOCKS5 / auth on Codex)")
                : L("启用增强代理（WinDivert 驱动级 443 转发，可选）", "Enable enhanced proxy (WinDivert 443 routing, optional)");
            antigravityHintLabel.Text = L(
                "通过 version.dll 注入主进程与子进程，无需驱动与管理员权限",
                "Injects via version.dll into host & child processes; no driver/admin");
            launchButton.Content = IsAntigravitySelected
                ? L("部署代理并启动 Antigravity", "Apply proxy & launch Antigravity")
                : L("启动 ChatGPT / Codex", "Launch ChatGPT / Codex");
            footnote.Text = IsAntigravitySelected
                ? L("配置自动保存；每次启动校验并恢复 version.dll，并将主机/端口/协议/凭据写入 config.json（密码本地加密保存）。",
                    "Settings are saved automatically (password encrypted locally); verifies version.dll and writes host/port/protocol/auth to config.json.")
                : L("普通模式仅支持无认证 HTTP；SOCKS5 或用户名/密码认证需勾选增强代理。配置自动保存，密码使用当前用户 DPAPI 加密存储。",
                    "Standard mode supports unauthenticated HTTP only; SOCKS5 or auth requires Enhanced Proxy. Settings save automatically with DPAPI encryption.");
            browserProxyButton.Content = L("检查 / 修复浏览器", "Check / repair browser");
            removePatchButton.Content = L("移除 Antigravity 代理补丁 (version.dll)", "Remove Antigravity proxy patch (version.dll)");
            ApplyTargetStatus();
        }

        private async void RemovePatchButtonClick(object sender, RoutedEventArgs e)
        {
            SetInputsEnabled(false);
            Cursor = Cursors.Wait;
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
            catch (Exception ex) { SetStatus(ex.Message, false); }
            finally
            {
                Cursor = null;
                SetInputsEnabled(true);
            }
        }

        private void SetInputsEnabled(bool enabled)
        {
            isBusy = !enabled;
            launchButton.IsEnabled = enabled;
            browserProxyButton.IsEnabled = enabled;
            removePatchButton.IsEnabled = enabled;
            processProxyCheckBox.IsEnabled = enabled;
            targetBox.IsEnabled = enabled;
            hostBox.IsEnabled = enabled;
            portBox.IsEnabled = enabled;
            protocolBox.IsEnabled = enabled;
            usernameBox.IsEnabled = enabled;
            passwordBox.IsEnabled = enabled;
        }

        private async void LaunchButtonClick(object sender, RoutedEventArgs e)
        {
            SetInputsEnabled(false);
            Cursor = Cursors.Wait;
            Exception launchFailure = null;
            try
            {
                try
                {
                    string host = hostBox.Text.Trim();
                    int port;
                    if (!Int32.TryParse(portBox.Text.Trim(), out port) || port < 1 || port > 65535)
                        throw new Exception(L("请输入有效端口 (1-65535)。", "Enter a valid port (1-65535)."));
                    bool isAntigravity = IsAntigravitySelected;
                    string protocol = SelectedProtocol;
                    bool enhanced = processProxyCheckBox.IsChecked == true;
                    string username = usernameBox.Text;
                    string password = passwordBox.Password;
                    bool hasAuth = !String.IsNullOrEmpty(username);

                    diagnostics.Stage("checking-proxy");
                    if (host.Length == 0) throw new Exception(L("请输入代理主机。", "Enter a proxy host."));
                    if (!String.IsNullOrEmpty(password) && String.IsNullOrEmpty(username))
                        throw new Exception(L("填写代理密码时必须同时填写用户名。", "A proxy username is required when a password is provided."));
                    ProcessProxy.ProxyPlan.ValidateCredential(username, "Proxy username");
                    ProcessProxy.ProxyPlan.ValidateCredential(password, "Proxy password");
                    if (!isAntigravity && hasAuth && !enhanced)
                        throw new Exception(L("带用户名/密码的代理不支持普通模式 Codex / ChatGPT，请勾选「启用增强代理」或清空凭据。",
                            "Authenticated proxies are not supported in standard Codex / ChatGPT mode. Enable Enhanced Proxy or clear the credentials."));
                    if (!isAntigravity && protocol == "socks5" && !enhanced)
                        throw new Exception(L("SOCKS5 不支持普通模式 Codex / ChatGPT，请勾选「启用增强代理」或改用 HTTP。",
                            "SOCKS5 is not supported in standard Codex / ChatGPT mode. Enable Enhanced Proxy or switch to HTTP."));
                    SaveCurrentSettings();
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
                        await Task.Run(() => AntigravityLauncher.DeployAndWriteConfig(agExe, host, port, protocol, null, username, password));
                        await Task.Run(() => AntigravityLauncher.Launch(agExe));
                        diagnostics.Stage("running");
                        SetStatus(String.Format(L("已通过 {0}://{1}:{2}{3} 启动 Antigravity", "Antigravity started via {0}://{1}:{2}{3}"),
                            protocol, host, port, hasAuth ? L("（已认证）", " (authenticated)") : String.Empty), true);
                        return;
                    }

                    if (await Task.Run(() => IsDesktopAppRunning()))
                        throw new Exception(L("ChatGPT 已在运行，请完全退出后再试。", "ChatGPT is already running. Fully quit it and try again."));

                    string proxyDisplay = String.Format("{0}://{1}:{2}", protocol, host, port);
                    string clientProxyArg = hasAuth ? String.Format("direct://{0}:{1}", host, port) : proxyDisplay;
                    if (enhanced)
                    {
                        SetStatus(L("正在准备增强代理组件…", "Preparing enhanced proxy components…"), null);
                        diagnostics.Stage("preparing-runtime");
                        var resumeSettings = new PortableResume
                        {
                            Host = host, Port = port, Protocol = protocol, Enhanced = true, English = english
                        };
                        bool handedOff = await Task.Run(() => PortableRuntime.Handoff(resumeSettings));
                        if (handedOff) { Close(); return; }
                        SetStatus(L("增强代理：等待引擎与驱动授权", "Enhanced proxy: waiting for engine and driver approval"), null);
                        diagnostics.Stage("preparing-proxy-host");
                        await Task.Run(() => processProxy.Prepare());

                        await Task.Run(() =>
                        {
                            string executable = ProcessProxy.ProxyController.ResolveExecutable();
                            processProxy.Start(new ProcessProxy.ProxyPlan
                            {
                                ExecutablePath = executable,
                                BrowserHelperPaths = ProcessProxy.BrowserHelperScope.Resolve(executable),
                                CoreHelperPaths = ProcessProxy.CoreHelperScope.Resolve(executable),
                                ProxyHost = host,
                                ProxyPort = port,
                                ProxyType = protocol,
                                ProxyUsername = username,
                                ProxyPassword = password,
                                Process443Approved = true,
                                DriverApproved = true
                            });
                        });
                        SetStatus(L("增强代理已就绪", "Enhanced proxy ready"), true);
                    }

                    diagnostics.Stage("checking-browser-forwarding");
                    if (browserForwardingWatch == null) browserForwardingWatch = await Task.Run(() => CuaProxyForwardingWatch.CreateDefault());
                    SetStatus(L("正在查找 ChatGPT…", "Finding ChatGPT…"), null);
                    diagnostics.Stage("activating-package");
                    await StartPackagedChatGPT(clientProxyArg, null, processProxy.State == ProcessProxy.ProxyState.Ready ? processProxy.TargetExecutable : null);
                    diagnostics.Stage("running");
                    SetStatus(L("已通过 " + proxyDisplay + (hasAuth ? "（已认证）" : String.Empty) + " 启动 ChatGPT",
                        "ChatGPT started through " + proxyDisplay + (hasAuth ? " (authenticated)" : String.Empty)), true);
                }
                catch (Exception error) { launchFailure = error; }
                if (launchFailure != null)
                {
                    var ex = launchFailure;
                    diagnostics.Stage("launch-error");
                    bool keepProxy = false;
                    try { keepProxy = !IsAntigravitySelected && processProxy.State == ProcessProxy.ProxyState.Ready && await Task.Run(() => IsDesktopAppRunning()); } catch { }
                    if (keepProxy) SetStatus(L("客户端已启动，增强代理保持运行；后续步骤失败：", "Client started; enhanced proxy remains active. A later step failed: ") + ex.Message, false);
                    else
                    {
                        string cleanup = "";
                        if (!IsAntigravitySelected)
                        {
                            try { await Task.Run(() => processProxy.Dispose()); }
                            catch (Exception stopError) { cleanup = " " + stopError.Message; }
                        }
                        SetStatus(ex.Message + cleanup, ex is OperationCanceledException ? (bool?)null : false);
                    }
                }
            }
            finally
            {
                Cursor = null;
                SetInputsEnabled(true);
            }
        }

        private void SetStatus(string text, bool? success)
        {
            statusLabel.Text = text;
            if (success == true)
            {
                statusPanel.Background = BrushFromRgb(236, 253, 245);
                statusPanel.BorderBrush = BrushFromRgb(167, 243, 208);
                statusLabel.Foreground = BrushFromRgb(4, 120, 87);
                statusMark.Foreground = BrushFromRgb(16, 185, 129);
            }
            else if (success == false)
            {
                statusPanel.Background = BrushFromRgb(254, 242, 242);
                statusPanel.BorderBrush = BrushFromRgb(254, 202, 202);
                statusLabel.Foreground = BrushFromRgb(185, 28, 28);
                statusMark.Foreground = BrushFromRgb(239, 68, 68);
            }
            else
            {
                statusPanel.Background = Brushes.White;
                statusPanel.BorderBrush = BrushFromRgb(220, 232, 242);
                statusLabel.Foreground = BrushFromRgb(71, 85, 105);
                statusMark.Foreground = BrushFromRgb(14, 165, 233);
            }
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
            WindowState = WindowState.Normal;
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
                await Task.Run(() => process.WaitForExit());
                string output = (await outputRead).Trim();
                string error = (await errorRead).Trim();
                if (process.ExitCode != 0 || output.Length == 0)
                    throw new InvalidOperationException(error.Length == 0 ? "ChatGPT package activation failed." : error);
            }
        }

        private static SolidColorBrush BrushFromRgb(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static BitmapImage LoadEmbeddedLogo()
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app-icon.png"))
                {
                    if (stream == null) return null;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.StreamSource = stream;
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
            catch { return null; }
        }

        private void ApplyControlStyles()
        {
            // Rounded Button template
            var btnTemplate = new ControlTemplate(typeof(Button));
            var btnBorder = new FrameworkElementFactory(typeof(Border));
            btnBorder.Name = "bd";
            btnBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            btnBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            btnBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            btnBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var btnContent = new FrameworkElementFactory(typeof(ContentPresenter));
            btnContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            btnContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            btnBorder.AppendChild(btnContent);
            btnTemplate.VisualTree = btnBorder;
            var btnHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            btnHover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.90, "bd"));
            var btnDisabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            btnDisabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "bd"));
            btnTemplate.Triggers.Add(btnHover);
            btnTemplate.Triggers.Add(btnDisabled);
            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Control.TemplateProperty, btnTemplate));
            Resources.Add(typeof(Button), btnStyle);

            Resources.Add(typeof(TextBox), CreateInputStyle(typeof(TextBox)));
            Resources.Add(typeof(PasswordBox), CreateInputStyle(typeof(PasswordBox)));

            // Clean rounded ComboBox template via XAML
            const string comboTemplateXaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>" +
                "<Grid>" +
                "<ToggleButton x:Name='ToggleButton' Focusable='false' ClickMode='Press' " +
                "IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>" +
                "<ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>" +
                "<Border x:Name='bd' Background='#FFFFFF' BorderBrush='#CBD5E1' BorderThickness='1' CornerRadius='7' Padding='10,0,10,0'>" +
                "<Grid><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
                "<Path Grid.Column='1' VerticalAlignment='Center' HorizontalAlignment='Center' Data='M 0 0 L 4 4 L 8 0' Stroke='#64748B' StrokeThickness='1.5' Fill='Transparent'/>" +
                "</Grid></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#0284C7'/></Trigger>" +
                "<Trigger Property='IsChecked' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#0284C7'/></Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate></ToggleButton.Template></ToggleButton>" +
                "<ContentPresenter x:Name='ContentSite' IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' " +
                "ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' Margin='11,0,28,0' VerticalAlignment='Center' HorizontalAlignment='Left'/>" +
                "<Popup x:Name='Popup' Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>" +
                "<Grid MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}' Margin='0,4,0,0'>" +
                "<Border Background='#FFFFFF' BorderBrush='#CBD5E1' BorderThickness='1' CornerRadius='8' Padding='4'>" +
                "<ScrollViewer SnapsToDevicePixels='True'><StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>" +
                "</Border></Grid></Popup></Grid></ControlTemplate>";
            var cbStyle = new Style(typeof(ComboBox));
            cbStyle.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse(comboTemplateXaml)));
            cbStyle.Setters.Add(new Setter(Control.ForegroundProperty, BrushFromRgb(15, 23, 42)));
            Resources.Add(typeof(ComboBox), cbStyle);

            var cbItemStyle = new Style(typeof(ComboBoxItem));
            cbItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
            cbItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, BrushFromRgb(15, 23, 42)));
            Resources.Add(typeof(ComboBoxItem), cbItemStyle);
        }

        private Style CreateInputStyle(Type controlType)
        {
            var template = new ControlTemplate(controlType);
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "bd";
            border.SetValue(Border.BackgroundProperty, Brushes.White);
            border.SetValue(Border.BorderBrushProperty, BrushFromRgb(203, 213, 225));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.PaddingProperty, new Thickness(8, 0, 8, 0));
            var host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(host);
            template.VisualTree = border;
            var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, BrushFromRgb(2, 132, 199), "bd"));
            template.Triggers.Add(focusTrigger);
            var style = new Style(controlType);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, BrushFromRgb(15, 23, 42)));
            return style;
        }
    }
}
