; ---------------------------------------------------------------------------
; Overlyric - per-user NSIS installer.
;
; Build with installer\build-nsis.ps1 (the reproducible entry point) or directly:
;
;   makensis /DAPP_VERSION=0.4.0 "/DPAYLOAD_DIR=<publish dir>" ^
;            "/DOUT_FILE=dist\Overlyric-0.4.0-x64-setup.exe" installer\Overlyric.nsi
;
; Compile-time defines supplied by the build:
;   APP_VERSION   X.Y.Z product version (from src\Overlyric.csproj)
;   PAYLOAD_DIR   folder holding the published payload (Overlyric.exe, *.pdb)
;   OUT_FILE      the setup executable to write
;   APP_ICON      the product icon (default: src\Assets\Overlyric.ico)
;
; Contract, deliberately the same as the MSI (installer\Overlyric.wxs):
;   * per-user, no elevation: %LOCALAPPDATA%\Programs\Overlyric
;   * every published file ships except *.pdb
;   * uninstall keeps %LOCALAPPDATA%\Overlyric (config, lyric cache, versions) unless the
;     uninstaller is run with /REMOVEDATA (or the prompt is answered Yes)
;   * "Start with Windows" is the application's own setting (Settings -> Behaviour), because it
;     writes and removes the HKCU Run value itself; the installer does not add a second owner for it
; ---------------------------------------------------------------------------

Unicode true
ManifestDPIAware true

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "0.0.0"
!endif
!ifndef PAYLOAD_DIR
  !error "PAYLOAD_DIR is required - run installer\build-nsis.ps1"
!endif
!ifndef OUT_FILE
  !error "OUT_FILE is required - run installer\build-nsis.ps1"
!endif
!ifndef APP_ICON
  !define APP_ICON "${__FILEDIR__}\..\src\Assets\Overlyric.ico"
!endif

!define APP_NAME       "Overlyric"
!define APP_PUBLISHER  "Overlyric"
!define APP_EXE        "Overlyric.exe"
!define UNINST_EXE     "Uninstall.exe"
!define APP_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Overlyric"
!define APP_DATA_DIR   "$LOCALAPPDATA\Overlyric"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "${OUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Overlyric"
InstallDirRegKey HKCU "${APP_UNINST_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName"       "${APP_NAME}"
VIAddVersionKey "FileDescription"   "${APP_NAME} Setup"
VIAddVersionKey "FileVersion"       "${APP_VERSION}"
VIAddVersionKey "ProductVersion"    "${APP_VERSION}"
VIAddVersionKey "CompanyName"       "${APP_PUBLISHER}"
VIAddVersionKey "LegalCopyright"    "MIT"

; --------------------------------------------------------------- interface
!define MUI_ICON   "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "${APP_NAME} ${APP_VERSION}"
!define MUI_WELCOMEPAGE_TEXT "This installs ${APP_NAME}, the desktop lyric overlay, for the current user.$\r$\n$\r$\nNo administrator rights are needed and nothing is written outside your profile: the program goes to %LOCALAPPDATA%\Programs\Overlyric.$\r$\n$\r$\nYour settings and lyric cache stay in %LOCALAPPDATA%\Overlyric and are kept when you uninstall."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APP_NAME} now"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; --------------------------------------------------------------- sections
Section "Overlyric application" SEC_APP
  SectionIn RO
  SetShellVarContext current

  ; An upgrade over a running tray instance must not end in FilesInUse or a reboot prompt.
  Call CloseRunningApp

  SetOutPath "$INSTDIR"

  ; The executable is written by name; anything else the publish output grows later is picked up
  ; automatically, minus symbol files and minus a second copy of the exe.
  ; Every published file except the symbol files. Recursive so a future payload with a subfolder
  ; still ships; the build script verifies the executable is in there before this runs.
  File /r /x "*.pdb" "${PAYLOAD_DIR}\*.*"

  WriteUninstaller "$INSTDIR\${UNINST_EXE}"

  WriteRegStr   HKCU "${APP_UNINST_KEY}" "DisplayName"           "${APP_NAME}"
  WriteRegStr   HKCU "${APP_UNINST_KEY}" "DisplayVersion"        "${APP_VERSION}"
  WriteRegStr   HKCU "${APP_UNINST_KEY}" "Publisher"             "${APP_PUBLISHER}"
  WriteRegStr   HKCU "${APP_UNINST_KEY}" "DisplayIcon"           "$INSTDIR\${APP_EXE},0"
  WriteRegStr   HKCU "${APP_UNINST_KEY}" "InstallLocation"       "$INSTDIR"
  WriteRegStr   HKCU "${APP_UNINST_KEY}" "UninstallString"       '"$INSTDIR\${UNINST_EXE}"'
  WriteRegStr   HKCU "${APP_UNINST_KEY}" "QuietUninstallString"  '"$INSTDIR\${UNINST_EXE}" /S'
  WriteRegDWORD HKCU "${APP_UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${APP_UNINST_KEY}" "NoRepair" 1

  ; Apps & Features shows this as the product's size.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${APP_UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "Start Menu shortcut" SEC_STARTMENU
  SetShellVarContext current

  CreateDirectory "$SMPROGRAMS\Overlyric"
  CreateShortCut "$SMPROGRAMS\Overlyric\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section /o "Desktop shortcut" SEC_DESKTOP
  SetShellVarContext current

  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP}       "The application itself. Required."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_STARTMENU} "Adds Overlyric to the Start Menu."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP}   "Adds a shortcut to the desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; --------------------------------------------------------------- uninstall
Section "Uninstall"
  SetShellVarContext current

  Call un.CloseRunningApp

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\${UNINST_EXE}"

  ; Payload extras from a newer build, so an uninstall after an upgrade leaves nothing behind.
  RMDir /r "$INSTDIR"

  RMDir "$LOCALAPPDATA\Programs"

  Delete "$SMPROGRAMS\Overlyric\${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\Overlyric"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  DeleteRegKey HKCU "${APP_UNINST_KEY}"

  ; The settings folder is the listener's data, not the installer's: keep it unless asked.
  StrCpy $R2 "0"
  ${GetParameters} $R0
  ${GetOptions} $R0 "/REMOVEDATA" $R1
  ${If} $R1 == "/REMOVEDATA"
    StrCpy $R2 "1"
  ${ElseIfNot} ${Silent}
    MessageBox MB_YESNO|MB_ICONQUESTION \
      "Also delete your Overlyric settings, lyric cache and downloaded lyric versions?$\r$\n$\r$\nChoose No to keep them for a later reinstall." \
      IDNO done
    StrCpy $R2 "1"
  ${EndIf}

  done:
  ${If} $R2 == "1"
    RMDir /r "${APP_DATA_DIR}"
  ${EndIf}
SectionEnd

; --------------------------------------------------------------- helpers
; The tray process has no window a normal close would reach, so it is asked once, given a moment,
; and then terminated. Same outcome as the MSI's CloseApplication (polite first, terminate after 5 s),
; and no FilesInUse prompt either way.
Function CloseRunningApp
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM "${APP_EXE}"'
  Pop $0
  ${If} $0 == "0"
    Sleep 1500
  ${EndIf}
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${APP_EXE}"'
  Pop $0
FunctionEnd

Function un.CloseRunningApp
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM "${APP_EXE}"'
  Pop $0
  ${If} $0 == "0"
    Sleep 1500
  ${EndIf}
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${APP_EXE}"'
  Pop $0
FunctionEnd

Function .onInit
  SetShellVarContext current
FunctionEnd
