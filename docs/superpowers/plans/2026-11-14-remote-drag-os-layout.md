# 송출컴에서 끄는 동안 송출컴 배치 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 송출컴에서 마우스 버튼을 누른 채 움직이는 동안 송출컴 윈도우 배치로 움직여, 송출컴 모니터끼리 창을 끌어 옮길 수 있게 한다.

**Architecture:** `InputRouter`가 가두기에서 쓰는 송출컴 배치(`_remoteOs`)를 끄는 동안에도 쓴다. 가두기(`RemoteLocked`, 트레이 주황색)와 따로 `_dragOs` 표시로 구분해서, 끌기는 상태 표시에 드러나지 않는다.

**Tech Stack:** .NET 8, xUnit (`dotnet test`).

**Spec:** `docs/superpowers/specs/2026-11-14-remote-drag-os-layout-design.md`

## Global Constraints

- 게임컴 쪽 동작은 바꾸지 않는다. 새 설정 항목 없음, 항상 켜짐.
- 끄는 동안 트레이 아이콘·상태(`RemoteLocked`)는 바뀌지 않는다.
- 좌표 변환은 기존 가두기와 같은 `_remoteOs` / `_layout.ToOs` / `_layout.FromOs`를 쓴다.

## Review Focus

1. 버튼 없이 송출컴 1 끝을 밀면 지금처럼 게임컴으로 넘어가야 한다 → `NoButton_StillCrossesToGame`.
2. 끄는 중 가두기 키 → 떼도 가두기 유지, 트레이는 그때부터 주황 → `LockDuringDrag_StaysLockedAfterRelease`.
3. 가두기 중 끌기 → 떼어도 가두기 유지 → `DragWhileLocked_ReleaseKeepsLock`.
4. 끄는 중 빠른 전환은 막힘 → `Drag_BlocksQuickSwitch`.
5. 끄는 중 연결 끊김·배치 변경·커서 홈 보내기 → 끌기 상태도 풀려 다음 이동이 책상 배치 → `Drag_ClearedWhenCursorIsSentHome`.

---

### Task 1: 끄는 동안 송출컴 배치

**Files:**
- Modify: `src/MonitorBridge.Core/Routing/InputRouter.cs` (`OnButton` 약 170행, `RemoteMove` 약 134행, `ToggleRemoteLock`/`EndRemoteLock` 약 211~231행, `QuickSwitch` 약 233행)
- Test: `tests/MonitorBridge.Core.Tests/InputRouterTests.cs` (기존 `CrossToA()` 도우미 사용: A에 들어가 있고 OS (1919,320), 주차 (1280,720))
- Modify: `README.md`, `README.en.md`, `README.ja.md` 단축키 절 아래 목록에 한 줄; `node docs/manual/make-pdf.mjs`

**Interfaces:**
- Produces: 공개 API 변화 없음. 내부 표시 `bool _dragOs`.

- [ ] **Step 1: 실패하는 테스트 쓰기** (`InputRouterTests`, 이름은 `RemoteDrag_` 접두)
  - `RemoteDrag_CrossesToOtherStreamMonitor`: `CrossToA()` → `OnButton(Left, true)` → `OnMouseMove(1285, 720)` 결과가 `CursorMove(1924, 320)`, `ActivePcId == "stream"`, `RemoteLocked == false`.
  - `RemoteDrag_Release_ReturnsToDeskLayout`: 위에 이어 `OnButton(Left, false)` → `Position == new VPoint(4484, 320)`; 이어서 `OnMouseMove(1270, 720)`(왼쪽으로)이 `CursorMove(1914, 320)`이 아니라 게임컴으로 넘어감(`ActivePcId == "game"`).
  - `NoButton_StillCrossesToGame`: `CrossToA()` → `OnMouseMove(1285, 720)` → `ActivePcId == "game"`.
  - `DragWhileLocked_ReleaseKeepsLock`: `ToggleRemoteLock()` → 버튼 누름/뗌 → `RemoteLocked == true`, `OnMouseMove(1285,720)`가 `CursorMove(1924,320)`.
  - `LockDuringDrag_StaysLockedAfterRelease`: 버튼 누름 → `ToggleRemoteLock()`이 `true` → 버튼 뗌 → `RemoteLocked == true`; 다시 `ToggleRemoteLock()`은 `false`.
  - `Drag_BlocksQuickSwitch`: 버튼 누름 → `QuickSwitch()`가 `Block`이고 `ToRemote` 비어 있음, `ActivePcId == "stream"`.
  - `Drag_ClearedWhenCursorIsSentHome`: 기존 `RemoteLock_ClearedWhenCursorIsSentHome`과 같은 방식으로 커서를 홈으로 보낸 뒤 다시 들어가 버튼 없이 밀면 게임컴으로 넘어감.
- [ ] **Step 2: 실행해서 실패 확인** — `dotnet test --filter "FullyQualifiedName~RemoteDrag_|FullyQualifiedName~Drag_|FullyQualifiedName~NoButton_|FullyQualifiedName~LockDuringDrag_|FullyQualifiedName~DragWhileLocked_"` → `RemoteDrag_CrossesToOtherStreamMonitor` 등 실패.
- [ ] **Step 3: 구현**
  - `OnButton`: 원격일 때 누름이 `_heldButtons`의 첫 버튼이고 가두기·끌기 중이 아니면 가두기 진입과 같은 변환(`_remoteOs` 만들기, `Position`을 OS 좌표로)을 하고 `_dragOs = true`. 뗌으로 `_heldButtons`가 비고 `_dragOs`면 `Position = _layout.FromOs(...)`, `_remoteOs = null`, `_dragOs = false`.
  - 공통 변환은 `ToggleRemoteLock`의 진입 부분을 `EnterRemoteOs()`로 빼서 같이 쓴다.
  - `RemoteMove`: 배치 선택을 `RemoteLocked || _dragOs`로.
  - `ToggleRemoteLock`: `_dragOs` 중이면 변환 없이 `_dragOs = false; RemoteLocked = true`.
  - `EndRemoteLock`: `_dragOs`도 함께 끝낸다(연결 끊김·배치 변경·홈 보내기 경로가 이미 이걸 부른다).
  - `QuickSwitch`: `RemoteLocked || _dragOs`면 막는다.
- [ ] **Step 4: 다시 실행** → 새 테스트 PASS, 이어서 `dotnet test` 전체 PASS.
- [ ] **Step 5: 설명서** — 세 README의 단축키 절 목록, "송출컴에서 가두면…" 줄 바로 아래에 추가:
  - 한국어: `- 송출컴에서 창을 끄는 동안(마우스 버튼을 누른 채)에도 송출컴 윈도우의 디스플레이 배치를 따라 움직여서, 송출컴 모니터끼리 창을 끌어 바로 옮길 수 있습니다. 버튼을 떼면 원래 배치로 돌아옵니다.`
  - English: `- While you drag on the streaming PC (a mouse button held), the cursor also follows the streaming PC's Windows display arrangement, so you can drag a window straight between streaming monitors. Releasing the button goes back to the desk layout.`
  - 日本語: `- 配信PCでドラッグしている間（マウスボタンを押したまま）も配信PCの Windows のディスプレイ配置に従って動くので、配信モニター同士でウィンドウをそのままドラッグで移せます。ボタンを離すと元の配置に戻ります。`
  - `node docs/manual/make-pdf.mjs` → PDF 3개 생성.
- [ ] **Step 6: 커밋** — `feat: drag windows between stream PC monitors (OS layout while a button is held)`
