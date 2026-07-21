using Kitware.VTK;
using System.Collections.Generic;
using System.Windows.Media;

namespace WpfApp1
{
    /// <summary>
    /// 点云颜色模式（与 MainWindow 共用，移到此处便于多点数云模型引用）
    /// </summary>
    public enum ColorMode
    {
        Original,
        Height,
        Solid
    }

    /// <summary>
    /// 裁剪模式
    /// </summary>
    public enum ClipMode
    {
        None,
        Box,    // 包围盒裁剪（保留框内/框外）
        Plane   // 平面裁剪（保留平面一侧）
    }

    /// <summary>
    /// 裁剪参数设置
    /// </summary>
    public class ClipSettings
    {
        public ClipMode Mode = ClipMode.None;

        // 框选裁剪范围
        public double BoxMinX, BoxMaxX;
        public double BoxMinY, BoxMaxY;
        public double BoxMinZ, BoxMaxZ;

        // 平面裁剪：平面方程 normal·(x - origin) = 0
        public double PlaneOriginX, PlaneOriginY, PlaneOriginZ;
        public double PlaneNormalX, PlaneNormalY, PlaneNormalZ;

        // InsideOut：框选时保留框外；平面时保留法线反侧
        public bool InsideOut = false;

        public ClipSettings Clone()
        {
            return new ClipSettings
            {
                Mode = Mode,
                BoxMinX = BoxMinX, BoxMaxX = BoxMaxX,
                BoxMinY = BoxMinY, BoxMaxY = BoxMaxY,
                BoxMinZ = BoxMinZ, BoxMaxZ = BoxMaxZ,
                PlaneOriginX = PlaneOriginX, PlaneOriginY = PlaneOriginY, PlaneOriginZ = PlaneOriginZ,
                PlaneNormalX = PlaneNormalX, PlaneNormalY = PlaneNormalY, PlaneNormalZ = PlaneNormalZ,
                InsideOut = InsideOut
            };
        }
    }

    /// <summary>
    /// 单个点云项：包含原始数据、显示数据（含裁剪）、渲染 Actor 与元数据
    /// </summary>
    public class PointCloudItem
    {
        public int Id;                 // 数据库记录 Id（0 表示尚未入库）
        public string Name = "";
        public string Format = "";     // PLY / PCD / Sample / MQTT
        public string SourcePath = ""; // 文件来源路径（入库后指向 SavedClouds 副本）

        public vtkPolyData? OriginalData; // 原始（未裁剪）数据
        public vtkPolyData? DisplayData;  // 当前显示数据（裁剪后）
        public vtkActor? Actor;

        // 单视口模式下，每个点云的包围盒与色标条 Actor 加入全局 Renderer
        public vtkActor? BoundingBoxActor;
        public vtkScalarBarActor? ScalarBarActor;

        public bool Visible = true;
        public Color Color = Colors.Cyan;     // Solid 模式使用的基色
        public double PointSize = 2.0;
        public ColorMode ColorMode = ColorMode.Original;

        public ClipSettings Clip = new ClipSettings();

        public long PointCount;
        public double[] Bounds = new double[6];

        // —— 切片显示（单视口模式下按轴分片渐显，不直接显示完整点云）——
        public List<vtkPolyData>? SliceData;   // 各切片几何（含颜色）
        public List<vtkActor>? SliceActors;    // 各切片 Actor（初始隐藏，逐片显示）
        public int SliceCount = 50;            // 切片总数
        public int SliceAxis = 2;              // 切片轴：0=X, 1=Y, 2=Z
        public int ShownSlices = 0;            // 已显示的切片数（进度）
        public bool SliceMode = false;         // true=处于切片渐显态；false=显示完整/隐藏

        public double VoxelVolume;   // 体素占据体积（0 = 未计算）
        public double ConvexVolume;   // 凸包体积
        public bool VolumeComputed;

        public int ListIndex;        // 列表中的索引，用于 UI 绑定
    }
}
