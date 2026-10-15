; Read an existing program's version resources without executing it. A matching filename alone
; never authorizes merging into a nonempty directory.
!ifndef KOTOSHIRO_UPDATE_INSTALL_INCLUDED
!define KOTOSHIRO_UPDATE_INSTALL_INCLUDED
Var ExistingKotoshiroRecognized
Var ExistingKotoshiroVersion
Var ExistingKotoshiroIdentity
Var ExistingKotoshiroStamp

!macro READ_KOTOSHIRO_VERSION_STRING KEY
  System::Call 'version::VerQueryValueW(p r0, w "$6\${KEY}", *p .r2, *i .r3) i .r1'
  ${If} $1 = 0
  ${OrIf} $2 = 0
  ${OrIf} $3 <= 1
  ${OrIf} $3 > ${NSIS_MAX_STRLEN}
    Goto existing_probe_done
  ${EndIf}
  System::Call 'kernel32::lstrcpynW(w .r9, p r2, i ${NSIS_MAX_STRLEN}) p'
!macroend

Function ProbeExistingKotoshiro
  Push $0
  Push $1
  Push $2
  Push $3
  Push $4
  Push $5
  Push $6
  Push $7
  Push $8
  Push $9
  StrCpy $ExistingKotoshiroRecognized 0
  StrCpy $ExistingKotoshiroVersion ""
  StrCpy $ExistingKotoshiroIdentity ""
  StrCpy $ExistingKotoshiroStamp ""
  StrCpy $0 0
  IfFileExists "$INSTDIR\Kotoshiro.exe" 0 existing_probe_done
  ; Reject a directory or linked file before reading metadata from it.
  System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR\Kotoshiro.exe") i .r1'
  IntOp $2 $1 & 0x410
  ${If} $1 = -1
  ${OrIf} $2 <> 0
    Goto existing_probe_done
  ${EndIf}
  ClearErrors
  GetDLLVersion "$INSTDIR\Kotoshiro.exe" $7 $8
  IfErrors existing_probe_done
  ${If} $7 = 0
  ${AndIf} $8 = 0
    Goto existing_probe_done
  ${EndIf}
  System::Call 'version::GetFileVersionInfoSizeW(w "$INSTDIR\Kotoshiro.exe", *i .r2) i .r1'
  ${If} $1 <= 0
  ${OrIf} $1 > 1048576
    Goto existing_probe_done
  ${EndIf}
  System::Alloc $1
  Pop $0
  ${If} $0 = 0
    Goto existing_probe_done
  ${EndIf}
  System::Call 'version::GetFileVersionInfoW(w "$INSTDIR\Kotoshiro.exe", i 0, i r1, p r0) i .r2'
  ${If} $2 = 0
    Goto existing_probe_done
  ${EndIf}
  System::Call 'version::VerQueryValueW(p r0, w "\VarFileInfo\Translation", *p .r2, *i .r3) i .r1'
  ${If} $1 = 0
  ${OrIf} $2 = 0
  ${OrIf} $3 < 4
    Goto existing_probe_done
  ${EndIf}
  System::Call '*$2(&i2 .r4, &i2 .r5)'
  IntFmt $4 "%04x" $4
  IntFmt $5 "%04x" $5
  StrCpy $6 "\StringFileInfo\$4$5"
  !insertmacro READ_KOTOSHIRO_VERSION_STRING "ProductName"
  ${If} $9 != "言代Kotoshiro"
    Goto existing_probe_done
  ${EndIf}
  !insertmacro READ_KOTOSHIRO_VERSION_STRING "CompanyName"
  ${If} $9 != "言代Kotoshiro"
    Goto existing_probe_done
  ${EndIf}
  !insertmacro READ_KOTOSHIRO_VERSION_STRING "FileDescription"
  ${If} $9 != "言代Kotoshiro"
    Goto existing_probe_done
  ${EndIf}
  !insertmacro READ_KOTOSHIRO_VERSION_STRING "InternalName"
  StrCpy $ExistingKotoshiroIdentity $9
  ${If} $9 != "Kotoshiro.dll"
  ${AndIf} $9 != "Kotoshiro.exe"
    Goto existing_probe_done
  ${EndIf}
  !insertmacro READ_KOTOSHIRO_VERSION_STRING "OriginalFilename"
  ${If} $9 != "Kotoshiro.dll"
  ${AndIf} $9 != "Kotoshiro.exe"
    Goto existing_probe_done
  ${EndIf}
  IntOp $1 $7 >> 16
  IntOp $2 $7 & 0xffff
  IntOp $3 $8 >> 16
  IntOp $4 $8 & 0xffff
  StrCpy $ExistingKotoshiroVersion "$1.$2.$3.$4"
  GetFileTime "$INSTDIR\Kotoshiro.exe" $1 $2
  StrCpy $ExistingKotoshiroStamp "$1:$2"
  StrCpy $ExistingKotoshiroRecognized 1
  existing_probe_done:
  ${If} $0 <> 0
    System::Free $0
  ${EndIf}
  Pop $9
  Pop $8
  Pop $7
  Pop $6
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
  ClearErrors
FunctionEnd
!endif
