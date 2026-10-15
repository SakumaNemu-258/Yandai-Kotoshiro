; Unicode, current-user installer. Build-Installer.ps1 supplies an exact payload manifest.
; The manifest contains only distributable application files: no settings, secrets or models.
Unicode true
RequestExecutionLevel user
ManifestDPIAware true
SetCompressor /SOLID lzma
SetDatablockOptimize on
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "nsDialogs.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "Win\COM.nsh"

!ifndef APP_VERSION
  !error "APP_VERSION is required. Use Build-Installer.ps1."
!endif
!ifndef APP_PRODUCT_VERSION
  !error "APP_PRODUCT_VERSION is required."
!endif
!ifndef PAYLOAD_DIR
  !error "PAYLOAD_DIR is required."
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required."
!endif
!ifndef APP_ICON
  !error "APP_ICON is required."
!endif
!ifndef MANIFEST_INCLUDE
  !error "MANIFEST_INCLUDE is required."
!endif
!ifndef APP_IDENTITY
  !define APP_IDENTITY "Kotoshiro"
!endif
!if "${APP_IDENTITY}" == "Kotoshiro"
  !define APP_NAME "言代Kotoshiro"
!else
  !define APP_NAME "言代Kotoshiro (${APP_IDENTITY})"
!endif
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_IDENTITY}"
!define INSTALL_MARKER ".kotoshiro-install.ini"
!include "${MANIFEST_INCLUDE}"
!include "VcRuntime.nsh"
!include "UpdateInstall.nsh"

Name "${APP_NAME}"
Caption "${APP_NAME} ${APP_VERSION} 安装"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_IDENTITY}"
VIProductVersion "${APP_PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "言代Kotoshiro"
VIAddVersionKey /LANG=2052 "FileDescription" "言代Kotoshiro 安装程序"
VIAddVersionKey /LANG=2052 "CompanyName" "言代Kotoshiro"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "言代Kotoshiro"

!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 ${APP_NAME}"
!define MUI_WELCOMEPAGE_TEXT "此向导将安装 ${APP_NAME} ${APP_VERSION}。$\r$\n$\r$\n接下来可以自行选择安装位置，以及是否创建桌面快捷方式。$\r$\n$\r$\n安装前请关闭正在运行的言代Kotoshiro及其播放器。"
!insertmacro MUI_PAGE_WELCOME
!define MUI_DIRECTORYPAGE_TEXT_TOP "请选择安装文件夹。可以安装到其他磁盘中有写入权限的文件夹。"
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateDestination
!insertmacro MUI_PAGE_DIRECTORY
Page custom ShortcutOptionsPage ShortcutOptionsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Kotoshiro.exe"
!define MUI_FINISHPAGE_RUN_TEXT "运行言代Kotoshiro"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_NOREBOOTSUPPORT
!define MUI_PAGE_CUSTOMFUNCTION_SHOW FinishRuntimeStatus
!define MUI_FINISHPAGE_TEXT "${APP_NAME} 已安装。$\r$\n$\r$\n语音模型可以在软件设置中另行下载和选择存放位置。"
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "卸载 ${APP_NAME} 的程序文件。您自行添加的文件、个人设置和语音模型会保留。请先关闭软件及其播放器。"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Var DesktopChoice
Var DesktopCheckbox
Var OptionsDialog
Var PreviousInstall
Var ValidationMessage
Var InstallMutex
Var DesktopChoiceExplicit
Var DesktopPreferencePath
Var UpdateRequested
Var UpdateMode
Var UpdateConfirmedPath
Var UpdateConfirmedVersion
Var UpdateConfirmedStamp

