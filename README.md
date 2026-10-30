# Auto Clip Golds

A component for LiveSplit on Windows that automatically saves your gold splits using the OBS replay buffer. It can also capture completed runs that fit inside the buffer.

Clips are trimmed without re-encoding and saved as MKV files with the original video and audio tracks.

## Features

- Capture gold splits, or switch to full-run capture.
- Set the footage to keep before and after a segment.
- Undo a split to cancel its pending clip or recycle its saved video.
- Find FFmpeg automatically.
- Account for timer offsets in gold filenames and remove offset tags from split names.
- Test the OBS connection from the component settings.

## Requirements

- Windows and the desktop version of [LiveSplit](https://livesplit.org/).
- [OBS Studio](https://obsproject.com/) running on the same PC.
- OBS WebSocket **5.x**. It is built into OBS **28 and later**; WebSocket 4.x is not supported. See the [OBS WebSocket project](https://github.com/obsproject/obs-websocket).
- FFmpeg and FFprobe installed together in the same folder.
- A running OBS replay buffer long enough to contain the segment or run you want to save.

This is a preview release. It has been tested locally with a desktop LiveSplit installation, simulated OBS connections, and real video trimming. Compatibility across all LiveSplit builds and performance during gameplay have not been established.

## Setup

### 1. Install the component

1. Download **LiveSplit.GoldClip.dll** from this repository's release, or use the [DLL in the dist folder](dist/LiveSplit.GoldClip.dll). When viewing the DLL on GitHub, use **Download raw file**.
2. Close LiveSplit.
3. Copy the DLL into the **Components** folder inside your LiveSplit installation:

   ```text
   LiveSplit/
     LiveSplit.exe
     Components/
       LiveSplit.GoldClip.dll
   ```

4. Reopen LiveSplit.
5. Right-click LiveSplit → **Edit Layout** → **+** → **Other** → **Auto Clip Golds**.

The filename is still `LiveSplit.GoldClip.dll` for compatibility with existing layouts. The component appears as **Auto Clip Golds** inside LiveSplit and does not add a visible timer element.

### 2. Install FFmpeg

Open **PowerShell** from the Windows Start menu and run:

```powershell
winget install --id Gyan.FFmpeg --exact
```

Accept the prompts and wait for it to finish, then restart LiveSplit.

Alternatively, download **ffmpeg-release-essentials.zip** from [Gyan's Windows builds](https://www.gyan.dev/ffmpeg/builds/), a build provider linked by the [FFmpeg download page](https://ffmpeg.org/download.html). Extract it into a permanent folder. Its `bin` folder should contain both `ffmpeg.exe` and `ffprobe.exe`.

In Auto Clip Golds settings, click **Auto-detect**. You should see **FFmpeg and FFprobe found**. You can also enter the full path to `ffmpeg.exe` manually.

### 3. Connect OBS

1. In OBS, open **Tools → WebSocket Server Settings**.
2. Enable the WebSocket server.
3. Keep authentication enabled and note the server password and port.
4. In LiveSplit's **Layout Settings**, select **Auto Clip Golds**.
5. Enter the OBS address and password. The default address is:

   ```text
   ws://127.0.0.1:4455
   ```

6. If OBS shows a different port, replace `4455` with that port.
7. Click **Test connection**.

`127.0.0.1` refers to your own PC. This component supports local OBS connections only. Passwords are protected for your Windows user account when saved in the layout. If Windows cannot protect the password, it must be entered again next session.

### 4. Configure the replay buffer

1. Open **OBS Settings → Output** and enable **Replay Buffer**. The available controls depend on Simple or Advanced output mode.
2. Set **Maximum Replay Time** long enough for your longest segment plus padding and a little headroom. For example, **390 seconds (6:30)** is suitable when your longest split is just under six minutes.
3. If a memory limit is available, allow enough memory to retain that duration at your recording bitrate.
4. Apply the settings and click **Start Replay Buffer** in OBS.

The buffer must remain running throughout the segment. A newly started buffer only contains the footage recorded since it started. Saving a replay does not clear it.

### 5. Choose what to capture

| Setting | What it does |
| --- | --- |
| **Gold splits** | Saves segments that LiveSplit identifies as golds using the selected timing method. |
| **Full runs** | Saves footage from starting the timer to finishing the final split, provided the saved replay contains the run's start. |
| **Before / After** | Requested padding in seconds. Defaults to 3 seconds on each side. |
| **Clip folder** | Where clips and `GoldClip.log` are saved. Defaults to your Windows Videos folder → Gold Clips. |

Save your layout and start a fresh attempt after adding the component.

## Filenames and offsets

Gold clips use the split name, local date, and segment time to two decimal places:

```text
Maul Gold 4-15-26 3꞉41.88.mkv
```

Full-run clips use the category name, date, and final displayed run time, with no game name:

```text
Any% 4-15-26 25꞉41.88.mkv
```

Dates use month-day-year. The time separator `꞉` is U+A789, a Windows-safe colon-like character. Duplicate filenames receive a numbered suffix. An empty full-run category becomes `Uncategorized`.

For golds, offset tags can appear anywhere in the split name:

```text
[45.0] Split name
[4.312] Test
[10:03:41.111] Hours included
[4:31] Minutes seconds
One decimal place [15:11.1]
Two decimal [0:11.22] places
[131.34] Total seconds
```

The first valid tag is subtracted from the filename's timer duration. A positive **Start Timer at** value is also subtracted on the first split. Valid offset tags are removed from gold filenames; other bracketed text is retained.

Offsets add timer time, not footage. **Twenty seconds of actual play with a `[60]` tag still produces about twenty seconds of video plus padding.** If the adjusted timer value is not positive, the filename falls back to elapsed wall time and the log records a warning.

Malformed tags are ignored. Fractions support up to three decimal places; colon-separated seconds and subordinate minutes must be below 60. Full-run filenames retain the final displayed time, including offsets; the footage always covers the actual run.

## Padding and performance

Video is stream-copied, without re-encoding. Trimming selects the keyframe closest to the requested start that does not begin after the segment starts. **Actual before-padding can be shorter or longer than requested.** Exact frame cuts require re-encoding. Shorter OBS keyframe intervals can give finer control over the boundary.

Saving and trimming still read and write video files. CPU use should generally be lower than re-encoding, but disk activity can affect gameplay. Processing runs separately at low priority; performance depends on your recording settings and hardware.

## Undo and resets

- Undo during the padding wait cancels the pending capture.
- Undo after saving sends the video to the Windows Recycle Bin.
- Undo while processing marks the clip for removal when processing finishes.
- Undoing the final split in full-run mode removes that run's clip.
- Undo and re-split create separate clip identities.
- Reset keeps earned clips. Undo associations last while the component remains loaded and until a new attempt begins.
- Skipped segments are excluded from gold capture when they cannot represent an individual completed segment.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| Component is missing | Close LiveSplit, verify the DLL is in `Components`, then reopen it. Use a compatible desktop LiveSplit build. |
| OBS connection fails | Enable the server, check the port and password, and use WebSocket 5.x. OBS and LiveSplit must be on the same PC. |
| FFmpeg is not found | Keep `ffprobe.exe` beside `ffmpeg.exe`; enter the full FFmpeg path manually if needed. |
| No clip appears | Check the capture mode, replay-buffer status, whether the split was a gold, and the latest status/log. A pending clip is cancelled if undone before saving. |
| Clip starts too late | The buffer may be too short or may not have been running long enough. Increase its duration/memory allowance and start it before the attempt. |
| Full run is skipped | The saved replay does not contain the run's start. Allow headroom for end padding and saving delays. |
| Before-padding differs | The start must fall on a usable keyframe. Exact padding is not guaranteed without re-encoding. |
| A raw file remains | A failed trim can leave a `raw-...` replay in the clip folder for recovery. Check the log before removing it. |

The settings footer shows recent status. **View log** opens `GoldClip.log`; you can also right-click LiveSplit → **Auto Clip Golds status**. An undo message alone does not prove a video was created—look for a preceding **Saved:** entry.

Avoid manually saving replays or having another tool save them at the same time as this component: OBS replay-save events do not include a request identifier.

## Build and test

The source is in [`src/GoldClip.cs`](src/GoldClip.cs). Build on Windows against your own LiveSplit installation; its assemblies are not bundled here.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -LiveSplitPath "C:\Path\To\LiveSplit"
```

The script uses the installed .NET Framework C# compiler and writes `dist\LiveSplit.GoldClip.dll`. The local LiveSplit installation used during development targets .NET Framework 4.8.1. An SDK-based `dotnet build` is not required.

Tests additionally require Python 3, FFmpeg, and FFprobe, with Python and FFmpeg available on PATH:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1 -LiveSplitPath "C:\Path\To\LiveSplit"
```

They use a local simulated OBS server on port 4456, synthetic videos, real FFmpeg trimming, and LiveSplit timer events. They also exercise recycling of generated test clips. Temporary files stay under `work`.

## Update or uninstall

To update, close LiveSplit and replace `Components\LiveSplit.GoldClip.dll`. Existing component settings remain in your saved layout.

To uninstall, remove Auto Clip Golds from the layout, close LiveSplit, then delete the DLL. Saved clips remain in your chosen folder.

## Related projects

- [LiveSplit](https://github.com/LiveSplit/LiveSplit)
- [OBS Studio](https://github.com/obsproject/obs-studio) and [OBS WebSocket](https://github.com/obsproject/obs-websocket)
- [FFmpeg](https://ffmpeg.org/)

These projects are separate dependencies; their binaries are not included in this repository.
