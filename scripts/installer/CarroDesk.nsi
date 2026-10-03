; CarroDesk NSIS 安装脚本
;
; 由 scripts/release.py 调用 makensis 编译，参数通过 /D 传入：
;   VERSION      版本号 x.y.z
;   PAYLOAD_DIR  GUI 输出目录（含 CarroDesk.exe / CarroDesk.Cli.exe / 样例文件）
;   ICON_FILE    应用图标绝对路径
;   OUTPUT_FILE  生成的安装包完整路径
;
; 设计要点：
;   1. per-user 安装（RequestExecutionLevel user），默认装到 %LOCALAPPDATA%\Programs\CarroDesk，
;      全程不弹 UAC，与程序自启使用的 HKCU 注册表处于同一用户上下文。
;   2. 安装/卸载前自动结束 CarroDesk.exe 与 CarroDesk.Cli.exe（taskkill /T 连子进程树），
;      解决"复制便携版覆盖时文件被占用需手动杀进程"的问题。
;   3. 数据模式单选（自定义页）：漫游模式（%AppData%\CarroDesk）或
;      便携模式（安装目录 app_data，与绿色版行为一致）。切换模式时自动迁移数据。
;   4. 升级只覆盖程序文件；app_data / %AppData% 数据、样例文件、自启项均保留。

Unicode true

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "StrFunc.nsh"
!include "WinMessages.nsh"
!include "nsDialogs.nsh"

; StrFunc 子串查找（StrStr）：为脚本生成对应 NSIS 函数
${Using:StrFunc} StrStr

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef PAYLOAD_DIR
  !define PAYLOAD_DIR "."
!endif
!ifndef ICON_FILE
  !define ICON_FILE "."
!endif
!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "CarroDesk-setup.exe"
!endif

!define APP_NAME         "CarroDesk"
!define APP_PUBLISHER    "CarroDesk"
!define APP_EXE          "CarroDesk.exe"
!define APP_CLI_EXE      "CarroDesk.Cli.exe"
!define APP_PORTABLE_INI "portable.ini"
!define APP_DATA_DIR     "app_data"
!define UNINST_KEY       "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define AUTOSTART_KEY    "Software\Microsoft\Windows\CurrentVersion\Run"
!define AUTOSTART_VALUE  "${APP_NAME}"
!define DEFAULT_INSTDIR  "$LOCALAPPDATA\Programs\${APP_NAME}"
!define APPDATA_DIR      "$APPDATA\${APP_NAME}"
!define PORTABLE_DATADIR "$INSTDIR\${APP_DATA_DIR}"

Var DataMode            ; 0 = 漫游（%AppData%）, 1 = 便携（安装目录 app_data）
Var PrevDataMode        ; 上次安装的数据模式，-1 = 未安装
Var InstalledVersion    ; 上次安装的版本号
Var RoamingRadio
Var PortableRadio
Var RunningFlag         ; DetectRunningApp 结果：1 = 有进程在运行
Var DataDirResult       ; ResolveDataDir 结果：当前数据目录
Var MigrateSrc          ; MigrateDataIfNeeded 使用的旧数据目录

!define MUI_ICON   "${ICON_FILE}"
!define MUI_UNICON "${ICON_FILE}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动 ${APP_NAME}"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\README.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "打开安装目录"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

; per-user 安装：不需要管理员权限，不弹 UAC
RequestExecutionLevel user

; LZMA 固态压缩，安装包体积最小
SetCompressor /SOLID lzma

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom DataModePageCreate DataModePageLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!define MUI_UNCONFIRMPAGE_TEXT_TOP "即将卸载 ${APP_NAME}。程序文件与快捷方式将被移除。$\n$\n\
配置与任务数据（${APP_DATA_DIR} / %APPDATA%\${APP_NAME}）默认保留，如需彻底清除请手动删除。"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ================================================================ 公共函数

; 结束 CarroDesk 主进程与 CLI 进程（含子进程树）。
; 进程不存在时 taskkill 返回非 0，此处刻意忽略。
Function StopRunningApp
  DetailPrint "正在结束运行中的 ${APP_NAME} 进程..."
  nsExec::ExecToLog 'taskkill /F /T /IM "${APP_NAME}.exe"'
  Pop $0
  nsExec::ExecToLog 'taskkill /F /T /IM "${APP_CLI_EXE}"'
  Pop $0
  Sleep 500
FunctionEnd

