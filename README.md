# Prism Image Viewer

一款高性能的 Windows 图像查看器，专为专业摄影师和设计师打造。支持 GPU 加速渲染、RAW 格式、PSD/PSB 文件以及超大图像的分块加载。

## 主要特性

### 图像格式支持
- **通用格式**: JPG, PNG, BMP, GIF, TIFF, WebP
- **专业格式**: PSD, PSB (Photoshop)
- **RAW 格式**: ARW, CR2, CR3, NEF, ORF, RAF, RW2, DNG, PEF 等

### 性能优化
- **GPU 加速**: 基于 DirectN + WIC 的硬件加速渲染
- **智能预加载**: 前后各预加载 3 张图片，方向对称
- **导航队列**: 连续快速切换时自动合并请求，直接跳到目标图片
- **动态缓存**: 根据系统内存自动调整缓存数量（16GB→15张, 8GB→10张）
- **渐进式加载**: 大文件先显示缩略图预览，再加载完整图像

### 用户体验
- **无边框窗口**: 自定义标题栏，支持拖拽移动和边缘缩放
- **智能缩略图**: 悬停渐显，支持常驻显示模式
- **鸟瞰图模式**: 快速浏览大图全貌
- **文件夹穿透**: 浏览完当前文件夹自动进入相邻文件夹
- **多语言支持**: 简体中文、英文

### 交互功能
- 鼠标滚轮缩放 / 拖拽平移
- 键盘左右键切换图片
- 旋转、适应窗口、原始大小 (1:1)
- 打印功能
- 在资源管理器中打开、复制文件路径、删除图片

## 系统要求

- Windows 10/11 (x64)
- .NET 10 Runtime
- 推荐 8GB+ 内存（用于大图像缓存）

## 开源致谢

本项目使用了以下开源项目：

| 项目 | 许可证 | 用途 |
|------|--------|------|
| [Magick.NET](https://github.com/dlemstra/Magick.NET) | Apache-2.0 | 图像解码与处理（RAW/PSD/PSB 支持） |
| [ImageSharp](https://github.com/SixLabors/ImageSharp) | Apache-2.0 | 辅助图像处理 |
| [DirectN](https://github.com/smourier/DirectN) | MIT | DirectX 封装，GPU 加速 |
| [WicNet](https://github.com/smourier/WicNet) | MIT | Windows Imaging Component 封装 |
| [Wpf.Controls.PanAndZoom](https://github.com/wieslawsoltes/PanAndZoom) | MIT | 图像平移与缩放控件 |
| [psd-tools](https://github.com/psd-tools/psd-tools) | MIT | PSD/PSB 文件格式参考 |

### psd-tools 说明

本项目在实现 PSD/PSB 文件支持时，参考了 [psd-tools](https://github.com/psd-tools/psd-tools) 项目的文件格式解析逻辑。psd-tools 是一个优秀的 Python 库，提供了完整的 PSD/PSB 文件规范实现，为本项目的专业格式支持提供了重要参考。

---

## 版本迭代

<details>
<summary>点击展开查看完整版本历史</summary>

### v2.0.0 (当前版本)

#### 核心重构
- 软件架构全面重构
- 引入 DirectN + WIC，实现 GPU 加速的图像渲染
- ImageBooster 图像加速服务
- 后台预加载与优先队列管理

#### 专业格式支持
- PSD/PSB 文件支持（基于 psd-tools 参考实现）
- 超大图像分块加载（Tile 模式）
- RAW 格式支持（ARW, CR2, CR3, NEF, ORF, RAF, RW2, DNG 等）
- 增强元数据服务，支持颜色配置

#### 性能优化
- 缩略图多线程生成
- 缩略图智能提取与磁盘缓存
- 渐进式加载
- 动画系统
- 增强型文件监视（带防抖）
- 删除队列处理
- 视口管理优化

#### 稳定性修复
- 修复 ImageCache 和 ThumbnailCache 并发读写问题
- 修复 ShowImage 取消逻辑竞态问题
- 修复 ImageBooster 中 .Result 可能死锁
- 修复 TileLoader 每次都加载完整图像
- 修复 MemoryManager GC 调用问题
- 修复文件监视器防抖问题
- 确保所有 MagickImage 实例都使用 using 释放

#### UI 优化
- 无边框窗口设计
- 自定义标题栏（最小化、最大化/还原、关闭）
- 窗口边缘 8 方向缩放支持
- 未选择图片时显示启动界面
- 当前图片绿色边框高亮
- 信息按钮、缩放百分比、图片序号显示

#### 交互功能
- 拖放图片到窗口打开
- 点击缩略图切换图片
- 智能展示缩略图（悬停渐显）
- 图片旋转（左/右）
- 适应窗口 / 原始大小 (1:1)
- 图片缩放（鼠标滚轮）与拖拽平移
- 打印功能
- 文件夹穿透

#### 右键菜单
- 在资源管理器中打开
- 复制文件路径
- 删除图片

#### 快捷键
- 左右方向键 / PageUp/PageDown: 切换图片
- ESC: 退出最大化
- Ctrl + 滚轮: 缩放

</details>

---

## 许可证

本项目采用 MIT 许可证开源。

```
MIT License

Copyright (c) 2025 Prism Image Viewer Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
