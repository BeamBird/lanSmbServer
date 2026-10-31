using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;

namespace LanFileShare;

public enum SmbServiceStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

public sealed record ShareRecord(string Name, string Path);

/// <summary>
/// 将 Windows 原生 SMB 服务编排为“一键共享全部磁盘”的能力。
/// 实现流程：确保 LanmanServer 服务运行 -> 创建本地账号 root/123 -> 为每个磁盘创建 SMB 共享。
/// 使用系统自带 SMB 服务（占用 445 端口），避免与 Windows 自带共享冲突。
/// </summary>
public sealed class SmbServiceManager
{
    public const string ShareUser = "root";
    public const string SharePassword = "123";

    /// <summary>
    /// 是否把 root 加入本机 Administrators 组，以获得对所有系统文件的完全访问权限。
    /// 默认 false。开启后等同于把管理员权限通过弱口令暴露到局域网，风险极高，请勿轻易开启。
    /// </summary>
    public static readonly bool GrantFullSystemAccess = false;

    private const string ServerServiceName = "LanmanServer";
    private readonly object _sync = new();

    private readonly List<ShareRecord> _createdShares = new();
    private bool _createdUser;

    public SmbServiceStatus Status { get; private set; } = SmbServiceStatus.Stopped;
    public string? LastError { get; private set; }

    public IReadOnlyList<ShareRecord> Shares
    {
        get { lock (_sync) return _createdShares.ToList(); }
    }

    public event Action<SmbServiceStatus>? StatusChanged;
    public event Action<string>? Log;

    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        SetStatus(SmbServiceStatus.Starting);
        LastError = null;
        lock (_sync) { _createdShares.Clear(); _createdUser = false; }

