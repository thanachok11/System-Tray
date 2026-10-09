using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TrayWrapperApp
{
    public class AppConfig
    {
        public string TargetPath { get; set; } = @"Z:\Prog\Library_Release\AutoStartPrintCenter\AutoStartDevPrintCenterTimer.bat";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public bool HideWindow { get; set; } = true;
        public bool AutoRestartOnCrash { get; set; } = false; // ปิดเป็น default เพื่อไม่ให้ loop ถ้าไฟล์ .bat exit เร็ว
        public string TrayTooltip { get; set; } = "Print Center Service";
        public string CustomIconPath { get; set; } = "";
    }

    static class Program
    {
        private static Mutex mutex;
        private static NotifyIcon trayIcon;
        private static Process childProcess;
        private static AppConfig config;
        private static bool isManualExit = false;
        private static DateTime processStartTime;
        private static int crashCount = 0;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        [STAThread]
        static void Main()
        {
            // 1. Single Instance Protection (รันได้ทีละ 1 ตัวเท่านั้น)
            const string appGuid = "Global\\TrayWrapperApp_SingleInstance_Guid_987123";
            mutex = new Mutex(true, appGuid, out bool isNewInstance);
            if (!isNewInstance)
            {
                // ถ้ามีตัวเดิมเปิดอยู่แล้ว ให้ปิดตัวใหม่ทันที
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadConfig();
            InitTrayIcon();
            StartTargetProcess();

            Application.Run();

            // Release Mutex
            GC.KeepAlive(mutex);
        }

        private static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                else
                {
                    config = new AppConfig();
                    string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(ConfigPath, json);
                }
            }
            catch
            {
                config = new AppConfig();
            }
        }

        private static void InitTrayIcon()
        {
            Icon icon = SystemIcons.Application;

            if (!string.IsNullOrEmpty(config.CustomIconPath) && File.Exists(config.CustomIconPath))
            {
                try { icon = new Icon(config.CustomIconPath); } catch { }
            }
            else if (File.Exists(config.TargetPath) && Path.GetExtension(config.TargetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try { icon = Icon.ExtractAssociatedIcon(config.TargetPath) ?? SystemIcons.Application; } catch { }
            }

            trayIcon = new NotifyIcon
            {
                Icon = icon,
                Text = config.TrayTooltip.Length > 63 ? config.TrayTooltip.Substring(0, 63) : config.TrayTooltip,
                Visible = true
            };

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            
            var statusItem = new ToolStripMenuItem("Tray Wrapper: Active") { Enabled = false };
            var restartItem = new ToolStripMenuItem("🔄 Restart Target", null, (s, e) => RestartProcess());
            var openFolderItem = new ToolStripMenuItem("📁 Open Folder", null, (s, e) => OpenTargetFolder());
            var exitItem = new ToolStripMenuItem("❌ Exit", null, (s, e) => ExitApplication());

            contextMenu.Items.Add(statusItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(restartItem);
            contextMenu.Items.Add(openFolderItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(exitItem);

            trayIcon.ContextMenuStrip = contextMenu;
        }

        private static void StartTargetProcess()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(config.TargetPath)) return;

                string target = config.TargetPath;
                string args = config.Arguments ?? "";
                string ext = Path.GetExtension(target).ToLower();

                ProcessStartInfo psi = new ProcessStartInfo();

                if (ext == ".bat" || ext == ".cmd")
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/c \"\"{target}\" {args}\"";
                }
                else
                {
                    psi.FileName = target;
                    psi.Arguments = args;
                }

                if (!string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory))
                {
                    psi.WorkingDirectory = config.WorkingDirectory;
                }
                else if (File.Exists(target))
                {
                    psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(target));
                }

                if (config.HideWindow)
                {
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }
                else
                {
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Normal;
                }

                childProcess = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                processStartTime = DateTime.Now;

                childProcess.Exited += (s, e) =>
                {
                    if (isManualExit) return;

                    var runtime = DateTime.Now - processStartTime;

                    // ป้องกันลูปนรก: ถ้ารันไม่ถึง 10 วินาทีแล้วดับ หรือ Exit Code เป็น 0 (ทำงานเสร็จปกติ) จะไม่ restart ซ้ำ
                    if (!config.AutoRestartOnCrash || runtime.TotalSeconds < 10)
                    {
                        return;
                    }

                    crashCount++;
                    if (crashCount <= 3)
                    {
                        Task.Delay(5000).ContinueWith(_ => StartTargetProcess());
                    }
                    else
                    {
                        trayIcon?.ShowBalloonTip(5000, "Tray Wrapper", "Process ดับบ่อยเกินไป จึงหยุด Auto-Restart อัตโนมัติ", ToolTipIcon.Warning);
                    }
                };

                childProcess.Start();
            }
            catch (Exception ex)
            {
                trayIcon?.ShowBalloonTip(4000, "Tray Wrapper Error", $"ไม่สามารถเปิดไฟล์ได้: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private static void RestartProcess()
        {
            crashCount = 0;
            KillTargetProcess();
            StartTargetProcess();
            trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", "สั่ง Restart เรียบร้อยแล้ว", ToolTipIcon.Info);
        }

        private static void KillTargetProcess()
        {
            try
            {
                if (childProcess != null && !childProcess.HasExited)
                {
                    childProcess.Kill(entireProcessTree: true);
                    childProcess.WaitForExit(3000);
                }
            }
            catch { }
        }

        private static void OpenTargetFolder()
        {
            try
            {
                string dir = !string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory)
                    ? config.WorkingDirectory
                    : Path.GetDirectoryName(Path.GetFullPath(config.TargetPath));

                if (Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
            }
            catch { }
        }

        private static void ExitApplication()
        {
            isManualExit = true;
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
            }
            KillTargetProcess();
            Application.Exit();
        }
    }
}