; 检测主进程或 CLI 进程是否在运行（结果置 $RunningFlag）
; tasklist 无匹配时输出本地化提示文本（形如「信息: 没有运行的任务匹配指定标准。」），
; 因此不能用「输出非空」判断，必须匹配 CSV 行首的 "进程名"。
Function DetectRunningApp
  StrCpy $RunningFlag "0"

  nsExec::ExecToStack 'tasklist /NH /FO CSV /FI "IMAGENAME eq ${APP_NAME}.exe"'
  Pop $0
  Pop $1
  StrCpy $2 $1
  ${StrStr} $2 '"CarroDesk.exe"' $2
  ${If} $2 != ""
    StrCpy $RunningFlag "1"
  ${EndIf}

  nsExec::ExecToStack 'tasklist /NH /FO CSV /FI "IMAGENAME eq ${APP_CLI_EXE}"'
  Pop $0
  Pop $1
  StrCpy $2 $1
  ${StrStr} $2 '"CarroDesk.Cli.exe"' $2
  ${If} $2 != ""
    StrCpy $RunningFlag "1"
  ${EndIf}
FunctionEnd

; 解析当前数据模式对应的数据目录，结果置 $DataDirResult
Function ResolveDataDir
  StrCpy $DataDirResult "${APPDATA_DIR}"
  ${If} $DataMode == 1
    StrCpy $DataDirResult "${PORTABLE_DATADIR}"
  ${EndIf}
FunctionEnd

; 数据模式切换时迁移数据：旧目录 -> 新目录，仅在新目录无 config.json 时执行
Function MigrateDataIfNeeded
  ${If} $DataMode == $PrevDataMode
    Goto mig_done
  ${EndIf}
  ${If} $PrevDataMode != 0
  ${AndIf} $PrevDataMode != 1
    Goto mig_done
  ${EndIf}

  ; 旧目录
  StrCpy $MigrateSrc "${APPDATA_DIR}"
  ${If} $PrevDataMode == 1
    StrCpy $MigrateSrc "${PORTABLE_DATADIR}"
  ${EndIf}
  ${IfNot} ${FileExists} "$MigrateSrc\*.*"
    Goto mig_done
  ${EndIf}

  Call ResolveDataDir
  ; 目标已有配置则不迁移，避免覆盖现役数据
  ${If} ${FileExists} "$DataDirResult\config.json"
    Goto mig_done
  ${EndIf}

  DetailPrint "正在迁移数据：$MigrateSrc -> $DataDirResult"
  CreateDirectory "$DataDirResult"
  ; 逐项复制：CopyFiles 对 "src\*.*" 会正确展开文件，但目录需单独处理
  SetOutPath "$DataDirResult"
  CopyFiles "$MigrateSrc\*.*" "$DataDirResult"
  ${If} ${FileExists} "$MigrateSrc\logs\*.*"
    CreateDirectory "$DataDirResult\logs"
    CopyFiles "$MigrateSrc\logs\*.*" "$DataDirResult\logs"
  ${EndIf}
  ${If} ${FileExists} "$MigrateSrc\scripts\*.*"
    CreateDirectory "$DataDirResult\scripts"
    CopyFiles "$MigrateSrc\scripts\*.*" "$DataDirResult\scripts"
  ${EndIf}

  mig_done:
FunctionEnd

; ================================================================ .onInit

Name "${APP_NAME} ${VERSION} 安装程序"
OutFile "${OUTPUT_FILE}"

Function .onInit
  ; 读取既有安装信息
  StrCpy $InstalledVersion "0.0.0"
  StrCpy $PrevDataMode "-1"
  IfFileExists "$INSTDIR\${APP_EXE}" 0 detect_running
    ReadRegStr $InstalledVersion HKCU "${UNINST_KEY}" "DisplayVersion"
    ${If} $InstalledVersion == ""
      StrCpy $InstalledVersion "未知版本"
    ${EndIf}
  detect_running:

  ; 已安装版且为便携模式 -> 默认沿用便携模式
  IfFileExists "$INSTDIR\${APP_PORTABLE_INI}" 0 check_upgrade
    StrCpy $PrevDataMode "1"
    StrCpy $DataMode "1"
  check_upgrade:

  ; 检测运行中的进程，询问是否在安装前结束
  Call DetectRunningApp
  ${If} $RunningFlag == "1"
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "检测到 ${APP_NAME} 正在运行。$\n$\n\
安装需要先结束主进程与命令行进程，否则文件无法被覆盖。$\n$\n\
是否现在结束并继续安装？" IDYES ask_yes
    Goto ask_abort
    ask_yes:
  ${EndIf}

  ; 升级提示
  ${If} $InstalledVersion != "0.0.0"
  ${AndIf} $InstalledVersion != "未知版本"
    MessageBox MB_OK|MB_ICONINFORMATION "检测到已安装版本：$InstalledVersion$\n$\n\
本次将升级到：${VERSION}$\n\
程序文件会被覆盖，配置与任务数据保持不变。"
  ${EndIf}

  Goto init_done
  ask_abort:
    Abort
  init_done:
