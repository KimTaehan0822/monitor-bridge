# 우클릭으로 상대 PC 파일 붙여넣기 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 파일은 복사할 때 목록만 알리고, 상대 PC에서 우클릭 메뉴로 가져올 때만 보낸다. 진행 창과 전송 취소를 넣고, 단축키는 비워 둘 수 있게 한다.

**Architecture:**
- 보내는 쪽은 `Offer(paths)`로 목록만 알린다. 받는 쪽이 `FileRequest(id)`를 보내면 그때 기존 스트림(`FileOfferStart…End`)으로 보낸다.
- `FileTransfer`의 네트워크 출력은 주입받는 두 함수(`reply`, `sendBulk`)로 바꾼다. 그러면 보내기·받기를 네트워크 없이 테스트할 수 있다.
- 우클릭 메뉴는 HKCU 레지스트리 동사(verb)로 만든다. `--paste <폴더>`로 실행된 프로그램은 이름 있는 파이프로 폴더를 실행 중인 프로그램에 넘긴다.

**Tech Stack:** .NET 8, WinForms, xUnit (테스트 프로젝트는 net8.0-windows이고 앱 프로젝트를 참조함)

**Spec:** `docs/superpowers/specs/2026-11-14-paste-from-other-pc-design.md`

## Global Constraints
- **원본은 절대 지우거나 옮기거나 바꾸지 않는다.** 보내는 쪽은 `FileAccess.Read`로만 연다.
- 취소하거나 실패했을 때 지우는 것은 받는 쪽 숨김 폴더(`.MonitorBridge-받는중`) 안의 것뿐이다.
- Ctrl+V와 붙여넣기는 건드리지 않는다. 받은 파일을 클립보드에 넣지도 않는다.
- 진행 창은 저절로 뜨지 않는다. 트레이 클릭이나 시작 알림 클릭으로만 연다.
- 메뉴 이름: `Monitor Bridge: 상대 PC에서 복사한 파일 여기에 붙여넣기`
- 사람에게 보이는 문구는 모두 한국어로, 설계서 그대로 쓴다.

## Review Focus
1. 복사한 뒤 가져오기 전에 원본이 지워졌거나 이름이 바뀌었으면, 받는 쪽에 거절(`FileCancel`)을 보내고 보내는 쪽에 알린다. 프로그램이 멈추면 안 된다. → Task 2 `SourceGoneBeforeRequest_Refuses`
2. 받는 중에 또 우클릭으로 가져오기를 하면 "이미 파일을 받는 중이에요"라고 알리고 거절한다. 앞 전송은 이어진다. → Task 2 `SecondRequestWhileReceiving_IsRefused`
3. 보내는 쪽에서 취소하면 받는 쪽이 반쯤 받은 파일을 지운다. → Task 2 `SenderCancel_ReceiverCleansPartial`
4. 탐색기가 넘기는 경로가 `"D:\"`처럼 따옴표 앞에 `\`가 오는 드라이브 루트이면, 인자가 `D:"`로 깨진다. 이를 바로잡는다. → Task 3 `PasteArgs_DriveRoot`
5. 일반 권한으로 실행된 메뉴 프로세스가 관리자 권한으로 도는 송출컴 프로그램의 파이프에 붙을 수 있어야 한다. 자동 테스트로는 확인할 수 없어서 수동으로 확인한다(Task 6 체크리스트).

---

### Task 1: 메시지 2개

**Files:** `Core/Protocol/Messages.cs`, `MessageCodec.cs`, `tests/.../MessageCodecTests.cs`

**Interfaces:**
- Produces:
  - `FileListOffer(Guid Id, int Count, long TotalBytes, string FirstName)`: 타입 19. `Count == 0`이면 목록을 거둔다는 뜻이다.
  - `FileRequest(Guid Id)`: 타입 20

- [ ] 왕복 테스트 목록에 두 메시지를 추가하고 실패를 확인한다.
- [ ] 인코딩과 디코딩을 구현하고 통과를 확인한 뒤 커밋한다.

### Task 2: 목록 알림·요청·취소 (`FileTransfer`)

**Files:** Modify `src/MonitorBridge/Shared/FileTransfer.cs`, `tests/MonitorBridge.Core.Tests/FileTransferTests.cs`

