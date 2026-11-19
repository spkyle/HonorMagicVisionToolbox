@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 姝ｅ湪鍚姩 Magic 瑙嗙晫宸ュ叿绠?涓€閿嵏杞界▼搴忥紙灏嗚姹傜鐞嗗憳鏉冮檺锛?..
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restore_topbar.ps1" -PauseOnExit