FunctionEnd

; ================================================================ 数据模式页

Function DataModePageCreate
  Call DetectRunningApp

  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ; 运行中提示
  ${If} $RunningFlag == "1"
    ${NSD_CreateLabel} 0 6u 100% 30u \
      "检测到 ${APP_NAME} 正在运行，安装程序会自动结束主进程与命令行进程。$\n\
若程序有未保存的输入，请先自行关闭后再继续。"
    Pop $1
  ${EndIf}

  ; 单选组：首个用 First，后续用 Additional，保证同组互斥
  ${NSD_CreateFirstRadioButton} 0 100u 100% 34u \
    "漫游模式（推荐）$\n\
数据与配置存放于 %APPDATA%\${APP_NAME}$\n\
适合常规安装使用，与绿色版完全解耦。"
  Pop $RoamingRadio

  ${NSD_CreateAdditionalRadioButton} 0 140u 100% 34u \
    "便携模式$\n\
数据与配置存放于安装目录的 ${APP_DATA_DIR} 子目录$\n\
连同安装目录整体拷贝即可迁移，行为与绿色版一致。"
  Pop $PortableRadio

  ; 预选当前模式（BM_CLICK 模拟点击以设置默认选中项）
  ${If} $DataMode == 1
    SendMessage $PortableRadio ${BM_CLICK} "" ""
  ${Else}
    SendMessage $RoamingRadio ${BM_CLICK} "" ""
  ${EndIf}

  nsDialogs::Show
FunctionEnd

Function DataModePageLeave
  ; BM_GETCHECK：非 0 表示选中
  ${NSD_GetChecked} $RoamingRadio $0
  ${If} $0 != 0
    StrCpy $DataMode "0"
  ${Else}
    StrCpy $DataMode "1"
  ${EndIf}
FunctionEnd

; ================================================================ 安装主体

Section "程序文件" SectionMain
  SectionIn RO

  ; 1) 结束运行中的主进程与 CLI 进程（覆盖文件前必须）
  Call StopRunningApp

  ; 2) 数据模式变化时迁移数据
  Call MigrateDataIfNeeded

  ; 3) 覆盖程序文件
  SetOutPath "$INSTDIR"
  File /oname=${APP_EXE} "${PAYLOAD_DIR}\${APP_EXE}"
  File /oname=${APP_CLI_EXE} "${PAYLOAD_DIR}\${APP_CLI_EXE}"
  ${If} ${FileExists} "${PAYLOAD_DIR}\CarroDesk.exe.config"
    File /oname=CarroDesk.exe.config "${PAYLOAD_DIR}\CarroDesk.exe.config"
  ${EndIf}

  ; 4) 样例文件：仅在目标缺失时写入，避免覆盖用户已改动的样例
  ${If} ${FileExists} "${PAYLOAD_DIR}\config.sample.json"
  ${AndIfNot} ${FileExists} "$INSTDIR\config.sample.json"
    File /oname=config.sample.json "${PAYLOAD_DIR}\config.sample.json"
  ${EndIf}
  ${If} ${FileExists} "${PAYLOAD_DIR}\tasks.sample.json"
  ${AndIfNot} ${FileExists} "$INSTDIR\tasks.sample.json"
    File /oname=tasks.sample.json "${PAYLOAD_DIR}\tasks.sample.json"
  ${EndIf}
  ${If} ${FileExists} "${PAYLOAD_DIR}\portable.sample.ini"
  ${AndIfNot} ${FileExists} "$INSTDIR\portable.sample.ini"
    File /oname=portable.sample.ini "${PAYLOAD_DIR}\portable.sample.ini"
  ${EndIf}

  ; 5) 按数据模式写入 / 清理 portable.ini
  ${If} $DataMode == 1
    FileOpen $0 "$INSTDIR\${APP_PORTABLE_INI}" w
      FileWrite $0 "; ${APP_NAME} 便携模式标志文件（由安装程序创建）$\r$\n\
; 数据与配置存放于：$INSTDIR\${APP_DATA_DIR}$\r$\n"
    FileClose $0
  ${Else}
    Delete "$INSTDIR\${APP_PORTABLE_INI}"
  ${EndIf}

  ; 6) 确保数据目录存在
  Call ResolveDataDir
  CreateDirectory "$DataDirResult"
  DetailPrint "数据目录：$DataDirResult"

  ; 7) 开始菜单快捷方式（图标取自带 Icon.ico，索引 0）
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" \
    "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" \
    "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
  Delete "$DESKTOP\${APP_NAME}.lnk"

  ; 8) 写入卸载信息（HKCU，免管理员）
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKCU "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr   HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" "1"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" "1"

  ; 9) 生成卸载器
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 10) 安装说明（完成页"打开安装目录"指向它）
  Call ResolveDataDir
  StrCpy $0 "漫游模式"
  ${If} $DataMode == 1
    StrCpy $0 "便携模式"
  ${EndIf}
  ${IfNot} ${FileExists} "$INSTDIR\README.txt"
    FileOpen $1 "$INSTDIR\README.txt" w
      FileWrite $1 "${APP_NAME} ${VERSION}$\r$\n"
      FileWrite $1 "-----------------------------------------$\r$\n"
      FileWrite $1 "安装目录：$INSTDIR$\r$\n"
      FileWrite $1 "数据目录：$DataDirResult$\r$\n"
      FileWrite $1 "数据模式：$0$\r$\n$\r$\n"
      FileWrite $1 "主程序：${APP_EXE}$\r$\n"
      FileWrite $1 "命令行：${APP_CLI_EXE}$\r$\n"
      FileWrite $1 "开机自启：托盘设置窗口可开关（写入 HKCU Run）$\r$\n$\r$\n"
      FileWrite $1 "卸载：控制面板「程序和功能」-> ${APP_NAME} -> 卸载$\r$\n"
      FileWrite $1 "（卸载只移除程序文件，配置与任务数据默认保留）$\r$\n"
    FileClose $1
  ${EndIf}
