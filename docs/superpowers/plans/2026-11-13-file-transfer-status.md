# 파일 전송 상태 표시와 한도 맞추기 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 파일 전송의 시작·진행·끝을 사람이 알 수 있게 하고, 두 PC의 복사 한도 차이를 연결을 끊지 않고 알려 준다.

**Architecture:** 진행률 계산과 문구는 Core의 `TransferProgress`에 둔다. `FileTransfer`가 이 값을 갱신하고, `TrayContext`가 1초마다 트레이 글자에 보여 준다. 설정 창은 같은 값을 받아서 보여 준다. 한도는 새 메시지 `PeerSettings`로 서로 알려 준다.

**Tech Stack:** .NET 8, WinForms, xUnit

**Spec:** `docs/superpowers/specs/2026-11-13-file-transfer-status-design.md`

## Global Constraints
- 화면에 따로 뜨는 창이나 오버레이를 만들지 않는다. 게임컴 화면은 캡처보드로 방송에 나간다. 쓸 수 있는 것은 트레이 풍선, 트레이 글자, 설정 창뿐이다.
- 사람에게 보이는 글은 모두 한국어로 쓴다. 문구는 설계서에 적힌 그대로 쓴다.
- 트레이 글자(`NotifyIcon.Text`)는 127자를 넘기지 않는다. 넘으면 자른다.
- 이전 버전과 섞어 쓰지 않는다. 두 PC를 함께 업데이트한다.

## Review Focus
1. 상대가 `PeerSettings`를 보내지 않았을 때(아직 안 받았거나 연결 직후)는 상대 한도를 모르는 것으로 본다. 내 한도만으로 확인하고 경고는 띄우지 않는다. → Task 1 `EffectiveLimit_UnknownPeer_UsesOwn`
2. 전송 중 연결이 끊기면 진행률이 "진행 중"에 멈춰 있지 않고 실패로 바뀐다. → Task 3 `OnDisconnected` 단계
3. 전송 중 다른 파일을 복사하면 앞 전송은 "취소됨"으로 바뀐다. 40%에 멈춰 있으면 안 된다. → Task 3
4. 트레이 글자가 127자를 넘으면 예외가 난다. → Task 1 `Tooltip_IsCappedAt127`
5. 전송 중에 한도만 바꿔 저장해도 전송이 이어진다. 다시 연결하지 않는다. → Task 4 수동 확인 항목

---

### Task 1: 진행률 계산과 문구 (Core)

**Files:**
- Create: `src/MonitorBridge.Core/Files/TransferProgress.cs`
- Test: `tests/MonitorBridge.Core.Tests/TransferProgressTests.cs`

**Interfaces:**
- Produces:
  - `enum TransferState { Running, Done, Failed, Cancelled }`
  - `sealed record TransferProgress(bool Sending, string PeerName, string Name, long Total, long Done, long ElapsedMs, TransferState State)`
  - 메서드 `string Describe()`
  - 정적 `string Size(long bytes)`
  - 정적 `long EffectiveLimit(long ownBytes, long? peerBytes)`
  - 정적 `string Tooltip(string status, string? progress)`

- [ ] **Step 1: 실패하는 테스트 작성**
  - `Describe_Running`: `new TransferProgress(false, "송출컴", "a.mkv", 10L<<30, 5L<<29, 25_000, Running).Describe()` == `"파일 받는 중 25% · 2.5/10.0GB · 102MB/s · 약 2분 남음"`. 남은 시간은 분 단위로 올림한다.
  - `Describe_Sending_Done`: 보내는 쪽, Done 상태 → `"다 보냈어요: a.mkv"`
  - `Describe_Failed`: → `"전송 실패: a.mkv"`
  - `Describe_NoSpeedYet`: `ElapsedMs = 0` → `"… · 계산 중"`
  - `Size`: 500MB 미만은 `"420MB"`처럼, 1GB 이상은 `"69.6GB"`처럼 쓴다.
  - `EffectiveLimit_UnknownPeer_UsesOwn`: `(500, null)` → 500
  - `EffectiveLimit_TakesSmaller`: `(80000, 500)` → 500
  - `Tooltip_IsCappedAt127`: 긴 입력을 넣어도 길이가 127 이하다.