**Interfaces:**
- Consumes: Task 1
- Produces:
  - 생성자: `FileTransfer(Func<long> limitBytes, string peerName, Action<string> notify, Action<IReadOnlyList<string>, string> onSaved, Action<Message> reply, Func<Message, CancellationToken, ValueTask> sendBulk, string? legacyTempRoot = null)`
    - `receiveFolder` 인자는 없앤다.
    - `Send(PeerConnection, …)`도 없앤다.
  - `void Offer(IReadOnlyList<string> paths)`: 한도를 확인한 뒤 `FileListOffer`를 보낸다.
  - `void Withdraw()`: 빈 `FileListOffer`를 보낸다.
  - `FileListOffer? Pending { get; }`: 상대가 알려 준, 가져올 수 있는 목록
  - `RequestResult Request(string folder)`
    - `enum RequestResult { Started, NothingOffered, Busy, TooBig }`
    - `TooBig`은 내 한도나 상대 한도를 넘은 경우다.
  - `bool CancelAll()`: 진행 중인 보내기·받기를 모두 멈추고, 상대에게 `FileCancel`을 보낸다.

- [ ] **Step 1: 기존 테스트 4개 고치기**
  - `UnwritableFolder…`, `OverLimit…`, `Complete…`, `MoveFails…`를 새 흐름으로 바꾼다.
  - 새 흐름: `OnMessage(new FileListOffer(id,…))` → `Request(folder)` → 스트림 메시지
- [ ] **Step 2: 새 테스트 작성 후 실패 확인**
  - `CopyOnlyAnnounces`
    - `Offer([file])`을 부르면 `reply`로 `FileListOffer(_, 1, len, "a.txt")` 하나만 간다.
    - `sendBulk`는 한 번도 불리지 않는다.
  - `NonFileCopyWithdraws`: `Withdraw()` → `FileListOffer(Guid.Empty, 0, 0, "")`
  - `RequestStreamsTheOfferedFiles`: `Offer` 뒤에 `FileRequest(id)`를 받으면, `sendBulk` 순서가 Start, Entry, Chunk…, End이다. End는 TaskCompletionSource로 기다린다.
  - `UnknownRequest_Refused`: 다른 id로 요청하면 `reply` `FileCancel(그 id)`
  - `SourceGoneBeforeRequest_Refuses`
    - `Offer` 뒤에 원본을 지우고 요청하면, `FileCancel`을 보내고 알림이 1개 뜬다.
    - 예외가 나지 않는다.
  - `CancelWhileSending_SourceUntouched`
    - 가짜 `sendBulk`가 두 번째 Chunk에서 막혀 있다.
    - 이때 `CancelAll()`을 부르면 `reply`에 `FileCancel(id)`가 있다.
    - 원본의 내용, 크기, 수정 시각이 그대로다.
  - `RequestWithoutOffer_ReturnsNothingOffered`
  - `Request_SendsFileRequest_AndSavesIntoThatFolder`
  - `SecondRequestWhileReceiving_IsRefused`: 결과가 `Busy`이고 앞 전송은 그대로 진행된다.
  - `ReceiverCancel_DeletesPartialAndTellsSender`
    - 반쯤 받은 상태에서 `CancelAll()`을 부른다.
    - 숨김 폴더 안이 비어 있고, 대상 폴더에 파일이 없다.
    - `reply`에 `FileCancel(id)`가 있다.
  - `SenderCancel_ReceiverCleansPartial`: 반쯤 받은 상태에서 `FileCancel(id)`를 받으면 숨김 폴더가 비어 있다.
  - `UnrequestedOfferStart_Refused`: 요청하지 않은 `FileOfferStart`를 받으면 `reply` `FileCancel`이고, 아무것도 만들지 않는다.
- [ ] **Step 3: 구현**
  - 보낼 목록(경로, id)은 `Offer` 때 저장한다. 요청을 받으면 `FileBundle.Plan`을 다시 계산해서 지금 상태를 반영한다.
  - 받는 쪽은 `Request`에서 대상 폴더를 정해 두고, 그 id의 `FileOfferStart`에만 반응한다.
  - 상대가 취소했을 때의 알림: "상대 PC에서 파일 전송을 멈췄어요."
  - 내가 취소했을 때의 알림: "파일 전송을 취소했어요. 받던 파일은 지웠어요." 보내는 쪽이면 "파일 전송을 취소했어요."
