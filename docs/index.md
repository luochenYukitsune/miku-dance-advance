---
title: MDA 使用文档
---

# MDA 使用文档

MikuDance Advance（简称 MDA）是运行在 Windows x64 桌面端的 MMD 动画编辑与渲染软件，提供基于 Direct3D 12 的现代渲染引擎和面向原版 MMD／MME 的传统渲染引擎，涵盖模型与动作导入、骨骼与表情关键帧编辑、Bullet 刚柔体物理、实时视口预览以及图片与视频导出。

- [完成第一段动画](guide/first-animation.md)：从安装软件、导入模型与动作，到保存工程并导出一支视频的完整上手步骤。
- [用户指南](guide/index.md)：安装与文件关联、双引擎功能（画风、灯光、MME 与 Toon）、时间轴编辑、自动保存、预览与导出。
- [从源码构建](development/build.md)：本地编译托管代码与原生依赖的方法，以及 GitHub Actions 构建和发版流程。
- [插件开发](development/plugins.md)：插件 API 1 的 `mda-plugin.json` 清单格式、接口生命周期与打包要求。
- [GitHub 源码仓库](https://github.com/luochenYukitsune/miku-dance-advance)

安装包与各版本更新说明统一发布在 [GitHub Releases](https://github.com/luochenYukitsune/miku-dance-advance/releases)。下载时选择 `MDA-<版本>-win-x64-setup.exe` 安装程序即可；标有 `Pre-release` 的版本为预发布版。

Copyright (c) 2026 luochen Yukitsune。原创代码采用 AGPL-3.0-only 许可，第三方组件保留各自原有许可。
