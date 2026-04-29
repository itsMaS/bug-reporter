# Screen Recording Application

A Windows application that records your screen while a recording hotkey is pressed and can also save a configurable rolling buffer on demand.

## Features

- **Easy Recording**: Simply press your configured recording hotkey to start recording and release to stop
- **Instant Replay**: Press your configured clip hotkey to save the last configured duration as a separate clip
- **Automatic Saving**: Videos are automatically saved to your Videos folder
- **Configurable Frame Rate**: Choose the live recording FPS from 5 to 60
- **MP4 Format**: Records in MP4 format
- **Full Screen Capture**: Captures the entire screen

## Requirements

- Windows OS
- .NET 9.0 or higher
- Sufficient disk space for video files

## Installation & Usage

1. **Build the application:**
   ```bash
   dotnet build
   ```

2. **Run the application:**
   ```bash
   dotnet run
   ```

3. **Recording:**
   - Press your configured recording hotkey to start recording
   - Release the recording hotkey to stop recording and save
   - Press your configured clip hotkey to save the previous configured duration as a separate clip

## Video Storage

Videos are automatically saved to:
```
C:\Users\[YourUsername]\Videos\ScreenRecordings\
```

Videos are named with the timestamp of when they were recorded:
```
screen_recording_2026-04-28_14-30-45.mp4
instant_replay_2026-04-28_14-30-45-123.mp4
```

## Technical Details

- **Video Codec**: MP4V
- **Frame Rate**: 30 FPS
- **Resolution**: Full screen resolution (1920x1080 or based on your monitor)
- **Keyboard Detection**: Windows API GetAsyncKeyState for configurable recording and clip hotkeys
- **Screen Capture**: Windows.Forms Graphics.CopyFromScreen

## Notes

- The application runs in the background and monitors separate configurable hotkeys for recording and clip capture
- Multiple recordings can be made in a single session
- Ensure you have adequate free disk space, as video files can be large

## Bundled FFmpeg (Publish)

The app can run with bundled FFmpeg tools in published output.

Place binaries in this repository path:

```
tools\ffmpeg\bin\ffmpeg.exe
tools\ffmpeg\bin\ffprobe.exe
```

When present, publish copies them into the same relative path under the publish folder.
The runtime lookup already checks this location.

Current publish behavior:

- If binaries are present, they are included in publish output.
- If binaries are missing, publish continues and emits a warning; runtime then depends on system-installed FFmpeg/FFprobe.

For internal traceability, keep FFmpeg attribution in:

```
tools\ffmpeg\LICENSE.txt
```

## Troubleshooting

- If videos are not being saved, check that you have write permissions to the Videos folder
- Make sure F11 is not being used by another application
- If performance is slow, close other applications to free up system resources
