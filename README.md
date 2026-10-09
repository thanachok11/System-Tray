# TrayWrapperApp (.NET Windows Forms)

แอปพลิเคชันสำหรับรัน Background Process/Service พร้อมไอคอน System Tray และเมนูสั่ง Restart

## โครงสร้างโปรเจกต์
- [TrayWrapperApp.csproj](file:///Users/thanachok/.gemini/antigravity-ide/scratch/TrayWrapperApp/TrayWrapperApp.csproj): ไฟล์ Project กำหนด target เป็น `net8.0-windows` และเปิดใช้งาน Windows Forms (`<UseWindowsForms>true</UseWindowsForms>`)
- [Program.cs](file:///Users/thanachok/.gemini/antigravity-ide/scratch/TrayWrapperApp/Program.cs): โค้ดหลักในการสร้าง Tray Icon และควบคุม Process

## การตั้งค่าก่อนใช้งาน
แก้ไข Path ไฟล์เป้าหมายใน [Program.cs](file:///Users/thanachok/.gemini/antigravity-ide/scratch/TrayWrapperApp/Program.cs#L45-L48):
```csharp
FileName = @"C:\Path\To\YourTargetApp.exe",
WorkingDirectory = @"C:\Path\To\",
```

## คำสั่งสำหรับ Build & Publish (Windows)
```bash
# Build
dotnet build

# Publish แบบ Single-file Executable สำหรับ Windows x64 (ไม่ต้องลง .NET Runtime แยก)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
