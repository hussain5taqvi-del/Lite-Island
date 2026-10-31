# LiteIsland

A lightweight Dynamic Island / MacBook-notch style bar for Windows 10 and 11. Media controls, favorite apps, clock, calendar, weather and system stats at the top of your screen. Free and open source.

## Download and run
1. Go to the **Releases** page of this repository and download **`LiteIsland.exe`**.
2. Double click it. The island appears at the top of your screen. That is all: no installer, no command window, no admin rights, no .NET to install, and it works without internet (only the weather needs internet).
3. Click the tray icon to open Settings. Turn on **Start with Windows** there if you want it after a restart.

To remove it: turn off Start with Windows in Settings, right click the tray icon, Quit, then delete the exe. Your settings are in `%AppData%\LiteIsland`.

## Is it safe? (Windows may warn you)
- **"Windows protected your PC" (SmartScreen):** this appears for every new app that has no paid code-signing certificate. Click *More info*, then *Run anyway*. The code is public, so you can read it.
- **Open source:** everything is in this repository. The release files are built automatically by GitHub (see `.github/workflows/release.yml`) from this exact code.
- **Check your download:** each release has `SHA256.txt`. In PowerShell run `Get-FileHash .\LiteIsland.exe` and compare the hash.
- **Build it yourself:** install the .NET 8 SDK, then run `build.bat`.
- **No admin rights, no scripts, no installer.** It only writes `%AppData%\LiteIsland\settings.json`, and the Windows startup entry (current user only) when you turn on *Start with Windows*.

### What it connects to
- **Weather:** `ipwho.is` (finds your approximate city from your IP address) and `open-meteo.com` (the weather). Nothing else is sent.
- **Speed test:** `speed.cloudflare.com`, only when you press the Start button.
- **Wallpapers:** `moewalls.com`, `desktophut.com` and `4kwallpapers.com`, only while the Wallpapers window is open (tray icon, right click, Wallpapers). Files you download are saved in `%AppData%\LiteIsland\Wallpapers`.
- No accounts, no ads, no analytics, no tracking. The camera and microphone dots only read Windows' own "in use" status; the app never opens your camera or microphone. The music equalizer only reads how loud the speaker output is (a number from 0 to 1); nothing is recorded.

---

# Release notes

## v1.2.14 (Wallpapers)
- New **Wallpapers** window (tray icon, right click, Wallpapers): browse live wallpapers (MoeWalls, DesktopHut) and static wallpapers (4K Wallpapers) with thumbnails and search, click **Download & set** and it is on your desktop.
- Static wallpapers are set by Windows itself (no RAM or CPU used by the app). Live wallpapers play behind the desktop icons, are muted, loop without a gap, and pause while a maximized / full-screen window covers the desktop. The live wallpaper comes back after a restart.
- **Lite quality** (DesktopHut) downloads the small preview video: far less RAM on weak PCs.
- **My library** lists everything downloaded; **Add file...** lets you use your own picture or video (for example from a synced Google Drive folder).
- Nothing is loaded until the window is opened, only one page of small thumbnails is kept in memory, and the RAM is released when the window is closed.

## v1.2.11
- Start with Windows made reliable: the entry always points to the running exe (old entries are repaired), a "switched off in Task Manager" mark is cleared when you turn it on, the box no longer shows a tick for a dead entry, and at sign-in the island waits for the desktop to be ready.
- Problems are written to `%AppData%\LiteIsland\error.log` instead of closing silently.

## v1.2.10
- One file only: the Lite exe is gone. `LiteIsland.exe` contains everything and needs no internet or .NET.

## v1.2.9
- Fixed build.bat: the Lite build failed (NETSDK1176) because compression was set for both builds.

## v1.2.8
- Removed the description line under Settings > Shape.
- Public release kit: one-file exe, SHA256 fingerprints, automatic GitHub build, no scripts needed to run.

## v1.2.7 / v1.2.6

## What is new in v1.2.6
- System page: all six boxes have the same layout and scale automatically with the island size, so labels, numbers and bars line up in every box.
- Favorite apps: right click no longer opens a pop-up menu (that crashed). Use the **Edit** button (or right click an app): tap the red x to remove, drag an app to rearrange, **Done** to finish. Order is saved.
- Default animation is now **Scale** (changeable in Settings).

