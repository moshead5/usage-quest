; Usage Quest 설치 프로그램 (사용자 계정에만 설치, 관리자 권한 불필요)
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!define APPNAME "Usage Quest"
!define APPKEY "UsageQuest"
!define VERSION "2.5.3"
!define EXE "UsageQuest.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPKEY}"

Name "${APPNAME}"
OutFile "out\UsageQuest-Setup-${VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APPKEY}"
InstallDirRegKey HKCU "${UNINSTKEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION}"
VIProductVersion "2.5.3.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${APPNAME} 설치"
VIAddVersionKey "LegalCopyright" "MIT"

!define MUI_ICON "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "지금 Usage Quest 실행"

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Korean"

Function .onInit
  ; .NET Framework 4.5 이상 확인 (Windows 10/11에는 4.8이 기본 포함)
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 378389
    MessageBox MB_YESNO|MB_ICONEXCLAMATION ".NET Framework 4.8이 필요합니다.$\r$\n지금 설치 페이지를 열까요?" IDNO +2
      ExecShell "open" "https://dotnet.microsoft.com/download/dotnet-framework/net48"
    Abort
  ${EndIf}
FunctionEnd

; 앱의 자동 업데이트가 "/S /RUN" 으로 실행: 화면 없이 설치하고 끝나면 앱을 다시 켠다
Function .onInstSuccess
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RUN" $R1
  ${IfNot} ${Errors}
    Exec '"$INSTDIR\${EXE}"'
  ${EndIf}
FunctionEnd

Section "Install"
  ; 실행 중인 새 버전/예전 버전 종료
  nsExec::Exec 'taskkill /F /IM "${EXE}"'
  nsExec::Exec 'taskkill /F /IM "Usage Quest.exe"'
  Sleep 500

  ; 예전 Electron 버전(1.x)이 있으면 조용히 제거
  ${If} ${FileExists} "$LOCALAPPDATA\Programs\Usage Quest\Uninstall Usage Quest.exe"
    DetailPrint "예전 버전(1.x) 제거 중…"
    ExecWait '"$LOCALAPPDATA\Programs\Usage Quest\Uninstall Usage Quest.exe" /S'
    Sleep 1500
    RMDir /r "$LOCALAPPDATA\Programs\Usage Quest"
  ${EndIf}
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "electron.app.Usage Quest"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Usage Quest"

  SetOutPath "$INSTDIR"
  File "out\${EXE}"
  File "fonts\FONT-LICENSES.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS"
  CreateShortcut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXE}"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXE}"

  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "Usage Quest"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" 5200
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /F /IM "${EXE}"'
  Sleep 500
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\FONT-LICENSES.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APPKEY}"
  DeleteRegKey HKCU "${UNINSTKEY}"
  ; 설정(%APPDATA%\UsageQuest)은 남겨 둔다 — 다시 설치하면 그대로 이어짐
SectionEnd
