# 파일 전송 속도 제한 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 보내는 쪽 파일 전송 속도를 MB/s로 제한하고(기본 30, 0 = 제한 없음), 두 PC 중 작은 값을 적용하며, 설정 창에 접히는 속도 기준을 보여 준다.

**Architecture:** Core에 순수 계산(`TransferProgress.EffectiveSpeed`, `SpeedPacer`)과 새 메시지 `PeerSpeed`(22)를 둔다. 앱 쪽 `FileTransfer`가 조각을 보낼 때마다 `SpeedPacer`가 알려 주는 만큼 기다린다. 값은 `ClipboardSync.SendSettings`로 교환하고, 설정 창·트레이가 보여 준다.

**Tech Stack:** .NET 8 WinForms, xUnit (`dotnet test`).

**Spec:** `docs/superpowers/specs/2026-11-14-file-speed-limit-design.md`

## Global Constraints

- 화면에 보이는 글은 모두 한국어.
- 단위 MB/s, 1MB = 1,048,576바이트(코드의 `MiB`와 같음). 기본값 30, 0 = 제한 없음, 최대 1,000.
- 적용 값: 두 PC 중 작은 쪽, 0은 가장 큰 값으로 취급, 상대 값을 모르면 내 값.
- 예전 버전과 연결이 끊기면 안 된다(새 메시지는 예전 버전에서 `UnknownMessage`로 버려진다).
- 키보드·마우스 신호는 제한하지 않는다. 받는 쪽 코드는 바꾸지 않는다.
- 원본 파일은 절대 지우거나 바꾸지 않는다(지금 동작 유지).
- 설정 창은 명시적 칸 배치(`grid.Controls.Add(c, col, row)`)를 유지한다.

## Review Focus

1. 예전 `config.json`(속도 칸 없음)을 불러오면 0(제한 없음)이 아니라 30이어야 한다 → Task 1 `Load_OldFileWithoutSpeed_Uses30`.
2. 전송 중 값이 바뀌면(내가 저장, 상대가 보냄, 0↔숫자) 다음 조각부터 새 속도, 밀린 만큼 몰아서 보내지 않음 → Task 2 `RateChange_StartsFresh`, `SlowSender_DoesNotBurst`.
3. 제한 때문에 기다리는 중에 전송 취소하면 바로 멈춰야 한다 → Task 3 `Cancel_DuringPacedSend_StopsQuickly`.
4. 예전 버전 상대(속도 정보 없음) → 내 값만 적용 → Task 2 `EffectiveSpeed_UnknownPeer_UsesOwn`.
5. 속도 기준을 펼치고 접어도 설정 창 줄이 어긋나지 않아야 한다(게임컴·송출컴 둘 다) → Task 5 렌더 확인.

---

### Task 1: 설정 값과 메시지

**Files:**
- Modify: `src/MonitorBridge.Core/Config/AppConfig.cs`, `src/MonitorBridge.Core/Protocol/Messages.cs`, `src/MonitorBridge.Core/Protocol/MessageCodec.cs`
- Test: `tests/MonitorBridge.Core.Tests/AppConfigTests.cs`, `tests/MonitorBridge.Core.Tests/MessageCodecTests.cs`

**Interfaces:**
- Produces: `AppConfig.FileSpeedLimitMBps` (int, 기본 30); `public sealed record PeerSpeed(int MBps) : Message;` 종류 바이트 22.

- [ ] **Step 1: 실패하는 테스트 쓰기**
  - `AppConfigTests`: `Defaults_SpeedIs30` → `Assert.Equal(30, new AppConfig().FileSpeedLimitMBps)`; `Load_OldFileWithoutSpeed_Uses30` → `FileSpeedLimitMBps`가 없는 JSON 파일을 저장 후 `AppConfig.Load`로 불러와 30; 저장/불러오기 왕복 테스트에 `FileSpeedLimitMBps = 0`을 넣고 0이 유지되는지 확인.
  - `MessageCodecTests`: 왕복 목록에 `new PeerSpeed(30)`과 `new PeerSpeed(0)` 추가.
