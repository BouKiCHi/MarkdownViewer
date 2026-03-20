Unicode true
RequestExecutionLevel admin

!include "MUI2.nsh"
!include "x64.nsh"

!define WEBVIEW2_CLIENT_GUID "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

!ifndef APP_NAME
  !error "APP_NAME is required"
!endif

!ifndef APP_VERSION
  !error "APP_VERSION is required"
!endif

!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR is required"
!endif

!ifndef OUTPUT_DIR
  !error "OUTPUT_DIR is required"
!endif

!ifndef OUTPUT_NAME
  !error "OUTPUT_NAME is required"
!endif

!ifndef APP_EXE
  !error "APP_EXE is required"
!endif

Name "${APP_NAME}"
OutFile "${OUTPUT_DIR}\${OUTPUT_NAME}"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
InstallDirRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "InstallLocation"
Icon "..\MarkdownViewer\icon.ico"
UninstallIcon "..\MarkdownViewer\icon.ico"

!define MUI_ABORTWARNING

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Japanese"

!ifdef WEBVIEW2_BOOTSTRAPPER
Function IsWebView2RuntimeInstalled
  Push $0
  Push $1

  StrCpy $0 "0"

  ${If} ${RunningX64}
    SetRegView 32
  ${Else}
    SetRegView 32
  ${EndIf}

  ReadRegStr $1 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_CLIENT_GUID}" "pv"
  ${If} $1 != ""
  ${AndIf} $1 != "0.0.0.0"
    StrCpy $0 "1"
  ${EndIf}

  ${If} $0 != "1"
    ReadRegStr $1 HKCU "Software\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_CLIENT_GUID}" "pv"
    ${If} $1 != ""
    ${AndIf} $1 != "0.0.0.0"
      StrCpy $0 "1"
    ${EndIf}
  ${EndIf}

  Pop $1
  Exch $0
FunctionEnd

Function EnsureWebView2Runtime
  Call IsWebView2RuntimeInstalled
  Pop $0
  StrCmp $0 "1" done

  DetailPrint "Installing Microsoft Edge WebView2 Runtime..."
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=MicrosoftEdgeWebView2Setup.exe "${WEBVIEW2_BOOTSTRAPPER}"
  ExecWait '"$PLUGINSDIR\MicrosoftEdgeWebView2Setup.exe" /silent /install' $1
  IntCmp $1 0 done install_failed install_failed

  install_failed:
    MessageBox MB_ICONSTOP|MB_OK "Microsoft Edge WebView2 Runtime installation failed. (ExitCode=$1)"
    Abort

  done:
FunctionEnd
!endif

Section "Install"
  Call EnsureWebView2Runtime
  SetRegView 64
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*.*"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "Publisher" "${APP_NAME}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "QuietUninstallString" "$INSTDIR\Uninstall.exe /S"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetRegView 64
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
SectionEnd
