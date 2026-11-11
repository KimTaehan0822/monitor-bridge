# Monitor Bridge 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 게임컴(호스트) 키보드/마우스 하나로, 모니터 단위로 자유롭게 배치한 두 PC를 한 PC처럼 오가게 하고 클립보드(글자, 이미지, 파일)를 공유하는 트레이 프로그램을 만든다.

**Architecture:** 순수 로직(배치 계산, 입력 라우팅, 프로토콜, 암호화, 파일 묶음)은 `MonitorBridge.Core`(net8.0)에 두고 xUnit으로 테스트한다. Win32 훅, SendInput, 커서 숨기기, 클립보드, WinForms UI는 `MonitorBridge`(net8.0-windows) 앱에 둔다. 커서 위치는 호스트가 가상 좌표로 전부 계산하고, 받는 쪽은 절대 좌표 명령만 따른다.

**Tech Stack:** .NET 8 SDK 8.0.427, C# 12, WinForms, xUnit, System.Security.Cryptography(AesGcm, Rfc2898DeriveBytes, HMACSHA256). 외부 NuGet 패키지는 xUnit 테스트용만 쓴다.

**Spec:** `docs/superpowers/specs/2026-11-11-monitor-bridge-design.md`

## Global Constraints

- Windows 10/11 전용. 앱 TFM은 `net8.0-windows`, Core TFM은 `net8.0`.
- Per-Monitor DPI Aware v2. 모든 좌표는 물리 픽셀이다.
- exe 하나. 역할은 설정의 `Role`(Host / Receiver)로 고른다.
- 설정 파일 위치는 `%AppData%\MonitorBridge\config.json`이다.
- 기본 포트는 24850, 받는 쪽이 대기하고 호스트가 접속한다.
- 가두기 키 기본값은 `Scroll`(Scroll Lock), 빠른 전환 키 기본값은 `Ctrl+Alt+Oem3`(백틱)이다.
- 파일 용량 제한 기본값은 500MB이다.
- 임시 폴더는 `%TEMP%\MonitorBridge\<복사ID>\`이고 최근 1개만 남긴다.
- 연결이 끊기면 커서는 반드시 게임컴으로 돌아오고, 호스트는 3초 간격으로 끝없이 재연결을 시도한다.
- UI 문구, 알림, README는 한국어로 쓴다.
- 외부 의존성은 추가하지 않는다(테스트 프로젝트의 xUnit만 예외).

## Review Focus

1. **배율이 다른 모니터 사이 이동** (100% 옆에 125%): 커서가 경계에서 튀지 않고 맞닿은 위치로 넘어가야 한다. → Task 2 `Move_CrossesBetweenDifferentSizes_KeepsVirtualY`.
2. **키를 누른 채 PC를 넘어감** (Ctrl을 누른 채 경계 통과): 떠나는 PC에 눌린 키가 남으면 안 된다. → Task 5 `CrossingWithHeldKey_ReleasesOnLeavingSide`.
3. **커서가 송출컴에 있을 때 연결이 끊김:** 커서가 게임컴에 다시 보여야 한다. → Task 5 `Disconnect_WhileRemote_ReturnsToHost`.
4. **받는 쪽 모니터를 뺌** (커서가 그 모니터에 있을 때): 커서는 남은 모니터로 와야 하고, 배치에서 사라져야 한다. → Task 5 `LayoutChanged_ActiveMonitorRemoved_MovesToHost`.
5. **악의적이거나 망가진 파일 경로** (`..\..\Windows`): 임시 폴더 밖에 쓰면 안 된다. → Task 10 `Receiver_RejectsTraversal`.

---

## File Structure

```
monitor-bridge/
  MonitorBridge.sln
  README.md                                   처음 설정 안내 (Task 11)
  src/MonitorBridge.Core/
    Layout/MonitorInfo.cs                     모니터 정보, 배치 레코드
    Layout/VirtualLayout.cs                   가상 배치, 이동/경계 통과, 좌표 변환, snap
    Protocol/Messages.cs                      메시지 레코드
    Protocol/MessageCodec.cs                  바이너리 직렬화
    Net/SecureChannel.cs                      인증 + AES-GCM 프레임
    Net/PeerConnection.cs                     우선순위 송신 큐, ping, 수신 이벤트
    Config/AppConfig.cs                       설정 + JSON 저장/불러오기
    Config/Hotkey.cs                          단축키 파싱/일치 판정
    Routing/InputRouter.cs                    호스트 입력 라우팅 상태 기계
    Clipboard/ClipboardEcho.cs                받은 내용 되돌려 보내기 방지
    Files/FileBundle.cs                       보낼 파일 목록 + 총 용량
    Files/FileReceiver.cs                     임시 폴더에 안전하게 쓰기
  src/MonitorBridge/
    Program.cs
    Native/Win32.cs                           P/Invoke 모음
    Shared/MonitorEnumerator.cs
    Shared/CursorHider.cs
    Shared/ClipboardSync.cs
    Shared/FileTransfer.cs
    Host/LowLevelHooks.cs
    Host/HostService.cs
    Receiver/InputInjector.cs
    Receiver/ReceiverService.cs
    UI/TrayContext.cs
    UI/SettingsForm.cs
    UI/LayoutCanvas.cs
    UI/AutoStart.cs
  tests/MonitorBridge.Core.Tests/             xUnit