- [ ] **Step 2: 실행해서 실패 확인** — `dotnet test --filter "FullyQualifiedName~AppConfigTests|FullyQualifiedName~MessageCodecTests"` → 컴파일 오류(`FileSpeedLimitMBps`, `PeerSpeed` 없음).
- [ ] **Step 3: 구현** — `AppConfig`에 `public int FileSpeedLimitMBps { get; set; } = 30;`. 코덱: `case PeerSpeed x: w.Write((byte)22); w.Write(x.MBps);`, 읽기 `22 => new PeerSpeed(r.ReadInt32())`.
- [ ] **Step 4: 다시 실행** → PASS.
- [ ] **Step 5: 커밋** — `feat: speed limit setting and PeerSpeed message`

### Task 2: 적용 값 계산과 속도 맞추기

**Files:**
- Modify: `src/MonitorBridge.Core/Files/TransferProgress.cs`
- Create: `src/MonitorBridge.Core/Files/SpeedPacer.cs`
- Test: `tests/MonitorBridge.Core.Tests/TransferProgressTests.cs`, Create `tests/MonitorBridge.Core.Tests/SpeedPacerTests.cs`

**Interfaces:**
- Produces:
  - `public static int TransferProgress.EffectiveSpeed(int ownMBps, int? peerMBps)` — 0 = 제한 없음.
  - `public static string TransferProgress.SpeedText(int mbps)` — `0` → `"제한 없음"`, 그 외 `"30MB/s"`.
  - `public sealed class SpeedPacer { public SpeedPacer(Func<long> nowMs); public int DelayMs(int bytes, int mbps); }` — 방금 `bytes`를 보냈을 때 기다릴 밀리초.

- [ ] **Step 1: 실패하는 테스트 쓰기**
  - `EffectiveSpeed`: `(30, null)`→30 (`EffectiveSpeed_UnknownPeer_UsesOwn`), `(30, 10)`→10, `(0, 10)`→10, `(30, 0)`→30, `(0, 0)`→0, `(0, null)`→0.
  - `SpeedText`: `SpeedText(0) == "제한 없음"`, `SpeedText(30) == "30MB/s"`.
  - `SpeedPacerTests` (가짜 시계 `long _now`):
    - `Unlimited_NeverWaits`: `DelayMs(1<<20, 0) == 0`.
    - `TenMBps_OneMiB_Waits100ms`: 시각 0에서 `DelayMs(1<<20, 10)`가 100(±1).
    - `Steady_KeepsPace`: 1MiB 보내고 100ms 지난 뒤 다시 1MiB → 다시 약 100.
    - `SlowSender_DoesNotBurst`: 1MiB 보낸 뒤 시계를 5,000ms 넘기고 1MiB → 0이 아니라 약 100 (밀린 시간을 저축하지 않음).
    - `RateChange_StartsFresh`: 10MB/s로 3MiB 보낸 뒤(기다림 무시) 같은 시각에 30MB/s로 1MiB → 약 33.
- [ ] **Step 2: 실행해서 실패 확인** — `dotnet test --filter "FullyQualifiedName~TransferProgressTests|FullyQualifiedName~SpeedPacerTests"` → 컴파일 오류.
- [ ] **Step 3: 구현** — `EffectiveSpeed`: 0을 `int.MaxValue`로 바꿔 최솟값을 구하고 `int.MaxValue`면 0. `SpeedPacer`: 기준 시각과 기준 이후 보낸 바이트를 기억한다. `mbps`가 지난번과 다르거나 0이면 기준을 지금으로 초기화. 보낸 바이트를 더한 뒤 `due = bytes*1000/(mbps*MiB)`; `wait = 기준 + due - now`. 실제가 1초 넘게 늦었으면(`wait < -1000`) 기준을 지금, 보낸 바이트를 이번 `bytes`로 다시 잡고 다시 계산. 결과는 `Math.Max(0, wait)`.
- [ ] **Step 4: 다시 실행** → PASS.
- [ ] **Step 5: 커밋** — `feat: speed pacer and effective speed`

### Task 3: 보낼 때 속도 지키기

**Files:**
- Modify: `src/MonitorBridge/Shared/FileTransfer.cs` (생성자, 조각 보내는 반복문 약 205~212행)
- Test: `tests/MonitorBridge.Core.Tests/FileTransferTests.cs`

**Interfaces:**
- Consumes: `SpeedPacer`, `TransferProgress.EffectiveSpeed` (Task 2).
- Produces: 생성자 마지막 선택 인자 `Func<int>? speedMBps = null` (null = 제한 없음, 기존 테스트 그대로); 속성 `public int? PeerSpeedMBps { get; set; }`.

