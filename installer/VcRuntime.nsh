; Shared VC++ prerequisite logic. Only the Microsoft bootstrapper is elevated.
!ifndef KOTOSHIRO_VC_RUNTIME_INCLUDED
!define KOTOSHIRO_VC_RUNTIME_INCLUDED
!include "LogicLib.nsh"
!include "WordFunc.nsh"
!ifndef VC_REDIST_VERSION
  !error "VC_REDIST_VERSION is required."
!endif
!ifdef VC_RUNTIME_TEST_MODE
  !if "${APP_IDENTITY}" != "Kotoshiro-Installer-Test"
    !error "VC runtime mocks are forbidden in production installers."
  !endif
  !ifndef VC_RUNTIME_TEST_SCENARIO
    !error "VC_RUNTIME_TEST_SCENARIO is required for mocks."
  !endif
  !define VC_RUNTIME_KNOWN_TEST_SCENARIO 0
  !macro VC_ACCEPT_TEST_SCENARIO NAME
    !if "${VC_RUNTIME_TEST_SCENARIO}" == "${NAME}"
      !undef VC_RUNTIME_KNOWN_TEST_SCENARIO
      !define VC_RUNTIME_KNOWN_TEST_SCENARIO 1
    !endif
  !macroend
  !insertmacro VC_ACCEPT_TEST_SCENARIO "missing-success"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "old-success"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "equal-skip"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "newer-skip"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "missing-dll-success"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "cancelled"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "launch-failed"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "install-failed"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "reboot-required"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "already-installed"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "already-installed-invalid"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "hresult-already-installed"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "hresult-already-installed-invalid"
  !insertmacro VC_ACCEPT_TEST_SCENARIO "success-but-invalid"
  !if ${VC_RUNTIME_KNOWN_TEST_SCENARIO} = 0
    !error "Unknown VC runtime mock scenario."
  !endif
!else
  !ifndef VC_REDIST_FILE
    !error "VC_REDIST_FILE is required for production installers."
  !endif
!endif
!define VC_RUNTIME_KEY "SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"

Var VcRuntimeDetected
Var VcRuntimeInstalledVersion
Var VcRuntimeCandidateInstalled
Var VcRuntimeCandidateVersion
Var VcRuntimeDllsPresent
Var VcRuntimeStatus
Var VcRuntimeNeedsReboot
Var VcRuntimeExitCode
Var VcRuntimeLaunchError
Var VcRuntimeLogPath
Var VcRuntimeMicrosoftLogPath
Var VcRuntimeErrorText
!ifdef VC_RUNTIME_TEST_MODE
Var VcRuntimeMockScenario
!endif
Var VcRuntimeMockRuns
Var VcRuntimeMockProbes

Function VcRuntimeTrace
  Exch $0
  Push $1
  Push $2
  StrCpy $2 0
  ${If} ${Errors}
    StrCpy $2 1
  ${EndIf}
  ${If} $VcRuntimeLogPath != ""
    ClearErrors
    FileOpen $1 "$VcRuntimeLogPath" a
    ${IfNot} ${Errors}
      FileSeek $1 0 END
      FileWriteUTF16LE /BOM $1 "$0$\r$\n"
      FileClose $1
    ${EndIf}
  ${EndIf}
  ${If} $2 = 1
    SetErrors
  ${Else}
    ClearErrors
  ${EndIf}
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

!macro VC_TRACE TEXT
  Push "${TEXT}"
  Call VcRuntimeTrace
!macroend

; Input is a registry view (32 or 64). Read numeric components, avoiding lexical version order.
Function VcRuntimeReadRegistryView
  Exch $0
  Push $1
  Push $2
  Push $3
  Push $4
  Push $5
  StrCpy $VcRuntimeCandidateInstalled 0
  StrCpy $VcRuntimeCandidateVersion ""
