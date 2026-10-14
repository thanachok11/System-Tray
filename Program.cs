using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TrayWrapperApp
{
    public class ProgramItemConfig
    {
        public string Name { get; set; } = "Service";
        public string TargetPath { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public bool HideWindow { get; set; } = true;
        public bool AutoRestartOnCrash { get; set; } = false;
        public string CustomConfigPath { get; set; } = "";
        public string CustomIconPath { get; set; } = "";
    }

    public class AppConfig
    {
        public string TrayTooltip { get; set; } = "DxRunner Print Center Service";
        public string CustomIconPath { get; set; } = "";
        public string ExitPassword { get; set; } = "1234";
        public List<ProgramItemConfig> Programs { get; set; } = new List<ProgramItemConfig>();

        // Backward compatibility
        public string? TargetPath { get; set; }
        public string? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }
        public bool? HideWindow { get; set; }
        public bool? AutoRestartOnCrash { get; set; }
    }

    public class ManagedApp
    {
        public ProgramItemConfig Config { get; set; }
        public Process? ChildProcess { get; set; }
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;
        public bool IsWindowShown { get; set; } = false;
        public int SpawnedPid { get; set; } = 0;
        public DateTime StartTime { get; set; }
        public int CrashCount { get; set; } = 0;

        public ManagedApp(ProgramItemConfig config)
        {
            Config = config;
        }
    }

    static class Program
    {
        #region Win32 API
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

        [DllImport("user32.dll")]
        private static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        private const uint SC_CLOSE = 0xF060;
        private const uint MF_BYCOMMAND = 0x00000000;
        private const uint MF_GRAYED = 0x00000001;
        private const uint MF_DISABLED = 0x00000002;
        #endregion

        private static Mutex? mutex;
        private static NotifyIcon? trayIcon;
        private static AppConfig config = new AppConfig();
        private static List<ManagedApp> managedApps = new List<ManagedApp>();
        private static bool isManualExit = false;

        private static string GetConfigPath()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string? dir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        return Path.Combine(dir, "config.json");
                    }
                }
            }
            catch { }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        }

        private static readonly string ConfigPath = GetConfigPath();

        [STAThread]
        static void Main(string[] args)
        {
            const string appGuid = "Global\\TrayWrapperApp_SingleInstance_Guid_987123";
            mutex = new Mutex(true, appGuid, out bool isNewInstance);
            if (!isNewInstance)
            {
                return;
            }

            EnsureSystemPath();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadConfig();

            // รองรับ Drag & Drop ไฟล์มาหย่อนทับ
            if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                string droppedFile = CleanPath(args[0]);
                if (File.Exists(droppedFile))
                {
                    if (managedApps.Count == 0)
                    {
                        var newProg = new ProgramItemConfig
                        {
                            Name = Path.GetFileNameWithoutExtension(droppedFile),
                            TargetPath = droppedFile,
                            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(droppedFile)) ?? "",
                            HideWindow = true
                        };
                        config.Programs.Add(newProg);
                        managedApps.Add(new ManagedApp(newProg));
                    }
                    else
                    {
                        managedApps[0].Config.TargetPath = droppedFile;
                        managedApps[0].Config.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(droppedFile)) ?? "";
                    }
                    SaveConfig();
                }
            }

            InitTrayIcon();

            if (managedApps.Count == 0)
            {
                PromptAddNewProgram();
            }
            else
            {
                StartAllProcesses();
            }

            Application.Run();

            GC.KeepAlive(mutex);
        }

        private static void EnsureSystemPath()
        {
            try
            {
                string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string wbem = Path.Combine(sys32, "wbem");
                string powershell = Path.Combine(sys32, "WindowsPowerShell", "v1.0");

                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                var paths = new List<string>(currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries));

                if (!paths.Contains(sys32, StringComparer.OrdinalIgnoreCase)) paths.Insert(0, sys32);
                if (!paths.Contains(winDir, StringComparer.OrdinalIgnoreCase)) paths.Insert(1, winDir);
                if (!paths.Contains(wbem, StringComparer.OrdinalIgnoreCase)) paths.Add(wbem);
                if (!paths.Contains(powershell, StringComparer.OrdinalIgnoreCase)) paths.Add(powershell);

                string newPath = string.Join(";", paths);
                Environment.SetEnvironmentVariable("PATH", newPath);
            }
            catch { }
        }

        private static void LoadConfig()
        {
            if (!File.Exists(ConfigPath))
            {
                CreateDefaultConfig();
                return;
            }

            string rawText = File.ReadAllText(ConfigPath);
            string sanitizedJson = SanitizeJsonBackslashes(rawText);

            try
            {
                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };

                var parsed = JsonSerializer.Deserialize<AppConfig>(sanitizedJson, options);
                if (parsed != null)
                {
                    config = parsed;
                }
            }
            catch
            {
                config = ParseConfigManually(rawText);
            }

            if (config.Programs == null || config.Programs.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(config.TargetPath))
                {
                    config.Programs = new List<ProgramItemConfig>
                    {
                        new ProgramItemConfig
                        {
                            Name = Path.GetFileNameWithoutExtension(config.TargetPath) ?? "Service",
                            TargetPath = CleanPath(config.TargetPath),
                            Arguments = config.Arguments ?? "",
                            WorkingDirectory = CleanPath(config.WorkingDirectory ?? ""),
                            HideWindow = config.HideWindow ?? true,
                            AutoRestartOnCrash = config.AutoRestartOnCrash ?? false
                        }
                    };
                }
            }

            managedApps.Clear();
            foreach (var p in config.Programs)
            {
                p.TargetPath = CleanPath(p.TargetPath);
                p.WorkingDirectory = CleanPath(p.WorkingDirectory);

                if (string.IsNullOrWhiteSpace(p.WorkingDirectory) && !string.IsNullOrWhiteSpace(p.TargetPath))
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(Path.GetFullPath(p.TargetPath));
                        if (!string.IsNullOrWhiteSpace(dir)) p.WorkingDirectory = dir;
                    }
                    catch { }
                }

                managedApps.Add(new ManagedApp(p));
            }
        }

        private static string SanitizeJsonBackslashes(string json)
        {
            try
            {
                return Regex.Replace(json, @"""((?:\\.|[^""\\])*)""", m =>
                {
                    string content = m.Groups[1].Value;
                    string fixedContent = Regex.Replace(content, @"(?<!\\)\\(?![""\\/bfnrtu])", @"\\");
                    return $"\"{fixedContent}\"";
                });
            }
            catch
            {
                return json;
            }
        }

        private static AppConfig ParseConfigManually(string text)
        {
            var cfg = new AppConfig();

            try
            {
                var progMatches = Regex.Matches(text, @"\{[^{}]*""TargetPath""[^{}]*\}", RegexOptions.Singleline);
                foreach (Match m in progMatches)
                {
                    string block = m.Value;
                    string name = ExtractRegexValue(block, "Name") ?? "Service";
                    string target = ExtractRegexValue(block, "TargetPath") ?? "";
                    string args = ExtractRegexValue(block, "Arguments") ?? "";
                    string workDir = ExtractRegexValue(block, "WorkingDirectory") ?? "";
                    string customCfg = ExtractRegexValue(block, "CustomConfigPath") ?? "";
                    string customIcon = ExtractRegexValue(block, "CustomIconPath") ?? "";
                    bool hide = ExtractRegexBool(block, "HideWindow", true);
                    bool autoRestart = ExtractRegexBool(block, "AutoRestartOnCrash", false);

                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        cfg.Programs.Add(new ProgramItemConfig
                        {
                            Name = name,
                            TargetPath = target,
                            Arguments = args,
                            WorkingDirectory = workDir,
                            HideWindow = hide,
                            AutoRestartOnCrash = autoRestart,
                            CustomConfigPath = customCfg,
                            CustomIconPath = customIcon
                        });
                    }
                }

                cfg.TrayTooltip = ExtractRegexValue(text, "TrayTooltip") ?? "DxRunner Print Center Service";
                cfg.CustomIconPath = ExtractRegexValue(text, "CustomIconPath") ?? "";
                cfg.ExitPassword = ExtractRegexValue(text, "ExitPassword") ?? "1234";
            }
            catch { }

            return cfg;
        }

        private static string? ExtractRegexValue(string block, string key)
        {
            var m = Regex.Match(block, $@"""{key}""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool ExtractRegexBool(string block, string key, bool defaultVal)
        {
            var m = Regex.Match(block, $@"""{key}""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
            if (m.Success && bool.TryParse(m.Groups[1].Value, out bool b)) return b;
            return defaultVal;
        }

        private static void CreateDefaultConfig()
        {
            config = new AppConfig
            {
                TrayTooltip = "DxRunner Print Center Service",
                CustomIconPath = "",
                ExitPassword = "1234",
                Programs = new List<ProgramItemConfig>
                {
                    new ProgramItemConfig
                    {
                        Name = "DxRunner Print Center",
                        TargetPath = @"C:\AppSoft\bin\DxRunner.exe",
                        Arguments = "\"HIDE_BAT_AUTO_ComputerName=COM1,PrintOrder=39|40,TimerSec=15\"",
                        WorkingDirectory = @"C:\AppSoft\bin",
                        HideWindow = true,
                        AutoRestartOnCrash = false,
                        CustomConfigPath = "",
                        CustomIconPath = ""
                    }
                }
            };

            SaveConfig();
        }

        private static string CleanPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Trim().Trim('\"', '\'');
        }

        private static void SaveConfig()
        {
            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        /// <summary>
        /// สกัดอ่านไฟล์ Logo/Icon จริงๆ ของโปรแกรม (.exe, .lnk หรือรูป .png, .jpg, .ico)
        /// </summary>
        private static Icon GetAppIcon(string? targetPath, string? customIconPath)
        {
            // 1. ตรวจสอบ CustomIconPath ก่อน (รองรับทั้ง .ico และไฟล์รูป .png, .jpg, .bmp)
            if (!string.IsNullOrEmpty(customIconPath) && File.Exists(customIconPath))
            {
                var custom = LoadImageAsIcon(customIconPath);
                if (custom != null) return custom;
            }

            if (string.IsNullOrWhiteSpace(targetPath)) return SystemIcons.Application;

            string resolvedPath = targetPath;
            if (File.Exists(targetPath))
            {
                string ext = Path.GetExtension(targetPath).ToLower();
                if (ext == ".bat" || ext == ".cmd")
                {
                    try
                    {
                        string content = File.ReadAllText(targetPath);
                        var match = Regex.Match(content, @"([a-zA-Z]:\\[^\r\n""]+\.exe)", RegexOptions.IgnoreCase);
                        if (match.Success && File.Exists(match.Groups[1].Value))
                        {
                            resolvedPath = match.Groups[1].Value;
                        }
                        else
                        {
                            string? dir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                string exeSame = Path.Combine(dir, Path.GetFileNameWithoutExtension(targetPath) + ".exe");
                                if (File.Exists(exeSame)) resolvedPath = exeSame;
                            }
                        }
                    }
                    catch { }
                }
            }

            if (File.Exists(resolvedPath))
            {
                // ตรวจสอบว่าเป็นรูปภาพหรือไม่
                string resExt = Path.GetExtension(resolvedPath).ToLower();
                if (resExt == ".png" || resExt == ".jpg" || resExt == ".jpeg" || resExt == ".bmp" || resExt == ".ico")
                {
                    var imgIcon = LoadImageAsIcon(resolvedPath);
                    if (imgIcon != null) return imgIcon;
                }

                // สกัด Icon จาก Windows Shell API
                try
                {
                    SHFILEINFO shinfo = new SHFILEINFO();
                    IntPtr hImg = SHGetFileInfo(resolvedPath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_SMALLICON);
                    if (hImg != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                    {
                        Icon icon = (Icon)Icon.FromHandle(shinfo.hIcon).Clone();
                        DestroyIcon(shinfo.hIcon);
                        return icon;
                    }
                }
                catch { }

                try
                {
                    Icon? assoc = Icon.ExtractAssociatedIcon(resolvedPath);
                    if (assoc != null) return assoc;
                }
                catch { }
            }

            return SystemIcons.Application;
        }

        /// <summary>
        /// แปลงไฟล์รูปภาพ เช่น .png, .jpg, .ico ให้เป็น Icon 32x32 อย่างคมชัด
        /// </summary>
        private static Icon? LoadImageAsIcon(string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath).ToLower();
                if (ext == ".ico")
                {
                    return new Icon(filePath);
                }

                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
                {
                    using (Bitmap original = new Bitmap(filePath))
                    using (Bitmap resized = new Bitmap(original, new Size(32, 32)))
                    {
                        IntPtr hIcon = resized.GetHicon();
                        Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
                        DestroyIcon(hIcon);
                        return icon;
                    }
                }
            }
            catch { }

            return null;
        }

        private static void InitTrayIcon()
        {
            Icon icon = SystemIcons.Application;

            if (!string.IsNullOrEmpty(config.CustomIconPath) && File.Exists(config.CustomIconPath))
            {
                icon = LoadImageAsIcon(config.CustomIconPath) ?? SystemIcons.Application;
            }
            else if (managedApps.Count > 0)
            {
                icon = GetAppIcon(managedApps[0].Config.TargetPath, managedApps[0].Config.CustomIconPath);
            }

            string tooltip = config.TrayTooltip;
            if (managedApps.Count == 1 && !string.IsNullOrWhiteSpace(managedApps[0].Config.Name))
            {
                tooltip = $"{managedApps[0].Config.Name} (Running)";
            }

            trayIcon = new NotifyIcon
            {
                Icon = icon,
                Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip,
                Visible = true
            };

            trayIcon.DoubleClick += (s, e) => ToggleAllTargetWindows();

            UpdateContextMenu();
        }

        private static void UpdateContextMenu()
        {
            if (trayIcon == null) return;

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            
            var toggleAllItem = new ToolStripMenuItem("👁️ แสดง / ซ่อน ทุกหน้าต่าง (Show/Hide All)", null, (s, e) => ToggleAllTargetWindows());
            var restartAllItem = new ToolStripMenuItem("🔄 Restart ทุกโปรแกรม (Restart All)", null, (s, e) => RestartAllProcesses());
            
            contextMenu.Items.Add(toggleAllItem);
            contextMenu.Items.Add(restartAllItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            for (int i = 0; i < managedApps.Count; i++)
            {
                var app = managedApps[i];
                string displayName = string.IsNullOrWhiteSpace(app.Config.Name) ? $"โปรแกรม {i + 1}" : app.Config.Name;

                Icon appIcon = GetAppIcon(app.Config.TargetPath, app.Config.CustomIconPath);
                var subMenu = new ToolStripMenuItem($"⚙️ {displayName}", appIcon.ToBitmap());
                
                string targetFileName = Path.GetFileName(app.Config.TargetPath);
                var targetInfoItem = new ToolStripMenuItem($"📄 {targetFileName}") { Enabled = false };
                subMenu.DropDownItems.Add(targetInfoItem);
                subMenu.DropDownItems.Add(new ToolStripSeparator());

                subMenu.DropDownItems.Add(new ToolStripMenuItem("👁️ แสดง / ซ่อน หน้าต่าง", null, (s, e) => ToggleSingleAppWindow(app)));
                subMenu.DropDownItems.Add(new ToolStripMenuItem("🔄 Restart โปรแกรมนี้", null, (s, e) => RestartSingleProcess(app)));
                subMenu.DropDownItems.Add(new ToolStripMenuItem("📝 เปิดไฟล์ Config โปรแกรมนี้...", null, (s, e) => OpenAppConfigFile(app)));
                subMenu.DropDownItems.Add(new ToolStripMenuItem("🎯 เปลี่ยนไฟล์โปรแกรมนี้...", null, (s, e) => PromptChangeAppTarget(app)));
                subMenu.DropDownItems.Add(new ToolStripMenuItem("📁 เปิดโฟลเดอร์", null, (s, e) => OpenSingleFolder(app)));

                contextMenu.Items.Add(subMenu);
            }

            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(new ToolStripMenuItem("➕ เพิ่มโปรแกรมใหม่...", null, (s, e) => PromptAddNewProgram()));
            contextMenu.Items.Add(new ToolStripMenuItem("📝 เปิดแก้ไข config.json (TrayWrapper)", null, (s, e) => OpenConfigFile()));
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(new ToolStripMenuItem("❌ ปิดโปรแกรมทั้งหมด (Exit)", null, (s, e) => HandleExitRequest()));

            trayIcon.ContextMenuStrip = contextMenu;
        }

        private static void OpenAppConfigFile(ManagedApp app)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(app.Config.CustomConfigPath) && File.Exists(app.Config.CustomConfigPath))
                {
                    Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{app.Config.CustomConfigPath}\"", UseShellExecute = true });
                    return;
                }

                string dir = !string.IsNullOrWhiteSpace(app.Config.WorkingDirectory) && Directory.Exists(app.Config.WorkingDirectory)
                    ? app.Config.WorkingDirectory
                    : Path.GetDirectoryName(Path.GetFullPath(app.Config.TargetPath)) ?? "";

                string targetFileName = Path.GetFileNameWithoutExtension(app.Config.TargetPath);

                if (Directory.Exists(dir))
                {
                    string[] candidates = new[]
                    {
                        $"{targetFileName}.exe.config",
                        $"{targetFileName}.config",
                        $"{targetFileName}.json",
                        $"{targetFileName}.ini",
                        "appsettings.json",
                        "config.json",
                        "config.ini",
                        "settings.ini"
                    };

                    foreach (var cand in candidates)
                    {
                        string p = Path.Combine(dir, cand);
                        if (File.Exists(p))
                        {
                            app.Config.CustomConfigPath = p;
                            SaveConfig();
                            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{p}\"", UseShellExecute = true });
                            return;
                        }
                    }

                    var otherConfigs = Directory.GetFiles(dir, "*.config")
                        .Concat(Directory.GetFiles(dir, "*.ini"))
                        .Concat(Directory.GetFiles(dir, "*.json"))
                        .ToArray();

                    if (otherConfigs.Length > 0)
                    {
                        string first = otherConfigs[0];
                        app.Config.CustomConfigPath = first;
                        SaveConfig();
                        Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{first}\"", UseShellExecute = true });
                        return;
                    }
                }

                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = $"เลือกไฟล์ Config สำหรับ {app.Config.Name}";
                    ofd.Filter = "Configuration files (*.config;*.ini;*.json;*.xml;*.txt)|*.config;*.ini;*.json;*.xml;*.txt|All files (*.*)|*.*";
                    if (Directory.Exists(dir)) ofd.InitialDirectory = dir;

                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        app.Config.CustomConfigPath = ofd.FileName;
                        SaveConfig();
                        Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{ofd.FileName}\"", UseShellExecute = true });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ไม่สามารถเปิดไฟล์ config ได้: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void PromptChangeAppTarget(ManagedApp app)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = $"เลือกไฟล์ใหม่สำหรับ: {app.Config.Name}";
                ofd.Filter = "Executable & Scripts (*.bat;*.cmd;*.exe;*.lnk)|*.bat;*.cmd;*.exe;*.lnk|All Files (*.*)|*.*";
                
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    app.Config.TargetPath = ofd.FileName;
                    app.Config.WorkingDirectory = Path.GetDirectoryName(ofd.FileName) ?? "";
                    SaveConfig();

                    UpdateContextMenu();
                    RestartSingleProcess(app);
                    trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", $"เปลี่ยนไฟล์สำหรับ {app.Config.Name} สำเร็จแล้ว", ToolTipIcon.Info);
                }
            }
        }

        private static void PromptAddNewProgram()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "เลือกไฟล์โปรแกรม (.bat / .exe / .lnk / script) ที่ต้องการเพิ่ม";
                ofd.Filter = "Executable & Scripts (*.bat;*.cmd;*.exe;*.lnk)|*.bat;*.cmd;*.exe;*.lnk|All Files (*.*)|*.*";
                
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    var newConfig = new ProgramItemConfig
                    {
                        Name = Path.GetFileNameWithoutExtension(ofd.FileName),
                        TargetPath = ofd.FileName,
                        WorkingDirectory = Path.GetDirectoryName(ofd.FileName) ?? "",
                        HideWindow = true,
                        AutoRestartOnCrash = false
                    };

                    config.Programs.Add(newConfig);
                    SaveConfig();

                    var newApp = new ManagedApp(newConfig);
                    managedApps.Add(newApp);

                    UpdateContextMenu();
                    StartSingleProcess(newApp);
                    trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", $"เพิ่มโปรแกรม {newConfig.Name} เรียบร้อยแล้ว", ToolTipIcon.Info);
                }
            }
        }

        private static void OpenConfigFile()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{ConfigPath}\"", UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show($"ไม่พบไฟล์ config.json ที่:\n{ConfigPath}", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch { }
        }

        private static void StartAllProcesses()
        {
            foreach (var app in managedApps)
            {
                StartSingleProcess(app);
            }
        }

        private static void StartSingleProcess(ManagedApp app)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(app.Config.TargetPath))
                {
                    return;
                }

                if (!File.Exists(app.Config.TargetPath))
                {
                    trayIcon?.ShowBalloonTip(4000, "Tray Wrapper Warning", $"ไม่พบไฟล์เป้าหมาย:\n{app.Config.TargetPath}", ToolTipIcon.Warning);
                    return;
                }

                string target = app.Config.TargetPath;
                string args = app.Config.Arguments ?? "";
                string ext = Path.GetExtension(target).ToLower();

                ProcessStartInfo psi = new ProcessStartInfo();

                if (ext == ".bat" || ext == ".cmd")
                {
                    string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string cmdExePath = Path.Combine(sys32, "cmd.exe");
                    psi.FileName = File.Exists(cmdExePath) ? cmdExePath : "cmd.exe";
                    psi.Arguments = $"/k \"\"{target}\" {args}\"";
                }
                else if (ext == ".lnk")
                {
                    psi.FileName = target;
                    psi.Arguments = args;
                    psi.UseShellExecute = true;
                }
                else
                {
                    psi.FileName = target;
                    psi.Arguments = args;
                }

                if (!string.IsNullOrWhiteSpace(app.Config.WorkingDirectory) && Directory.Exists(app.Config.WorkingDirectory))
                {
                    psi.WorkingDirectory = app.Config.WorkingDirectory;
                }
                else if (File.Exists(target))
                {
                    psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(target)) ?? "";
                }

                if (app.Config.HideWindow && ext != ".lnk")
                {
                    psi.UseShellExecute = false;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }
                else
                {
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Normal;
                }

                app.ChildProcess = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                app.StartTime = DateTime.Now;

                app.ChildProcess.Exited += (s, e) =>
                {
                    if (isManualExit) return;

                    var runtime = DateTime.Now - app.StartTime;

                    if (!app.Config.AutoRestartOnCrash || runtime.TotalSeconds < 10)
                    {
                        return;
                    }

                    app.CrashCount++;
                    if (app.CrashCount <= 3)
                    {
                        Task.Delay(5000).ContinueWith(_ => StartSingleProcess(app));
                    }
                    else
                    {
                        trayIcon?.ShowBalloonTip(5000, "Tray Wrapper", $"โปรแกรม {app.Config.Name} ดับบ่อยเกินไป จึงระงับ Auto-Restart", ToolTipIcon.Warning);
                    }
                };

                app.ChildProcess.Start();
                app.SpawnedPid = app.ChildProcess.Id;

                Task.Run(async () =>
                {
                    app.WindowHandle = IntPtr.Zero;

                    for (int i = 0; i < 25; i++)
                    {
                        await Task.Delay(100);
                        app.WindowHandle = FindProcessWindow(app);
                        if (app.WindowHandle != IntPtr.Zero)
                        {
                            DisableCloseButton(app.WindowHandle);

                            if (app.Config.HideWindow)
                            {
                                ShowWindow(app.WindowHandle, SW_HIDE);
                                app.IsWindowShown = false;
                            }
                            else
                            {
                                app.IsWindowShown = true;
                            }
                            break;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                trayIcon?.ShowBalloonTip(4000, "Tray Wrapper Error", $"ไม่สามารถเปิด {app.Config.Name} ได้: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private static void DisableCloseButton(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            try
            {
                IntPtr hMenu = GetSystemMenu(hWnd, false);
                if (hMenu != IntPtr.Zero)
                {
                    EnableMenuItem(hMenu, SC_CLOSE, MF_BYCOMMAND | MF_DISABLED | MF_GRAYED);
                    RemoveMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);
                }
            }
            catch { }
        }

        private static IntPtr FindProcessWindow(ManagedApp app)
        {
            if (app.ChildProcess != null)
            {
                try
                {
                    app.ChildProcess.Refresh();
                    if (app.ChildProcess.MainWindowHandle != IntPtr.Zero)
                    {
                        return app.ChildProcess.MainWindowHandle;
                    }
                }
                catch { }
            }

            IntPtr foundHwnd = IntPtr.Zero;
            string targetFileName = Path.GetFileNameWithoutExtension(app.Config.TargetPath);

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint procId);

                if (app.SpawnedPid > 0 && procId == app.SpawnedPid)
                {
                    foundHwnd = hWnd;
                    return false;
                }

                int length = GetWindowTextLength(hWnd);
                if (length > 0)
                {
                    StringBuilder sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();

                    if (!string.IsNullOrEmpty(targetFileName) && title.IndexOf(targetFileName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                }

                return true;
            }, IntPtr.Zero);

            return foundHwnd;
        }

        private static void ToggleAllTargetWindows()
        {
            bool anyVisible = false;

            foreach (var app in managedApps)
            {
                if (app.WindowHandle == IntPtr.Zero) app.WindowHandle = FindProcessWindow(app);
                if (app.WindowHandle != IntPtr.Zero && IsWindowVisible(app.WindowHandle))
                {
                    anyVisible = true;
                    break;
                }
            }

            foreach (var app in managedApps)
            {
                if (app.WindowHandle == IntPtr.Zero) app.WindowHandle = FindProcessWindow(app);
                if (app.WindowHandle == IntPtr.Zero) continue;

                DisableCloseButton(app.WindowHandle);

                if (anyVisible)
                {
                    ShowWindow(app.WindowHandle, SW_HIDE);
                    app.IsWindowShown = false;
                }
                else
                {
                    ShowWindow(app.WindowHandle, SW_RESTORE);
                    ShowWindow(app.WindowHandle, SW_SHOW);
                    SetForegroundWindow(app.WindowHandle);
                    app.IsWindowShown = true;
                }
            }
        }

        private static void ToggleSingleAppWindow(ManagedApp app)
        {
            try
            {
                if (app.WindowHandle == IntPtr.Zero)
                {
                    app.WindowHandle = FindProcessWindow(app);
                }

                if (app.WindowHandle == IntPtr.Zero)
                {
                    trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", $"ไม่พบหน้าต่างของ {app.Config.Name}", ToolTipIcon.Info);
                    return;
                }

                DisableCloseButton(app.WindowHandle);

                if (app.IsWindowShown && IsWindowVisible(app.WindowHandle))
                {
                    ShowWindow(app.WindowHandle, SW_HIDE);
                    app.IsWindowShown = false;
                }
                else
                {
                    ShowWindow(app.WindowHandle, SW_RESTORE);
                    ShowWindow(app.WindowHandle, SW_SHOW);
                    SetForegroundWindow(app.WindowHandle);
                    app.IsWindowShown = true;
                }
            }
            catch (Exception ex)
            {
                trayIcon?.ShowBalloonTip(3000, "Error", $"ไม่สามารถสลับหน้าต่างได้: {ex.Message}", ToolTipIcon.Warning);
            }
        }

        private static void RestartAllProcesses()
        {
            foreach (var app in managedApps)
            {
                RestartSingleProcess(app);
            }
            trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", "สั่ง Restart ทุกโปรแกรมเรียบร้อยแล้ว", ToolTipIcon.Info);
        }

        private static void RestartSingleProcess(ManagedApp app)
        {
            app.CrashCount = 0;
            KillSingleProcess(app);
            StartSingleProcess(app);
        }

        private static void KillSingleProcess(ManagedApp app)
        {
            try
            {
                app.WindowHandle = IntPtr.Zero;
                if (app.ChildProcess != null && !app.ChildProcess.HasExited)
                {
                    app.ChildProcess.Kill(entireProcessTree: true);
                    app.ChildProcess.WaitForExit(3000);
                }
            }
            catch { }
        }

        private static void OpenSingleFolder(ManagedApp app)
        {
            try
            {
                string dir = !string.IsNullOrWhiteSpace(app.Config.WorkingDirectory) && Directory.Exists(app.Config.WorkingDirectory)
                    ? app.Config.WorkingDirectory
                    : Path.GetDirectoryName(Path.GetFullPath(app.Config.TargetPath)) ?? "";

                if (Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
            }
            catch { }
        }

        private static void HandleExitRequest()
        {
            if (!string.IsNullOrEmpty(config.ExitPassword))
            {
                string? input = PromptPasswordDialog("กรุณากรอกรหัสผ่านเพื่อปิดโปรแกรมทั้งหมด:", "ยืนยันการปิด Services");
                if (input == null) return;

                if (input != config.ExitPassword)
                {
                    MessageBox.Show("รหัสผ่านไม่ถูกต้อง!", "ปฏิเสธการเข้าถึง", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            ExitApplication();
        }

        private static string? PromptPasswordDialog(string text, string caption)
        {
            using (Form prompt = new Form())
            {
                prompt.Width = 360;
                prompt.Height = 175;
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.Text = caption;
                prompt.StartPosition = FormStartPosition.CenterScreen;
                prompt.MaximizeBox = false;
                prompt.MinimizeBox = false;
                prompt.TopMost = true;

                Label textLabel = new Label() { Left = 20, Top = 15, Text = text, AutoSize = true };
                TextBox textBox = new TextBox() { Left = 20, Top = 45, Width = 300, PasswordChar = '●' };
                
                Button confirmation = new Button() { Text = "ตกลง", Left = 155, Width = 80, Top = 85, DialogResult = DialogResult.OK };
                Button cancel = new Button() { Text = "ยกเลิก", Left = 240, Width = 80, Top = 85, DialogResult = DialogResult.Cancel };

                confirmation.Click += (sender, e) => { prompt.Close(); };
                cancel.Click += (sender, e) => { prompt.Close(); };

                prompt.Controls.Add(textBox);
                prompt.Controls.Add(confirmation);
                prompt.Controls.Add(cancel);
                prompt.Controls.Add(textLabel);
                prompt.AcceptButton = confirmation;
                prompt.CancelButton = cancel;

                return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : null;
            }
        }

        private static void ExitApplication()
        {
            isManualExit = true;
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
            }

            foreach (var app in managedApps)
            {
                KillSingleProcess(app);
            }

            Application.Exit();
        }
    }
}