SectionEnd

; ================================================================ 卸载

; 卸载器需调用 un. 前缀的函数副本（NSIS 限制）
Function un.StopRunningApp
  DetailPrint "正在结束运行中的 ${APP_NAME} 进程..."
  nsExec::ExecToLog 'taskkill /F /T /IM "${APP_NAME}.exe"'
  Pop $0
  nsExec::ExecToLog 'taskkill /F /T /IM "${APP_CLI_EXE}"'
  Pop $0
  Sleep 500
FunctionEnd

Function un.onInit
  Call un.StopRunningApp
FunctionEnd

Section "Uninstall"
  Call un.StopRunningApp

  ; 仅当自启项指向本安装目录时才删除，避免误删指向其它副本的项
  ;（StrFunc 的 StrStr 生成函数在卸载段不可用，改用剥引号 + 前缀长度比较）
  ReadRegStr $0 HKCU "${AUTOSTART_KEY}" "${AUTOSTART_VALUE}"
  ${If} $0 != ""
    ; 自启项形如 "C:\...\CarroDesk.exe"，先去掉首尾引号
    StrCpy $1 $0
    ${If} $1 != ""
      StrCpy $1 $1 "" 1          ; 跳过起始引号
    ${EndIf}
    StrLen $2 "$INSTDIR"
    StrCpy $3 $1 $2              ; 取与安装目录等长的前缀
    ${If} $3 == "$INSTDIR"
      DeleteRegValue HKCU "${AUTOSTART_KEY}" "${AUTOSTART_VALUE}"
    ${EndIf}
  ${EndIf}

  ; 快捷方式
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  ; 程序文件
  Delete "$INSTDIR\README.txt"
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\${APP_CLI_EXE}"
  Delete "$INSTDIR\CarroDesk.exe.config"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\${APP_PORTABLE_INI}"
  Delete "$INSTDIR\config.sample.json"
  Delete "$INSTDIR\tasks.sample.json"
  Delete "$INSTDIR\portable.sample.ini"

  ; 便携模式下的 app_data：仅在无数据时清理，非空则保留
  ${If} ${FileExists} "${PORTABLE_DATADIR}\*.*"
  ${Else}
    RMDir "${PORTABLE_DATADIR}"
  ${EndIf}
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINST_KEY}"
SectionEnd

Function un.onUninstSuccess
  MessageBox MB_ICONINFORMATION|MB_OK \
    "${APP_NAME} 已卸载。$\r$\n$\r$\n\
配置与任务数据仍保留在原数据目录中（${APP_DATA_DIR} 或 %APPDATA%\${APP_NAME}），\
如需彻底清除请手动删除。"
FunctionEnd
