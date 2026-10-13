# ToolHub

PC의 개별 프로그램 5개를 하나의 트레이 프로그램에서 관리·실행하는 통합 허브 (.NET 8).

## 구조

```
ToolHub\
  ToolHub.exe                       허브 본체 (트레이, 관리 창, 모듈 로더)
  build.ps1                         빌드 스크립트
  modules\<이름>\Module.<이름>.dll   모듈 (폴더 하나 = 모듈 하나)
  src\Core\                         허브 소스  (ToolHub.csproj)
  src\Modules\<이름>\               모듈 소스  (<이름>.csproj)
```

| 모듈 | 기능 | 원본 소스 |
|---|---|---|
| PowerPoint | PowerPoint Shift+휠 가로 스크롤 | PowerPointShiftWheel v2 |
| Excel | Excel Shift+휠 가로 스크롤 | ExcelShiftWheel v4 |
| MouseDrag | 터치패드 세 손가락 드래그 | ThreeFingerDrag |
| Timestamp | Win+; 로 현재 시각 입력 | github.com/sundc753-byte/timestamp |
| Clipboard | Win+V 클립보드 기록(+이미지 OCR) | github.com/sundc753-byte/clipboard (브랜치 `claude/clipboard-app`, b14382c) |

설정/로그: `%APPDATA%\ToolHub\settings.ini`, `%APPDATA%\ToolHub\ToolHub.log`
클립보드 기록: `%APPDATA%\ClipboardApp` (기존 단독 프로그램과 같은 위치 — 기록이 그대로 이어짐)

## 빌드 (.NET 8 SDK 필요)

```
.\build.ps1                    전체 빌드
.\build.ps1 -Only Clipboard    해당 모듈만 빌드 -> modules\Clipboard\ 만 갱신
.\build.ps1 -Only Core         허브만 빌드
.\build.ps1 -Out C:\temp\hub   다른 폴더로 빌드 (실행 중인 허브를 건드리지 않고 시험)
```

SDK 는 `%USERPROFILE%\.dotnet` 에 설치되어 있고 build.ps1 이 자동으로 찾습니다.
실행 중인 허브가 관리자 권한이면 파일이 잠겨 덮어쓸 수 없으므로 먼저 트레이에서 종료하세요.

## 단위별 수정

| 하고 싶은 일 | 방법 |
|---|---|
| 한 기능만 수정 | `src\Modules\<이름>\` 수정 후 `.\build.ps1 -Only <이름>` |
| 기능 제거 | `modules\<이름>\` 폴더 삭제 |
| 기능 추가 | `src\Modules\<새이름>\<새이름>.csproj` + `ModuleBase` 상속 클래스 추가 (기존 모듈 폴더 복사 후 수정이 가장 쉬움) |
| 허브 수정 | `.\build.ps1 -Only Core` |
| 원본 git 의 새 커밋 반영 | 해당 모듈 폴더의 소스를 갱신 (Timestamp: `TimestampModule.cs`/`KeyboardHook.cs`, Clipboard: `ClipboardModule.cs` 가 허브 연결부이고 나머지는 원본 그대로) |

점검: `ToolHub.exe --smoke` — 모든 모듈을 3초간 시작/중지하고 `%APPDATA%\ToolHub\smoke.txt` 에 결과를 기록.

## 동작 규칙

- 각 모듈은 트레이 메뉴 또는 관리 창(트레이 더블클릭)에서 사용/중지. 사용 여부는 모듈별로 저장.
- 같은 기능의 기존 단독 프로그램이 떠 있으면 종료 여부를 묻고, 종료해야 모듈이 시작됨(입력 중복 방지).
- 트레이 메뉴 "기존 단독 프로그램 자동 시작 항목 정리..." 로 기존 exe 들의 시작 프로그램 등록을 제거(확인 후).
- 관리자 권한이 필요한 경우 "관리자 권한으로 다시 시작" 사용.
- 타임스탬프/클립보드는 원본의 자체 트레이·자동 시작 코드를 제외하고 허브가 대신 담당.