# LiteIsland v1.2.5

## What is new in v1.2.5
- Island is now always centered on the screen (placed in real pixels, so 125% / 150% / 4K scaling cannot push it to one side). It re-centers when the display changes.
- Text size slider (Settings > Collapsed) now goes from 60% to 220%. Text size depends only on the island HEIGHT and this slider, never on the width.
- A narrow island no longer shrinks the text: AM/PM steps aside first, then the weather, the text keeps its size.
- Old saved text sizes are reset to at least 100% once.

# LiteIsland v1.2.4

## What is new in v1.2
- The collapsed island can now be much smaller (width slider goes down to 150 px). Weather, battery % and the song name step aside automatically when there is not enough room.
- Media page: album art and song name are centered. New premium vector buttons (round hover, springy press, animated play/pause).
- Seek bar: one continuous animation (no stepping), grows while hovered, follows the cursor 1:1 while dragging.

- Shape (smooth return fixed): MacBook notch or iPhone Dynamic Island style (Settings > Shape), with gap from the top.
- Shadow behind the island with size, strength and offset.

- Smoothness: animations follow the screen refresh rate (120/144 Hz), content cache dropped while idle.

# LiteIsland v1.1 (final)

Made by NOBODY.

A lightweight Dynamic Island for Windows (WPF, .NET 8).

## Easiest way (also for friends): double click `Setup.bat`
It checks for the .NET SDK and installs it if missing (needs internet), builds the app, installs it to `%LocalAppData%\LiteIsland`, adds it to Windows startup, makes a desktop shortcut and starts it. `Uninstall.bat` removes everything.