- [ ] **Step 1: 실패하는 테스트 쓰기** (기존 보내기 테스트의 연결 도우미를 재사용)
  - `PacedSend_TakesAtLeastTheLimit`: `speedMBps: () => 1`, 1.5MiB 파일 전송 → 끝날 때까지 1,000ms 이상 걸림, 받은 파일 내용이 원본과 같음.
  - `PeerSpeed_Smaller_Wins`: 내 값 0, `PeerSpeedMBps = 1` → 1.5MiB 전송이 1,000ms 이상.
  - `Cancel_DuringPacedSend_StopsQuickly`: `speedMBps: () => 1`, 10MiB 파일, 시작 200ms 뒤 `CancelAll()` → 1,000ms 안에 `Progress.State == Cancelled`, 원본 파일 그대로 존재.
- [ ] **Step 2: 실행해서 실패 확인** — `dotnet test --filter FullyQualifiedName~FileTransferTests` → 컴파일 오류(인자 없음).
- [ ] **Step 3: 구현** — 보내기 시작할 때 `new SpeedPacer(() => Environment.TickCount64)`를 만들고, 각 `FileChunk`를 `_sendBulk`한 뒤 `int ms = pacer.DelayMs(n, TransferProgress.EffectiveSpeed(_speed?.Invoke() ?? 0, PeerSpeedMBps)); if (ms > 0) await Task.Delay(ms, cts.Token);`. 값은 조각마다 다시 읽으므로 중간 변경이 바로 적용된다.
- [ ] **Step 4: 다시 실행** → PASS, 이어서 `dotnet test` 전체 PASS.
- [ ] **Step 5: 커밋** — `feat: pace file sends to the speed limit`

### Task 4: 값 교환과 트레이 메뉴

**Files:**
- Modify: `src/MonitorBridge/Shared/ClipboardSync.cs`, `src/MonitorBridge/UI/TrayContext.cs`

**Interfaces:**
- Consumes: `PeerSpeed`, `FileTransfer(..., speedMBps)`, `PeerSpeedMBps`, `SpeedText`, `EffectiveSpeed`.
- Produces: `ClipboardSync(Func<PeerConnection?> conn, Func<long> fileLimitBytes, Func<int> speedMBps, string peerName, Action<string> notify)`; `public int? PeerSpeedMBps => _files.PeerSpeedMBps;`.

- [ ] **Step 1: 구현**
  - `ClipboardSync`: 생성자에 `speedMBps`를 받아 `FileTransfer`에 넘김. `SendSettings`에서 `PeerSettings` 바로 뒤에 `conn.Send(new PeerSpeed(_speedMBps()))`. `OnMessage`에 `case PeerSpeed s: _files.PeerSpeedMBps = s.MBps; break;`. `OnDisconnected`에서 `PeerSpeedMBps = null`(다른 PC가 붙을 수 있음).
  - `TrayContext`: 생성 시 `() => _cfg.FileSpeedLimitMBps` 전달. 한도 줄 아래에 메뉴 줄 하나(비활성), 열 때마다 글 갱신:
    - 상대 값 모름 또는 같음: `파일 전송 속도: {SpeedText(own)}`
    - 다름: `파일 전송 속도: 이 PC {SpeedText(own)} · 상대 PC {SpeedText(peer)} → {SpeedText(eff)}`
  - 가벼운 저장 경로(`SameConnection`)에 `_cfg.FileSpeedLimitMBps = next.FileSpeedLimitMBps;` 추가 — 연결은 끊지 않고 `SendSettings()`가 새 값을 보낸다.
  - `SettingsForm` 생성에 `() => _clipboard?.PeerSpeedMBps` 전달(Task 5의 인자).
- [ ] **Step 2: 빌드와 테스트** — `dotnet build src/MonitorBridge` 경고 없이 성공, `dotnet test` 전체 PASS.
- [ ] **Step 3: 커밋** — `feat: exchange speed limits and show them in the tray`

### Task 5: 설정 창

**Files:**
- Modify: `src/MonitorBridge/UI/SettingsForm.cs`, `src/MonitorBridge/Program.cs` (`--render-settings`에 `--speed-guide` 선택지)

**Interfaces:**
- Consumes: `SpeedText`, `EffectiveSpeed`, `AppConfig.FileSpeedLimitMBps`.
- Produces: 생성자 인자 `Func<int?>? peerSpeedMBps` (`peerLimitMB` 바로 뒤); `public const int MaxSpeedMBps = 1_000;`.

