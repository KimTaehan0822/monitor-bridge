# Monitor Bridge

[한국어](README.md) | **English** | [日本語](README.ja.md)

A Windows program that lets one keyboard and mouse on your gaming PC also control your streaming PC. It is similar to Input Director, but you can **arrange every monitor freely**.

Example: if your desk has `Stream 1 | Game | Stream 2`, the cursor crosses in that order. It feels like one PC with three monitors.

- Mouse and keyboard sharing (gaming PC → streaming PC)
- Shared clipboard (both ways): text, images, files
- Lock key: keep the cursor on the PC it is on (for gaming)
- Quick-switch key: jump to the other PC with one key. A ripple at the landing spot helps you find the cursor
- While connected and the cursor is on the gaming PC, the streaming PC's cursor is hidden (no arrow left on the broadcast)

The program's screens are in Korean. Korean labels are shown below as they appear, with their meaning.

## Download

Get `Monitor-Bridge-v<version>.zip` from the [latest release](https://github.com/KimTaehan0822/monitor-bridge/releases/latest) and unzip it. It contains:

- `MonitorBridge.exe`: the program (no installer; the same file on both PCs)
- Manual PDFs, one per language
  - `Monitor Bridge 사용설명서.pdf`: Korean
  - `Monitor Bridge Manual (English).pdf`: English
  - `Monitor Bridge 取扱説明書 (日本語).pdf`: Japanese
- `LICENSE.txt`: this program's license (MIT)
- `THIRD-PARTY-NOTICES.txt`: notices for the open source it includes

## Requirements

- Two 64-bit PCs with Windows 10 or later (one gaming PC, one streaming PC)
- Both PCs on the same router (LAN)
- A keyboard and mouse for the streaming PC, only during first setup

You don't need to install .NET; it is inside the exe.

## Quick start

### 1. Plug a keyboard and mouse into the streaming PC for now

First setup must be done on the streaming PC itself. Until the program runs and connects, the gaming PC cannot control it. Unplug them when you're done.

### 2. Close programs like Input Director

Running Input Director, Mouse Without Borders, Barrier and similar tools **at the same time tangles the input.** Close or remove them on both PCs.

### 3. Run `MonitorBridge.exe` on both PCs

Put the exe in any folder and run it. The settings window opens the first time.

**On the streaming PC:**
1. In **이 PC는** (This PC is), choose **송출컴** (streaming PC).
2. Enter a **비밀번호** (password). The gaming PC needs the same one.
3. Note the **이 PC의 IP** (this PC's IP) at the bottom of the window (e.g. `192.168.0.10`).
4. Turn on **윈도우 로그인 시 관리자 권한으로 자동 실행** (start as administrator at Windows sign-in) and save. Click **Yes** on the admin prompt.
5. When the firewall's **Allow access** window appears, tick **Private networks** and allow.

**On the gaming PC:**
1. In **이 PC는**, choose **게임컴** (gaming PC).
2. Enter the streaming PC's IP in **송출컴 IP**.
3. Enter the same **비밀번호** as the streaming PC.
4. Save to connect. When the icon at the right of the taskbar turns **teal**, you're connected.

### 4. Arrange the monitors like your desk

Double-click the gaming PC's tray icon to open settings. In **모니터 배치** (monitor layout) at the bottom, drag the monitor boxes into the order on your desk and save.

- Teal boxes are gaming PC monitors; navy boxes are streaming PC monitors.
- A box snaps to its neighbor when dragged close.
- A red border means monitors overlap. Move one aside.

### 5. Check, then unplug the streaming PC's keyboard and mouse

Once the gaming PC's mouse moves onto the streaming PC's screens, you can unplug the streaming PC's keyboard and mouse.

## Hotkeys

| Feature | Default key | Description |
| --- | --- | --- |
| Lock (가두기) | `Scroll Lock` | Keeps the cursor on the current PC. Press again to release. While locked the tray icon is orange. The quick-switch key still moves you to the other PC, and a lock made on the gaming PC comes back when you return to it |
| Quick switch (빠른 전환) | ``Ctrl + Alt + ` `` | Jumps to the other PC, at the spot you last left it, with a short ripple there. A ripple on the streaming PC briefly shows on the broadcast too |

- **Changing a key**: click a hotkey box in settings. It changes color and shows "새 키를 누르세요" (press a new key). Current hotkeys don't fire meanwhile, so just press the key you want (Esc: cancel).
- **Not using one**: empty the box (**[지우기]** (clear) button, or click it and press Backspace). The feature is off and the key isn't intercepted.
- Locking on the gaming PC stops this program from watching the keyboard and mouse at all, so games aren't affected.
- Locking on the streaming PC keeps the cursor on its monitors. It then follows **the streaming PC's Windows display arrangement**, so you can move between streaming monitors even if a gaming monitor sits between them on your desk.
- While you drag on the streaming PC (a mouse button held), the cursor also follows the streaming PC's Windows display arrangement, so you can drag a window straight between streaming monitors. Releasing the button goes back to the desk layout.
- While not connected, the program doesn't touch any key or mouse input, hotkeys included. Other programs using the same keys keep working.

## Moving files

1. Copy files or folders (Ctrl+C) on the sending PC. **Copying alone sends nothing.** It only tells the other PC what you copied.
2. On the receiving PC, **right-click** an empty spot in the target folder (or the desktop) in Explorer.
   - On Windows 11, click **Show more options**, or use **Shift+right-click** from the start.
3. Click **Monitor Bridge: 상대 PC에서 복사한 파일 여기에 붙여넣기** (paste files copied on the other PC here).
4. A window by the cursor asks "이 폴더로 가져올까요?" (bring them into this folder?). Click **[가져오기]** (bring). The transfer starts then.
5. A notice appears when it starts. **Click the tray icon or the notice** to open the progress window (progress, time left, cancel button). It never opens by itself, so it won't appear on the broadcast.
6. When done, a "다 받았어요" (received) notice appears; click it to open the folder.

- Normal Ctrl+V pasting is untouched.
- Existing names are never overwritten; the copy is saved as `name (2)`.
- **Cancel**: click **[전송 취소]** in the progress window or **파일 전송 취소** in the tray menu. Either PC stops both sides. Partly received files are deleted automatically; **the sender's original files are never deleted or changed.**
- **Copy limit**: 500MB by default, up to 100,000MB. You can send **up to the smaller of the two PCs' limits.** When they differ, settings show a red warning and the tray menu shows both limits.
- **Transfer speed**: files are sent at up to **30MB/s** by default. A file filling the network cable can push back the streaming PC's broadcast upload and drop the stream. Change it in **파일 전송 속도** (file transfer speed) in settings (up to 1,000MB/s); **0 means no limit**. The **smaller of the two PCs' values** is used (0 counts as the largest, since it means no limit), and a change saved mid-transfer applies right away.

### Speed guide

<details><summary>Show</summary>

- 1MB/s is 8Mbps in internet-speed terms.
- 1 Gigabit (1000Mbps) network: up to about 110MB/s. The router, the cables and both PCs' network cards must all support 1 Gigabit.
- 100 Megabit (100Mbps) network: up to about 11MB/s. If any one of those three is 100 Megabit, the whole link runs at 100 Megabit.
- Wi-Fi speed goes up and down.
- Broadcast upload: at FHD 8000kbps, about 4GB per hour (rounded up for headroom), or about 1.1MB/s. The cable needs room left over so the stream doesn't drop.

| Situation | Suggested value |
| --- | --- |
| Streaming on a network that supports 1 Gigabit (1000Mbps) (router, cables and both PCs' network cards all support 1 Gigabit) | 30MB/s (default) |
| 100 Megabit network (any one part at 100 Megabit), Wi-Fi, or not sure | 5–10MB/s |
| Not streaming | 0 (no limit) |

</details>

## Settings

| Item | Default | Meaning |
| --- | --- | --- |
| 이 PC는 (This PC is) | | 게임컴 (gaming PC, has the keyboard and mouse) or 송출컴 (streaming PC) |
| 송출컴 IP (streaming PC IP) | | Gaming PC only. The IP shown in the streaming PC's settings |
| 비밀번호 (password) | | Same on both PCs. Used to encrypt the connection |
| Lock key / quick-switch key | `Scroll Lock` / ``Ctrl+Alt+` `` | Gaming PC only. Leave empty to turn the feature off |
| File copy limit | 500MB | Up to 100,000MB. The smaller of the two PCs applies |
| 파일 전송 속도 (file transfer speed) | 30MB/s | Up to 1,000. 0 = no limit. The smaller of the two PCs applies. **속도 기준 보기** (show speed guide) opens the guide |
| Auto start | | Starts at Windows sign-in. Streaming PC as administrator |
| 모니터 배치 (monitor layout) | | Gaming PC only. Match your desk |

The connection uses TCP port `24850`.

## Tray icon

| Color / menu | Meaning |
| --- | --- |
| Teal | Connected |
| Grey | Not connected |
| Orange | Lock on |
| `버전:` line at the top of the menu | This PC's Monitor Bridge version |
| Limit line | Both PCs' file copy limits |
| Speed line | This PC's transfer speed; if the PCs differ, both values and the one in use |
| 파일 전송 보기 / 파일 전송 취소 | Open the progress window / stop the transfer |
| 권한: 관리자 아님 (not admin) line / 관리자 권한으로 다시 켜기 (restart as admin) | Shown only without admin rights. Click it, choose "Yes" in the Windows prompt, and Monitor Bridge restarts as admin |

## Troubleshooting

| Symptom | Cause | What to do |
| --- | --- | --- |
| Won't connect (grey icon) | Program not running on the streaming PC, firewall, IP changed | Check it runs on the streaming PC → allow MonitorBridge under "Windows Defender Firewall → Allow an app" (or open TCP 24850) → recheck the streaming PC's IP (reserve it in your router) |
| "비밀번호가 달라서 연결하지 못했어요" (password mismatch) | Passwords differ | Enter the same password on both PCs |
| "두 PC의 Monitor Bridge 버전이 달라요" (versions differ) | Only one PC was updated | Keyboard and mouse keep working. Put both PCs on the same version |
| Can't use the streaming PC's sign-in screen | The program starts after sign-in | Turn on auto sign-in on the streaming PC, or plug in a keyboard just to sign in |
| Installer/uninstaller windows or programs like OBS ignore input | That program runs as administrator but Monitor Bridge on the streaming PC does not (for example, started by double-clicking the exe). A notice appears at start and settings show a red note | Turn on **관리자 권한으로 자동 실행** on the streaming PC and restart the PC, or right-click the exe → **Run as administrator** |
| Lock/quick-switch keys and cursor moves do nothing in a game (only after Alt+Tab) | Monitor Bridge on the gaming PC runs without admin rights. A notice appears at start and the tray menu and settings show it | Click **관리자 권한으로 다시 켜기** (restart as admin) in the notice, tray menu or settings. To do it every time, turn on auto start |
| Right-click item missing | Windows 11's new menu, or the program never ran | **Show more options** or Shift+right-click; run Monitor Bridge once |
| "Monitor Bridge가 실행 중이 아니에요" (not running) | Off on that PC | Start it on that PC first |
| File copy fails | Over the limit, not enough free space | Check both limits. Files are first written to a hidden folder (`.MonitorBridge-받는중`) inside the target folder, so that drive needs the space |
| Received files couldn't be moved into the folder | Antivirus etc. holds the file | Nothing is deleted; the files stay in the hidden folder named in the notice |
| Streaming PC cursor is invisible | Normal while connected | It shows when the gaming PC's cursor moves over. After a crash, run Monitor Bridge again |
| Keys act stuck after Ctrl+Alt+Del | Key releases can't be seen on that screen | Press and release Ctrl and Alt once |

If something goes wrong, check `%AppData%\MonitorBridge\log.txt`. Settings are saved in `config.json` in the same folder.

## Not supported

- UAC prompts, the Ctrl+Alt+Del screen, the sign-in screen
- Gaming while the cursor is on the streaming PC (game on the gaming PC with the lock key on)
- Three or more PCs
- Connecting outside the same router (LAN)

## Updating

1. Exit Monitor Bridge from the tray on both PCs.
2. Overwrite `MonitorBridge.exe` on both PCs with the new file, in the same place, so the firewall rule and auto start stay.
3. Start it again, streaming PC first, then gaming PC.

Different versions keep the keyboard and mouse connected, but some new features may not work, so keep both PCs on the same version.

## Building

Needs the .NET 8 SDK.

```bash
dotnet test
dotnet publish src/MonitorBridge -c Release -o publish   # publish/MonitorBridge.exe
node docs/manual/make-pdf.mjs                 # manual PDFs (needs Edge, Noto Sans KR/JP)
```

## Notices

This program is released under the MIT license (see `LICENSE`). Notices for the .NET runtime bundled in the exe are in `THIRD-PARTY-NOTICES.txt`. Input Director, Mouse Without Borders and Barrier belong to their developers; this is an unofficial project unrelated to them.
