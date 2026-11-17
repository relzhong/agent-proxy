using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace AgentProxy
{
    internal sealed class CuaProxyMaintenanceDialog : Window
    {
        private readonly CuaProxyForwardingMaintenance maintenance;
        private CuaProxyForwardingMaintenance.Inspection checkedConfig;
        private readonly bool english;
        private readonly TextBox report = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            Padding = new Thickness(10),
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12.5,
            Background = Brushes.White,
            Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1)
        };
        private readonly Button check = new Button();
        private readonly Button repair = new Button();
        private readonly Button browserRead = new Button();
        private readonly Button rollback = new Button();

        private string L(string zh, string en) { return english ? en : zh; }

        internal CuaProxyMaintenanceDialog(bool useEnglish)
            : this(useEnglish, CuaProxyForwardingMaintenance.CreateDefault()) { }

        internal CuaProxyMaintenanceDialog(bool useEnglish, CuaProxyForwardingMaintenance service)
        {
            english = useEnglish;
            maintenance = service;
            Title = L("浏览器检查 / 修复", "Browser check / repair");
            Width = 780;
            Height = 460;
            MinWidth = 720;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(244, 248, 251));
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var note = new TextBlock
            {
                Text = L(
                    "读取兼容修复让普通入口自动使用 DOM 读取和页面截图，避开无障碍接口超时；代理修复补齐转发。\n每项修改都有完整备份。修复后请用新聊天验证，旧工具进程不会被强制结束。应用更新后可重新检查。",
                    "Read compatibility uses DOM and page screenshots instead of the stalled browser accessibility API. Forwarding repair adds proxy forwarding.\nEach change has a full backup. Validate in a fresh chat; existing tool processes are not terminated. Check again after app updates."),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 0, 12),
                LineHeight = 20
            };
            Grid.SetRow(note, 0);
            root.Children.Add(note);

            Grid.SetRow(report, 1);
            root.Children.Add(report);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 14, 0, 0)
            };
            check.Content = L("检查配置", "Check");
            repair.Content = L("修复转发", "Repair forwarding");
            browserRead.Content = L("修复读取 / 截图", "Repair reads / screenshots");
            rollback.Content = L("从备份回滚", "Rollback backup");
            repair.IsEnabled = false;
            browserRead.IsEnabled = false;

            foreach (Button btn in new[] { check, repair, browserRead, rollback })
            {
                btn.Height = 34;
                btn.Padding = new Thickness(14, 0, 14, 0);
                btn.Margin = new Thickness(0, 0, 10, 0);
                btn.Background = Brushes.White;
                btn.Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));
                btn.BorderThickness = new Thickness(1);
                btn.Cursor = Cursors.Hand;
                actions.Children.Add(btn);
            }
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);

            Content = root;

            check.Click += async delegate { await CheckAsync(); };
            repair.Click += async delegate
            {
                var snapshot = checkedConfig;
                Busy(true);
                try
                {
                    var result = await Task.Run(() => maintenance.Repair(snapshot));
                    checkedConfig = await Task.Run(() => maintenance.Check());
                    report.Text = checkedConfig.Summary + "\r\n\r\n" + (result.Changed
                        ? L("修复完成。回滚记录：", "Repaired. Rollback record: ") + result.RecordPath
                        : L("配置已正确，未写入。", "Already correct; no write."));
                }
                catch (Exception ex) { Failure(ex); }
                finally { Busy(false); }
            };
            browserRead.Click += async delegate
            {
                var snapshot = checkedConfig;
                Busy(true);
                try
                {
                    var result = await Task.Run(() => maintenance.RepairBrowserRead(snapshot));
                    checkedConfig = await Task.Run(() => maintenance.Check());
                    report.Text = checkedConfig.Summary + "\r\n\r\n"
                        + L("已设置读取兼容模式。请在新聊天中验证。回滚记录：", "Read compatibility configured. Verify in a fresh chat. Rollback record: ")
                        + result.RecordPath;
                }
                catch (Exception ex) { Failure(ex); }
                finally { Busy(false); }
            };
            rollback.Click += async delegate
            {
                var picker = new OpenFileDialog
                {
                    Title = L("选择 AgentProxy 回滚记录", "Select AgentProxy rollback record"),
                    Filter = "AgentProxy rollback (*.record.json)|*.record.json",
                    InitialDirectory = maintenance.BackupRoot,
                    CheckFileExists = true
                };
                if (picker.ShowDialog(this) != true) return;
                string recordPath = picker.FileName;
                Busy(true);
                try
                {
                    await Task.Run(() => maintenance.Rollback(recordPath));
                    checkedConfig = await Task.Run(() => maintenance.Check());
                    report.Text = L("已完整恢复原文件。\r\n\r\n", "Original file restored completely.\r\n\r\n") + checkedConfig.Summary;
                }
                catch (Exception ex) { Failure(ex); }
                finally { Busy(false); }
            };

            Loaded += async delegate { await CheckAsync(); };
        }

        private void Busy(bool busy)
        {
            check.IsEnabled = !busy;
            rollback.IsEnabled = !busy;
            repair.IsEnabled = !busy && checkedConfig != null && checkedConfig.Missing.Length > 0;
            browserRead.IsEnabled = !busy && checkedConfig != null && checkedConfig.BrowserReadMode == "1";
            Cursor = busy ? Cursors.Wait : null;
        }

        private void Failure(Exception error)
        {
            checkedConfig = null;
            report.Text = L("操作未完成：", "Operation not completed: ") + error.Message + "\r\n\r\n"
                + L("没有重启应用。若配置正在同步，请稍后重新检查。备份目录：", "No app restart. If configuration is syncing, check again later. Backup directory: ")
                + maintenance.BackupRoot;
        }

        private async Task CheckAsync()
        {
            Busy(true);
            try
            {
                checkedConfig = await Task.Run(() => maintenance.Check());
                report.Text = checkedConfig.Summary;
            }
            catch (Exception ex) { Failure(ex); }
            finally { Busy(false); }
        }
    }
}
