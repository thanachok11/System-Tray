using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TrayWrapperApp
{
    static class Program
    {
        private static NotifyIcon trayIcon;
        private static Process childProcess;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 1. รันไฟล์ exe หลักขึ้นมาเบื้องหลัง
            StartTargetProcess();

            // 2. สร้าง System Tray Icon
            trayIcon = new NotifyIcon()
            {
                // ใส่ Icon ของโปรแกรม หรือใช้ Icon ระบบชั่วคราว
                Icon = SystemIcons.Application,
                Text = "My Background Service",
                Visible = true
            };

            // สร้าง Context Menu (คลิกขวาที่ Tray Icon)
            ContextMenuStrip contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Restart Service", null, (s, e) => RestartTargetProcess());
            
            // ถ้าไม่ต้องการให้มีปุ่มปิด ก็ไม่ต้องใส่เมนู Exit หรือใส่แบบมีรหัสผ่านป้องกัน
            // contextMenu.Items.Add("Exit", null, (s, e) => Application.Exit());

            trayIcon.ContextMenuStrip = contextMenu;

            // รัน Application Loop โดยไม่ต้องแสดงหน้าต่าง Form
            Application.Run();
        }

        private static void StartTargetProcess()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = @"C:\Path\To\YourTargetApp.exe", // กำหนด Path ไฟล์ exe ของคุณ
                    WorkingDirectory = @"C:\Path\To\",
                    UseShellExecute = true,
                    // WindowStyle = ProcessWindowStyle.Hidden // ถ้าต้องการสั่งซ่อนหน้าต่าง exe นั้นไปเลย
                };

                childProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start process: {ex.Message}");
            }
        }

        private static void RestartTargetProcess()
        {
            try
            {
                if (childProcess != null && !childProcess.HasExited)
                {
                    childProcess.Kill();
                }
                StartTargetProcess();
            }
            catch { }
        }
    }
}
