# 관리자 권한 알림·다시 켜기, 가두기 중 빠른 전환 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 관리자 권한이 아니면 두 PC 모두에서 알리고 한 번에 관리자 권한으로 다시 켜게 하며, 가둔 상태에서도 빠른 전환 키로 넘어가게 한다(게임컴은 돌아오면 다시 가둠).

**Architecture:** 커서 판단은 `InputRouter`(Core, 테스트 가능), 게임컴 가두기·단축키는 `HostService`/`LowLevelHooks`, 알림·메뉴·다시 켜기는 `TrayContext`/`SettingsForm`/`AutoStart`/`Program`. 문구 고르기와 인자 읽기는 작은 정적 함수로 빼서 테스트한다.

**Tech Stack:** .NET 8 WinForms, xUnit (`dotnet test`; 테스트 프로젝트는 앱 프로젝트를 참조함).

**Spec:** `docs/superpowers/specs/2026-11-14-admin-status-restart-design.md`

## Global Constraints

- 화면 글은 모두 한국어. 문구는 설계 문서 그대로.
- 게임컴 화면은 방송에 나간다: 저절로 뜨는 창 금지(알림 풍선과 사용자가 누른 뒤의 확인 창만 허용).
- exe는 항상 관리자 권한으로 만들지 않는다(매니페스트 변경 금지).
- 창을 끄는 중에는 빠른 전환을 계속 막는다.

## Review Focus

1. 관리자 권한 확인 창에서 "아니오" → 지금 프로그램이 그대로 돌고 알림만 → Task 3 수동 확인 + `RestartElevated` 반환값 처리.
2. 다시 켤 때 "이미 실행 중" 창이 뜨면 안 됨(새 프로세스가 예전 것이 끝나길 기다림) → Task 3 `WaitForPid_*` 테스트 + 수동 확인.
3. 게임컴 가둠 → 빠른 전환으로 송출컴 → 커서를 밀어서 돌아와도 다시 가둠 → Task 2 수동 확인 항목.
4. 송출컴 가둠 중 빠른 전환 → 가두기 풀리고 게임컴으로, 트레이 주황색 꺼짐 → Task 1 `RemoteLock_QuickSwitch_LeavesAndUnlocks`.
5. 빠른 전환 키가 비어 있거나(사용 안 함) 등록 실패 → 가두기는 정상, 실패면 알림 → Task 2 수동 확인.

---

### Task 1: 송출컴 가두기 중 빠른 전환 허용

**Files:**
- Modify: `src/MonitorBridge.Core/Routing/InputRouter.cs` (`QuickSwitch`)
- Test: `tests/MonitorBridge.Core.Tests/InputRouterTests.cs`

**Interfaces:** 공개 API 변화 없음.

- [ ] **Step 1: 테스트 바꾸기** — 기존 `RemoteLock_BlocksQuickSwitch_UntilUnlocked`를 `RemoteLock_QuickSwitch_LeavesAndUnlocks`로 바꾼다: `CrossToA()` → `ToggleRemoteLock()` → `QuickSwitch()`의 `ToRemote`에 `CursorLeave`가 있고, `ActivePcId == "game"`, `RemoteLocked == false`. 다시 `QuickSwitch()` → `ActivePcId == "stream"`, `RemoteLocked == false`(되살리지 않음). `Drag_BlocksQuickSwitch`는 그대로 둔다.
- [ ] **Step 2: 실행해서 실패 확인** — `dotnet test --filter FullyQualifiedName~InputRouterTests` → 새 테스트 FAIL.
- [ ] **Step 3: 구현** — `QuickSwitch`: `_dragOs`면 Swallow, 아니면 진행. 가두기는 `LeaveRemote`가 이미 `EndRemoteLock`으로 푼다.
- [ ] **Step 4: 다시 실행** → PASS, 전체 `dotnet test` PASS.
- [ ] **Step 5: 커밋** — `feat: quick switch leaves a stream PC lock`

### Task 2: 게임컴 가두기 중 빠른 전환, 돌아오면 다시 가둠