; Isolated QA identities get a diagnostic log next to their test EXE. Production builds contain
; neither this writer nor its call sites, so normal installations do not create extra files.
!macro DEFINE_TEST_TRACE PREFIX
!if "${APP_IDENTITY}" != "Kotoshiro"
Function ${PREFIX}WriteTestTrace
  Exch $0
  Push $1
  Push $2
  StrCpy $2 0
  ${If} ${Errors}
    StrCpy $2 1
  ${EndIf}
  ClearErrors
  FileOpen $1 "$EXEDIR\installer-test-trace.log" a
  ${IfNot} ${Errors}
    FileSeek $1 0 END
    FileWriteUTF16LE /BOM $1 "$0$\r$\n"
    FileClose $1
  ${EndIf}
  ${If} $2 == 1
    SetErrors
  ${Else}
    ClearErrors
  ${EndIf}
  Pop $2
  Pop $1
  Pop $0
FunctionEnd
!endif
!macroend
!insertmacro DEFINE_TEST_TRACE ""
!insertmacro DEFINE_TEST_TRACE "un."
!macro TEST_TRACE PREFIX TEXT
!if "${APP_IDENTITY}" != "Kotoshiro"
  Push "${TEXT}"
  Call ${PREFIX}WriteTestTrace
!endif
!macroend

Function .onInit
  !insertmacro TEST_TRACE "" "BEGIN onInit INSTDIR=$INSTDIR"
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此安装包需要 64 位 Windows。" /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "此版本需要 Windows 10 或更新的系统。" /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\${APP_IDENTITY}-Installer") p .r0 ?e'
  Pop $1
  StrCpy $InstallMutex $0
  ${If} $1 == 183
    MessageBox MB_OK|MB_ICONSTOP "另一个 ${APP_NAME} 安装或卸载程序正在运行，请先将它关闭。" /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
  ReadRegStr $PreviousInstall HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $INSTDIR == "$LOCALAPPDATA\Programs\${APP_IDENTITY}"
  ${AndIf} $PreviousInstall != ""
    StrCpy $INSTDIR $PreviousInstall
  ${EndIf}
  StrCpy $DesktopChoice 0
  StrCpy $DesktopChoiceExplicit 0
  StrCpy $UpdateRequested 0
  ${GetParameters} $0
  !insertmacro TEST_TRACE "" "PARAMETERS=[$0]"
  ClearErrors
  ${GetOptions} $0 "/DESKTOP=" $1
  ${IfNot} ${Errors}
    !insertmacro TEST_TRACE "" "DESKTOP=[$1]"
    ${If} $1 == "0"
      StrCpy $DesktopChoice 0
      StrCpy $DesktopChoiceExplicit 1
    ${ElseIf} $1 == "1"
      StrCpy $DesktopChoice 1
      StrCpy $DesktopChoiceExplicit 1
    ${Else}
      MessageBox MB_OK|MB_ICONSTOP "DESKTOP 参数必须为 0 或 1。" /SD IDOK
      SetErrorLevel 2
      Quit
    ${EndIf}
  ${EndIf}
  ClearErrors
  ${GetOptions} $0 "/UPDATE=" $1
  ${IfNot} ${Errors}
    ${If} $1 == "1"
      StrCpy $UpdateRequested 1
    ${ElseIf} $1 != "0"
      MessageBox MB_OK|MB_ICONSTOP "UPDATE 参数必须为 0 或 1。" /SD IDOK
      SetErrorLevel 2
      Quit
    ${EndIf}
  ${EndIf}
  ClearErrors
  !insertmacro TEST_TRACE "" "END onInit INSTDIR=$INSTDIR DESKTOP=$DesktopChoice"
FunctionEnd

Function ShortcutOptionsPage
  !insertmacro MUI_HEADER_TEXT "快捷方式" "选择是否在当前用户的桌面上放置快捷方式。"
  nsDialogs::Create 1018
  Pop $OptionsDialog
  ${If} $OptionsDialog == error
    Abort
  ${EndIf}
  ${If} $UpdateMode = 1
    ${NSD_CreateLabel} 0 0 100% 28u "更新安装：$INSTDIR$\r$\n保留现有设置、术语表、密钥、登录信息和模型。"
  ${Else}
    ${NSD_CreateLabel} 0 0 100% 28u "安装位置：$INSTDIR"
  ${EndIf}
  Pop $0
  ${NSD_CreateCheckbox} 0 38u 100% 15u "创建桌面快捷方式"
  Pop $DesktopCheckbox
  ${If} $DesktopChoice == 1
    ${NSD_Check} $DesktopCheckbox
  ${Else}
    ${NSD_Uncheck} $DesktopCheckbox
  ${EndIf}
  ${NSD_CreateLabel} 0 66u 100% 32u "软件也会出现在当前用户的开始菜单中。安装完成后，可以在 Windows 设置中卸载。"
  Pop $0
  nsDialogs::Show
