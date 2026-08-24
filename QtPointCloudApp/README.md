# QtPointCloudApp — WpfApp1 的 Qt 复刻

用 **Qt 6 (Widgets)** + **VTK 9** 重新实现 `Apps/WpfApp1` 的点云可视化与处理功能。
本工程是源代码的"移植骨架"，覆盖原项目的核心能力；构建需在装有 Qt6 与 VTK（带 Qt 支持）的环境中进行。

## 功能对照（相对 WpfApp1）

| 功能 | 状态 |
|------|------|
| PLY / PCD 导入 | ✅（PCD 为 ASCII 解析） |
| 示例点云（球/立方/螺旋/平面） | ✅ |
| 多点云管理（列表、显隐、调色板、包围盒） | ✅ |
| 颜色模式（原始 RGB / 高度 / 纯色） | ✅ |
| 坐标轴、背景色、取景 | ✅ |
| 体积计算（体素占据体积 / 凸包体积） | ✅ |
| 裁剪（框选 / 平面） | ✅ |
| 切片渐显动画 | ✅（QTimer 逐层显示） |
| 导出图片 PNG/JPG/BMP | ✅ |
| SQLite 持久化（元数据 + PLY 副本） | ✅ |
| MQTT 状态发布（JSON） | ✅（需 Qt6::Mqtt，可选） |

## 依赖

- **Qt 6**：`Widgets`、`OpenGLWidgets`、`Sql`（必需）；`Mqtt`（可选，用于 MQTT 发布）
- **VTK 9**：需编译时开启 **Qt 支持**（`RenderingQt` / `GUISupportQt`，提供 `QVTKOpenGLNativeWidget`）
- **CMake ≥ 3.16**

> 注意：VTK 的 `QVTKOpenGLNativeWidget` 来自 `VTK::RenderingQt` 模块。若你的 VTK 是系统包，请确认其包含 Qt 绑定（例如 Debian 的 `libvtk9-dev` 通常不含 Qt 模块，建议用 vcpkg 或从源码开启 `VTK_GROUP_ENABLE_Qt` 编译）。

## 用 vcpkg 安装依赖（示例）

```bash
vcpkg install qtbase qtdeclarative qtmqtt vtk[qt,opengl]
```

构建：

```bash
cmake -B build -S . -DCMAKE_TOOLCHAIN_FILE=<vcpkg>/scripts/buildsystems/vcpkg.cmake
cmake --build build --config Release
```

或在本机直接用 CMake（Qt/VTK 已在 PATH / CMAKE_PREFIX_PATH 中）：

```bash
cmake -B build -S .
cmake --build build
./build/QtPointCloudApp
```

## 目录结构

```
QtPointCloudApp/
├── CMakeLists.txt
├── README.md
└── src/
    ├── main.cpp              # 入口、OpenGL 上下文格式
    ├── MainWindow.h/.cpp     # 主窗口与全部交互
    ├── PointCloudItem.h      # 点云数据模型
    ├── CloudManager.h/.cpp   # 渲染器与多组点云管理（对应 WPF 的多点云引擎）
    ├── GeometryHelper.h/.cpp # 体积（体素/凸包）+ 裁剪（框/平面）
    ├── PcdParser.h/.cpp      # ASCII PCD 解析
    ├── Exporter.h/.cpp       # 渲染窗口截图导出
    ├── MqttClient.h/.cpp     # QMqtt 封装（可选编译）
    └── DatabaseHelper.h/.cpp # Qt SQL / SQLite 持久化
```

## 说明

- 本工程是在**没有本地 Qt/VTK 工具链**的环境下手写的，未经本地编译验证；
  首次构建时可能需要根据你所用的 Qt6 / VTK9 版本微调少量 API（尤其是 `QVTKOpenGLNativeWidget`、
  `vtkHull` 的凸包构建与 `vtk_module_autoinit` 的使用）。
- 颜色模式 `Original` 仅在导入数据带有 RGB 标量（如带颜色的 PCD/PLY）时生效，
  否则回退为纯色；示例点云无颜色，建议用 `Height` 或 `Solid` 查看立体效果。
