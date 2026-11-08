[English](README.md) | [中文](README.zh-CN.md)

![封面](assets/cover-16x9.jpg)

# 大肥鱼空间管家

大肥鱼空间管家是 Windows 上的 C 盘清理工具。它做三件事：清理 C 盘、清理系统垃圾，以及清理 Codex、Cursor 和 DeepSeek 里可以重新生成的缓存。每次清理都先预览，确认之后才删除。不会自动勾选，对话、登录凭证和模型文件都不会动。

## 构建

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

构建不会删除文件。桌宠会一起复制到程序旁边。