FunctionEnd

Function ShortcutOptionsLeave
  ${NSD_GetState} $DesktopCheckbox $DesktopChoice
  StrCpy $DesktopChoiceExplicit 1
FunctionEnd

Function FinishRuntimeStatus
  ${If} $VcRuntimeNeedsReboot = 1
    ${NSD_SetText} $mui.FinishPage.Text "${APP_NAME} 已安装。$\r$\n$\r$\nMicrosoft Visual C++ x64 运行库需要重启电脑才能完全生效。请保存工作后自行重启，再打开软件；安装器不会自动重启。"
    ${NSD_Uncheck} $mui.FinishPage.Run
    EnableWindow $mui.FinishPage.Run 0
  ${EndIf}
FunctionEnd

; Shared by installation and uninstallation. Reject aliases/junctions before any writes/deletes.
!macro DEFINE_PATH_GUARD PREFIX
Function ${PREFIX}CheckSafePath
  Push $0
  Push $1
  Push $2
  Push $3
  StrCpy $ValidationMessage ""
  System::Call 'shlwapi::PathIsRelativeW(w "$INSTDIR") i .r0'
  ${If} $0 != 0
    StrCpy $ValidationMessage "请选择完整的安装文件夹路径。"
    Goto path_done
  ${EndIf}
  ; NSIS GetFullPathName clears its output before reading the input; using $INSTDIR
  ; for both therefore empties the source. WinAPI with separate input/output also
  ; supports a new folder whose parent directories have not been created yet.
  StrCpy $0 $INSTDIR
  System::Call 'kernel32::GetFullPathNameW(w r0, i ${NSIS_MAX_STRLEN}, w .r1, p 0) i .r2'
  ${If} $2 = 0
  ${OrIf} $2 >= ${NSIS_MAX_STRLEN}
    StrCpy $ValidationMessage "此路径无效，请重新选择安装位置。"
    Goto path_done
  ${EndIf}
  StrCpy $INSTDIR $1
  System::Call 'shlwapi::PathIsRootW(w "$INSTDIR") i .r0'
  ${If} $0 != 0
    StrCpy $ValidationMessage "请创建一个专用文件夹，不要直接选择磁盘或网络共享的根目录。"
    Goto path_done
  ${EndIf}
  StrCpy $0 $INSTDIR 1 -1
  ${If} $0 == "\"
    StrCpy $INSTDIR $INSTDIR -1
  ${EndIf}
  ; GetLongPathName normalizes existing short-name aliases such as C:\WINDOW~1.
  System::Call 'kernel32::GetLongPathNameW(w "$INSTDIR", w .r1, i ${NSIS_MAX_STRLEN}) i .r0'
  ${If} $0 != 0
    StrCpy $INSTDIR $1
  ${EndIf}
  ${If} $INSTDIR == "$PROFILE"
    StrCpy $ValidationMessage "请在用户文件夹中创建一个专用的安装文件夹。"
    Goto path_done
  ${EndIf}
  StrCpy $2 $INSTDIR
  path_ancestors:
    System::Call 'kernel32::GetLongPathNameW(w r2, w .r1, i ${NSIS_MAX_STRLEN}) i .r0'
    ${If} $0 != 0
      StrCpy $2 $1
    ${EndIf}
    ${If} $2 == "$WINDIR"
    ${OrIf} $2 == "$SYSDIR"
      StrCpy $ValidationMessage "请勿将软件安装到 Windows 或系统文件夹中。"
      Goto path_done
    ${EndIf}
    System::Call 'kernel32::GetFileAttributesW(w r2) i .r0'
    ${If} $0 <> -1
      IntOp $3 $0 & 0x400
      ${If} $3 != 0
        StrCpy $ValidationMessage "此路径经过文件夹链接，请选择实际文件夹的完整路径。"
        Goto path_done
      ${EndIf}
    ${EndIf}
    ${GetParent} "$2" $1
    ${If} $1 != ""
    ${AndIf} $1 != $2
      StrCpy $2 $1
      Goto path_ancestors
    ${EndIf}
  path_done:
  Pop $3
  Pop $2
  Pop $1
  Pop $0
  !insertmacro TEST_TRACE "${PREFIX}" "CheckSafePath INSTDIR=[$INSTDIR] ERROR=[$ValidationMessage]"
  ${If} $ValidationMessage != ""
    MessageBox MB_OK|MB_ICONSTOP "$ValidationMessage" /SD IDOK
    SetErrorLevel 2
    SetErrors
  ${Else}
    ClearErrors
  ${EndIf}