!ifdef VC_RUNTIME_TEST_MODE
  ${If} $VcRuntimeMockRuns > 0
  ${AndIf} $VcRuntimeMockScenario != "already-installed-invalid"
  ${AndIf} $VcRuntimeMockScenario != "hresult-already-installed-invalid"
  ${AndIf} $VcRuntimeMockScenario != "success-but-invalid"
    StrCpy $VcRuntimeCandidateInstalled 1
    StrCpy $VcRuntimeCandidateVersion "${VC_REDIST_VERSION}"
  ${ElseIf} $VcRuntimeMockScenario == "equal-skip"
  ${OrIf} $VcRuntimeMockScenario == "missing-dll-success"
    StrCpy $VcRuntimeCandidateInstalled 1
    StrCpy $VcRuntimeCandidateVersion "${VC_REDIST_VERSION}"
  ${ElseIf} $VcRuntimeMockScenario == "newer-skip"
    StrCpy $VcRuntimeCandidateInstalled 1
    ${If} $0 = 64
      StrCpy $VcRuntimeCandidateVersion "14.99.65000.0"
    ${Else}
      StrCpy $VcRuntimeCandidateVersion "14.0.0.0"
    ${EndIf}
  ${ElseIf} $VcRuntimeMockScenario == "old-success"
    StrCpy $VcRuntimeCandidateInstalled 1
    StrCpy $VcRuntimeCandidateVersion "14.0.1.0"
  ${EndIf}
!else
  ${If} $0 = 64
    SetRegView 64
  ${Else}
    SetRegView 32
  ${EndIf}
  ClearErrors
  ReadRegDWORD $1 HKLM "${VC_RUNTIME_KEY}" "Installed"
  IfErrors vc_registry_done
  ${If} $1 <> 1
    Goto vc_registry_done
  ${EndIf}
  ReadRegDWORD $2 HKLM "${VC_RUNTIME_KEY}" "Major"
  IfErrors vc_registry_done
  ReadRegDWORD $3 HKLM "${VC_RUNTIME_KEY}" "Minor"
  IfErrors vc_registry_done
  ReadRegDWORD $4 HKLM "${VC_RUNTIME_KEY}" "Bld"
  IfErrors vc_registry_done
  ReadRegDWORD $5 HKLM "${VC_RUNTIME_KEY}" "Rbld"
  IfErrors vc_registry_done
  ${If} $2 >= 14
  ${AndIf} $3 >= 0
  ${AndIf} $4 >= 0
  ${AndIf} $5 >= 0
    StrCpy $VcRuntimeCandidateInstalled 1
    StrCpy $VcRuntimeCandidateVersion "$2.$3.$4.$5"
  ${EndIf}
  vc_registry_done:
  SetRegView 64
!endif
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
  ClearErrors
FunctionEnd

Function VcRuntimeProbeInstalled
  Push $0
  Push $1
  StrCpy $VcRuntimeDetected 0
  StrCpy $VcRuntimeInstalledVersion ""
  IntOp $VcRuntimeMockProbes $VcRuntimeMockProbes + 1
  Push 64
  Call VcRuntimeReadRegistryView
  ${If} $VcRuntimeCandidateInstalled = 1
    StrCpy $VcRuntimeInstalledVersion $VcRuntimeCandidateVersion
  ${EndIf}
  Push 32
  Call VcRuntimeReadRegistryView
  ${If} $VcRuntimeCandidateInstalled = 1
    ${If} $VcRuntimeInstalledVersion == ""
      StrCpy $VcRuntimeInstalledVersion $VcRuntimeCandidateVersion
    ${Else}
      ${VersionCompare} "$VcRuntimeCandidateVersion" "$VcRuntimeInstalledVersion" $0
      ${If} $0 = 1
        StrCpy $VcRuntimeInstalledVersion $VcRuntimeCandidateVersion
      ${EndIf}
    ${EndIf}
  ${EndIf}
  StrCpy $VcRuntimeDllsPresent 0
!ifdef VC_RUNTIME_TEST_MODE
  ${If} $VcRuntimeMockScenario != "missing-dll-success"
  ${OrIf} $VcRuntimeMockRuns > 0
    StrCpy $VcRuntimeDllsPresent 1
  ${EndIf}
