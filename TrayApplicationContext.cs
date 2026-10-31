using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LanFileShare;

/// <summary>
/// 托盘应用上下文：启动时静默初始化 SMB 服务，通过托盘图标展示运行状态，
/// 支持手动启动/停止，以及安全退出。
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SmbServiceManager _manager = new();
    private readonly Form _syncAnchor;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;

    private StatusForm? _statusForm;
    private bool _shuttingDown;

    public TrayApplicationContext()
    {
        _manager.StatusChanged += OnStatusChanged;
        _manager.Log += OnLog;

        // 用于把后台线程的事件回调封送到 UI 线程的隐藏窗口。
        _syncAnchor = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0
        };
        _syncAnchor.CreateControl();

        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("状态：已停止") { Enabled = false };
        _startItem = new ToolStripMenuItem("启动 SMB 服务", null, (_, _) => _ = StartServiceAsync());
        _stopItem = new ToolStripMenuItem("停止 SMB 服务", null, (_, _) => _ = StopServiceAsync()) { Enabled = false };
        var openItem = new ToolStripMenuItem("打开状态窗口", null, (_, _) => ShowStatusForm());
        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => Exit());

        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = MakeStatusIcon(Color.Gray),
            Text = "LanFileShare - SMB 文件共享（已停止）",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowStatusForm();

        // 启动即静默初始化 SMB 服务。
        _ = StartServiceAsync();
    }

    private async Task StartServiceAsync()
    {
        RunOnUi(() => SetBusy(true));
        try
        {
            await _manager.StartAsync();
            RunOnUi(() => _trayIcon.ShowBalloonTip(3000, "LanFileShare",
                "SMB 服务已启动，已共享本机所有磁盘。\n账号 root / 密码 123", ToolTipIcon.Info));
        }
        catch (Exception ex)
        {
            RunOnUi(() => _trayIcon.ShowBalloonTip(5000, "LanFileShare 启动失败", ex.Message, ToolTipIcon.Error));
        }
        finally
        {
            RunOnUi(() => SetBusy(false));
        }
    }

    private async Task StopServiceAsync()
    {
        RunOnUi(() => SetBusy(true));
        try
        {
            await _manager.StopAsync();
            RunOnUi(() => _trayIcon.ShowBalloonTip(3000, "LanFileShare", "SMB 服务已停止。", ToolTipIcon.Info));
        }
        catch (Exception ex)
        {
            RunOnUi(() => _trayIcon.ShowBalloonTip(5000, "LanFileShare", "停止失败：" + ex.Message, ToolTipIcon.Error));
        }
        finally
        {
            RunOnUi(() => SetBusy(false));
        }
    }

    private void OnStatusChanged(SmbServiceStatus status)
    {
        RunOnUi(() =>
        {
            _trayIcon.Icon = MakeStatusIcon(StatusColor(status));
            switch (status)
            {
                case SmbServiceStatus.Running:
                    _trayIcon.Text = "LanFileShare - 运行中（root / 123）";
                    _statusItem.Text = "状态：运行中";
                    _startItem.Enabled = false;
                    _stopItem.Enabled = true;
                    break;
                case SmbServiceStatus.Failed:
                    _trayIcon.Text = "LanFileShare - 启动失败";
                    _statusItem.Text = "状态：失败";
                    _startItem.Enabled = true;
                    _stopItem.Enabled = false;
                    break;
                case SmbServiceStatus.Stopped:
                    _trayIcon.Text = "LanFileShare - 已停止";
                    _statusItem.Text = "状态：已停止";
                    _startItem.Enabled = true;
                    _stopItem.Enabled = false;
                    break;
                default:
                    _trayIcon.Text = "LanFileShare - 处理中...";
                    _statusItem.Text = "状态：处理中...";
                    break;
            }
            _statusForm?.UpdateStatus(_manager);
        });
    }

    private void OnLog(string line)
    {
        RunOnUi(() => _statusForm?.AppendLog(line));
    }

    private void SetBusy(bool busy)
    {
        _startItem.Enabled = !busy && !_shuttingDown;
        _stopItem.Enabled = !busy && !_shuttingDown;
    }

    private void ShowStatusForm()
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_manager);
            _statusForm.FormClosed += (_, _) => _statusForm = null;
        }
        _statusForm.Show();
        _statusForm.BringToFront();
        _statusForm.Activate();
    }

    private void Exit()
    {
        _shuttingDown = true;
        _trayIcon.Visible = false;
        // 注意：不能在 UI 线程上直接阻塞等待 StopAsync——它内部 await 的续体会被投递回
        // UI 线程，直接 GetResult() 会造成死锁（这正是“退出后进程不消失”的原因）。
        // 放到线程池执行可安全等待。
        try { Task.Run(() => _manager.StopAsync()).GetAwaiter().GetResult(); } catch { /* 忽略退出时的停止异常 */ }
        _trayIcon.Dispose();
        ExitThread();
    }

    private void RunOnUi(Action action)
    {
        if (_syncAnchor.IsHandleCreated && _syncAnchor.InvokeRequired)
            _syncAnchor.BeginInvoke(action);
        else
            action();
    }

    private static Color StatusColor(SmbServiceStatus status) => status switch
    {
        SmbServiceStatus.Running => Color.ForestGreen,
        SmbServiceStatus.Failed => Color.Firebrick,
        _ => Color.Gray
    };

    private static Icon MakeStatusIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.White, 3f);
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }

        IntPtr hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _manager.StatusChanged -= OnStatusChanged;
            _manager.Log -= OnLog;
            _trayIcon.Dispose();
            _statusForm?.Dispose();
            _syncAnchor.Dispose();
        }
        base.Dispose(disposing);
    }
}
