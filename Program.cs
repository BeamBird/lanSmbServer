using System.Text;

namespace LanFileShare;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // 注册代码页提供程序，以便正确解码 net.exe 等命令的本地化输出（例如中文 GBK）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 单实例保护：避免多次双击 exe 启动多个实例（否则会互相占用文件、重复创建共享）。
        using var mutex = new Mutex(true, "LanFileShare_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("LanFileShare 已在运行，请查看系统托盘图标。", "LanFileShare",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
