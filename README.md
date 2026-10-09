# TrayWrapperApp (.NET Windows Forms System Tray)

โปรแกรมสำหรับครอบ (Wrap) ไฟล์ **`.bat`**, **`node.js`**, หรือ **`.exe`** ให้ทำงานอยู่เบื้องหลังใน **Windows System Tray (มุมขวาล่าง)** โดยซ่อนหน้าต่างดำ (Command Prompt) และมีเมนูคลิกขวาสั่ง **Restart** หรือ **Exit (พร้อมระบบรหัสผ่านป้องกันการปิด)**

---

## ✨ ความสามารถหลัก
- 🚀 **ซ่อนหน้าต่าง Command Prompt (cmd.exe)**: ซ่อนหน้าต่างดำ ไม่ให้มี cmd เด้งลอยกวนใจ
- 🔒 **ระบบรหัสผ่านก่อนปิด (Exit Password)**: ป้องกันไม่ให้ผู้ใช้หรือใครเผลอกดปิด Service ได้ง่ายๆ
- 🔄 **Restart ได้ทันที**: เมนูคลิกขวาสั่ง `Restart Service` โดยจะทำการ Kill Process Tree (ปิดทั้ง cmd และ node.js ตัวลูกทั้งหมด) ก่อนเริ่มใหม่
- 🛡️ **Single-Instance Protection**: ป้องกันการเปิดโปรแกรมซ้อนกันหลายตัว
- ⚙️ **ตั้งค่าผ่าน `config.json`**: เปลี่ยน Path หรือแก้ไขการทำงานได้โดยไม่ต้อง compile โค้ดใหม่

---

## ⚙️ ตัวอย่างการตั้งค่าใน `config.json`

> **คำแนะนำ:** ในไฟล์ JSON ทุกครั้งที่พิมพ์ Path โฟลเดอร์ของ Windows ต้องใช้ `\\` (เบิ้ล 2 ตัว) เสมอครับ

```json
{
  "TargetPath": "Z:\\Prog\\Library_Release\\AutoStartPrintCenter\\AutoStartDevPrintCenterTimer.bat",
  "Arguments": "",
  "WorkingDirectory": "Z:\\Prog\\Library_Release\\AutoStartPrintCenter",
  "HideWindow": true,
  "AutoRestartOnCrash": false,
  "TrayTooltip": "Print Center Service",
  "CustomIconPath": "",
  "ExitPassword": "your_password_here"
}
```

| ค่า | คำอธิบาย | ตัวอย่าง |
|---|---|---|
| `TargetPath` | Path ของไฟล์ที่ต้องการรัน | `"Z:\\Prog\\Library_Release\\AutoStartPrintCenter\\AutoStartDevPrintCenterTimer.bat"` |
| `Arguments` | Parameters หรือ Arguments เพิ่มเติม | `""` |
| `WorkingDirectory` | โฟลเดอร์ที่ต้องการให้โปรแกรมรัน | `"Z:\\Prog\\Library_Release\\AutoStartPrintCenter"` |
| `HideWindow` | ซ่อนหน้าต่างดำ | `true` |
| `AutoRestartOnCrash` | เปิดใหม่เองถ้า Crash (สำหรับ .bat แนะนำ `false`) | `false` |
| `TrayTooltip` | ข้อความ Tooltip เวลาชี้เมาส์ที่ไอคอน | `"Print Center Service"` |
| `CustomIconPath` | Path ไฟล์ไอคอน `.ico` กำหนดเอง | `""` |
| `ExitPassword` | **รหัสผ่านสำหรับสั่งปิด** (ถ้าไม่ใส่รหัสให้เว้นว่าง `""`) | `"1234"` |

---

## 📦 ดาวน์โหลดและติดตั้ง
1. เข้าไปที่ **Releases**: [https://github.com/thanachok11/System-Tray/releases](https://github.com/thanachok11/System-Tray/releases)
2. ดาวน์โหลดไฟล์ `TrayWrapperApp-Windows-x64.zip`
3. แตกไฟล์ออก และแก้ไข `config.json`
4. ดับเบิ้ลคลิก `TrayWrapperApp.exe` ใช้งานได้ทันที