FunctionEnd
!macroend
!insertmacro DEFINE_PATH_GUARD ""
!insertmacro DEFINE_PATH_GUARD "un."

Function ValidateDestination
  !insertmacro TEST_TRACE "" "BEGIN ValidateDestination INSTDIR=[$INSTDIR]"
  Call CheckSafePath
  IfErrors 0 +2
    Abort
  StrCpy $UpdateMode 0
  ; Existing application folders require a matching product identity as well as a real version.
  Call ProbeExistingKotoshiro
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ReadINIStr $1 "$INSTDIR\${INSTALL_MARKER}" "Install" "Identity"
  ReadINIStr $2 "$INSTDIR\${INSTALL_MARKER}" "Install" "Location"
  ${If} $0 == $INSTDIR
  ${AndIf} $1 == "${APP_IDENTITY}"
  ${AndIf} $2 == $INSTDIR
  ${AndIf} $ExistingKotoshiroRecognized = 1
    !insertmacro TEST_TRACE "" "DESTINATION registered update candidate version=$ExistingKotoshiroVersion"
    Goto destination_update
  ${EndIf}
  ${If} $ExistingKotoshiroRecognized = 1
    !insertmacro TEST_TRACE "" "DESTINATION portable update candidate version=$ExistingKotoshiroVersion"
    Goto destination_update
  ${EndIf}
  ; New installs accept an empty folder. Never merge into unrelated or portable application folders.
  ClearErrors
  FindFirst $0 $1 "$INSTDIR\*"
  ${IfNot} ${Errors}
    directory_next:
      ${If} $1 == ""
        Goto directory_empty
      ${ElseIf} $1 == "."
        FindNext $0 $1
        Goto directory_next
      ${ElseIf} $1 == ".."
        FindNext $0 $1
        Goto directory_next
      ${EndIf}
      FindClose $0
      MessageBox MB_OK|MB_ICONSTOP "此文件夹已经包含文件，且未识别到言代Kotoshiro的有效程序信息。请选择一个新建或空的专用文件夹，避免覆盖其他程序。更新安装请选原言代Kotoshiro所在的文件夹。" /SD IDOK
      SetErrorLevel 2
      Abort
    directory_empty:
    FindClose $0
  ${EndIf}
  ClearErrors
  !insertmacro TEST_TRACE "" "DESTINATION new/empty accepted"
  Return
  destination_update:
  StrCpy $UpdateMode 1
  ${If} $UpdateConfirmedPath == $INSTDIR
  ${AndIf} $UpdateConfirmedVersion == $ExistingKotoshiroVersion
  ${AndIf} $UpdateConfirmedStamp == $ExistingKotoshiroStamp
    Goto destination_update_confirmed
  ${EndIf}
  ${If} $UpdateRequested = 1
    Goto destination_update_accept
  ${EndIf}
  IfSilent destination_update_silent_refused
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "此文件夹中已安装言代Kotoshiro $ExistingKotoshiroVersion。$\r$\n$\r$\n是否进行更新安装到 ${APP_VERSION}？$\r$\n更新仅替换程序文件，保留设置、术语表、密钥、登录信息和模型。$\r$\n$\r$\n选择“是”更新安装，选择“否”返回并选择其他安装目录。" /SD IDNO IDYES destination_update_accept
  Abort
  destination_update_silent_refused:
  MessageBox MB_OK|MB_ICONSTOP "所选目录已有言代Kotoshiro。静默更新须明确提供 /UPDATE=1；未确认更新，现有文件未改动。" /SD IDOK
  SetErrorLevel 2
  Abort
  destination_update_accept:
  StrCpy $UpdateConfirmedPath $INSTDIR
  StrCpy $UpdateConfirmedVersion $ExistingKotoshiroVersion
  StrCpy $UpdateConfirmedStamp $ExistingKotoshiroStamp
  destination_update_confirmed:
  Call PreserveUpdateDesktopPreference
  !insertmacro KOTOSHIRO_CHECK_PAYLOAD_WRITABLE ""
  ClearErrors
  !insertmacro TEST_TRACE "" "DESTINATION update accepted path=$INSTDIR version=$ExistingKotoshiroVersion"
