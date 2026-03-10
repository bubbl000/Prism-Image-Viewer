# ImageViewer 项目规范

## 项目简介
WPF 图片浏览器，支持多格式浏览、缩略图、设置持久化、GIF 动画等。
单人开发，Code-Behind 架构，不使用 MVVM。

## 技术栈
- C# / WPF (.NET)
- `Wpf.Controls.PanAndZoom`：图片缩放与平移（`ZoomBorder`）

## 文件结构
| 文件 | 职责 |
|------|------|
| `MainWindow.xaml/.cs` | 主窗口，核心浏览逻辑 |
| `AppSettings.cs` | 设置数据模型，JSON 持久化到 `%APPDATA%\ImageViewer\appsettings.json`，静态单例 `AppSettings.Current` |
| `SettingsWindow.xaml/.cs` | 设置窗口（文件关联 / 常规 / 习惯） |
| `ConfirmDialog.xaml/.cs` | 自定义深色删除确认弹窗 |

## 架构约定
- Code-Behind，不引入 ViewModel，不使用数据绑定
- UI 控件通过 `x:Name` 在 code-behind 直接访问
- 弹窗（SettingsWindow、ConfirmDialog）用 `Owner = this` 居中于主窗口

## 核心状态（MainWindow）
- `_imageFiles`：当前图片路径列表
- `_currentIndex`：当前图片索引（-1 表示无图片）
- `_currentRotation`：旋转角度（0/90/180/270）
- `GifAnimator`：内部类，管理 GIF 帧动画的 `DispatcherTimer`

## 支持格式
`.jpg` `.jpeg` `.png` `.bmp` `.gif` `.tiff` `.tif` `.webp`
定义在 `SupportedExts`，新增格式同步更新 OpenFileDialog Filter 和文件关联。

## 关键流程
- 图片加载统一走 `LoadFromPath()` → `ShowImage()`，不绕过
- 图片加载为异步（`async`/`await` + `Task.Run`），加载中显示覆层提示
- GIF 检测：多帧 → `GifAnimator`；单帧 → 静态显示
- UI 状态更新：`UpdateTitleInfo()` + `UpdateNavButtons()`

## 设置项（AppSettings）
| 字段 | 说明 |
|------|------|
| `WheelMode` | 滚轮模式：缩放 / 翻页 / 滚动 |
| `LoopWithinFolder` | 循环翻页 |
| `SmartToolbar` | 智能工具栏（无操作 1.5s 后渐隐） |
| `ShowThumbnails` | 缩略图栏开关 |
| `ShowBirdEye` | 鸟瞰图开关 |
| `AlwaysOnTop` | 窗口置顶 |
| `RememberPosition` | 记住窗口位置 |

## 编码习惯
- 用 `─── 注释 ───` 分隔功能区块
- 工具/静态方法放在文件末尾
- 缩略图异步加载用 `CancellationToken`，切换图片时取消上次加载

## 版本记录规则
- 完成大功能记录为 v1.0 v2.0 等，小功能修改记录为 v1.0.1 等
- 每次完成代码后在下方追加记录，README.md 同步添加可折叠版本记录

## 版本历史

### v1.1 — 初始版本
- 自定义无边框窗口（深色主题）
- 打开文件 / 拖放 / 文件夹扫描
- 左右导航、键盘翻页
- ZoomBorder 缩放平移、缩放%输入框
- 旋转（左/右 90°）
- 图片信息弹窗、删除、在资源管理器中显示、复制路径
- Ctrl+拖拽导出到其他程序

### v1.2 — 缩略图栏 + 工具栏重构
- 新增底部缩略图栏（88px），两阶段异步加载，`CancellationToken` 支持切换取消
- 缩略图点击跳转，当前项蓝色高亮，导航时自动滚动到可见区域
- 缩放%输入框移至工具栏左侧
- OpenFileDialog Filter 改为从 `SupportedExts` 自动生成
- 删除后 UI 重置提取为 `ClearImageState()` 方法

### v1.2.1 — 工具栏布局细化
- 信息按钮（ℹ）移至缩放输入框左侧
- 旋转、更多按钮改为右对齐
- 移除删除按钮
- 缩略图栏改为无滚动条（Hidden），未打开图片时隐藏