- [ ] **Step 2:** `dotnet test` → 컴파일 실패를 확인한다.
- [ ] **Step 3:** 구현한다.
  - 속도 = Done / 경과 시간 (MB/s, 정수)
  - Tooltip = `status + "\n" + progress`, 127자에서 자른다.
- [ ] **Step 4:** `dotnet test` → 모두 통과를 확인한다.
- [ ] **Step 5:** 커밋한다.

### Task 2: `PeerSettings` 메시지

**Files:** Modify `Core/Protocol/Messages.cs`, `MessageCodec.cs`, `tests/.../MessageCodecTests.cs`

**Interfaces:** Produces `sealed record PeerSettings(int FileLimitMB) : Message`. 타입 번호는 18이다.

- [ ] **Step 1:** 왕복 테스트 목록에 `new PeerSettings(500)`을 추가한다. 실패를 확인한다.
- [ ] **Step 2:** 인코딩과 디코딩을 구현한다. `dotnet test` 통과를 확인하고 커밋한다.

### Task 3: 전송 쪽 상태·알림·상대 한도 (`FileTransfer`, `ClipboardSync`)

**Files:** Modify `src/MonitorBridge/Shared/FileTransfer.cs`, `src/MonitorBridge/Shared/ClipboardSync.cs`

**Interfaces:**
- Consumes: Task 1, Task 2
- Produces:
  - `FileTransfer(Func<long> limitBytes, string peerName, Action<string> notify, Action<IReadOnlyList<string>> onReceived)`
  - `TransferProgress? Progress { get; }`
  - `int? PeerLimitMB { get; set; }`
  - `void OnDisconnected()`
  - `ClipboardSync.SendSettings()`
  - `ClipboardSync.Progress`
  - `ClipboardSync.PeerLimitMB`
  - `ClipboardSync.OnDisconnected()`

- [ ] **Step 1: 보내는 쪽**
  - `EffectiveLimit(내 한도, 상대 한도)`로 먼저 확인한다. 상대 한도에 걸리면 "상대 PC 한도({N}MB)보다 커서 보내지 않았어요"라고 알린다.
  - 시작 알림: "{peer}으로 파일을 보내기 시작했어요: {이름} ({크기})"
  - 조각을 보낼 때마다 Done을 늘린다. 끝나면 "다 보냈어요"로 바꾼다.
  - 이름은 파일이 하나면 그 이름, 여러 개면 "{첫 이름} 외 {n}개"로 쓴다.
- [ ] **Step 2: 받는 쪽**
  - `FileOfferStart`를 받으면 알림을 띄운다: "{peer}에서 파일을 받기 시작했어요: {크기}". 이름은 첫 `FileEntry`가 오면 채운다.
  - 조각마다 Done을 늘린다.
  - `Complete` 뒤에 "다 받았어요. 붙여넣으세요(Ctrl+V)"라고 알린다.
  - 한도를 넘어서 거절하면 "파일이 이 PC 한도({N}MB)보다 커서 받지 않았어요"라고 알린다.
- [ ] **Step 3: 끊김·취소**
  - 새 전송이 시작되면 이전 것은 Cancelled가 된다.
  - `OnDisconnected()`가 진행 중인 송수신을 Failed로 바꾸고 받던 파일을 취소한다.
- [ ] **Step 4: `ClipboardSync`**
  - `PeerSettings`를 받으면 `PeerLimitMB`를 갱신한다.
  - `SendSettings()`는 지금 연결로 `PeerSettings(내 한도MB)`를 보낸다.
- [ ] **Step 5:** 빌드와 테스트를 확인하고 커밋한다.

### Task 4: 트레이와 저장 동작 (`TrayContext`)

**Files:** Modify `src/MonitorBridge/UI/TrayContext.cs`