**Files:**
- Modify: `src/MonitorBridge/Host/LowLevelHooks.cs` (단축키 id 2개), `src/MonitorBridge/Host/HostService.cs` (`Lock`, `Unlock`, `Execute`, 생성자의 `HotkeyPressed`)

**Interfaces:**
- Produces: `LowLevelHooks.RegisterHotkey(int id, Hotkey h) -> bool`, `UnregisterHotkey(int id)`, `event Action<int>? HotkeyPressed`; 상수 `LockHotkeyId = 1`, `QuickHotkeyId = 2`.

- [ ] **Step 1: 구현**
  - `LowLevelHooks`: 단축키 등록·해제·이벤트에 id를 받는다. `WM_HOTKEY`의 `wParam`이 id. 종료 시 두 id 모두 해제.
  - `HostService.Lock()`: 가두기 키 등록 후, 빠른 전환 키(`_quick`)가 있으면 `QuickHotkeyId`로 등록. 실패하면 가두기는 유지하고 `_notify("가둔 동안 빠른 전환 키를 쓸 수 없어요. 다른 프로그램이 같은 키를 쓰고 있을 수 있어요.")`.
  - `HotkeyPressed(id)`: `LockHotkeyId` → 지금처럼 `Unlock()`. `QuickHotkeyId` → `Unlock()` 후 `QuickSwitch()`, `_relockOnReturn = true`.
  - `Unlock()`: 두 단축키 모두 해제.
  - `Execute` 끝: `_relockOnReturn && !_router.IsRemote && !_locked`이면 `_relockOnReturn = false; Lock();` (빠른 전환·커서 밀기·연결 끊김 모두 이 길을 지난다). 사용자가 가두기 키로 직접 푼 경우(`LockHotkeyId`)에는 `_relockOnReturn = false`.
- [ ] **Step 2: 빌드·테스트** — `dotnet build src/MonitorBridge` 경고 0, `dotnet test` PASS.
- [ ] **Step 3: 커밋** — `feat: quick switch while the game PC is locked; relock on return`
- 수동 확인(두 PC 시험 때): 게임컴 가둠 → 게임 화면에서 빠른 전환 → 송출컴 → (a) 빠른 전환으로 복귀 (b) 커서를 밀어 복귀, 둘 다 트레이 주황색으로 다시 가둠.

### Task 3: 관리자 권한 알림과 다시 켜기

**Files:**
- Create: `src/MonitorBridge/Shared/AdminNotice.cs`
- Modify: `src/MonitorBridge/UI/AutoStart.cs`, `src/MonitorBridge/Program.cs`, `src/MonitorBridge/UI/TrayContext.cs`, `src/MonitorBridge/UI/SettingsForm.cs`
- Test: Create `tests/MonitorBridge.Core.Tests/AdminNoticeTests.cs`
- Docs: `README.md`, `README.en.md`, `README.ja.md`, `node docs/manual/make-pdf.mjs`

**Interfaces:**
- Produces:
  - `internal static class AdminNotice { public static string Balloon(Role role); public static string Settings(Role role); public static int? WaitForPid(string[] args); public const string TrayLine = "권한: 관리자 아님 (일부 프로그램·게임에서 동작 안 함)"; }`
  - `AutoStart.RestartElevated() -> bool` (확인 창 "예"로 새 프로세스가 시작되면 true, "아니오"·실패면 false).
  - `SettingsForm.RestartRequested` (bool, 버튼을 눌러 닫혔는지).

- [ ] **Step 1: 실패하는 테스트 쓰기** (`AdminNoticeTests`)
  - `Balloon_Host_TalksAboutGames`: `Balloon(Role.Host)`가 설계 문서의 게임컴 문구와 같다.
  - `Balloon_Receiver_TalksAboutInstallers`: 송출컴 문구와 같다.
  - `Settings_EndsWithButtonHint`: 두 역할 모두 `"아래 버튼을 누르면 관리자 권한으로 다시 켜요. 매번 자동으로 관리자 권한으로 켜려면 위 자동 실행을 켜 두세요."`로 끝나고 "이 알림을 누르면"이 없다.
  - `WaitForPid_Reads`: `WaitForPid(new[]{"--wait-for","1234"}) == 1234`; 없으면 null; 숫자가 아니면 null.
