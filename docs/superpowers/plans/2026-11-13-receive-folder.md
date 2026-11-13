# 받은 파일 저장 폴더 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 상대 PC에서 복사한 파일을 설정에 정한 폴더(기본: 다운로드)에 바로 저장한다. 설정 창에는 그 경로를 보여 준다.

**Architecture:** 받는 동안에는 저장 폴더 안의 숨김 폴더에 쓴다. 다 받으면 `FileReceiver.MoveInto`가 겹치지 않는 이름으로 저장 폴더에 옮긴다. 옮기기와 이름 정하기는 Core에 둔다. 폴더 경로는 `AppConfig.ReceiveFolder`에 두고, 비어 있으면 윈도우 다운로드 폴더를 쓴다.

**Tech Stack:** .NET 8, WinForms, xUnit

**Spec:** `docs/superpowers/specs/2026-11-13-receive-folder-design.md`

## Global Constraints
- 화면에 새 창이나 오버레이를 띄우지 않는다. 쓸 수 있는 것은 알림 풍선, 트레이, 설정 창, 그리고 알림을 눌렀을 때 여는 탐색기다.
- 사람에게 보이는 글은 모두 한국어로 쓴다. 문구는 설계서 그대로 쓴다.
- 숨김 폴더 이름은 `.MonitorBridge-받는중`이다.
- 이름이 겹치면 `이름 (2).확장자` 형식을 쓴다.
- 저장 폴더를 바꿔도 다시 연결하지 않는다.

## Review Focus
1. 저장 폴더가 없거나(빠진 USB, 끊긴 네트워크 드라이브) 쓸 수 없으면, 전송을 거절하고 알림으로 이유를 말한다. 다운로드 폴더에 반쯤 받은 파일이 남지 않는다. → Task 3 단계
2. 같은 이름의 **폴더**를 받으면 `폴더 (2)`로 저장한다. 안을 덮어쓰거나 합치지 않는다. → Task 1 `MoveInto_FolderCollision`
3. `이름 (2)`도 이미 있으면 `(3)`으로 저장한다. → Task 1 `UniqueName_SkipsTaken`
4. 받는 중에 취소되거나 끊기면 숨김 폴더째 지운다. 저장 폴더에는 아무것도 남지 않는다. → Task 1 `Cancel_LeavesDestinationEmpty`
5. 새 버전을 처음 켤 때 예전 임시 폴더를 비운다. 지금 거기 있는 70GB는 사라진다. → README에 경고를 적고, 업데이트 전에 사용자에게 알린다(Task 5).

---

### Task 1: 옮기기와 이름 정하기 (Core)

**Files:**
- Modify: `src/MonitorBridge.Core/Files/FileReceiver.cs`
- Test: `tests/MonitorBridge.Core.Tests/ClipboardFilesTests.cs`. 기존 파일 테스트가 있는 곳이다. 없으면 `FileReceiverMoveTests.cs`를 새로 만든다.

**Interfaces:**
- Produces:
  - `public const string StagingFolderName = ".MonitorBridge-받는중";`
  - `public static string UniqueName(string folder, string name)`: 겹치지 않는 전체 경로를 돌려준다.
  - `public static IReadOnlyList<string> MoveInto(IReadOnlyList<string> tops, string dest)`: 옮긴 뒤의 전체 경로를 돌려준다.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (임시 폴더 사용)
  - `UniqueName_Free`: 빈 폴더에서 `a.mkv` → `<f>\a.mkv`
  - `UniqueName_SkipsTaken`: `a.mkv`, `a (2).mkv`가 있으면 → `<f>\a (3).mkv`
  - `UniqueName_NoExtension`: `README`가 있으면 → `README (2)`
  - `MoveInto_FileCollision`: dest에 `a.mkv`가 있으면, 옮긴 파일은 `a (2).mkv`가 되고 원래 파일 내용은 그대로다.
  - `MoveInto_FolderCollision`: dest에 `dir\x.txt`가 있으면, 받은 `dir`은 `dir (2)`가 되고 그 안에 받은 파일이 있다.
  - `Cancel_LeavesDestinationEmpty`: staging 루트를 dest 아래 `StagingFolderName`으로 만든다. `FileReceiver`로 파일 하나를 반쯤 쓰고 `Cancel()`을 부른다. 그러면 dest 안에 `StagingFolderName` 밖의 항목이 하나도 없다.
- [ ] **Step 2:** `dotnet test` → 컴파일 실패를 확인한다.
- [ ] **Step 3:** 구현한다.
  - `MoveInto`는 각 항목마다 `UniqueName`을 구한 뒤 `File.Move`나 `Directory.Move`로 옮긴다.
  - 옮기는 순간 같은 이름이 생겨 `IOException`이 나면 다음 번호로 최대 5번 다시 시도한다.
- [ ] **Step 4:** `dotnet test` → 통과를 확인한 뒤 커밋한다.

### Task 2: 설정 값과 다운로드 폴더

**Files:**
- Modify: `src/MonitorBridge.Core/Config/AppConfig.cs`, `tests/MonitorBridge.Core.Tests/AppConfigTests.cs`, `src/MonitorBridge/Native/Win32.cs`
- Create: `src/MonitorBridge/Shared/ReceiveFolder.cs`

**Interfaces:**
- Produces:
  - `AppConfig.ReceiveFolder { get; set; } = ""`
  - `static class ReceiveFolder { public static string Resolve(AppConfig cfg); public static string Downloads(); }`