- [ ] **Step 1: 연결과 끊김**
  - 연결되면 `_clipboard.SendSettings()`를 호출한다. 게임컴은 `StatusChanged(Connected)`, 송출컴은 `ConnectedChanged(true)` 때다.
  - 끊기면 `_clipboard.OnDisconnected()`를 호출하고 `PeerLimitMB = null`로 지운다.
- [ ] **Step 2: 트레이 글자**
  - UI 타이머(1초)로 `TransferProgress.Tooltip(상태 글자, Progress?.Describe())`를 `_icon.Text`에 넣는다.
  - 상태 글자는 기존 `SetStatus`가 만드는 것을 쓰도록 필드에 저장한다.
  - 진행이 끝난 상태(Done/Failed/Cancelled)는 1분 동안 보이고 사라진다.
- [ ] **Step 3: 트레이 메뉴 한도 줄**
  - 상대 한도를 알 때: "파일 복사 한도: 이 PC 80,000MB · 상대 PC 500MB"
  - 모를 때: 지금처럼 쓴다.
- [ ] **Step 4: 가벼운 저장**
  - `ShowSettings`에서 역할, IP, 포트, 비밀번호, 두 단축키가 같으면 다시 시작하지 않는다.
  - 대신 기존 `_cfg` 객체에 `FileSizeLimitMB`와 `AutoStart`를 그대로 옮기고 저장한다. 게임컴 서비스가 같은 `_cfg` 객체를 쥐고 있어서 객체를 바꾸면 안 된다.
  - 배치가 바뀌었으면 `_host?.ApplyLayout(next.Placements)`를 호출한다.
  - 끝으로 `SendSettings()`를 호출한다.
- [ ] **Step 5:** 빌드하고 커밋한다.

### Task 5: 설정 창 표시 (`SettingsForm`, frontend-design 사용)

**Files:** Modify `src/MonitorBridge/UI/SettingsForm.cs`, `src/MonitorBridge/UI/TrayContext.cs`, `src/MonitorBridge/Program.cs`

**Interfaces:** `SettingsForm` 생성자에 선택 인자를 추가한다.
- `Func<int?>? peerLimitMB = null`
- `Func<TransferProgress?>? progress = null`

- [ ] **Step 1: 경고 줄**
  - 한도 줄 아래에 경고 Label(빨강 `#C0392B`)을 추가한다.
  - 문구: "상대 PC 한도는 {N}MB예요. 작은 쪽({min}MB)까지만 보낼 수 있어요."
  - 입력값을 바꾸면 바로 다시 계산한다. 같으면 숨긴다.
- [ ] **Step 2: 파일 전송 줄**
  - "파일 전송" 줄을 추가해 `Describe()` 또는 "전송 중인 파일이 없어요"를 보여 준다.
  - 폼 안의 타이머(1초)로 갱신한다.
- [ ] **Step 3: 화면 확인**
  - `--render-settings`에 가짜 값(상대 500MB, 진행 25%)을 넣어 PNG로 확인한다.
  - 사용자 화면에 띄우지 않는다. 화면 밖에 그린다. 포커스를 가져갈 수 있으니 이 단계를 실행하기 전에 사용자에게 묻는다.
- [ ] **Step 4:** 커밋한다.

### Task 6: 안내서·빌드·배포

- [ ] **Step 1: README 고치기**
  - 파일 복사 절: 전송 시작 알림, 트레이에 마우스를 올려 진행률 보기, 한도가 다를 때의 경고를 설명한다.
- [ ] **Step 2: 확인과 배포 준비**
  - `dotnet test`가 모두 통과하는지 확인한다.
  - `dotnet publish`로 exe를 만들고 `monitor-bridge-share\new\`에 복사한다.
- [ ] **Step 3: 교체 (각각 허락받은 뒤)**
  - 송출컴: 자동 실행 작업으로 교체한다.
  - 게임컴: 사용자가 `http://172.30.1.5:8000`에서 받는다.
- [ ] **Step 4:** 커밋한다.