- [ ] **Step 4:** 테스트가 모두 통과하는지 확인하고 커밋한다.

### Task 3: 우클릭 메뉴·파이프·`--paste`

**Files:**
- Create: `src/MonitorBridge/Shared/ShellMenu.cs`, `src/MonitorBridge/Shared/PasteChannel.cs`
- Modify: `Program.cs`
- Test: `tests/.../ShellMenuTests.cs`

**Interfaces:**
- `ShellMenu`
  - `Register(RegistryKey classesRoot, string exePath)`: 다음 세 키에 쓴다.
    - `Directory\Background\shell\MonitorBridge.Paste`
    - `Directory\shell\MonitorBridge.Paste`
    - `DesktopBackground\shell\MonitorBridge.Paste`
  - 각 키의 값
    - 기본값: 메뉴 이름
    - `Icon`: `"{exe}",0`
    - `command` 기본값: `"{exe}" --paste "%V"`
  - `Register(string exePath)`: HKCU `Software\Classes`를 쓰는 버전
  - `Unregister(RegistryKey classesRoot)`
- `PasteArgs`
  - `string? Folder(string[] args)`: `--paste` 다음 인자를 돌려준다. 끝에 붙은 `"`는 지우고, `D:`처럼 루트만 남으면 `\`를 붙인다.
- `PasteChannel`
  - 파이프 이름: `MonitorBridge.Paste.{현재 사용자 SID}`
  - `PasteChannel.Start(Action<string> onFolder)`: 받은 경로를 UI 스레드에서 넘겨준다. 반환값은 `IDisposable`이다.
  - `static bool TrySend(string folder, int timeoutMs = 2000)`
  - 서버는 `PipeSecurity`로 현재 사용자에게 ReadWrite를 허용한다.

- [ ] **Step 1: 테스트 작성 후 실패 확인**
  - `Register_WritesThreeVerbs`: HKCU `Software\MonitorBridgeTest\{guid}` 아래에 쓰고 값을 확인한 뒤, 정리한다.
  - `PasteArgs_DriveRoot`: `["--paste", "D:\""]` → `D:\`
  - `PasteArgs_Normal`: `["--paste", @"C:\a b"]` → `C:\a b`
  - `PasteChannel_RoundTrip`: 서버를 켜고 `TrySend(@"C:\x")`를 보내면 `onFolder`가 `C:\x`를 받는다. 테스트에서는 UI 스레드 없이 바로 호출한다.
- [ ] **Step 2:** 구현한다.
  - `Program.Main` 맨 앞, 단일 실행 검사보다 먼저 `--paste`를 처리한다.
  - `TrySend`가 실패하면 MessageBox를 띄운다: "Monitor Bridge가 실행 중이 아니에요. 먼저 Monitor Bridge를 실행해 주세요."
- [ ] **Step 3:** 통과를 확인하고 커밋한다.

### Task 4: 클립보드·트레이 연결 (`ClipboardSync`, `TrayContext`)

**Files:** Modify `Shared/ClipboardSync.cs`, `UI/TrayContext.cs`

- [ ] **Step 1: `ClipboardSync`**
  - 파일을 복사하면 `_files.Offer(files)`를 부른다. 다른 것을 복사하면 `_files.Withdraw()`를 부른다.
  - 받은 파일을 클립보드에 넣던 `SetFiles`와 그 echo 처리를 없앤다.
  - 밖으로 내보낼 것: `Pending`, `Request(folder)`, `CancelAll()`, `Progress`
  - `sendBulk`는 `_conn()?.SendBulkAsync`로, `reply`는 `_conn()?.Send`로 넘긴다.
- [ ] **Step 2: `TrayContext`**
  - 켜질 때 `ShellMenu.Register(Environment.ProcessPath!)`를 부른다. 실패해도 계속 진행하고 로그만 남긴다.
  - `PasteChannel.Start(OnPaste)`를 켠다.
  - `OnPaste(folder)`
    - `Pending`이 없으면 알린다: "상대 PC에서 복사한 파일이 없어요. 상대 PC에서 파일을 먼저 복사(Ctrl+C)해 주세요."
    - 있으면 `PasteConfirm`(Task 5)을 띄운다. 가져오기를 누르면 `Request(folder)`를 부르고, 결과(Busy, TooBig)에 맞게 알린다.
  - 트레이 메뉴
    - "파일 전송 보기"를 추가하고, 아이콘을 한 번 클릭해도 열리게 한다.
    - "파일 전송 취소"를 추가한다. 진행 중일 때만 누를 수 있다.
  - 알림 클릭: 시작 알림은 진행 창을, 끝 알림은 폴더를 연다.
  - 받은 파일 저장 폴더(`ReceiveFolders`, `AppConfig.ReceiveFolder`) 관련 코드는 모두 없앤다.
- [ ] **Step 3:** 빌드와 테스트를 확인하고 커밋한다.

### Task 5: 확인 창·진행 창 (frontend-design)

**Files:** Create `UI/PasteConfirm.cs`, `UI/TransferWindow.cs`. Modify `Program.cs`(렌더 확인용 인자).

- [ ] **Step 1: `PasteConfirm`**
  - 작은 창을 커서 근처, 화면 안쪽에 띄운다.
  - 문구: "{상대}에서 복사한 파일 {이름 외 N개}({크기})를 이 폴더로 가져올까요?" 아래 줄에 폴더 경로
  - 버튼: [가져오기] [취소]
  - 크기는 `TransferProgress.Size`로 표시한다.
- [ ] **Step 2: `TransferWindow`**
  - 하나만 띄우고, 다시 열면 그 창을 앞으로 가져온다.
  - 내용: 파일 이름, 진행 막대, `Describe()` 문구, [전송 취소] 버튼. 1초마다 갱신한다.
  - 진행 중이 아닐 때는 "전송 중인 파일이 없어요"를 보여 주고 취소 버튼을 끈다.
- [ ] **Step 3:** `--render-confirm`, `--render-transfer`로 창을 활성화하지 않고 PNG를 만들어 확인한 뒤 커밋한다.

### Task 6: 설정 창·단축키 비우기·안내서

**Files:** Modify `UI/SettingsForm.cs`(+ `HotkeyBox`), `Core/Config/AppConfig.cs`, `tests/.../AppConfigTests.cs`, `README.md`. Delete `Shared/ReceiveFolders.cs`.

- [ ] **Step 1: 설정 값 테스트**
  - `SaveLoad_EmptyHotkeys_StayEmpty`: `LockHotkey = ""`, `QuickSwitchHotkey = ""`로 저장한 뒤 불러와도 `""`이다. 실패를 확인한다.
  - `AppConfig.ReceiveFolder`를 없애고, 관련 테스트 줄도 지운다.
- [ ] **Step 2: `HotkeyBox`**
  - Backspace나 Delete를 누르면 `Value = ""`이 된다.
  - 빈 값이면 회색으로 "사용 안 함"을 보여 준다.
  - 각 칸 옆에 [지우기] 버튼을 둔다.
  - `Problem()`은 두 값이 모두 비어 있지 않을 때만 같은지 비교한다.
- [ ] **Step 3: 설정 창 정리**
  - "받은 파일 저장 폴더" 줄을 지운다.
  - "파일 전송" 줄 아래에 회색 안내를 넣는다: "상대 PC에서 복사한 파일은 탐색기에서 우클릭 → (윈도우 11은 '더 많은 옵션 표시' 또는 Shift+우클릭) → 'Monitor Bridge: 상대 PC에서 복사한 파일 여기에 붙여넣기'로 가져와요."
- [ ] **Step 4: README**
  - 파일 복사 절을 다시 쓴다: 복사 → 우클릭 → 확인 → 진행 창 → 취소 순서
  - 단축키 표에 "비워 두면 사용하지 않아요"를 넣는다.
  - 원본은 지우지 않는다는 점을 적는다.
  - 수동 확인 체크리스트를 넣는다. 관리자 권한 송출컴에서 우클릭 메뉴가 동작하는지 보는 항목이다.
- [ ] **Step 5:** 렌더 PNG를 확인하고 테스트가 통과하면 커밋한다.

### Task 7: 빌드·리뷰·배포
- [ ] `dotnet test`가 통과하는지 확인한다. publish로 exe를 만들고 `monitor-bridge-share\new\`에 복사한다.
- [ ] 새 리뷰어로 전체 리뷰를 하고, 지적 사항을 고친다.
- [ ] 진행 중인 전송이 끝났는지 확인한 뒤, 허락을 받고 두 PC를 업데이트한다. 게임컴은 사용자가 직접 바꾼다.
