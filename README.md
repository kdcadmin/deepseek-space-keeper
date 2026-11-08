[English](README.md) | [中文](README.zh-CN.md)

![cover](assets/cover-16x9.jpg)

# deepseek-space-keeper

deepseek-space-keeper is a Windows cleaner for the C: drive. It does three things: clean the C: drive, remove system junk, and clear regenerable caches from Codex, Cursor, and DeepSeek. Every cleanup is previewed and confirmed first. Nothing is selected automatically, and chats, credentials, and model files are left untouched.

## Build

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

The build does not delete files. The desktop pet is copied next to the executable.