!else
  ; NSIS itself is x86. Sysnative reaches native x64 System32 without changing redirection state.
  IfFileExists "$WINDIR\Sysnative\MSVCP140.dll" 0 vc_probe_done
  IfFileExists "$WINDIR\Sysnative\VCRUNTIME140.dll" 0 vc_probe_done
  IfFileExists "$WINDIR\Sysnative\VCRUNTIME140_1.dll" 0 vc_probe_done
  IfFileExists "$WINDIR\Sysnative\VCOMP140.dll" 0 vc_probe_done
  StrCpy $VcRuntimeDllsPresent 1
!endif
  ${If} $VcRuntimeInstalledVersion != ""
  ${AndIf} $VcRuntimeDllsPresent = 1
    ${VersionCompare} "$VcRuntimeInstalledVersion" "${VC_REDIST_VERSION}" $0
    ${If} $0 <> 2
      StrCpy $VcRuntimeDetected 1
    ${EndIf}
  ${EndIf}
  vc_probe_done:
  !insertmacro VC_TRACE "Probe version=[$VcRuntimeInstalledVersion] nativeDlls=[$VcRuntimeDllsPresent] ready=[$VcRuntimeDetected]"
  Pop $1
  Pop $0
  ClearErrors
FunctionEnd

Function VcRuntimeRunBootstrapper
  StrCpy $VcRuntimeLaunchError 0
  StrCpy $VcRuntimeExitCode -1
!ifdef VC_RUNTIME_TEST_MODE
  IntOp $VcRuntimeMockRuns $VcRuntimeMockRuns + 1
  ${If} $VcRuntimeMockScenario == "cancelled"
    StrCpy $VcRuntimeLaunchError 1223
  ${ElseIf} $VcRuntimeMockScenario == "launch-failed"
    StrCpy $VcRuntimeLaunchError 5
  ${ElseIf} $VcRuntimeMockScenario == "install-failed"
    StrCpy $VcRuntimeExitCode 1603
  ${ElseIf} $VcRuntimeMockScenario == "reboot-required"
    StrCpy $VcRuntimeExitCode 3010
  ${ElseIf} $VcRuntimeMockScenario == "already-installed"
  ${OrIf} $VcRuntimeMockScenario == "already-installed-invalid"
    StrCpy $VcRuntimeExitCode 1638
  ${ElseIf} $VcRuntimeMockScenario == "hresult-already-installed"
  ${OrIf} $VcRuntimeMockScenario == "hresult-already-installed-invalid"
    StrCpy $VcRuntimeExitCode 0x80070666
  ${Else}
    StrCpy $VcRuntimeExitCode 0
  ${EndIf}
