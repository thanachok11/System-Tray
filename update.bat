@echo off
chcp 65001 > nul
title อัปเดต TrayWrapperApp จาก Git

echo ========================================================
echo        TrayWrapperApp - Git Auto Updater
echo ========================================================
echo.
echo กำลังดาวน์โหลดไฟล์เวอร์ชันล่าสุดจาก GitHub Releases...
echo.

curl -L -f -s -o "TrayWrapperApp.new.exe" "https://github.com/thanachok11/System-Tray/releases/latest/download/TrayWrapperApp.exe"

if not exist "TrayWrapperApp.new.exe" (
    echo [ผิดพลาด] ไม่สามารถดาวน์โหลดไฟล์ได้ กรุณาตรวจสอบการเชื่อมต่ออินเทอร์เน็ต
    pause
    exit /b 1
)

echo ดาวน์โหลดเรียบร้อย กำลังปิดโปรแกรมเดิม...
taskkill /f /im TrayWrapperApp.exe > nul 2>&1
timeout /t 1 /nobreak > nul

echo กำลังติดตั้งเวอร์ชันใหม่...
move /y "TrayWrapperApp.new.exe" "TrayWrapperApp.exe" > nul

echo อัปเดตเสร็จสมบูรณ์! กำลังเปิดโปรแกรม...
start "" "TrayWrapperApp.exe"

echo เรียบร้อยแล้ว หน้าต่างนี้จะปิดตัวใน 3 วินาที...
timeout /t 3 > nul
