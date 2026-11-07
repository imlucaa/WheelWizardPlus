@echo off
echo Building for Windows...
call build-win.bat

echo Building for Linux...
call build-linux.bat

echo Building for Linux ARM64...
call build-linux-arm64.bat

echo All builds completed!
pause
