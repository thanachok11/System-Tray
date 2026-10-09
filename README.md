# TrayWrapperApp (.NET Windows Forms Multi-Program Tray Wrapper)

โปรแกรมสำหรับครอบ (Wrap) หลาย Service เช่น **`.bat`**, **`node.js`**, หรือ **`.exe`** พร้อมกันในตัวเดียว ให้ทำงานอยู่เบื้องหลังใน **Windows System Tray (มุมขวาล่าง)** โดยซ่อนหน้าต่างดำ (Command Prompt) ปิดปุ่มกากบาท `[X]` และมีเมนูควบคุมเปิด/ปิด/Restart ได้ทั้งแบบรวมและแยกรายตัว

---

## ✨ ความสามารถหลัก
- 👥 **รองรับหลายโปรแกรมพร้อมกัน (Multi-Programs)**: รัน 2 โปรแกรมขึ้นไปได้ใน Tray Wrapper เดียว
- 🚀 **ซ่อนหน้าต่างดำ Command Prompt**: ไม่เด้งขึ้นมากวนใจ
- 🔒 **ปิดการทำงานปุ่มกากบาท `[X]` บนหน้าต่างดำ**: ป้องกันคนเผลอมือลั่นปิดหน้าต่าง Service
- 🔐 **ระบบรหัสผ่านก่อนปิด (Exit Password)**: ถามรหัสผ่านก่อนจะยอมให้ปิด Service ทั้งหมด
- 🖱️ **ดับเบิ้ลคลิกเพื่อ สลับเปิด/ซ่อน ทุกหน้าต่างพร้อมกัน**: เรียกดู log ได้ในคลิกเดียว
- 🔄 **Restart All หรือ Restart แยกรายโปรแกรม**: สั่ง kill process tree อย่างปลอดภัย

---

## ⚙️ ตัวอย่างการตั้งค่า `config.json` สำหรับ 2 โปรแกรม

```json
{
  "TrayTooltip": "Print Center & Background Services",
  "CustomIconPath": "",
  "ExitPassword": "1234",
  "Programs": [
    {
      "Name": "Print Center Timer",
      "TargetPath": "C:\\appsoft\\bin\\AutoStartPrintCenter\\AutoStartDevPrintCenterTimer.bat",
      "Arguments": "",
      "WorkingDirectory": "C:\\appsoft\\bin\\AutoStartPrintCenter",
      "HideWindow": true,
      "AutoRestartOnCrash": false
    },
    {
      "Name": "Dev Cmd Timer",
      "TargetPath": "C:\\appsoft\\bin\\AutoStartDevCmdTimer.bat",
      "Arguments": "",
      "WorkingDirectory": "C:\\appsoft\\bin",
      "HideWindow": true,
      "AutoRestartOnCrash": false
    }
  ]
}
```

---

## 📦 ดาวน์โหลดและติดตั้ง
1. เข้าไปที่ **Releases**: [https://github.com/thanachok11/System-Tray/releases](https://github.com/thanachok11/System-Tray/releases)
2. ดาวน์โหลดไฟล์ `TrayWrapperApp-Windows-x64.zip`
3. แตกไฟล์ออก และแก้ไข `config.json`
4. ดับเบิ้ลคลิก `TrayWrapperApp.exe` ใช้งานได้ทันที
