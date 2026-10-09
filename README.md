# TrayWrapperApp (.NET Windows Forms System Tray)

โปรแกรมสำหรับครอบ (Wrap) ไฟล์ **`.bat`**, **`node.js`**, หรือ **`.exe`** ให้ทำงานอยู่เบื้องหลังใน **Windows System Tray (มุมขวาล่าง)** โดยซ่อนหน้าต่างดำ (Command Prompt) และมีเมนูคลิกขวาสั่ง **Restart** หรือ **Exit** ได้อย่างปลอดภัย

---

## ✨ ความสามารถหลัก
- 🚀 **ซ่อนหน้าต่าง Command Prompt (cmd.exe)**: หมดปัญหาหน้าต่างดำลอยเกะกะสายตา
- 🔄 **Restart ได้ทันที**: เมนูคลิกขวาสั่ง `Restart Service` โดยจะทำการ Kill Process Tree (ปิดทั้ง cmd และ node.js ตัวลูกทั้งหมด) ก่อนเริ่มใหม่
- 🛡️ **Auto-Restart (Watchdog)**: หาก Service ดับหรือเกิด Error ระบบจะพยายามรันกลับขึ้นมาใหม่อัตโนมัติ
- ⚙️ **ตั้งค่าผ่าน `config.json`**: เปลี่ยน Path หรือแก้ไขการทำงานได้โดยไม่ต้อง compile โค้ดใหม่
- 📁 **Open Target Folder**: มีเมนูลัดเปิดโฟลเดอร์ของไฟล์เป้าหมายจาก Tray Menu

---

## ⚙️ การตั้งค่าใน `config.json`

เมื่อ build แล้วจะมีไฟล์ `config.json` อยู่ข้างๆ ตัวโปรแกรม:

```json
{
  "TargetPath": "Z:\\Prog\\Library_Release\\AutoStartPrintCenter\\AutoStartDevPrintCenterTimer.bat",
  "Arguments": "",
  "WorkingDirectory": "Z:\\Prog\\Library_Release\\AutoStartPrintCenter",
  "HideWindow": true,
  "AutoRestartOnCrash": true,
  "TrayTooltip": "Print Center Service (Running)",
  "CustomIconPath": ""
}
```

| ค่า | คำอธิบาย |
|---|---|
| `TargetPath` | Path ของไฟล์ `.bat`, `.exe`, หรือ script ที่ต้องการรัน |
| `Arguments` | Parameters หรือ Arguments เพิ่มเติม (ถ้ามี) |
| `WorkingDirectory` | โฟลเดอร์ที่ต้องการให้โปรแกรมรัน (ถ้าเว้นว่างจะอิงจาก Path ของ TargetPath) |
| `HideWindow` | `true` เพื่อซ่อนหน้าต่างดำ / `false` เพื่อเปิดหน้าต่างตามปกติ |
| `AutoRestartOnCrash` | `true` เพื่อให้เปิดใหม่เองถ้า Service หลุด |
| `TrayTooltip` | ข้อความที่จะแสดงเวลาเอาเมาส์ไปชี้ที่ไอคอนมุมขวาล่าง |
| `CustomIconPath` | Path ไฟล์ `.ico` หากต้องการเปลี่ยนไอคอนของ System Tray |

---

## 📦 คำสั่ง Build & Publish สำหรับนำไปใช้งาน

```bash
# Publish เป็น Single-File Executable สำหรับ Windows (x64)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

ไฟล์ที่ได้จะอยู่ในโฟลเดอร์:
`bin/Release/net8.0-windows/win-x64/publish/`

นำไฟล์ `TrayWrapperApp.exe` และ `config.json` ไปวางใช้งานได้ทันทีครับ