!else
  Push $0
  Push $1
  Push $2
  Push $3
  Push $4
  Push $5
  Push $6
  Push $7
  ; Own stable buffers until ShellExecuteEx has returned, then release them on every path.
  StrCpy $0 0
  StrCpy $2 0
  System::Call 'ole32::CoInitializeEx(p 0, i 6) i .r3'
  System::StrAlloc 6
  Pop $4
  System::StrAlloc ${NSIS_MAX_STRLEN}
  Pop $5
  System::StrAlloc ${NSIS_MAX_STRLEN}
  Pop $6
  ${If} $4 = 0
  ${OrIf} $5 = 0
  ${OrIf} $6 = 0
    StrCpy $VcRuntimeLaunchError 8
    Goto vc_runner_cleanup
  ${EndIf}
  System::Call '*$4(&w6 "runas")'
  System::Call '*$5(&w${NSIS_MAX_STRLEN} "$PLUGINSDIR\VC_redist.x64.exe")'
  System::Call '*$6(&w${NSIS_MAX_STRLEN} "/install /quiet /norestart /log $\"$VcRuntimeMicrosoftLogPath$\"")'
  ; x86 SHELLEXECUTEINFO: size 60, hProcess at offset 56. 0x540 requests a handle,
  ; synchronous launch and ordinary-error suppression; Windows still displays its UAC prompt.
  System::Call '*(i 60, i 0x540, p $HWNDPARENT, p r4, p r5, p r6, p 0, i 1, p 0, p 0, p 0, p 0, i 0, p 0, p 0) p .r0'
  ${If} $0 = 0
    StrCpy $VcRuntimeLaunchError 8
    Goto vc_runner_cleanup
  ${EndIf}
  System::Call 'shell32::ShellExecuteExW(p r0) i .r1 ?e'
  Pop $VcRuntimeLaunchError
  ${If} $1 = 0
    Goto vc_runner_cleanup
  ${EndIf}
  StrCpy $VcRuntimeLaunchError 0
  System::Call '*$0(i, i, p, p, p, p, p, i, p, p, p, p, i, p, p .r2)'
  ${If} $2 = 0
    StrCpy $VcRuntimeLaunchError 6
    Goto vc_runner_cleanup
  ${EndIf}
  ; Called only from the NSIS installation worker section, never from .onInit or a UI callback.
  System::Call 'kernel32::WaitForSingleObject(p r2, i 0xffffffff) i .r1 ?e'
  Pop $7
  ${If} $1 <> 0
    StrCpy $VcRuntimeLaunchError $7
    ${If} $VcRuntimeLaunchError = 0
      StrCpy $VcRuntimeLaunchError 6
    ${EndIf}
    Goto vc_runner_cleanup
  ${EndIf}
  System::Call 'kernel32::GetExitCodeProcess(p r2, *i .r7) i .r1 ?e'
  Pop $VcRuntimeLaunchError
  ${If} $1 <> 0
    StrCpy $VcRuntimeExitCode $7
    StrCpy $VcRuntimeLaunchError 0
  ${EndIf}
  vc_runner_cleanup:
  ${If} $2 <> 0
    System::Call 'kernel32::CloseHandle(p r2)'
  ${EndIf}
  System::Free $0
  System::Free $4
  System::Free $5
  System::Free $6
  ${If} $3 = 0
  ${OrIf} $3 = 1
    System::Call 'ole32::CoUninitialize()'
  ${EndIf}
  Pop $7
  Pop $6
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
!endif
FunctionEnd

Function EnsureVcRuntime
  Push $0
  Push $1
  StrCpy $VcRuntimeNeedsReboot 0
  StrCpy $VcRuntimeLogPath ""
  StrCpy $VcRuntimeMicrosoftLogPath ""
  StrCpy $VcRuntimeErrorText ""
  StrCpy $VcRuntimeStatus "checking"
  StrCpy $VcRuntimeMockRuns 0
  StrCpy $VcRuntimeMockProbes 0
!ifdef VC_RUNTIME_TEST_MODE
  ${If} $VcRuntimeMockScenario == ""
    StrCpy $VcRuntimeMockScenario "${VC_RUNTIME_TEST_SCENARIO}"
  ${EndIf}
!endif
  DetailPrint "正在检查 Microsoft Visual C++ x64 运行库……"
  Call VcRuntimeProbeInstalled
  ${If} $VcRuntimeDetected = 1
    DetailPrint "已安装满足要求的运行库 $VcRuntimeInstalledVersion，跳过补装。"
    StrCpy $VcRuntimeStatus "skipped"
    Goto vc_ready
  ${EndIf}
  System::Call 'kernel32::GetCurrentProcessId() i .r0'
  System::Call 'kernel32::GetTickCount() i .r1'
  StrCpy $VcRuntimeLogPath "$TEMP\Kotoshiro-vcredist-$0-$1.log"
  StrCpy $VcRuntimeMicrosoftLogPath "$TEMP\Kotoshiro-vcredist-$0-$1.microsoft.log"
  !insertmacro VC_TRACE "Required=${VC_REDIST_VERSION} detected=[$VcRuntimeInstalledVersion] nativeDlls=[$VcRuntimeDllsPresent]"
  DetailPrint "需要补装 Microsoft Visual C++ x64 ${VC_REDIST_VERSION}。请允许微软安装程序的系统授权提示。"
