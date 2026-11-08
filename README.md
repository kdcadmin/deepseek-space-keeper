[English](README.md) | [中文](README.zh-CN.md)

![cover](assets/cover-16x9.jpg)

# 大肥鱼空间管家

大肥鱼空间管家 is a Windows cleaner for the C: drive. It does three things: clean the C: drive, remove system junk, and clear regenerable caches from Codex, Cursor, and DeepSeek. Every cleanup is previewed and confirmed first. Nothing is selected automatically, and chats, credentials, and model files are left untouched.

## What the pictures show

![Home](assets/guide/home.png)

The home page. The three cards open C: drive cleanup, junk cleanup, and AI cache cleanup.

![C: drive scan](assets/guide/c-drive-scan.png)

C: drive cleanup while a scan is running. She watches the radar dish. The amount on screen follows the amount found. This picture leaves when the scan or the cleanup finishes.

![Junk scan](assets/guide/junk-scan.png)

Junk cleanup while a scan is running. The dish is a trash can. The can is full at about 10 GB.

![AI cache scan](assets/guide/ai-scan.png)

AI cache cleanup while a scan is running. The glowing shape is the cache. Only regenerable caches and logs from Codex, Cursor, and DeepSeek are listed.

![Idle pet](assets/guide/pet-idle.png)

The desktop pet at rest. A single click opens C: drive cleanup, junk cleanup, and AI cache.

![Head pat](assets/guide/pet-pat.png)

A double click. She closes her eyes, pats her head, and shakes it.

## Build

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

The build does not delete files. The desktop pet is copied next to the executable.