- [ ] **Step 2: 실행해서 실패 확인** — `dotnet test --filter FullyQualifiedName~AdminNoticeTests` → 컴파일 오류.
- [ ] **Step 3: 구현**
  - `AdminNotice` 문구는 설계 문서 그대로. 설정 창 문구는 풍선 문구의 첫 문장 + 버튼 안내 문장.
  - `AutoStart.RestartElevated()`: `Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-for {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas" })`; `Win32Exception`(취소 1223 포함)이면 false. 로그 `restart elevated requested` / `restart elevated declined`.
  - `Program.Main`: 단일 실행 확인 전에 `AdminNotice.WaitForPid(args)`가 있으면 그 프로세스가 끝날 때까지 최대 10초 기다린다(`Process.GetProcessById` 실패 = 이미 끝남).
  - `TrayContext`:
    - 기존 송출컴 전용 `NotAdminNotice` 알림을 두 역할 공통으로 바꾼다: `StartServices` 끝에서 관리자 아니면 `AdminNotice.Balloon(_cfg.Role)` 풍선, 이 풍선을 누르면 다시 켜기(풍선 클릭 처리에 `_clickRestart` 표시 추가, 다른 `Notify`가 오면 해제).
    - 메뉴: 버전 줄 아래에 `AdminNotice.TrayLine`(비활성)과 `관리자 권한으로 다시 켜기` 항목. 관리자면 두 줄 모두 숨김(메뉴 열 때 `Visible` 갱신).
    - `RestartElevated()`: `AutoStart.RestartElevated()`가 true면 `Exit()`, false면 `Notify("관리자 권한으로 다시 켜지 않았어요.")`.
    - 설정 창이 `RestartRequested`로 닫히면 저장하지 않고 `RestartElevated()`.
  - `SettingsForm`: `_adminWarn`을 두 역할 모두에 보여 주고(관리자가 아닐 때만), 글은 `AdminNotice.Settings(지금 고른 역할)`; 옆에 `[관리자 권한으로 다시 켜기]` 버튼(같은 행, `Inline`), 누르면 `RestartRequested = true; DialogResult = Cancel`.
- [ ] **Step 4: 다시 실행** → PASS, 전체 `dotnet test` PASS, 빌드 경고 0.
- [ ] **Step 5: 렌더 확인** — `--render-settings <png>`와 `--receiver`로 두 장(렌더 프로세스는 관리자가 아니므로 안내·버튼이 보임): 안내가 역할별 문구인지, 버튼과 줄 정렬이 맞는지.
- [ ] **Step 6: 설명서** — 세 README:
  - 단축키 표의 가두기 설명에서 "빠른 전환 키도 막힙니다"를 "가둔 동안에도 빠른 전환 키로 다른 PC로 넘어갈 수 있고, 게임컴으로 돌아오면 다시 가둡니다"로(영·일 같은 뜻).
  - "자주 막히는 문제"에 행 추가: `게임 중에 가두기·빠른 전환 키와 커서 이동이 안 됨 | 게임컴 Monitor Bridge가 관리자 권한 없이 실행됨 | 트레이 메뉴나 켤 때 뜬 알림, 설정 창의 **관리자 권한으로 다시 켜기**를 누르기. 매번 자동으로 하려면 자동 실행 켜기`; 기존 송출컴 행에도 다시 켜기 버튼 안내 추가.
  - 트레이 표에 `권한: 관리자 아님 줄 / 관리자 권한으로 다시 켜기 | 관리자 권한 없이 실행 중일 때만 보임` 행.
  - `node docs/manual/make-pdf.mjs`.
- [ ] **Step 7: 커밋** — `feat: admin status on both PCs with restart-as-admin`
- 수동 확인(두 PC 시험 때): new 폴더 exe 더블클릭 → 풍선 → 누름 → 확인 창 "예" → 관리자 권한으로 다시 켜짐("이미 실행 중" 창 없음). "아니오" → 그대로 돌고 알림.
