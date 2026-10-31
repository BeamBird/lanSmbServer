using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanFileShare;

/// <summary>最小化的状态窗口：展示运行状态、账号密码与主机 IP、共享列表与实时日志，并支持启动/停止。</summary>
public sealed class StatusForm : Form
{
    private readonly SmbServiceManager _manager;
    private readonly Label _statusLabel;
    private readonly Label _infoLabel;
    private readonly Label _sharesLabel;
    private readonly TextBox _logBox;
    private readonly Button _startButton;
    private readonly Button _stopButton;

    public StatusForm(SmbServiceManager manager)
    {
        _manager = manager;
        Text = "LanFileShare - SMB 文件共享服务";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 470);
        MinimumSize = new Size(480, 400);
        ShowInTaskbar = true;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14)
        };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // 状态
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));  // 账号密码 + 主机 IP（两行，留足高度防截断）
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));  // 共享资源
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 日志
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // 按钮
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
        };

        _infoLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DarkSlateBlue,
            Font = new Font("Segoe UI", 9.5f)
        };

        _sharesLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            AutoEllipsis = true
        };

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Font = new Font("Consolas", 9f)
        };

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        _startButton = new Button { Text = "启动服务", AutoSize = true, MinimumSize = new Size(100, 32) };
        _stopButton = new Button { Text = "停止服务", AutoSize = true, MinimumSize = new Size(100, 32) };
        _startButton.Click += (_, _) => _ = StartStopAsync(true);
        _stopButton.Click += (_, _) => _ = StartStopAsync(false);
        btnPanel.Controls.Add(_startButton);
        btnPanel.Controls.Add(_stopButton);

        table.Controls.Add(_statusLabel, 0, 0);
        table.Controls.Add(_infoLabel, 0, 1);
        table.Controls.Add(_sharesLabel, 0, 2);
        table.Controls.Add(_logBox, 0, 3);
        table.Controls.Add(btnPanel, 0, 4);

        Controls.Add(table);

        // 关闭窗口 = 隐藏到托盘，不退出程序。
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        _infoLabel.Text = BuildInfoText();
        UpdateStatus(_manager);
    }

    /// <summary>生成账号密码 + 主机 IP 提示文本（分两行显示，确保 IP 可见）。</summary>
    private static string BuildInfoText()
    {
        string ip = GetLocalIpv4();
        return $"账号：{SmbServiceManager.ShareUser}    密码：{SmbServiceManager.SharePassword}\r\n主机 IP：{ip}";
    }

    /// <summary>
    /// 获取“当前活动网络”的 IPv4 地址：
    /// 优先返回有默认网关、状态为 Up、且非虚拟网卡的接口地址；没有网关时退回其他活动接口。
    /// </summary>
    private static string GetLocalIpv4()
    {
        try
        {
            var gatewayIps = new List<string>();
            var otherIps = new List<string>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                if (IsVirtualAdapter(nic))
                    continue;

                var props = nic.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address is not null && g.Address.AddressFamily == AddressFamily.InterNetwork);

                var ipv4 = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(u.Address))
                    .Select(u => u.Address.ToString())
                    .ToList();

                if (ipv4.Count == 0)
                    continue;

                if (hasGateway) gatewayIps.AddRange(ipv4);
                else otherIps.AddRange(ipv4);
            }

            var result = gatewayIps.Count > 0 ? gatewayIps : otherIps;
            return result.Count == 0 ? "未检测到" : string.Join(" / ", result.Distinct());
        }
        catch
        {
            return "未检测到";
        }
    }

    /// <summary>识别常见虚拟网卡（VMware / VirtualBox / Hyper-V / VPN / 回环等），避免把虚拟 IP 当作主机 IP。</summary>
    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        string text = $"{nic.Description} {nic.Name}";
        return text.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("vmware", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("virtualbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("hyper-v", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("vethernet", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("tap-windows", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("vpn", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("wintun", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("loopback", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("pseudo", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private async Task StartStopAsync(bool start)
    {
        SetButtonsEnabled(false);
        try
        {
            if (start) await _manager.StartAsync();
            else await _manager.StopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LanFileShare", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _startButton.Enabled = enabled;
        _stopButton.Enabled = enabled;
    }

    public void UpdateStatus(SmbServiceManager manager)
    {
        if (IsDisposed) return;

        _statusLabel.Text = manager.Status switch
        {
            SmbServiceStatus.Running => "状态：运行中",
            SmbServiceStatus.Failed => "状态：启动失败",
            SmbServiceStatus.Starting => "状态：正在启动...",
            SmbServiceStatus.Stopping => "状态：正在停止...",
            _ => "状态：已停止"
        };

        var shares = manager.Shares;
        _sharesLabel.Text = shares.Count == 0
            ? "共享资源：暂无"
            : "共享资源：" + string.Join("   ", shares.Select(s => $"\\\\{Environment.MachineName}\\{s.Name}"));

        _startButton.Enabled = manager.Status is SmbServiceStatus.Stopped or SmbServiceStatus.Failed;
        _stopButton.Enabled = manager.Status == SmbServiceStatus.Running;
    }

    public void AppendLog(string line)
    {
        if (IsDisposed) return;
        if (_logBox.TextLength > 200_000)
            _logBox.Clear();
        _logBox.AppendText(line + Environment.NewLine);
    }
}