FunctionEnd

Function PreserveUpdateDesktopPreference
  Push $0
  Push $1
  ${If} $DesktopChoiceExplicit = 1
  ${OrIf} $DesktopPreferencePath == $INSTDIR
    Goto preserve_desktop_done
  ${EndIf}
  StrCpy $DesktopPreferencePath $INSTDIR
  ClearErrors
  ReadRegDWORD $0 HKCU "${UNINSTALL_KEY}" "DesktopShortcut"
  ${IfNot} ${Errors}
    ${If} $0 = 0
    ${OrIf} $0 = 1
      StrCpy $DesktopChoice $0
      Goto preserve_desktop_done
    ${EndIf}
  ${EndIf}
  StrCpy $DesktopChoice 0
  Push "$DESKTOP\${APP_NAME}.lnk"
  Call GetShortcutTarget
  Pop $1
  ${If} $1 == "$INSTDIR\Kotoshiro.exe"
  ${OrIf} $1 == "$PreviousInstall\Kotoshiro.exe"
    StrCpy $DesktopChoice 1
  ${EndIf}
  preserve_desktop_done:
  Pop $1
  Pop $0
  ClearErrors
FunctionEnd

!macro DEFINE_FILE_GUARD PREFIX
Function ${PREFIX}CheckPayloadFileWritable
  Exch $0
  Push $1
  Push $2
  Push $3
  StrCpy $3 $0
  ; A symlink in a payload subdirectory must not redirect installation outside its selected root.
  file_ancestors:
    System::Call 'kernel32::GetFileAttributesW(w r3) i .r1'
    ${If} $1 <> -1
      IntOp $2 $1 & 0x400
      ${If} $2 != 0
        Goto file_blocked
      ${EndIf}
    ${EndIf}
    ${If} $3 == $INSTDIR
      Goto file_check
    ${EndIf}
    ${GetParent} "$3" $2
    ${If} $2 == ""
    ${OrIf} $2 == $3
      Goto file_blocked
    ${EndIf}
    StrCpy $3 $2
    Goto file_ancestors
  file_check:
    IfFileExists "$0" 0 file_ok
    ; OPEN_EXISTING + GENERIC_WRITE checks locks/access without changing a single byte.
    System::Call 'kernel32::CreateFileW(w r0, i 0x40000000, i 3, p 0, i 3, i 0x80, p 0) p .r1'
    ${If} $1 = -1
      Goto file_blocked
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r1)'
  file_ok:
    ClearErrors
    Goto file_done
  file_blocked:
    !insertmacro TEST_TRACE "${PREFIX}" "FILE BLOCKED path=[$0] value1=[$1] value2=[$2] ancestor=[$3]"
    MessageBox MB_OK|MB_ICONSTOP "无法更新或删除以下程序文件：$\r$\n$0$\r$\n$\r$\n请先关闭言代Kotoshiro及其播放器，并确认此文件夹可写入且不包含文件链接，然后重试。" /SD IDOK
    SetErrorLevel 2
    SetErrors
  file_done:
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro DEFINE_FILE_GUARD ""
!insertmacro DEFINE_FILE_GUARD "un."

