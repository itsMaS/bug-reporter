# Third-party notices

Bug Reporter bundles the following third-party software. Each component remains under its own license; the texts are linked or included below.

## FFmpeg (GPLv3 build)

- Files: `tools/ffmpeg/bin/ffmpeg.exe`, `tools/ffmpeg/bin/ffprobe.exe` and the accompanying `*.dll` files.
- Version: `N-124254-g397c7c7524-20260429` (FFmpeg git commit `397c7c7524`), a Windows x86_64 shared build from the BtbN FFmpeg-Builds project, configured with `--enable-gpl --enable-version3` and including GPL-only encoders such as libx264 and libx265.
- License: GNU General Public License v3.0. The full text is in `tools/ffmpeg/LICENSE.txt`.
- Source: FFmpeg source for that commit is at https://git.ffmpeg.org/gitweb/ffmpeg.git/commit/397c7c7524 and the build scripts, patches and source tarballs for the bundled binaries are published by https://github.com/BtbN/FFmpeg-Builds (release tagged `autobuild-2026-04-29`). Bug Reporter invokes FFmpeg as a separate process and does not link against it.

## libVLC (VideoLAN)

- Files: `libvlc.dll`, `libvlccore.dll`, `plugins/**` in the published app.
- Version: 3.0.21, from the `VideoLAN.LibVLC.Windows` NuGet package.
- License: GNU Lesser General Public License v2.1 or later. https://code.videolan.org/videolan/vlc/-/blob/master/COPYING.LIB

## LibVLCSharp

- Version: 3.8.5 (NuGet `LibVLCSharp`, `LibVLCSharp.WinForms`).
- License: LGPL v2.1 or later. https://github.com/videolan/libvlcsharp/blob/3.x/LICENSE

## OpenCV and OpenCvSharp

- Files: `OpenCvSharpExtern.dll`, `opencv_videoio_ffmpeg490_64.dll`.
- Version: OpenCvSharp4 4.9.0 (NuGet `OpenCvSharp4`, `OpenCvSharp4.runtime.win`), bundling OpenCV 4.9.0.
- License: Apache License 2.0 for both OpenCV (https://github.com/opencv/opencv/blob/4.x/LICENSE) and OpenCvSharp (https://github.com/shimat/opencvsharp/blob/main/LICENSE). Note that `opencv_videoio_ffmpeg490_64.dll` is OpenCV's own LGPL FFmpeg wrapper.

## NAudio

- Version: 2.2.1 (NuGet `NAudio`).
- License: MIT. https://github.com/naudio/NAudio/blob/master/license.txt

## .NET runtime and Windows Forms

- The self-contained build embeds the .NET 9 runtime and Windows Forms.
- License: MIT. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

## System.Drawing.Common

- Version: 8.0.0 (NuGet).
- License: MIT. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