## Build and run manually
1. Double click `build.bat`
2. It creates `publish\LiteIsland.exe`
3. Copy that exe to a permanent folder, for example `C:\LiteIsland\`
4. Double click the exe. The island appears at the top of the screen and an icon appears in the tray.

Updating: quit the app (tray icon, right click, Quit), run `build.bat` again, and copy the new exe over the old one.

For development you can also use `dotnet run`.

## How it works
- **Collapsed (notch):** the middle shows the song name while music plays, otherwise the time and (when available) the weather, split by a thin divider exactly in the middle. Album art is on the left; the green equalizer (only while music plays) and the battery (laptops only) are on the right. Moving the cursor over it does nothing.
- The idle time is big, with a small AM / PM (12-hour mode).
- **Click** the island to open it. When the cursor leaves, it closes by itself.
- The island has a real notch shape: flat on top, with small curved "ears" blending into the screen edge.

## Pages (scroll the mouse wheel over the island, or click the dots)
1. **Media**: album art, title, previous / play-pause / next, seek bar (click or drag)
2. **Favorite apps**: click + (or drop an app on the island) to add, click to launch, hover an app and click the small (x) to remove it
3. **Clock**: big time and date (plus battery on laptops)
4. **Calendar**: this month, today is highlighted
5. **Weather**: city (detected automatically), temperature, condition, wind, feels like, humidity
6. **System**: CPU, RAM, storage, GPU load, live network speed and an internet **speed test** button (ping, download, upload)

## Settings (click the tray icon)
- Width, height and corner roundness for the collapsed and expanded island
- Notch top curve size (0 = no curve)
- **Mode**: Dark or Light (sets island + text colors; you can still tweak them)
- **Liquid Glass**: switch + glass opacity slider (lower = more see-through)
- Island color (default black), transparency (used when Glass is off)
- **Text color**: color swatches or any hex color
- **Animation style**: Smooth, Bounce, Genie, Scale or Snappy, with a speed slider (the new style plays as soon as you pick it)
- 12-hour / 24-hour time
- Start with Windows (works from the built exe, not from `dotnet run`)

Settings and lists are saved in `%AppData%\LiteIsland\settings.json`.

## Performance rules
- Almost no polling: media and Bluetooth are read on events or when a page opens; the clock ticks once a minute and the battery is checked once a minute (laptops only)
- Animations run only while the island is open (equalizer only while music plays)
- The hidden expanded page is not drawn or laid out while the island is collapsed
- Memory is trimmed 8 seconds after the island closes, so opening never stutters
- Nothing is loaded until its page is opened

## v1.6
- New logo: exe icon, tray icon and Settings window
- The green equalizer now follows the real loudness of the music (quiet = low bars, loud = high bars). It only runs while a song plays and the island is closed.

## v1.7
- Volume bar and brightness bar: change the volume or brightness and the notch widens for a moment and shows a bar
- Bluetooth alert: when headphones / AirPods connect, the notch shows the name and the battery (or "Connected")
- Each one can be switched off in Settings, General. A switched off one does not run at all
- Brightness works on laptop screens (built-in display). External monitors usually do not report brightness changes

## v1.8
- Premium volume / brightness / Bluetooth bar animation: the island widens, grows slightly taller and rounder with a soft spring, the new content grows in; when it goes back the content fades first, then the island settles with a long calm spring and the normal content eases back in. The bar fill glides instead of jumping. (Snappy style keeps it crisp and cheap.)
- **Liquid Glass** (Settings, Theme and Glass): see-through gradient glass, soft top light, thin bright glass rim (not on the top edge), and a shine that sweeps across with a rim flash every time the island opens. It fades in when switched on. Note: it does not blur the desktop behind it (plain WPF cannot do that cheaply), so it stays light on RAM and CPU.
- **Dark / Light mode** for the island (seek bar, tiles, battery, dots follow the mode).

## v1.9
- **Weather** (real, from Open-Meteo, free, no account). Shown next to the time when no music plays, plus a Weather page. The place is detected from your internet connection, or type a city in Settings. Updates every 15 minutes. If it cannot be fetched, nothing is shown (never fake numbers). Celsius or Fahrenheit in Settings.
- **System page**: real CPU, RAM, storage (Windows drive), GPU load, network speed, temperature. Temperature shows only what the PC reports: CPU through the Windows thermal sensor (many PCs do not expose it, then it says N/A), GPU temperature with NVIDIA drivers. Nothing is measured unless this page is open.
- **Calendar page** with the current month.
- **Camera / microphone dot**: green dot while a camera is in use, orange dot while a microphone is in use (read from Windows privacy status, checked every 2 seconds). Switch it off in Settings.
- **Equalizer color** (default green) and **Accent color**, with neon highlighter colors added to every color list.
- v1.9.1: weather city is always detected automatically (the city box was removed). New animation styles: Notch, Minimal, Fluid.
- Clearer, more transparent crystal Liquid Glass (lower default opacity, soft light spot, stronger rim).
- Wider default notch and expanded island so everything fits and stays centered. Old saved sizes are upgraded once.

## v1.1 final
- Much smoother animations: content is no longer re-measured on every frame, the window is small while the island is collapsed (Windows redraws the whole transparent window each frame), the expanded content is cached while it animates, and the notch ears are no longer rebuilt per frame.
- File tray and Bluetooth pages removed (Bluetooth connect alerts in the notch stay).
- System page: temperature replaced by an internet speed test (Cloudflare servers, only runs when you press the button). Numbers are already there the moment the page appears.
- Camera / microphone dots are vertical at the far right.
- Rounder, smoother notch: bigger corner radius defaults and softer "ears". Clearer, more see-through Liquid Glass with a visible glass rim all around.


## New in 1.3.0

**File ring.** Select a file (or several) in Explorer or on the Desktop and tap **Right Ctrl**. A ring of actions appears under the cursor; click one, or press 1-9. Esc closes it.
- Photo: To JPG, To PNG, Compress, Remove background, Copy text (OCR), To PDF
- Video: Compress, To MP4, To GIF, Audio only, Mute
- PDF: Compress, To images, Copy text (OCR), Merge (when several are selected)
- Audio: To M4A, To WAV, To MP3
- Word / RTF / text: To PDF
- Every file: Zip, Copy path

Photo, PDF, OCR and document work is fully offline and needs no extra download. Video/audio conversion needs ffmpeg and background removal needs a small model (about 5 MB); both are downloaded once on first use into `%AppData%\LiteIsland\tools`.

**Link downloads.** Copy a video link (YouTube, Instagram, X/Twitter, Facebook, TikTok and more) and the island shows a card with thumbnail, duration and a quality pill (720p up to the best the video offers). Video/Audio only, MP4, subtitles, folder and auto-delete are in the options panel. Downloads page shows progress with pause / resume / stop. yt-dlp is downloaded on first use and updated weekly.

**Clipboard history.** A new page with the last 25 copies, source app, time and colour swatches. Kept in RAM only, never written to disk. Password-manager copies are ignored.

**30 MB RAM cap.** LiteIsland sets a hard working-set maximum on itself (default 30 MB, `RamLimitMb` in `%AppData%\LiteIsland\settings.json`). All heavy work runs in short-lived helper processes, which are not counted in that cap.

## Privacy notes
- The keyboard hook only reacts to a lone Right Ctrl tap and never records keys.
- On the Desktop, selection is read by sending Ctrl+C, which briefly touches the clipboard (the app ignores that copy).

## Known limits
- PDF compress and merge re-render pages as images, so text stops being selectable.
- GIF is capped at 30 seconds.
- Only the island process itself is capped at 30 MB.
- This version was written without a compiler available; run `build.bat` and report any errors.

## 1.3.3
- Right Ctrl listener runs on its own thread (the ring opens every time). The ring has a "More formats" page (photo: BMP/TIFF/GIF/WebP/resize/rotate/mirror; video: MKV/MOV/AVI/WebM/720p/frame; audio: FLAC/OGG/OPUS/AAC/AIFF/WMA).
- Downloads show progress only on the Downloads page (and a message when done).
- Faster helper start (single-file compression removed, ReadyToRun on) and two conversions in parallel.
- Memory limit can be changed in Settings (30 / 45 / 60 / 100 MB).

## 1.3.4
- Clock and Weather are now ONE page (time and date on the left, weather on the right). The page count is 7.
- Downloads use a hidden cache folder (`%AppData%\LiteIsland\cache`) for parts and fragments; it is deleted when the download ends, fails or is stopped (and at every start). Only the finished file lands in your download folder.
- YouTube: a small helper (deno) is fetched once so every quality works. If YouTube asks for a sign-in, the app retries with the cookies of Firefox / Edge / Chrome / Brave.
- Removed the 10 MB chunk option that made some sites (Facebook, Instagram) return empty files.
- Links of ANY site are checked quietly with yt-dlp; the island opens only when a video is really found.

## 1.3.5
- File ring shortcut is now **Right Ctrl + X** (select a file in Explorer or on the Desktop first). The X key is swallowed there, so Explorer never "cuts" the file. Ring is smaller.
- The "Files, links and clipboard" section is gone from Settings. File ring, link detection (all sites), and clipboard history are always on; download speed is not limited (2 connections per download, one download at a time, so other apps keep their share of the internet).
- Helper processes start without the WPF app (faster). Single-file compression is back on (smaller exe).

## 1.3.6
- App icons for shortcuts (This PC, Recycle Bin...) no longer show the small arrow.
- Removed the "clip a part" (scissors) option from the download card.
- YouTube link card opens at once (title, channel, picture); length and best quality are filled in a moment later.
- After "Download" the island closes and the download runs in the background (progress on the Downloads page).
- Opening the island is smoother: the page content is prepared after the first frames of the animation.

## 1.4.0 – everything built in, new ring

**Nothing to download any more.** `build.bat` now assembles a release **folder**:

```
release\LiteIsland.exe          the app
release\engines\                 yt-dlp, deno, background-removal model
release\filee\                   Filee converter (filee-cli.exe + ffmpeg, pandoc, ghostscript, 7-Zip, rhwp)
```

Copy the whole folder anywhere and run `LiteIsland.exe`. (The internet is only needed on the PC that builds it.)
`build.bat full` additionally bundles LibreOffice, calibre, Java and OCR for the rarest document / e-book formats (much bigger).
yt-dlp keeps itself current: a private copy in `%AppData%\LiteIsland\tools` is updated once a week; the bundled one is the starting point.

**Link copy:** the card with the Download button appears at once; title, picture and qualities fill in a moment later.

**New ring (hold your key on a selected file):**
- **Hold the ring key** (default X; change it in Settings → General → "File ring key") on a selected file in Explorer or on the Desktop: the ring opens. **Let go: it closes.** You can click a slice while holding. When work has started in the ring it stays until it is done.
- Letter keys are swallowed in Explorer / Desktop only (so Explorer does not jump to files); Ctrl / Shift / Alt / Win keys are never swallowed. Pressing the key together with another key (Ctrl+X etc.) does nothing special.
- 8 slices per page, they fly in one after the other; the slice under the cursor pops out in your accent colour.
- Tab, mouse wheel, arrow keys or a click in the middle turn to the next page with the other formats (all that Filee can make from that file).
- Click a slice: the middle becomes a progress ring, then a green check. Esc closes the ring, the work continues.
- Right-click a format to **pin** it to the front page (small dot); right-click again to unpin.
- Keys 1–8 pick a slice. Archives get an Extract slice.
- The formats of each file type are looked up once (in the background) and remembered; the very first ring of a never-seen type opens in the quick form.

## Third-party
Filee (MIT, https://github.com/KnifeLemon/Filee) – the converter in `filee\`; its licence and notices are copied next to it.
FFmpeg, Pandoc, Ghostscript, 7-Zip, rhwp and yt-dlp are separate programs under their own licences (FFmpeg build: GPL-3.0).

## 1.4.2
- Downloads never depend on the Downloads page: close the island, the downloads keep going in the background (a message appears when each is done).
- Fewer failed downloads: up to three tries per video. Try 2 refreshes yt-dlp first and asks for whatever format the site really offers; try 3 uses IPv4 and other clients. Private videos, sign-in walls and offline PCs are not retried. More network retries / timeouts inside yt-dlp.
- Smoother animations: the island is drawn as one picture while it closes (as it already was while opening); page changes build the new page first, then slide both pages as pictures; the ring's shadow is drawn once instead of on every frame.

## 1.4.3
- Settings → General → **Record animation timing**: while on, every animation is measured frame by frame and written to `%AppData%\LiteIsland\perf.log` (render mode, dropped frames, CPU, RAM). Off by default; send the file to get lag fixed with real numbers.

## 1.5.0 – favorite apps ring (the Favorite apps page is gone)
Hold the **apps key** (default **Caps Lock**; change it in Settings → General → "Apps key"): a ring of your favorite apps opens under the cursor, in any program. **Click an app**, or **move onto it and let go of the key**, and it opens. Let go anywhere else and the ring disappears.

- Add apps with the **+ Add app** slice in the ring, or drop an app / shortcut on the island. **Right-click** an app in the ring to remove it. More than 7 apps: Tab, the mouse wheel or the middle of the ring turns the page.
- Caps Lock is swallowed while it is the apps key (it will not toggle capitals). With a modifier key (Ctrl, Alt ...) a shortcut such as Ctrl+C never opens the ring. A letter key as the apps key stops that letter from typing, so Caps Lock, Ctrl, Alt or an F-key work best.
- The apps key and the file-ring key must differ. Settings → "Favorite apps ring" turns the whole thing off.
- Your old favorites carry over. The island now has 6 pages (Media, Clock + Weather, Calendar, System, Downloads, Clipboard).

## 1.5.1
- Hold-key rings open every time: no stuck flag any more, an old ring can no longer block or toggle away a new hold, newest hold wins.
- Smoother animations: they now run at the refresh rate of the screen (120/144 Hz screens too) and the island gets a short RAM headroom while it moves (the 30 MB cap is back a few seconds later).
- Much smaller release: pandoc, ghostscript and HWP engines are only in the `build.bat full` pack; ffplay, language folders and import libs are removed.

## 1.6.1
- Music visuals (edge glow / music wallpaper) removed again: too heavy for the PC.
- Equalizer follows the music on every sound device: it listens to all active outputs, so plugging a headset / Bluetooth hands-free in or out never freezes it.
- A quick tap on the apps key (Caps Lock) or the ring key (X) still does its normal job; only a hold opens the ring.

## 1.7.0
- Rings open in every program (Chrome too): the apps ring and the file ring are tied to the held key, not to window focus.
- Rings follow Liquid Glass (and the light/dark theme) with readable text.
- Music page: working Shuffle and Repeat buttons (off / all / one) that show the real state of the player.
- Equalizer: state is re-checked every 3 s, so plugging a headset in or out can never leave it frozen.
- Auto update (Settings → Updates): `LiteIsland-update.zip` (only the app, ~85 MB) from GitHub Releases, checked twice a day.
- Releases: installer, portable zip and update package are built by `.github/workflows/release.yml` when a tag like `v1.7.0` is pushed.

## 1.7.1
- Settings: only the two keys (file ring, apps ring) are left; the apps ring is always on; descriptions under the rows removed; timing log switch removed.