; Read .lnk targets via Windows' built-in COM interfaces; no optional NSIS plug-in is needed.
!macro DEFINE_SHORTCUT_READER PREFIX
Function ${PREFIX}GetShortcutTarget
  Exch $0
  Push $1
  Push $2
  Push $3
  Push $4
  StrCpy $4 ""
  System::Call 'ole32::CoInitializeEx(p 0, i 2) i .r3'
  Push $3
  !insertmacro ComHlpr_CreateInProcInstance ${CLSID_ShellLink} ${IID_IShellLink} r1 ".r2"
  ${If} $2 == 0
    ${IUnknown::QueryInterface} $1 '("${IID_IPersistFile}", .r2).r3'
    ${If} $3 == 0
      ${IPersistFile::Load} $2 '("$0", 0).r3'
      ${If} $3 == 0
        ${IShellLink::GetPath} $1 '(.r4, ${NSIS_MAX_STRLEN}, 0, 4).r3'
        ${If} $3 != 0
          StrCpy $4 ""
        ${EndIf}
      ${EndIf}
      ${IUnknown::Release} $2 ""
    ${EndIf}
    ${IUnknown::Release} $1 ""
  ${EndIf}
  Pop $3
  ${If} $3 == 0
  ${OrIf} $3 == 1
    System::Call 'ole32::CoUninitialize()'
  ${EndIf}
  StrCpy $0 $4
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Exch $0
FunctionEnd
!macroend
!insertmacro DEFINE_SHORTCUT_READER ""
!insertmacro DEFINE_SHORTCUT_READER "un."