```

---

### Task 1: 솔루션 뼈대와 저장소

**Files:**
- Create: `MonitorBridge.sln`, `src/MonitorBridge.Core/MonitorBridge.Core.csproj`, `src/MonitorBridge/MonitorBridge.csproj`, `tests/MonitorBridge.Core.Tests/MonitorBridge.Core.Tests.csproj`, `.gitignore`

**Interfaces:**
- Produces: 세 프로젝트. 앱과 테스트가 Core를 참조한다.

- [ ] **Step 1:** `git init`, `dotnet new gitignore`, `dotnet new sln`, `dotnet new classlib -o src/MonitorBridge.Core -f net8.0`, `dotnet new winforms -o src/MonitorBridge -f net8.0`(csproj TFM을 `net8.0-windows`로, `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`, `<Nullable>enable</Nullable>`), `dotnet new xunit -o tests/MonitorBridge.Core.Tests -f net8.0`, 참조와 sln 추가. 템플릿 기본 클래스(Class1, Form1)는 지운다.
- [ ] **Step 2:** `dotnet build` → 경고 0, 오류 0. `dotnet test` → 테스트 0개로 통과.
- [ ] **Step 3: Commit** — spec과 plan 문서를 함께 커밋. `chore: scaffold solution`

---

### Task 2: 가상 배치 계산

**Files:**
- Create: `src/MonitorBridge.Core/Layout/MonitorInfo.cs`, `src/MonitorBridge.Core/Layout/VirtualLayout.cs`
- Test: `tests/MonitorBridge.Core.Tests/VirtualLayoutTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record MonitorInfo(string PcId, string DeviceName, int Width, int Height, int OsX, int OsY, double Scale) { public string Key => $"{PcId}/{DeviceName}"; }`
  - `public sealed record Placement(string Key, int X, int Y);`
  - `public readonly record struct VPoint(int X, int Y);`
  - `public sealed record PlacedMonitor(MonitorInfo Info, int X, int Y)` 와 `Right`/`Bottom`(배타 경계), `Contains(VPoint)`.
  - `public sealed class VirtualLayout`
    - `VirtualLayout(IEnumerable<MonitorInfo> monitors, IEnumerable<Placement> placements)`: placement가 없는 모니터는 기존 모니터들 오른쪽 끝에 붙여 놓는다. 모니터가 없는 placement는 버린다.
    - `IReadOnlyList<PlacedMonitor> Monitors`
    - `PlacedMonitor? MonitorAt(VPoint p)`
    - `VPoint Move(VPoint from, int dx, int dy)`
    - `(PlacedMonitor Monitor, int OsX, int OsY) ToOs(VPoint p)`: `OsX = Info.OsX + (p.X - X)`.
    - `VPoint? FromOs(string pcId, int osX, int osY)`
    - `PlacedMonitor? Neighbor(VPoint edgePoint, int dirX, int dirY)`: `edgePoint + (dirX, dirY)` 위치에 있는 모니터.
    - `static (int X, int Y) Snap(int x, int y, int w, int h, IEnumerable<PlacedMonitor> others, int threshold = 20)`
    - `IEnumerable<Placement> ToPlacements()`

**Move 알고리즘** (테스트만으로는 정해지지 않으므로 명시): X축을 먼저 처리하고 Y축을 처리한다. 각 축에서 남은 이동량이 0이 될 때까지 반복한다. 현재 모니터 안에서 갈 수 있는 만큼 가고, 경계 픽셀에 닿았는데 이동량이 남으면 `Neighbor(경계점, 방향)`을 본다. 이웃이 있으면 그 모니터로 넘어가 남은 이동량으로 계속하고, 없으면 경계에 멈춘다. 시작점이 어느 모니터에도 없으면 가장 가까운 모니터 안으로 끌어온다.

- [ ] **Step 1: 실패하는 테스트 작성.** 배치 `A(1920x1080 @0,0) | B(2560x1440 @1920,-180) | C(1920x1080 @4480,0)`, A와 C는 PcId "stream", B는 "game".
  - `Move_WithinMonitor_AddsDelta`: `Move((100,100), 10, -5) == (110,95)`
  - `Move_CrossesRightEdgeIntoNeighbor`: `Move((1915,500), 10, 0) == (1925,500)` 이고 `MonitorAt`의 PcId는 "game".
  - `Move_CrossesBetweenDifferentSizes_KeepsVirtualY`: `Move((4475,1000), 10, 0)`의 Y가 1000이고 C 위에 있다.
  - `Move_NoNeighbor_StopsAtEdge`: `Move((5,500), -50, 0) == (0,500)`.
  - `Move_EdgeWithoutOverlap_Stops`: B의 맨 위(Y=-180)에서 왼쪽으로 가면 A가 그 높이에 없으므로 `X == 1920`에서 멈춘다.
  - `Move_LargeDeltaSpansTwoMonitors`: `Move((1900,500), 3000, 0)`이 C 위에 있고 `X == 4900`.
  - `ToOs_FromOs_RoundTrip`: 송출컴 OsX가 1920인 모니터(C)에 대해 `ToOs((4500,10)) == (C, 1940, 10)`이고, `FromOs("stream",1940,10) == (4500,10)`.
  - `Constructor_UnplacedMonitorAppendedRight`
  - `Snap_WithinThreshold_AlignsEdges`: 오른쪽 경계에서 15px 떨어진 사각형은 딱 붙는다. 25px이면 그대로다.
- [ ] **Step 2:** `dotnet test --filter VirtualLayoutTests` → FAIL(타입 없음).
- [ ] **Step 3:** 위 인터페이스대로 구현.
- [ ] **Step 4:** `dotnet test --filter VirtualLayoutTests` → 전부 PASS.
- [ ] **Step 5: Commit** `feat: virtual monitor layout`

---

### Task 3: 메시지와 직렬화

**Files:**
- Create: `src/MonitorBridge.Core/Protocol/Messages.cs`, `src/MonitorBridge.Core/Protocol/MessageCodec.cs`
- Test: `tests/MonitorBridge.Core.Tests/MessageCodecTests.cs`

**Interfaces:**
- Produces (`abstract record Message` 하위):
  `MonitorList(MonitorInfo[] Monitors)`, `CursorEnter(int OsX, int OsY)`, `CursorMove(int OsX, int OsY)`, `CursorLeave()`, `MouseButton(MouseButtonKind Button, bool Down)`(enum `Left, Right, Middle, X1, X2`), `MouseWheel(bool Horizontal, int Delta)`, `Key(ushort ScanCode, bool Extended, bool Down)`, `ReleaseAll()`, `ClipboardText(string Text)`, `ClipboardImage(byte[] Png)`, `FileOfferStart(Guid Id, long TotalBytes, int Count)`, `FileEntry(Guid Id, string RelativePath, bool IsDirectory, long Length)`, `FileChunk(Guid Id, byte[] Data)`, `FileOfferEnd(Guid Id)`, `FileCancel(Guid Id)`, `Ping()`.
  - `static class MessageCodec { byte[] Encode(Message m); Message Decode(ReadOnlySpan<byte> data); }`: 첫 바이트가 타입이고 나머지는 `BinaryWriter` 필드이다. 모르는 타입이나 잘린 데이터는 `InvalidDataException`.

- [ ] **Step 1: 테스트 작성.** `[Theory]`로 모든 메시지 타입의 `Decode(Encode(m))`가 원본과 같은지 확인한다(배열 필드는 `SequenceEqual`). `Decode_UnknownType_Throws`, `Decode_Truncated_Throws`.
- [ ] **Step 2:** 실행 → FAIL.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 실행 → PASS.
- [ ] **Step 5: Commit** `feat: wire protocol messages`

---

### Task 4: 보안 채널과 연결

**Files:**
- Create: `src/MonitorBridge.Core/Net/SecureChannel.cs`, `src/MonitorBridge.Core/Net/PeerConnection.cs`
- Test: `tests/MonitorBridge.Core.Tests/SecureChannelTests.cs`, `tests/MonitorBridge.Core.Tests/PeerConnectionTests.cs`

**Interfaces:**
- Consumes: `MessageCodec` (Task 3).
- Produces:
  - `sealed class SecureChannel : IAsyncDisposable`
    - `static Task<SecureChannel> ConnectAsync(Stream s, string password, CancellationToken ct)` (호스트 쪽)
    - `static Task<SecureChannel> AcceptAsync(Stream s, string password, CancellationToken ct)` (받는 쪽)
    - `Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)`, `Task<byte[]> ReceiveAsync(CancellationToken ct)`
    - 비밀번호가 틀리면 양쪽 모두 `System.Security.Authentication.AuthenticationException`.
  - `sealed class PeerConnection : IAsyncDisposable`
    - `PeerConnection(SecureChannel ch)`, `Task RunAsync(CancellationToken ct)`(끊기면 끝남)
    - `void Send(Message m, bool bulk = false)`: 논블로킹. bulk가 아닌 메시지가 항상 먼저 나간다.
    - `event Action<Message>? Received`, `event Action? Closed`
    - 2초마다 `Ping`을 보내고, 6초 동안 아무것도 못 받으면 닫는다.

**인증 규칙** (명시): 마스터 키 = `Rfc2898DeriveBytes.Pbkdf2(password, salt: "MonitorBridge-v1"(UTF-8), 100_000, SHA256, 32)`. 접속하는 쪽과 받는 쪽이 각각 16바이트 난수를 보내고, 세션 키 = `HMACSHA256(master, nonceC ‖ nonceS)`. 각자 `HMACSHA256(session, "client")` / `"server"`를 보내고 `CryptographicOperations.FixedTimeEquals`로 확인한다. 프레임은 `[4바이트 길이][12바이트 nonce][암호문][16바이트 태그]` AES-GCM이다. nonce는 방향별 카운터이고, 방향 구분을 위해 첫 바이트에 방향(0/1)을 둔다. 프레임 최대 크기는 1MiB이고, 넘으면 연결을 닫는다. 받은 nonce 카운터가 증가하지 않으면 닫는다(재전송 방지).

- [ ] **Step 1: 테스트 작성.** 양방향 메모리 스트림(로컬 `TcpListener` 루프백 사용 가능).
  - `SamePassword_RoundTripsData`
  - `WrongPassword_BothSidesThrowAuthenticationException`
  - `TamperedFrame_Throws` (암호문 1바이트 변조)
  - `OversizedFrame_Closes`
  - `PeerConnection_InputBeatsBulk`: bulk `FileChunk` 20개를 넣은 뒤 `Key` 1개를 넣으면, 받는 쪽에서 `Key`가 bulk 3개 이내로 도착한다.
  - `PeerConnection_NoTraffic_ClosesAfterTimeout` (테스트용으로 타임아웃 값을 생성자 선택 인자로 줄인다: `TimeSpan? pingInterval = null, TimeSpan? timeout = null`)
- [ ] **Step 2:** 실행 → FAIL.
- [ ] **Step 3:** 구현. bulk 메시지는 64KiB 청크 단위로 보내는 쪽이 끊어 넣는다고 가정한다(Task 10).
- [ ] **Step 4:** 실행 → PASS.
- [ ] **Step 5: Commit** `feat: authenticated encrypted channel`

---

### Task 5: 설정, 단축키, 입력 라우터

**Files:**
- Create: `src/MonitorBridge.Core/Config/AppConfig.cs`, `src/MonitorBridge.Core/Config/Hotkey.cs`, `src/MonitorBridge.Core/Routing/InputRouter.cs`
- Test: `tests/MonitorBridge.Core.Tests/AppConfigTests.cs`, `HotkeyTests.cs`, `InputRouterTests.cs`

**Interfaces:**
- Consumes: `VirtualLayout`, `VPoint`, `Message` 하위 타입.
- Produces:
  - `enum Role { Host, Receiver }`
  - `sealed class AppConfig { Role Role; string Password = ""; string ReceiverAddress = ""; int Port = 24850; string LockHotkey = "Scroll"; string QuickSwitchHotkey = "Ctrl+Alt+Oem3"; int FileSizeLimitMB = 500; bool AutoStart; List<Placement> Placements = new(); static AppConfig Load(string path); void Save(string path); }` (파일이 없거나 깨졌으면 기본값, 깨진 파일은 `.bak`으로 옮겨 둔다).
  - `readonly record struct Hotkey(int VirtualKey, bool Ctrl, bool Alt, bool Shift, bool Win) { static bool TryParse(string s, out Hotkey h); override string ToString(); }`: 키 이름은 `System.Windows.Forms.Keys` 이름과 같은 VK 표(Core에는 WinForms가 없으므로 Core 안에 이름→VK 표를 둔다: A–Z, 0–9, F1–F24, Scroll, Pause, Oem3, Insert, Home 등).
  - `sealed class HotkeyMatcher { HotkeyMatcher(Hotkey h); bool OnKey(int vk, bool down); }`: 수정 키 상태를 추적하고, 대상 키가 눌리는 순간 true를 돌려준다.
  - `sealed class InputRouter`
    - `InputRouter(VirtualLayout layout, string hostPcId, VPoint startOs)`
    - `string ActivePcId`, `VPoint Position`
    - `RouterResult OnMouseMove(int x, int y)`: 호스트일 때 x,y는 훅의 OS 좌표, 원격일 때는 파킹 지점 기준 변화량을 계산한 OS 좌표이다.
    - `RouterResult OnButton(MouseButtonKind b, bool down)`, `RouterResult OnWheel(bool horizontal, int delta)`, `RouterResult OnKey(ushort scan, bool extended, bool down)`
    - `RouterResult QuickSwitch()`, `RouterResult OnDisconnected()`, `RouterResult OnLayoutChanged(VirtualLayout layout)`
  - `sealed record RouterResult(bool Block, IReadOnlyList<Message> ToRemote, IReadOnlyList<LocalAction> Local)`
  - `abstract record LocalAction`: `ParkAndHide(int OsX, int OsY)`, `UnparkAndShow(int OsX, int OsY)`, `ReleaseLocalKeys(ushort[] ScanCodes, MouseButtonKind[] Buttons)`

**라우팅 규칙** (명시):
- 호스트 활성 상태: 이동은 `Block=false`. 경계 통과 판정은 `pt`가 호스트 모니터 경계 밖이거나, 경계 픽셀 위이면서 바깥쪽으로 움직였을 때(직전 점과 같은 점에서 다시 이벤트가 오면 바깥쪽으로 민 것으로 본다)이다. 이때 `Neighbor`가 원격 모니터이면 전환한다. 전환 시 결과는 `Block=true`, `Local=[ReleaseLocalKeys(호스트에 눌린 것), ParkAndHide(호스트 모니터 중앙)]`, `ToRemote=[CursorEnter(os)]`이다.
- 원격 활성 상태: 모든 입력 `Block=true`. 이동은 `Position = layout.Move(Position, dx, dy)`이다. 같은 원격 PC면 `CursorMove`, 호스트 모니터로 왔으면 `ToRemote=[ReleaseAll, CursorLeave]`, `Local=[UnparkAndShow(os)]`, `Block=true`로 전환한다.
- 키/버튼: 활성 PC에 눌린 것을 추적한다. 새 PC에서 누른 적 없는 키의 "뗌"은 버린다.
- `QuickSwitch`: 반대쪽 PC의 마지막 위치(없으면 그 PC 첫 모니터 중앙)로 위와 같은 전환을 한다.
- `OnDisconnected`: 원격 활성이면 `UnparkAndShow(호스트 마지막 위치)`.
- `OnLayoutChanged`: 활성 위치가 사라진 모니터 위면 호스트로 전환(`UnparkAndShow`).

- [ ] **Step 1: 테스트 작성.**
  - Config: `Load_MissingFile_ReturnsDefaults`, `SaveLoad_RoundTrip`, `Load_Corrupt_ReturnsDefaultsAndBacksUp`
  - Hotkey: `TryParse("Ctrl+Alt+Oem3")`의 VK가 0xC0이고 Ctrl/Alt가 true. `TryParse("Scroll")`의 VK는 0x91. `TryParse("Ctrl+Nope")`는 false. `Matcher_FiresOnlyWithModifiers`.
  - Router(Task 2의 A|B|C 배치, host = "game"):
    - `MoveInsideHost_Passes`
    - `PushPastLeftEdge_SwitchesToStream`: B 왼쪽 경계(OS x=0)에서 x=-1 이동 → `Block`, `CursorEnter`, A의 OS 오른쪽 끝 부근.
    - `PushAtEdgeClamped_Switches`: 경계 픽셀에서 같은 점 이벤트 2번 → 전환.
    - `RemoteMove_SendsCursorMove`
    - `RemoteMoveBackOntoHost_UnparksAndReleases`
    - `RemoteToOtherRemoteMonitorAcrossHost_GoesThroughHost`: A에서 오른쪽으로 가면 C가 아니라 B(호스트)로 온다. 1 3 2 핵심 시나리오다.
    - `CrossingWithHeldKey_ReleasesOnLeavingSide`: Ctrl을 누른 채 전환 → `ReleaseLocalKeys`에 Ctrl 스캔코드. 원격에서 Ctrl 뗌은 버려진다.
    - `QuickSwitch_ReturnsToLastPosition`
    - `Disconnect_WhileRemote_ReturnsToHost`
    - `LayoutChanged_ActiveMonitorRemoved_MovesToHost`
- [ ] **Step 2:** 실행 → FAIL.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** `dotnet test` → 전부 PASS.
- [ ] **Step 5: Commit** `feat: config, hotkeys and input router`

---

### Task 6: Win32 기반 (모니터 목록, 커서 숨기기, 입력 주입)

**Files:**
- Create: `src/MonitorBridge/Native/Win32.cs`, `src/MonitorBridge/Shared/MonitorEnumerator.cs`, `src/MonitorBridge/Shared/CursorHider.cs`, `src/MonitorBridge/Receiver/InputInjector.cs`
- Temp: `src/MonitorBridge/Program.cs`에 `--selftest` 인자로 아래 수동 확인을 실행하는 코드를 넣는다(Task 9에서 정리).

**Interfaces:**
- Produces:
  - `static class MonitorEnumerator { IReadOnlyList<MonitorInfo> GetMonitors(); }`: `EnumDisplayMonitors` + `GetMonitorInfo(MONITORINFOEX)` + `GetDpiForMonitor(MDT_EFFECTIVE_DPI)/96.0`. PcId는 `Environment.MachineName`.
  - `static class CursorHider { void Hide(); void Restore(); bool IsHidden { get; } }`: Hide는 OCR_NORMAL, IBEAM, WAIT, CROSS, UP, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, NO, HAND, APPSTARTING, HELP마다 새 32x32 투명 커서(`CreateCursor`, AND 마스크 0xFF, XOR 마스크 0x00)로 `SetSystemCursor`를 부른다. Restore는 `SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0)`.
  - `sealed class InputInjector { void MoveTo(int osX, int osY); void Button(MouseButtonKind b, bool down); void Wheel(bool horizontal, int delta); void Key(ushort scan, bool extended, bool down); void ReleaseAll(); }`: `MoveTo`는 `MOUSEEVENTF_MOVE|ABSOLUTE|VIRTUALDESK`, 좌표는 `SM_XVIRTUALSCREEN` 기준 0–65535로 정규화한다. `Key`는 `KEYEVENTF_SCANCODE`(+`EXTENDEDKEY`). 눌린 키/버튼을 추적해서 `ReleaseAll`이 전부 뗀다.

- [ ] **Step 1:** 구현.
- [ ] **Step 2:** `dotnet build` → 오류 0.
- [ ] **Step 3: 수동 확인** (`MonitorBridge.exe --selftest`):
  - 모니터 목록이 디스플레이 설정과 같다(개수, 해상도, 배율).
  - 커서가 3초 숨었다가 돌아온다. **이때 OBS 디스플레이 캡처 미리보기에도 커서가 안 보여야 한다**(spec 미검증 항목, 실패하면 멈추고 사용자에게 보고).
  - 각 모니터 중앙으로 `MoveTo`가 정확히 간다(배율이 다른 모니터 포함).
  - 메모장에 `Key`로 "ab"를 입력한다.
  - 숨긴 상태에서 프로세스를 강제 종료한 뒤 다시 실행하면, 시작할 때 `Restore`로 커서가 돌아온다.
- [ ] **Step 4: Commit** `feat: win32 monitor, cursor and input injection`

---

### Task 7: 호스트 훅과 서비스

**Files:**
- Create: `src/MonitorBridge/Host/LowLevelHooks.cs`, `src/MonitorBridge/Host/HostService.cs`

**Interfaces:**
- Consumes: `InputRouter`, `HotkeyMatcher`, `PeerConnection`, `SecureChannel.ConnectAsync`, `MonitorEnumerator`, `CursorHider`, `AppConfig`.
- Produces:
  - `abstract record HookEvent` 하위: `HookMouseMove(int X, int Y)`, `HookMouseButton(MouseButtonKind Button, bool Down)`, `HookMouseWheel(bool Horizontal, int Delta)`, `HookKey(int Vk, ushort Scan, bool Extended, bool Down)`.
  - `sealed class LowLevelHooks : IDisposable { LowLevelHooks(Func<HookEvent, bool> handler); void Install(); void Uninstall(); }`: 전용 스레드에서 메시지 루프를 돌린다. `LLMHF_INJECTED`/`LLKHF_INJECTED` 이벤트는 무시한다. handler가 true면 이벤트를 막는다. handler는 빨라야 하므로 네트워크 송신은 `PeerConnection.Send`(논블로킹)만 쓴다.
  - `sealed class HostService : IDisposable { HostService(AppConfig cfg); void Start(); event Action<HostStatus>? StatusChanged; }` 와 `enum HostStatus { Disconnected, Connected, Locked }`.

**동작:**
- 3초 간격 재연결 루프(끝없이). 연결되면 받는 쪽 `MonitorList`를 받아 `VirtualLayout`을 만들고, 새 모니터를 배치에 추가해 저장한다.
- 원격 활성 중 마우스 이동: 파킹 지점(`ParkAndHide` 좌표)과의 차이를 변화량으로 쓰고, 처리 후 `SetCursorPos(park)`로 되돌린다.
- 가두기 키(커서가 호스트일 때만): 누르면 `Uninstall` 후 `RegisterHotKey(lock)`. 그 핫키가 오면 `UnregisterHotKey` 후 `Install`. 원격일 때 누르면 무시한다(막지도 보내지도 않음).
- 빠른 전환 키: 훅 안의 `HotkeyMatcher`로 감지해서 `QuickSwitch()`. 그 키 이벤트는 막는다.
- `RouterResult.Local`을 실행한다: `ReleaseLocalKeys` → `SendInput` 뗌, `ParkAndHide` → `SetCursorPos` + `CursorHider.Hide`, `UnparkAndShow` → `CursorHider.Restore` + `SetCursorPos`.

- [ ] **Step 1:** 구현.
- [ ] **Step 2:** `dotnet build` → 오류 0.
- [ ] **Step 3: 수동 확인**은 Task 8과 함께 한다.
- [ ] **Step 4: Commit** `feat: host hooks and service`

---

### Task 8: 받는 쪽 서비스 + 두 PC 연결 확인

**Files:**
- Create: `src/MonitorBridge/Receiver/ReceiverService.cs`

**Interfaces:**
- Consumes: `SecureChannel.AcceptAsync`, `PeerConnection`, `InputInjector`, `CursorHider`, `MonitorEnumerator`.
- Produces: `sealed class ReceiverService : IDisposable { ReceiverService(AppConfig cfg); void Start(); event Action<bool>? ConnectedChanged; }`

**동작:** 시작하면 `CursorHider.Hide()`. `TcpListener(Any, Port)`로 대기하고, 새 연결이 오면 기존 연결을 끊고 교체한다. 연결 직후와 `WM_DISPLAYCHANGE`(숨은 창으로 수신) 때마다 `MonitorList`를 보낸다. `CursorEnter` → `Restore` + `MoveTo`, `CursorMove` → `MoveTo`, `CursorLeave` → `Hide`, 입력 메시지 → `InputInjector`, `ReleaseAll` → `ReleaseAll`. 연결이 끊기면 `ReleaseAll` + `Hide`.

- [ ] **Step 1:** 구현. Program.cs 임시 진입점에서 `--host`/`--receiver`로 각 서비스를 config 없이 하드코딩 값으로 실행할 수 있게 한다.
- [ ] **Step 2:** `dotnet build` → 오류 0.
- [ ] **Step 3: 수동 확인 (두 PC)**. 배치를 `1 | 3 | 2`로 하드코딩한다.
  - 1 → 3 → 2, 2 → 3 → 1로 끊김 없이 이동한다. 송출컴 1과 2 사이를 바로 건너뛰지 않는다.
  - 송출컴에서 클릭, 드래그, 휠, 사이드 버튼, 한글 입력(한/영 키), Ctrl+C/V가 된다.
  - Ctrl을 누른 채 경계를 넘어도 어느 쪽에도 Ctrl이 남지 않는다.
  - 송출컴 커서가 있을 때 랜선을 뽑으면 6초 안에 커서가 게임컴에 보인다. 다시 꽂으면 자동으로 다시 연결된다.
  - 커서가 게임컴에 있을 때 OBS 미리보기에 송출컴 커서가 안 보인다.
  - 가두기 키를 켜면 경계를 넘지 않는다. 다시 누르면 풀린다.
  - 빠른 전환 키를 누르면 마지막 위치로 간다.
  - 비밀번호가 다르면 연결되지 않는다.
- [ ] **Step 4: Commit** `feat: receiver service`

---

### Task 9: 트레이, 설정 창, 배치 편집기, 자동 실행

**Files:**
- Create: `src/MonitorBridge/UI/TrayContext.cs`, `src/MonitorBridge/UI/SettingsForm.cs`, `src/MonitorBridge/UI/LayoutCanvas.cs`, `src/MonitorBridge/UI/AutoStart.cs`
- Modify: `src/MonitorBridge/Program.cs` (임시 진입점 제거, `--selftest`는 남김)

**Interfaces:**
- Consumes: `AppConfig`, `HostService`, `ReceiverService`, `VirtualLayout.Snap`, `Hotkey.TryParse`.
- Produces: `static class AutoStart { bool IsEnabled(); void Enable(string exePath); void Disable(); }`: `schtasks /Create /TN MonitorBridge /SC ONLOGON /RL HIGHEST /TR "\"<exe>\"" /F`와 `/Delete /TN MonitorBridge /F`. 관리자가 아니면 `runas`로 자신을 `--autostart on|off`로 다시 실행한다.

**동작:**
- `Program.Main`: 시작하자마자 `CursorHider.Restore()`. 단일 인스턴스(named Mutex). config가 비어 있으면 설정 창부터 연다.
- 트레이 아이콘 3종(연결됨 / 끊김 / 가두기). 메뉴: "설정", "종료". 종료 시 `Restore` + 서비스 정리.
- 설정 창: 역할, 비밀번호, 송출컴 IP(호스트만), 포트, 가두기 키, 빠른 전환 키(입력란에 눌러서 지정), 파일 용량 제한(MB), "윈도우 로그인 시 관리자 권한으로 자동 실행", 배치 편집기(호스트만). 저장하면 서비스를 다시 시작한다.
- 배치 편집기: 모든 모니터를 비율대로 축소한 사각형으로 그린다. 라벨은 "게임컴 1", "송출컴 2"(PC별 OS 좌표 순서)이고 PC별로 색이 다르다. 끌어 놓으면 `Snap`. 모니터끼리 겹치게 놓으면 저장 버튼을 끄고 "모니터가 겹쳐 있어요"를 표시한다. 연결 안 됐으면 "송출컴과 연결되면 모니터가 표시돼요"를 표시한다.
- 모니터를 못 찾아 배치에서 빠지면 트레이 알림을 띄운다. 용량 초과 알림도 같은 경로를 쓴다.

- [ ] **Step 1:** 구현.
- [ ] **Step 2:** `dotnet build` → 오류 0.
- [ ] **Step 3: 수동 확인:** 설정 저장 후 재시작해도 값이 유지된다. 배치를 바꾸면 커서 경로가 바뀐다. 자동 실행을 켜고 재부팅하면 송출컴에서 관리자 권한으로 뜨고(작업 관리자 "권한 상승" 열), 관리자로 켠 OBS를 조작할 수 있다. 송출컴 로그인을 늦게 해도 호스트가 알아서 붙는다.
- [ ] **Step 4: Commit** `feat: tray, settings and layout editor`

---

### Task 10: 클립보드 (글자, 이미지, 파일 단순판)

**Files:**
- Create: `src/MonitorBridge.Core/Clipboard/ClipboardEcho.cs`, `src/MonitorBridge.Core/Files/FileBundle.cs`, `src/MonitorBridge.Core/Files/FileReceiver.cs`, `src/MonitorBridge/Shared/ClipboardSync.cs`, `src/MonitorBridge/Shared/FileTransfer.cs`
- Modify: `HostService.cs`, `ReceiverService.cs` (둘 다 `ClipboardSync` 연결)
- Test: `tests/MonitorBridge.Core.Tests/ClipboardEchoTests.cs`, `FileBundleTests.cs`, `FileReceiverTests.cs`

**Interfaces:**
- Consumes: `PeerConnection.Send(m, bulk)`, 파일/클립보드 메시지(Task 3).
- Produces:
  - `sealed class ClipboardEcho { void MarkReceived(byte[] content); bool IsEcho(byte[] content); }`: SHA-256 해시로 비교한다. 받은 직후 내 클립보드 변화가 같은 내용이면 보내지 않는다.
  - `static class FileBundle { (IReadOnlyList<FileEntryPlan> Entries, long TotalBytes) Plan(IEnumerable<string> paths); }` 와 `record FileEntryPlan(string FullPath, string RelativePath, bool IsDirectory, long Length)`: 최상위 이름을 기준으로 상대 경로를 만든다(폴더는 하위 전체 포함). 읽을 수 없는 파일은 건너뛴다.
  - `sealed class FileReceiver { FileReceiver(string tempRoot, Guid id); void Add(FileEntry e); void Write(byte[] chunk); IReadOnlyList<string> Complete(); void Cancel(); static void CleanupExcept(string tempRoot, Guid? keep); }`: 항목 순서대로 내용을 이어 쓴다. `Complete`는 최상위 경로 목록을 돌려준다. 상대 경로가 루트 밖으로 나가면(`..`, 절대 경로, 드라이브 문자, 잘못된 문자) `InvalidDataException`.
  - `sealed class ClipboardSync : IDisposable { ClipboardSync(PeerConnection conn, AppConfig cfg, Action<string> notify); void OnMessage(Message m); }`: UI 스레드(STA)의 숨은 창에서 `AddClipboardFormatListener`를 쓴다. 우선순위는 파일(`CF_HDROP`) > 이미지 > 글자. 이미지는 "PNG" 형식이 있으면 그대로, 없으면 `Clipboard.GetImage()`를 PNG로 바꾼다. 받은 이미지는 `Bitmap`과 "PNG" 형식으로 함께 넣는다. 클립보드 열기 실패(다른 앱이 잡고 있음)는 100ms 간격 5회 재시도한다.
  - `FileTransfer`: 보낼 때 `TotalBytes > FileSizeLimitMB`이면 보내지 않고 `notify("파일이 {limit}MB를 넘어서 보내지 않았어요")`. 아니면 `FileOfferStart`, `FileEntry`들, 64KiB `FileChunk`(bulk), `FileOfferEnd` 순서로 보낸다. 새 복사가 생기면 진행 중인 전송을 취소하고 `FileCancel`을 보낸다. 받는 쪽은 `FileOfferEnd`에서 `Complete` → `Clipboard.SetFileDropList` + `ClipboardEcho.MarkReceived`. 받는 쪽도 TotalBytes가 제한을 넘으면 거절한다(신뢰 경계). 시작할 때 `CleanupExcept(root, null)`, 새 전송 시작 시 이전 폴더를 지운다.

- [ ] **Step 1: 테스트 작성.**
  - `Echo_SameContent_IsEcho`, `Echo_DifferentContent_NotEcho`
  - `Plan_FolderIncludesNestedFiles_WithRelativePaths` (임시 폴더에 `a/b/c.txt` 생성 → 상대 경로 `a\b\c.txt`), `Plan_TotalBytesSumsFiles`
  - `Receiver_WritesFilesAndReturnsTopLevel`
  - `Receiver_RejectsTraversal` (`..\evil.txt`, `C:\evil.txt`, `\\server\x`)
  - `Receiver_Cancel_DeletesFolder`
  - `CleanupExcept_KeepsOnlyGiven`
- [ ] **Step 2:** 실행 → FAIL.
- [ ] **Step 3:** Core 부분 구현 → `dotnet test` PASS. 이어서 앱 부분 구현 → `dotnet build` 오류 0.
- [ ] **Step 4: 수동 확인 (두 PC, 양방향):** 한글 글자 복사/붙여넣기, 캡처 도구 스크린샷 → 상대 PC 그림판 붙여넣기, 폴더 하나(하위 폴더 포함) → 탐색기 붙여넣기, 600MB 파일 → 알림만 뜨고 전송 없음, 큰 파일 전송 중 마우스가 버벅이지 않음, 받은 내용이 다시 돌아와 반복되지 않음.
- [ ] **Step 5: Commit** `feat: clipboard sync with simple file transfer`

---

### Task 11: 안내 문서와 배포 파일

**Files:**
- Create: `README.md`
- Modify: `src/MonitorBridge/MonitorBridge.csproj` (단일 파일 게시 설정)

- [ ] **Step 1:** README를 spec §6.8의 단계 그대로 한국어로 쓴다. 1단계 "송출컴에 키보드와 마우스를 잠깐 꽂으세요"를 굵게 강조한다. 인풋디렉터 끄기, 방화벽 허용(포트 24850, 개인 네트워크), 로그인 비밀번호가 있는 경우, 관리자 권한 앱이 조작 안 될 때, 기본 단축키 표, 지원하지 않는 것(UAC 창, 로그인 화면, 송출컴에서 게임)도 넣는다.
- [ ] **Step 2:** `dotnet publish src/MonitorBridge -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` → `MonitorBridge.exe` 하나가 생긴다. .NET이 없는 PC에서 실행된다.
- [ ] **Step 3:** 최종 확인: `dotnet test` 전부 PASS. 두 PC에서 Task 8, 10의 수동 확인 목록을 게시된 exe로 다시 한 번 실행한다.
- [ ] **Step 4: Commit** `docs: setup guide and publish settings`