- [ ] **Step 1: 구현**
  - `NumericUpDown _speedLimit` (Minimum 0, Maximum `MaxSpeedMBps`, Width 80), 값은 `Math.Clamp(cfg.FileSpeedLimitMBps, 0, MaxSpeedMBps)`, `Result`에 `FileSpeedLimitMBps = (int)_speedLimit.Value`.
  - `_limitWarn` 줄 바로 아래: `Row("파일 전송 속도", Inline(_speedLimit, Unit("MB/s까지 (0 = 제한 없음)")))`, 그 아래 `Row("", _speedWarn)`(빨강, 평소 숨김), 그 아래 `Row("", 링크)`, 그 아래 `Row("", _speedGuide)`(회색 글, 평소 숨김). 게임컴·송출컴 둘 다 보인다.
  - 링크 `LinkLabel`: 접힘 `속도 기준 보기 ▸`, 펼침 `속도 기준 접기 ▾`. 누르면 `_speedGuide.Visible`을 바꾼다.
  - `UpdateLive`에서: 상대 값이 있고 내 값과 다르면 `_speedWarn.Text = $"상대 PC 설정은 {SpeedText(peer)}, 실제 전송 속도는 {SpeedText(eff)}예요."`, 아니면 숨김. `_speedLimit.ValueChanged`도 `UpdateLive` 호출.
  - `_speedGuide.Text`(그대로 쓸 글):
    ```
    · 1MB/s는 인터넷 속도로 8Mbps예요.
    · 1기가(1000Mbps) 환경: 최대 약 110MB/s. 공유기, 랜선, 두 PC의 랜카드가 모두 1기가를 지원해야 해요.
    · 100메가(100Mbps) 환경: 최대 약 11MB/s. 셋 중 하나라도 100메가면 전체가 100메가로 동작해요.
    · 와이파이는 속도가 들쭉날쭉해요.
    · 방송 업로드는 보통 1~2MB/s 정도예요. 랜선에 자리가 남아 있어야 방송이 끊기지 않아요.

    추천 값
    · 1기가 환경에서 방송 중: 30MB/s (기본값)
    · 100메가 환경, 와이파이, 잘 모를 때: 5~10MB/s
    · 방송하지 않을 때: 0 (제한 없음)
    ```
  - `Program.cs`: `--render-settings <png>`에 `--speed-guide`가 있으면 기준을 펼친 상태로 렌더. 렌더용 설정에 상대 속도 10을 줘서 경고 줄도 보이게 한다(`--same-limit`이면 같은 값).
- [ ] **Step 2: 렌더 확인** (창을 띄우지 않는 렌더라 입력을 가로채지 않음) — 게임컴, `--receiver`, `--speed-guide`, `--receiver --speed-guide`, `--same-limit` 다섯 장을 만들어 보고 라벨과 칸이 같은 줄에 맞는지, 기준 글이 잘리지 않는지 확인.
- [ ] **Step 3: 테스트** — `dotnet test` 전체 PASS.
- [ ] **Step 4: 커밋** — `feat: speed limit row and folding speed guide in settings`

### Task 6: 사용설명서

**Files:**
- Modify: `README.md`, `README.en.md`, `README.ja.md`; 다시 만들기 `docs/manual/*.html`, `docs/manual/*.pdf`

- [ ] **Step 1: 고치기** — 세 README의 "파일 옮기기"에 **전송 속도** 항목(기본 30MB/s, 0 = 제한 없음, 두 PC 중 작은 값, 전송 중 바꿔도 바로 적용), "설정" 표에 `파일 전송 속도 | 30MB/s | 최대 1,000. 0 = 제한 없음. 두 PC 중 작은 쪽`, 새 소단원 "속도 기준"에 Task 5 기준 글과 같은 내용(추천 값은 표). README는 `<details>`로 접고, PDF는 그대로 펼친다(만드는 스크립트가 `<details>`/`<summary>` 줄을 건너뛰도록 한 줄 추가). 트레이 표에 `파일 전송 속도 줄` 추가. 영어·일본어도 같은 내용.
- [ ] **Step 2: PDF 만들기** — `node docs/manual/make-pdf.mjs` → PDF 3개 생성, 한국어·일본어 PDF를 열어 속도 기준 부분이 표로 보이는지 확인.
- [ ] **Step 3: 커밋** — `docs: speed limit and speed guide in the manuals`