Section "程序文件" SEC_APPLICATION
  !insertmacro TEST_TRACE "" "BEGIN InstallSection"
  Call ValidateDestination
  !insertmacro KOTOSHIRO_CHECK_PAYLOAD_WRITABLE ""
  !insertmacro TEST_TRACE "" "InstallSection writable checks passed"
  Call EnsureVcRuntime
  IfErrors 0 +2
    Abort
  ClearErrors
  CreateDirectory "$INSTDIR"
  IfErrors install_write_failed
  GetTempFileName $0 "$INSTDIR"
  IfErrors install_write_failed
  Delete "$0"
  !insertmacro KOTOSHIRO_INSTALL_PAYLOAD
  !insertmacro TEST_TRACE "" "InstallSection payload extracted"
  SetOutPath "$INSTDIR"
  ClearErrors
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors install_write_failed
  WriteINIStr "$INSTDIR\${INSTALL_MARKER}" "Install" "Identity" "${APP_IDENTITY}"
  WriteINIStr "$INSTDIR\${INSTALL_MARKER}" "Install" "Location" "$INSTDIR"
  WriteINIStr "$INSTDIR\${INSTALL_MARKER}" "Install" "Version" "${APP_VERSION}"
  IfErrors install_write_failed

  ; Desktop links are optional; never overwrite a same-name shortcut belonging to somebody else.
  IfFileExists "$DESKTOP\${APP_NAME}.lnk" 0 desktop_missing
  Push "$DESKTOP\${APP_NAME}.lnk"
  Call GetShortcutTarget
  Pop $0
  ${If} $0 == "$INSTDIR\Kotoshiro.exe"
  ${OrIf} $0 == "$PreviousInstall\Kotoshiro.exe"
    ${If} $DesktopChoice == 0
      Delete "$DESKTOP\${APP_NAME}.lnk"
      Goto desktop_done
    ${EndIf}
    Goto desktop_create
  ${EndIf}
  ${If} $DesktopChoice == 1
    MessageBox MB_OK|MB_ICONINFORMATION "桌面已有同名快捷方式，安装程序已为您保留它。可从开始菜单或安装文件夹打开软件。" /SD IDOK
  ${EndIf}
  StrCpy $DesktopChoice 0
  Goto desktop_done
  desktop_missing:
  ${If} $DesktopChoice == 0
    Goto desktop_done
  ${EndIf}
  desktop_create:
    ClearErrors
    CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\Kotoshiro.exe" "" "$INSTDIR\AppIcon.White.ico" 0
    ${If} ${Errors}
      StrCpy $DesktopChoice 0
      MessageBox MB_OK|MB_ICONINFORMATION "程序已安装，但桌面快捷方式未能创建。可从安装文件夹打开 Kotoshiro.exe。" /SD IDOK
    ${EndIf}
  desktop_done:

  ; This application gets its own named Start menu folder; unrelated shortcuts are not overwritten.
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  IfFileExists "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" 0 menu_create
  Push "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Call GetShortcutTarget
  Pop $0
  ${If} $0 != "$INSTDIR\Kotoshiro.exe"
  ${AndIf} $0 != "$PreviousInstall\Kotoshiro.exe"
    Goto menu_done
  ${EndIf}
  menu_create:
    CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\Kotoshiro.exe" "" "$INSTDIR\AppIcon.White.ico" 0
  menu_done:
  ClearErrors
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "言代Kotoshiro"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\AppIcon.White.ico,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "DesktopShortcut" $DesktopChoice
  IfErrors install_write_failed
  SetErrorLevel 0
  !insertmacro TEST_TRACE "" "InstallSection complete"
  Goto install_done
  install_write_failed:
    MessageBox MB_OK|MB_ICONSTOP "安装未完成：无法写入安装目录或登记安装信息。请确认目录权限和剩余空间后重试。" /SD IDOK
    SetErrorLevel 2
    Abort
  install_done:
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\${APP_IDENTITY}-Installer") p .r0 ?e'
  Pop $1
  StrCpy $InstallMutex $0
  ${If} $1 == 183
    MessageBox MB_OK|MB_ICONSTOP "另一个安装或卸载程序正在运行，请先将它关闭。" /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
  Call un.CheckSafePath
  IfErrors 0 +2
    Quit
  ReadINIStr $0 "$INSTDIR\${INSTALL_MARKER}" "Install" "Identity"
  ReadINIStr $1 "$INSTDIR\${INSTALL_MARKER}" "Install" "Location"
  ${If} $0 != "${APP_IDENTITY}"
  ${OrIf} $1 != $INSTDIR
    MessageBox MB_OK|MB_ICONSTOP "无法确认此文件夹的安装信息。为保留您的文件，卸载已停止。请从原安装位置运行卸载程序。" /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
  !insertmacro KOTOSHIRO_CHECK_PAYLOAD_WRITABLE "un."
FunctionEnd

Section "Uninstall"
  Call un.CheckSafePath
  IfErrors 0 +2
    Abort
  !insertmacro KOTOSHIRO_CHECK_PAYLOAD_WRITABLE "un."
  !insertmacro KOTOSHIRO_REMOVE_PAYLOAD
  Delete "$INSTDIR\${INSTALL_MARKER}"
  Delete "$INSTDIR\Uninstall.exe"
  ; Remove shared registration only if it still points to this exact installation.
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ${EndIf}
  Push "$DESKTOP\${APP_NAME}.lnk"
  Call un.GetShortcutTarget
  Pop $0
  ${If} $0 == "$INSTDIR\Kotoshiro.exe"
    Delete "$DESKTOP\${APP_NAME}.lnk"
  ${EndIf}
  Push "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Call un.GetShortcutTarget
  Pop $0
  ${If} $0 == "$INSTDIR\Kotoshiro.exe"
    Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  ${EndIf}
  RMDir "$SMPROGRAMS\${APP_NAME}"
  ; Deliberately non-recursive: user-added files and model folders survive uninstallation.
  RMDir "$INSTDIR"
  SetErrorLevel 0
SectionEnd
