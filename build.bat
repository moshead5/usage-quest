@echo off
rem Windows 기본 포함 C# 컴파일러로 빌드 (.NET Framework 4.x)
cd /d %~dp0
if not exist out mkdir out
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:app.ico /out:out\UsageQuest.exe ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
 /resource:fonts\Galmuri11.ttf,Galmuri11.ttf /resource:fonts\Galmuri11-Bold.ttf,Galmuri11-Bold.ttf ^
 /resource:fonts\Galmuri9.ttf,Galmuri9.ttf /resource:fonts\PressStart2P.ttf,PressStart2P.ttf src\*.cs
if errorlevel 1 pause & exit /b 1
echo 완료: out\UsageQuest.exe
pause
