# 阅画 (Yuehua)

快速打开、多格式支持、可标注、可另存为特定大小的图片查看器。

## 功能特性

-   M0：NativeAOT + Avalonia + Magick.NET 技术验证骨架
-   M1：快速打开 / 文件夹导航 / 缩略图条
-   M2：进阶标注（图形 + 文字 + 画笔 + 马赛克遮挡 + 图层 + 线性历史记录）
-   M3：另存为特定大小（像素尺寸 / 文件体积；裁剪导出待做）
-   M4：现代紧凑 UI + 楷体「画」字圆角图标（四角透明）+ 黄金分割窗口居中 + 关闭时旋转 / 翻转保存提示 + NativeAOT 单目录发布

## 系统要求

- Windows 10 / 11 x64（起步平台）
- .NET 10 SDK
- Visual Studio 2022 Build Tools（勾选“使用 C++ 的桌面开发”，NativeAOT 链接所需）

## 构建命令

```bat
dotnet restore
dotnet publish src/Yuehua -c Release -r win-x64 --self-contained
```

AOT 原生产物位于 `src/Yuehua/bin/Release/net10.0/win-x64/publish/`（约 22 MB 的 `Yuehua.exe` + 4 个原生依赖 DLL：`Magick.Native-Q8-x64.dll` / `libSkiaSharp.dll` / `av_libglesv2.dll` / `libHarfBuzzSharp.dll`，共约 63 MB）。

> **分享给别人**：由于 Magick / Skia 原生库在 NativeAOT 单文件打包时会出现版本错乱（运行报 libSkiaSharp 版本不匹配），**单文件 exe 不可靠**。推荐做法：把整个 `publish/` 目录打包成 zip（仓库已生成 `yuehua-win-x64.zip`，约 25 MB），对方解压后双击 `Yuehua.exe` 即可，**无需安装 .NET**。**切勿只发一个 exe**——缺少旁侧的 4 个 DLL 会无法启动。

若链接阶段报 `Platform linker not found` 或 `LNK1181`（注册表探测被拦截的环境），改用一键脚本：

```bat
powershell -ExecutionPolicy Bypass -File tools\build-aot.ps1
```

快速验证 Magick.NET 在 AOT 下可运行：

```bat
dotnet publish src/Yuehua.M0Check -c Release -r win-x64 --self-contained
.\src\Yuehua.M0Check\bin\Release\net10.0\win-x64\publish\Yuehua.M0Check.exe
```

## 使用说明

| 操作 | 入口 |
| --- | --- |
| 打开图片 / 文件夹 | 顶栏左端图标，或直接把文件、文件夹拖进窗口 |
| 上一张 / 下一张 | ← / →，或顶栏箭头、底部缩略图条 |
| 缩放 / 适应窗口 / 1:1 | + / -、滚轮；Ctrl+R 重置视图 |
| 旋转 / 翻转 | 顶栏旋转与翻转图标 |
| 进入标注模式 | Ctrl+E，或顶栏铅笔开关（默认关闭，看图优先） |
| 撤销 / 重做 | Ctrl+Z / Ctrl+Y；侧栏「标注」页还有一条**历史记录**，点任意一步可直接跳过去（最多 60 步） |
| 切换当前图层 | 侧栏「标注」页点图层行；被选中的层会高亮（左侧强调竖条 + 加粗），新标注都落到该层 |
| 另存为指定大小 | Ctrl+S，或顶栏导出图标 →「另存为」对话框；侧栏「导出」页也有入口 |
| 打开设置 | Ctrl+,，或顶栏齿轮图标 |
| 专注模式（隐藏面板与浮动工具条） | F11，或顶栏聚焦图标 |
| 深浅色切换 | Ctrl+D |
| 退出标注 / 退出专注 | Esc |
| 窗口大小 | 默认取主屏工作区的黄金分割比例（0.618）并居中；打开即「纯看图」——侧边面板与底部缩略图条默认隐藏，需要时再点顶栏图标唤出 |
| 关闭保存提示 | 当图片被旋转 / 翻转且尚未保存时，点关闭会弹「是否保存」：保存（打开「另存为」导出旋转 / 翻转后的图）/ 不保存 / 取消 |
| 大文件夹性能 | 缩略图条默认隐藏；仅当唤出缩略图条时才在后台逐张生成，前台先显示文件名占位，生成中显示「生成缩略图 n/总」进度，切换文件夹自动取消上一次未完成的生成 |

「另存为」对话框支持两种模式：

-   指定像素尺寸：直接给宽高，或点 100% / 75% / 50% / 25% / 1920 宽 快捷按钮，可锁定宽高比，实时显示缩放百分比。
-   指定文件体积：给目标 KB，有损格式（JPEG / WebP）二分搜索质量，无损格式（PNG / BMP / TIFF）二分搜索缩放尺寸。

导出默认尺寸为当前图片的原始像素；手动改过宽高后不再随翻页重置（点 100% 可恢复跟随）。
导出会保留当前的旋转 / 翻转（烘焙进导出图）；缩放仍以原始像素为准，不会放大模糊。勾选「把标注烙进导出图」时标注会按原图比例烘焙进去。

## 工具条浮动样式

设置（Ctrl+,）里可逐条切换工具条的呈现方式：

| 设置项 | 默认 | 说明 |
| --- | --- | --- |
| 顶栏工具条浮动 | 开 | 浮于画面之上，圆角 + 阴影 + 半透明，可用条左侧手柄拖动位置 |
| 标注工具箱浮动 | 开 | 同上，手柄在条顶；仅标注模式出现 |
| 缩略图条浮动 | 关 | 关闭时贴底固定并占据画面下方空间；开启后浮在画面底部，宽度收窄 |
| 显示行为 | 常驻悬浮 | 切换「空闲自动淡出」后，鼠标静止到设定延时即淡出，一动鼠标恢复；鼠标停在工具条上不淡出 |
| 淡出延时 | 2 秒 | 仅自动淡出模式下生效，范围 0.5–10 秒 |
| 复位工具条位置 | 按钮 | 浮动位置恢复默认（顶栏居中贴顶、工具箱左中、缩略图贴底） |

固定模式不是「浮动关掉就没了」：工具条会贴回边缘并让出画面空间，图片不会被遮挡。
设置保存在 `%LOCALAPPDATA%\Yuehua\settings.json`，即时生效；专注模式（F11）会连浮动工具条一起隐藏。

## 技术栈

- C# / .NET 10
- Avalonia 12（XAML, MVVM）
- NativeAOT 原生单目录发布
- Magick.NET（ImageMagick）多格式解码与编辑

## 目录结构

```
Yuehua/
├── src/Yuehua/          # 主程序（Avalonia + AOT 入口）
├── src/Yuehua.M0Check/  # M0：Magick.NET 在 AOT 下运行时探针（无界面）
└── tools/               # 构建 / 打包脚本
```

## 许可证

[MIT](LICENSE) © 2026 highwindmx

Powered by WorkBuddy