- [ ] **Step 1:** 기존 저장·불러오기 왕복 테스트에 `ReceiveFolder = @"D:\받기"`를 넣는다. 실패를 확인한 뒤 속성을 추가해서 통과시킨다.
- [ ] **Step 2: 다운로드 폴더 찾기**
  - `Downloads()`는 `SHGetKnownFolderPath(FOLDERID_Downloads {374DE290-123F-4565-9164-39C4925E467B})`로 찾는다.
  - 실패하면 `%USERPROFILE%\Downloads`를 쓴다.
  - `Resolve`는 값이 비어 있으면 `Downloads()`를 돌려준다.
- [ ] **Step 3:** 빌드하고 커밋한다.

### Task 3: 받기 흐름 바꾸기 (`FileTransfer`, `ClipboardSync`)

**Files:** Modify `src/MonitorBridge/Shared/FileTransfer.cs`, `src/MonitorBridge/Shared/ClipboardSync.cs`, `src/MonitorBridge/UI/TrayContext.cs`

**Interfaces:**
- Consumes: Task 1, Task 2
- Produces:
  - 생성자에 `Func<string> receiveFolder` 인자를 추가한다. `limitBytes` 다음 자리다. `ClipboardSync`에도 같은 인자를 추가한다.
  - `Action<IReadOnlyList<string>, string> onSaved`: 저장된 경로와 저장 폴더를 넘긴다. 기존 `onReceived`를 대신한다.

- [ ] **Step 1: 시작할 때**
  - 생성자에서 예전 `%TEMP%\MonitorBridge`와 현재 저장 폴더의 staging 폴더를 지운다.
- [ ] **Step 2: `FileOfferStart`를 받으면**
  - 그때의 `receiveFolder()`로 `dest`를 정한다.
  - `Directory.CreateDirectory(dest)`와 staging 폴더 만들기를 시도한다. staging 폴더에는 숨김 속성을 준다.
  - 실패하면 거절하고 알린다: "받은 파일 저장 폴더({dest})에 쓸 수 없어서 받지 않았어요. 설정에서 폴더를 바꿔 주세요."
  - 성공하면 `new FileReceiver(staging, id)`로 받는다.
- [ ] **Step 3: `FileOfferEnd`를 받으면**
  - `Complete()`로 나온 경로를 `MoveInto(tops, dest)`로 옮긴다.
  - `onSaved(finalPaths, dest)`를 호출한다.
  - 알림: "다 받았어요: {이름} → {dest}. 누르면 폴더를 열어요."
  - 옮기다 실패하면 실패로 처리하고 알린다.
- [ ] **Step 4: `ClipboardSync`**
  - `onSaved`에서 클립보드에 `finalPaths`를 넣는다(`SetFiles`).
  - 저장된 첫 경로를 `LastSaved`로 둔다.
- [ ] **Step 5: `TrayContext`**
  - `BalloonTipClicked`가 마지막 알림이 "다 받았어요"일 때만 `explorer.exe /select,"{LastSaved}"`를 연다.
  - 다른 알림이 뜨면 연결을 지운다.
  - 가벼운 저장 경로에서 `ReceiveFolder`도 그 자리에서 바꾼다.
- [ ] **Step 6:** 빌드와 테스트를 확인하고 커밋한다.

### Task 4: 설정 창 (`SettingsForm`, frontend-design)

**Files:** Modify `src/MonitorBridge/UI/SettingsForm.cs`

- [ ] **Step 1: 저장 폴더 줄**
  - 파일 복사 한도 줄 위에 "받은 파일 저장 폴더" 줄을 넣는다. 경로 TextBox(폭 300)와 `찾아보기…` 버튼(`FolderBrowserDialog`)을 둔다.
  - 아래 안내 Label(회색): "상대 PC에서 복사한 파일은 이 폴더에 저장돼요."
  - 빈 설정이면 `ReceiveFolder.Downloads()` 경로를 보여 준다.
- [ ] **Step 2: 확인과 저장**
  - `Problem()`에서 경로를 확인한다. 비어 있거나, 드라이브가 없거나, `Directory.CreateDirectory`가 실패하면 "받은 파일 저장 폴더를 쓸 수 없어요: {이유}"를 보여 준다.
  - `Result.ReceiveFolder`에 넣는다. 다운로드 폴더와 같으면 `""`로 저장해서, 다운로드 위치를 옮겨도 따라가게 한다.
- [ ] **Step 3:** `--render-settings`로 PNG를 확인하고 커밋한다.

### Task 5: 안내서·빌드·리뷰·배포

- [ ] **Step 1: README 고치기**
  - "파일 복사" 절을 받은 파일 저장 폴더 기준으로 바꾼다.
  - "새 버전으로 바꾸기"에 경고를 넣는다: "예전 버전에서 받은 파일은 임시 폴더에 있어요. 바꾸기 전에 옮겨 두세요."
- [ ] **Step 2: 빌드와 배포 준비**
  - `dotnet test`가 통과하는지 확인한다.
  - publish로 exe를 만들고 `monitor-bridge-share\new\`에 복사한다.
- [ ] **Step 3:** 새 리뷰어로 전체 리뷰를 하고 고친다.
- [ ] **Step 4: 두 PC 교체**
  - 사용자가 게임컴 임시 폴더의 70GB를 옮겼는지 확인한다.
  - 게임컴은 사용자가 바꾸고, 송출컴은 허락을 받은 뒤 바꾼다.