        try
        {
            if (!IsAdministrator())
            {
                throw new UnauthorizedAccessException(
                    "当前进程不是管理员权限，无法创建 SMB 共享与本地账号。请右键“以管理员身份运行”。");
            }

            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                EnsureServerService();
                ct.ThrowIfCancellationRequested();
                EnsureLocalUser();
                ct.ThrowIfCancellationRequested();
                ShareAllDrives();
            }, ct);

            SetStatus(SmbServiceStatus.Running);
            EmitLog("SMB 服务启动完成。");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetStatus(SmbServiceStatus.Failed);
            EmitLog("启动失败：" + ex.Message);
            try { await RollbackAsync(); } catch { /* 忽略回滚失败 */ }
            throw;
        }
    }

    public async Task StopAsync()
    {
        SetStatus(SmbServiceStatus.Stopping);
        await RollbackAsync();
        SetStatus(SmbServiceStatus.Stopped);
        EmitLog("SMB 服务已停止，已清理本次创建的共享与账号。");
    }

    private Task RollbackAsync() => Task.Run(Rollback);

    /// <summary>安全停止：仅删除本次运行创建的共享与账号，不触碰用户原有的资源。</summary>
    private void Rollback()
    {
        List<ShareRecord> shares;
        bool removeUser;
        lock (_sync)
        {
            shares = _createdShares.ToList();
            removeUser = _createdUser;
            _createdShares.Clear();
            _createdUser = false;
        }

        foreach (var share in shares)
        {
            try
            {
                RemoveShare(share.Name);
                EmitLog($"已删除共享：{share.Name}");
            }
            catch (Exception ex)
            {
                EmitLog($"删除共享 {share.Name} 失败：{ex.Message}");
            }
        }

        if (removeUser)
        {
            try
            {
                RemoveLocalUser();
                EmitLog($"已删除本地账号：{ShareUser}");
            }
            catch (Exception ex)
            {
                EmitLog($"删除本地账号 {ShareUser} 失败：{ex.Message}");
            }
        }
    }

    private void EnsureServerService()
    {
        ServiceController sc;
        try { sc = new ServiceController(ServerServiceName); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("未找到 SMB 服务（LanmanServer），系统可能异常。详情：" + ex.Message, ex);
        }

        using (sc)
        {
            if (sc.Status == ServiceControllerStatus.Running)
                return;

            EmitLog("正在启动 Server（SMB）服务...");
            try
            {
                if (sc.Status == ServiceControllerStatus.Stopped)
                    sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法启动 Server 服务（SMB）。常见原因：服务被禁用、端口 445 被其他程序占用。详情：" + ex.Message, ex);
            }
        }
    }

    private void EnsureLocalUser()
    {
        if (RunCommand("net", $"user {ShareUser}", out _) == 0)
        {
            EmitLog($"本地账号 {ShareUser} 已存在，直接使用（请确保其密码为 {SharePassword}）。");
            return;
        }

        EmitLog($"正在创建本地账号 {ShareUser} ...");
        int code = RunCommand("net", $"user {ShareUser} {SharePassword} /add /passwordchg:no", out string output);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"创建本地账号 {ShareUser} 失败（可能因系统密码策略不允许简单密码）。详情：{output}");
        }

        lock (_sync) _createdUser = true;
        EmitLog($"本地账号 {ShareUser} 已创建。");

        if (GrantFullSystemAccess)
        {
            int gcode = RunCommand("net", $"localgroup administrators {ShareUser} /add", out string gout);
            if (gcode != 0)
                throw new InvalidOperationException($"将 {ShareUser} 加入 Administrators 组失败：{gout}");
            EmitLog($"警告：已将 {ShareUser} 加入 Administrators 组（完全访问，高危！）。");
        }
    }

    private void ShareAllDrives()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
            .OrderBy(d => d.Name)
            .ToList();

        if (drives.Count == 0)
            throw new InvalidOperationException("未发现可共享的本地磁盘（Fixed/Removable）。");

        foreach (var drive in drives)
        {
            string shareName = drive.Name.TrimEnd('\\', ':');
            string path = drive.RootDirectory.FullName; // 例如 "C:\"
            EmitLog($"正在共享 {path} → \\\\{Environment.MachineName}\\{shareName}");
            CreateShare(shareName, path);
        }
    }

    private void CreateShare(string shareName, string path)
    {
        if (ShareExists(shareName))
        {
            EmitLog($"共享 {shareName} 已存在，跳过（停止时也不会删除该共享）。");
            return;
        }

        int code = RunCommand("net",
            $"share {shareName}={path} /GRANT:{ShareUser},FULL /REMARK:LanFileShare", out string output);
        if (code != 0)
            throw new InvalidOperationException($"创建共享 {shareName}（{path}）失败：{output}");

        lock (_sync) _createdShares.Add(new ShareRecord(shareName, path));
        EmitLog($"共享已创建：\\\\{Environment.MachineName}\\{shareName}");
    }

    private static bool ShareExists(string shareName) => RunCommand("net", $"share {shareName}", out _) == 0;

    private static void RemoveShare(string shareName) => RunCommand("net", $"share {shareName} /delete /y", out _);

    private static void RemoveLocalUser() => RunCommand("net", $"user {ShareUser} /delete", out _);

    private void SetStatus(SmbServiceStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void EmitLog(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Log?.Invoke(line);
        Logger.Write(line);
    }

    // ---------- 进程执行 ----------

    private static Encoding OemEncoding
    {
        get
        {
            try { return Encoding.GetEncoding((int)GetOEMCP()); }
            catch { return Encoding.UTF8; }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static int RunCommand(string fileName, string arguments, out string output)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            output = "无法启动进程：" + fileName;
            return -1;
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        output = (stdout + Environment.NewLine + stderr).Trim();
        return process.ExitCode;
    }
}

/// <summary>简单的文件日志，便于排查问题。</summary>
public static class Logger
{
    private static readonly object Lock = new();
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanFileShare");
    private static readonly string LogFile = Path.Combine(LogDir, "LanFileShare.log");

    public static string LogFilePath => LogFile;

    public static void Write(string line)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