!ifndef VC_RUNTIME_TEST_MODE
  InitPluginsDir
  ClearErrors
  File "/oname=$PLUGINSDIR\VC_redist.x64.exe" "${VC_REDIST_FILE}"
  ${If} ${Errors}
    StrCpy $VcRuntimeErrorText "无法解压内置的 Microsoft Visual C++ x64 运行库安装包。"
    Goto vc_failed
  ${EndIf}
!endif
  Call VcRuntimeRunBootstrapper
  !insertmacro VC_TRACE "Bootstrapper launchError=[$VcRuntimeLaunchError] exitCode=[$VcRuntimeExitCode] MicrosoftLog=[$VcRuntimeMicrosoftLogPath]"
  ${If} $VcRuntimeLaunchError = 1223
  ${OrIf} $VcRuntimeExitCode = 1602
  ${OrIf} $VcRuntimeExitCode = 0x800704C7
  ${OrIf} $VcRuntimeExitCode = 0x80070642
    StrCpy $VcRuntimeStatus "cancelled"
    StrCpy $VcRuntimeErrorText "您取消了 Microsoft Visual C++ x64 运行库安装或系统授权。言代Kotoshiro尚未安装，请重新运行安装器并允许微软安装程序完成。"
    Goto vc_failed
  ${ElseIf} $VcRuntimeLaunchError <> 0
    StrCpy $VcRuntimeErrorText "无法启动或等待 Microsoft Visual C++ x64 运行库安装程序，系统错误：$VcRuntimeLaunchError。"
    Goto vc_failed
  ${EndIf}
  ; Accept only documented raw exit codes, not arbitrary HRESULTs with a zero low word.
  ${If} $VcRuntimeExitCode = 0
  ${OrIf} $VcRuntimeExitCode = 3010
  ${OrIf} $VcRuntimeExitCode = 0x80070BC2
  ${OrIf} $VcRuntimeExitCode = 1638
  ${OrIf} $VcRuntimeExitCode = 0x80070666
    Call VcRuntimeProbeInstalled
    ${If} $VcRuntimeDetected <> 1
      StrCpy $VcRuntimeErrorText "微软安装程序已经结束，但仍未检测到满足要求的 x64 运行库及语音识别依赖文件。请查看日志，修复运行库后重试。"
      Goto vc_failed
    ${EndIf}
    ${If} $VcRuntimeExitCode = 3010
    ${OrIf} $VcRuntimeExitCode = 0x80070BC2
      SetRebootFlag true
      StrCpy $VcRuntimeNeedsReboot 1
      StrCpy $VcRuntimeStatus "restart-required"
      DetailPrint "运行库安装完成，需要稍后重启电脑。安装器不会自动重启。"
    ${Else}
      StrCpy $VcRuntimeStatus "installed"
      DetailPrint "Microsoft Visual C++ x64 运行库已准备好。"
    ${EndIf}
    !insertmacro VC_TRACE "RESULT status=[$VcRuntimeStatus] ready=[$VcRuntimeDetected]"
    Goto vc_ready
  ${EndIf}
  StrCpy $VcRuntimeErrorText "Microsoft Visual C++ x64 运行库安装失败，返回代码：$VcRuntimeExitCode。请查看日志后重试。"
  vc_failed:
  ${If} $VcRuntimeStatus != "cancelled"
    StrCpy $VcRuntimeStatus "failed"
  ${EndIf}
  !insertmacro VC_TRACE "FAILED status=[$VcRuntimeStatus] reason=[$VcRuntimeErrorText]"
  MessageBox MB_OK|MB_ICONSTOP "$VcRuntimeErrorText$\r$\n$\r$\n诊断日志：$VcRuntimeLogPath$\r$\n微软安装日志：$VcRuntimeMicrosoftLogPath" /SD IDOK
  Pop $1
  Pop $0
  SetErrorLevel 2
  SetErrors
  Return
  vc_ready:
  Pop $1
  Pop $0
  ClearErrors
FunctionEnd
!endif
