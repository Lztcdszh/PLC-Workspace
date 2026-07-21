using Kitware.VTK;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        // VTK 核心对象（单视口：所有点云叠加在同一 Renderer）
        private bool _isInitialized = false;
        private vtkRenderer? _renderer;
        private RenderWindowControl? _vtkControl;
        private vtkRenderWindow? _renderWindow;
        private vtkAxesActor? _axesActor;          // 全局坐标轴（单视口）
        private System.Windows.Threading.DispatcherTimer? _sliceTimer;
        private bool _slicePlaying = false;

        // 多点数云模型
        private List<PointCloudItem> _clouds = new();
        private PointCloudItem? _activeCloud = null;
        private PointCloudItem? _bbOwner = null;   // 当前显示包围盒的点云
        private int _nextColorIndex = 0;
        private static readonly Color[] Palette = new[]
        {
            Colors.Cyan, Colors.Orange, Colors.LimeGreen, Colors.Magenta,
            Colors.Yellow, Colors.Red, Colors.SkyBlue, Colors.Goldenrod,
            Colors.SpringGreen, Colors.Orchid
        };

        // 全局显示开关
        private bool _axesVisible = false;
        private bool _boundingBoxVisible = false;
        private bool _colorBarVisible = false;
        private int _bgColorIndex = 0;

        // MQTT
        private MqttPublisher? _mqtt;

        // UI 行集合
        private ObservableCollection<CloudRow> _cloudRows = new();
        private ObservableCollection<DbRow> _dbRows = new();

        public class CloudRow
        {
            public int Id { get; set; }
            public string Detail { get; set; } = "";
            public bool IsVisible { get; set; }
            public Brush Swatch { get; set; } = Brushes.Cyan;
        }
        public class DbRow
        {
            public int Id { get; set; }
            public string Detail { get; set; } = "";
        }

        public MainWindow()
        {
            InitializeComponent();
            CloudListBox.ItemsSource = _cloudRows;
            DbListBox.ItemsSource = _dbRows;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                DatabaseHelper.Init();
                RefreshDbList();
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_err.txt"),
                    $"[Window_Loaded] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); } catch { }
                MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            InitializeVtk();
        }

        #region VTK 初始化（单视口）

        private void InitializeVtk()
        {
            try
            {
                vtkObject.GlobalWarningDisplayOff();
                _isInitialized = true;
                StatusVtk.Text = "VTK 就绪";
                StatusVtk.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x88, 0x33));
                InitSingleView();
                ShowEmptyPlaceholder(_clouds.Count == 0);
                DebugWrite("VTK 初始化成功");
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_err.txt"),
                    $"[InitializeVtk] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); } catch { }
                MessageBox.Show($"VTK 初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>创建单一 3D 视口（所有点云共享同一 Renderer）</summary>
        private void InitSingleView()
        {
            try
            {
                if (VtkPanel == null) return;
                var control = new RenderWindowControl
                {
                    Dock = DockStyle.Fill,
                    BackColor = System.Drawing.Color.WhiteSmoke
                };
                VtkPanel.Controls.Add(control);
                _vtkControl = control;

                int attempt = 0;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                timer.Tick += (s, e) =>
                {
                    if (control.RenderWindow == null)
                    {
                        if (++attempt > 40) { timer.Stop(); DebugWrite("单视口初始化超时"); return; }
                        return;
                    }
                    timer.Stop();
                    _renderWindow = control.RenderWindow;
                    _renderer = vtkRenderer.New();
                    _renderWindow.AddRenderer(_renderer);
                    ApplyBackgroundColorTo(_renderer);

                    var interactor = _renderWindow.GetInteractor();
                    if (interactor != null)
                    {
                        var style = vtkInteractorStyleTrackballCamera.New();
                        interactor.SetInteractorStyle(style);
                        interactor.Initialize();
                        interactor.Enable();
                    }

                    if (_axesVisible) AddGlobalAxes();
                    _renderWindow.Render();

                    // 若初始化前已有点云（防御），补加其 Actor
                    foreach (var c in _clouds)
                    {
                        if (c.Actor != null) _renderer.AddActor(c.Actor);
                        if (c.SliceActors != null) foreach (var a in c.SliceActors) _renderer.AddActor(a);
                    }
                    if (_boundingBoxVisible && _activeCloud != null) UpdateBoundingBox();

                    // 若初始化前已有点云（防御/竞态），补取景并重启渐显
                    FrameScene();
                    if (_activeCloud != null && _activeCloud.SliceMode && _activeCloud.SliceActors != null
                        && _activeCloud.ShownSlices == 0)
                        StartSlicePlayback(_activeCloud);

                    UpdateStatus("就绪 - 可导入/生成点云（单显示框 + 切片渐显）");
                    ShowEmptyPlaceholder(_clouds.Count == 0);
                };
                timer.Start();
            }
            catch (Exception ex) { DebugWrite($"单视口初始化失败: {ex.Message}"); }
        }

        #endregion

        private void RenderScene()
        {
            _renderWindow?.Render();
        }

        /// <summary>所有点云的联合包围盒（无视可见性），用于相机取景</summary>
        private double[]? UnionBoundsOfClouds()
        {
            if (_clouds.Count == 0) return null;
            double[] ub = { double.MaxValue, double.MinValue, double.MaxValue, double.MinValue, double.MaxValue, double.MinValue };
            foreach (var c in _clouds)
            {
                if (c.Bounds == null || c.Bounds.Length < 6) continue;
                var b = c.Bounds;
                ub[0] = Math.Min(ub[0], b[0]); ub[1] = Math.Max(ub[1], b[1]);
                ub[2] = Math.Min(ub[2], b[2]); ub[3] = Math.Max(ub[3], b[3]);
                ub[4] = Math.Min(ub[4], b[4]); ub[5] = Math.Max(ub[5], b[5]);
            }
            if (ub[0] > ub[1] || ub[2] > ub[3] || ub[4] > ub[5]) return null;
            return ub;
        }

        /// <summary>将相机自动取景到所有点云的联合包围盒，并按 3/4 等距视角摆正（修复数据在视野外导致“一片空白”）</summary>
        private void FrameScene()
        {
            if (_renderer == null) return;
            var b = UnionBoundsOfClouds();
            if (b == null) return;
            try
            {
                double cx = (b[0] + b[1]) / 2, cy = (b[2] + b[3]) / 2, cz = (b[4] + b[5]) / 2;
                double dx = b[1] - b[0], dy = b[3] - b[2], dz = b[5] - b[4];
                double radius = 0.5 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (radius < 1e-6) radius = 1.0;
                var cam = _renderer.GetActiveCamera();
                if (cam == null) return;
                // 等距视角方向（指向相机）
                double[] d = { 1.0, -1.0, 0.8 };
                double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                double dist = radius * 2.6;
                cam.SetPosition(cx + d[0] / len * dist, cy + d[1] / len * dist, cz + d[2] / len * dist);
                cam.SetFocalPoint(cx, cy, cz);
                cam.SetViewUp(0.0, 0.0, 1.0);
                cam.SetClippingRange(Math.Max(0.01, dist - radius * 4), dist + radius * 4);
                _renderer.ResetCameraClippingRange();
                RenderScene();
            }
            catch (Exception ex) { DebugWrite($"FrameScene 失败: {ex.Message}"); }
        }

        private void ShowEmptyPlaceholder(bool show)
        {
            if (EmptyPlaceholder != null)
                EmptyPlaceholder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        #region 多点云引擎

        /// <summary>添加一个点云到场景并设为当前选中；导入后按切片渐显</summary>
        private void AddCloud(vtkPolyData poly, string name, string format, string sourcePath)
        {
            if (!_isInitialized || poly == null) return;
            EnsureVerts(poly);

            var item = new PointCloudItem
            {
                Name = name,
                Format = format,
                SourcePath = sourcePath,
                OriginalData = poly,
                DisplayData = poly,
                Color = Palette[_nextColorIndex % Palette.Length],
                ColorMode = ColorMode.Original,
                PointSize = PointSizeSlider.Value,
                PointCount = poly.GetNumberOfPoints(),
                Bounds = poly.GetBounds(),
                ListIndex = _clouds.Count,
                SliceAxis = SliceAxisCombo.SelectedIndex < 0 ? 2 : SliceAxisCombo.SelectedIndex,
                SliceCount = ParseSliceCount(),
                SliceMode = true,
                ShownSlices = 0
            };
            _nextColorIndex++;

            item.Actor = BuildActor(item);
            item.Actor.SetVisibility(0); // 切片模式下先隐藏完整点云
            _renderer?.AddActor(item.Actor);
            _clouds.Add(item);

            // 构建切片（初始全部隐藏，准备渐显）
            BuildSlices(item);

            int dbId = DatabaseHelper.Insert(item);
            if (dbId > 0) RefreshDbList();

            SetActiveCloud(item);
            FrameScene(); // 加云后把相机对到数据（否则数据在视野外，看不到）
            RefreshCloudList();
            ShowEmptyPlaceholder(false);
            RenderScene();
            UpdateInfoPanel();

            // 自动开始切片渐显（不显示完整点云）
            StartSlicePlayback(item);
            AutoPublish();
        }

        private vtkActor BuildActor(PointCloudItem item)
        {
            var data = GetRenderData(item);
            var mapper = vtkPolyDataMapper.New();
            mapper.SetInputData(data);
            mapper.SetScalarModeToUsePointData();
            var pd = data.GetPointData();
            if (pd.GetScalars() == null || pd.GetScalars().GetNumberOfTuples() == 0)
                mapper.SetScalarModeToDefault();

            var actor = vtkActor.New();
            actor.SetMapper(mapper);
            actor.GetProperty().SetPointSize((float)item.PointSize);
            actor.GetProperty().SetLighting(false);
            return actor;
        }

        private static vtkPolyData MakeSolid(vtkPolyData src, Color color)
        {
            long n = src.GetNumberOfPoints();
            var colors = vtkUnsignedCharArray.New();
            colors.SetNumberOfComponents(3);
            colors.SetNumberOfTuples((int)n);
            colors.SetName("SolidColors");
            byte r = color.R, g = color.G, b = color.B;
            for (int i = 0; i < (int)n; i++) colors.SetTuple3(i, r, g, b);
            var copy = vtkPolyData.New();
            copy.DeepCopy(src);
            copy.GetPointData().SetScalars(colors);
            return copy;
        }

        private vtkPolyData GetColoredData(PointCloudItem item)
        {
            var src = item.OriginalData!;
            if (item.ColorMode == ColorMode.Original)
            {
                var sc = src.GetPointData().GetScalars();
                if (sc != null && sc.GetNumberOfTuples() > 0) return src;
                return MakeSolid(src, item.Color);
            }
            if (item.ColorMode == ColorMode.Height)
            {
                var b = src.GetBounds();
                double zMin = b[4], zMax = b[5];
                if (zMax - zMin < 1e-9) zMax = zMin + 1;
                long n = src.GetNumberOfPoints();
                var colors = vtkUnsignedCharArray.New();
                colors.SetNumberOfComponents(3);
                colors.SetNumberOfTuples((int)n);
                colors.SetName("HeightColors");
                var pts = src.GetPoints();
                for (int i = 0; i < (int)n; i++)
                {
                    double[] p = pts.GetPoint(i);
                    double t = (p[2] - zMin) / (zMax - zMin);
                    var (r, g, bb) = HeightToColor(t);
                    colors.SetTuple3(i, r, g, bb);
                }
                var copy = vtkPolyData.New();
                copy.DeepCopy(src);
                copy.GetPointData().SetScalars(colors);
                return copy;
            }
            return MakeSolid(src, item.Color);
        }

        private vtkPolyData GetRenderData(PointCloudItem item)
        {
            var colored = GetColoredData(item);
            if (item.Clip.Mode == ClipMode.Box)
            {
                var c = item.Clip;
                var clipped = ClipHelper.ApplyBox(colored, c.BoxMinX, c.BoxMaxX, c.BoxMinY, c.BoxMaxY, c.BoxMinZ, c.BoxMaxZ, c.InsideOut);
                if (clipped != null) { item.DisplayData = clipped; return clipped; }
                item.DisplayData = colored; return colored;
            }
            if (item.Clip.Mode == ClipMode.Plane)
            {
                var c = item.Clip;
                var clipped = ClipHelper.ApplyPlane(colored, c.PlaneOriginX, c.PlaneOriginY, c.PlaneOriginZ,
                    c.PlaneNormalX, c.PlaneNormalY, c.PlaneNormalZ, c.InsideOut);
                if (clipped != null) { item.DisplayData = clipped; return clipped; }
                item.DisplayData = colored; return colored;
            }
            item.DisplayData = colored;
            return colored;
        }

        /// <summary>重建点云完整 Actor 与各切片 Actor</summary>
        private void ApplyItemVisual(PointCloudItem item)
        {
            if (_renderer != null && item.Actor != null)
            {
                _renderer.RemoveActor(item.Actor);
                item.Actor.Dispose();
            }
            item.Actor = BuildActor(item);
            item.Actor.SetVisibility((!item.Visible || item.SliceMode) ? 0 : 1);
            _renderer?.AddActor(item.Actor);

            // 重建切片（保留当前进展 ShownSlices 与 SliceMode）
            BuildSlices(item);
            RenderScene();
        }

        private void SetActiveCloud(PointCloudItem? item)
        {
            _activeCloud = item;
            if (item != null)
            {
                ColorModeCombo.SelectedIndex = (int)item.ColorMode;
                PointSizeSlider.Value = item.PointSize;
                PointSizeLabel.Text = item.PointSize.ToString("F0");
                SliceAxisCombo.SelectedIndex = item.SliceAxis;
                SliceCountBox.Text = item.SliceCount.ToString();
                FillClipDefaults(item);
                if (_boundingBoxVisible) UpdateBoundingBox();
                if (_colorBarVisible) UpdateColorBarForActive();
                UpdateSliceProgress(item);
            }
            UpdateInfoPanel();
        }

        private void RefreshCloudList()
        {
            _cloudRows.Clear();
            foreach (var c in _clouds)
            {
                _cloudRows.Add(new CloudRow
                {
                    Id = c.ListIndex,
                    Detail = $"#{c.ListIndex} {c.Name}  ({c.PointCount:N0}点)" +
                             (c.VolumeComputed ? $"  V≈{FormatVolume(c.VoxelVolume)}" : "") +
                             (c.Clip.Mode != ClipMode.None ? "  [已裁剪]" : ""),
                    IsVisible = c.Visible,
                    Swatch = new SolidColorBrush(c.Color)
                });
            }
            CloudCountText.Text = $"共 {_clouds.Count} 个点云";
        }

        private PointCloudItem? FindCloudByIndex(int listIndex) =>
            _clouds.FirstOrDefault(c => c.ListIndex == listIndex);

        private void RemoveCloudInternal(PointCloudItem item)
        {
            if (item.Actor != null && _renderer != null)
            {
                _renderer.RemoveActor(item.Actor);
                item.Actor.Dispose();
            }
            item.Actor = null;

            if (item.SliceActors != null)
            {
                foreach (var a in item.SliceActors)
                {
                    _renderer?.RemoveActor(a);
                    a.Dispose();
                }
                item.SliceActors = null;
            }
            item.SliceData = null;

            if (item.BoundingBoxActor != null && _renderer != null)
            {
                _renderer.RemoveActor(item.BoundingBoxActor);
                item.BoundingBoxActor.Dispose();
            }
            item.BoundingBoxActor = null;

            if (item.ScalarBarActor != null && _renderer != null)
            {
                _renderer.RemoveActor(item.ScalarBarActor);
                item.ScalarBarActor.Dispose();
            }
            item.ScalarBarActor = null;

            if (item.OriginalData != null) item.OriginalData.Dispose();
            if (item.DisplayData != null && item.DisplayData != item.OriginalData) item.DisplayData.Dispose();

            if (_bbOwner == item) _bbOwner = null;
            _clouds.Remove(item);
            if (_activeCloud == item) _activeCloud = null;
            ShowEmptyPlaceholder(_clouds.Count == 0);
        }

        private void RemoveCloud_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCloud == null) { MessageBox.Show("请先在点云列表中选择一个点云"); return; }
            if (_activeCloud != null && _slicePlaying) StopSliceTimer();
            RemoveCloudInternal(_activeCloud!);
            if (_boundingBoxVisible) { RemoveBoundingBox(); UpdateBoundingBox(); }
            RefreshCloudList();
            RenderScene();
            UpdateInfoPanel();
            AutoPublish();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            StopSliceTimer();
            foreach (var c in _clouds.ToList()) RemoveCloudInternal(c);
            _activeCloud = null;
            _bbOwner = null;
            RemoveBoundingBox();
            RefreshCloudList();
            RenderScene();
            UpdateInfoPanel();
            UpdateStatus("已清空全部点云（数据库记录保留）");
            AutoPublish();
        }

        private void CloudListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CloudListBox.SelectedItem is CloudRow row)
            {
                var cloud = FindCloudByIndex(row.Id);
                if (cloud != null) SetActiveCloud(cloud);
            }
        }

        private void CloudVisible_Checked(object sender, RoutedEventArgs e) => SetCloudVisibility(sender, true);
        private void CloudVisible_Unchecked(object sender, RoutedEventArgs e) => SetCloudVisibility(sender, false);

        private void SetCloudVisibility(object sender, bool visible)
        {
            if ((sender as FrameworkElement)?.DataContext is CloudRow row)
            {
                var cloud = FindCloudByIndex(row.Id);
                if (cloud != null) ApplyCloudVisibility(cloud, visible);
            }
        }

        private void ApplyCloudVisibility(PointCloudItem cloud, bool visible)
        {
            cloud.Visible = visible;
            if (cloud.SliceMode)
            {
                if (cloud.SliceActors != null)
                    for (int s = 0; s < cloud.SliceActors.Count; s++)
                        cloud.SliceActors[s].SetVisibility(visible && s < cloud.ShownSlices ? 1 : 0);
                cloud.Actor?.SetVisibility(0);
            }
            else
            {
                cloud.Actor?.SetVisibility(visible ? 1 : 0);
                if (cloud.SliceActors != null)
                    foreach (var a in cloud.SliceActors) a.SetVisibility(0);
            }
            RenderScene();
        }

        private void ShowAllClouds_Click(object sender, RoutedEventArgs e) => SetAllCloudsVisibility(true);
        private void HideAllClouds_Click(object sender, RoutedEventArgs e) => SetAllCloudsVisibility(false);

        private void SetAllCloudsVisibility(bool visible)
        {
            if (_clouds.Count == 0) return;
            foreach (var c in _clouds) ApplyCloudVisibility(c, visible);
            RefreshCloudList();
            RenderScene();
            UpdateStatus(visible ? "已显示全部点云" : "已隐藏全部点云");
            AutoPublish();
        }

        #endregion

        #region 文件导入

        private void ImportPly_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckInitialized()) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PLY 文件 (*.ply)|*.ply|所有文件 (*.*)|*.*",
                Title = "导入 PLY 点云文件",
                Multiselect = true
            };
            if (dialog.ShowDialog() == true)
            {
                foreach (var f in dialog.FileNames) LoadPlyFile(f);
            }
        }

        private void ImportPcd_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckInitialized()) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PCD 文件 (*.pcd)|*.pcd|所有文件 (*.*)|*.*",
                Title = "导入 PCD 点云文件",
                Multiselect = true
            };
            if (dialog.ShowDialog() == true)
            {
                foreach (var f in dialog.FileNames) LoadPcdFile(f);
            }
        }

        private void LoadPlyFile(string filePath)
        {
            try
            {
                var reader = vtkPLYReader.New();
                reader.SetFileName(filePath);
                reader.Update();
                var polyData = reader.GetOutput();
                if (polyData == null || polyData.GetNumberOfPoints() == 0)
                {
                    MessageBox.Show($"PLY 文件为空或格式不正确: {Path.GetFileName(filePath)}");
                    return;
                }
                AddCloud(polyData, Path.GetFileName(filePath), "PLY", filePath);
                UpdateStatus($"已加载 PLY: {Path.GetFileName(filePath)} | 点数: {polyData.GetNumberOfPoints():N0}");
            }
            catch (Exception ex)
            {
                DebugWrite($"加载 PLY 失败: {ex.Message}");
                MessageBox.Show($"加载 PLY 文件失败: {ex.Message}");
            }
        }

        private void LoadPcdFile(string filePath)
        {
            try
            {
                var polyData = ParsePcdFile(filePath);
                if (polyData == null || polyData.GetNumberOfPoints() == 0)
                {
                    MessageBox.Show($"PCD 文件为空或格式不支持（仅支持 ASCII）: {Path.GetFileName(filePath)}");
                    return;
                }
                AddCloud(polyData, Path.GetFileName(filePath), "PCD", filePath);
                UpdateStatus($"已加载 PCD: {Path.GetFileName(filePath)} | 点数: {polyData.GetNumberOfPoints():N0}");
            }
            catch (Exception ex)
            {
                DebugWrite($"加载 PCD 失败: {ex.Message}");
                MessageBox.Show($"加载 PCD 文件失败: {ex.Message}");
            }
        }

        #endregion

        #region PCD 解析

        private vtkPolyData? ParsePcdFile(string filePath)
        {
            var lines = File.ReadAllLines(filePath);
            int dataStartIndex = -1;
            int pointCount = 0;
            string dataType = "ascii";
            var fields = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("FIELDS"))
                    fields = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
                else if (line.StartsWith("POINTS"))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) int.TryParse(parts[1], out pointCount);
                }
                else if (line.StartsWith("DATA"))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) dataType = parts[1].ToLower();
                    dataStartIndex = i + 1;
                    break;
                }
            }

            if (dataType != "ascii" || dataStartIndex < 0 || pointCount == 0) return null;

            int xIdx = fields.IndexOf("x");
            int yIdx = fields.IndexOf("y");
            int zIdx = fields.IndexOf("z");
            if (xIdx < 0 || yIdx < 0 || zIdx < 0) return null;

            int rgbIdx = fields.IndexOf("rgb");
            int rIdx = fields.IndexOf("r");
            int gIdx = fields.IndexOf("g");
            int bIdx = fields.IndexOf("b");

            var points = vtkPoints.New();
            points.SetNumberOfPoints(pointCount);

            vtkUnsignedCharArray? colors = null;
            if (rgbIdx >= 0 || (rIdx >= 0 && gIdx >= 0 && bIdx >= 0))
            {
                colors = vtkUnsignedCharArray.New();
                colors.SetNumberOfComponents(3);
                colors.SetNumberOfTuples(pointCount);
                colors.SetName("Colors");
            }

            int pointIndex = 0;
            for (int i = dataStartIndex; i < lines.Length && pointIndex < pointCount; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                if (!double.TryParse(parts[xIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                    !double.TryParse(parts[yIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                    !double.TryParse(parts[zIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    continue;

                points.SetPoint(pointIndex, x, y, z);

                if (colors != null)
                {
                    if (rgbIdx >= 0 && parts.Length > rgbIdx)
                    {
                        if (float.TryParse(parts[rgbIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out float rgbFloat))
                        {
                            int rgbInt = BitConverter.ToInt32(BitConverter.GetBytes(rgbFloat), 0);
                            colors.SetTuple3(pointIndex, (byte)((rgbInt >> 16) & 0xFF), (byte)((rgbInt >> 8) & 0xFF), (byte)(rgbInt & 0xFF));
                        }
                        else if (int.TryParse(parts[rgbIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rgbInt))
                        {
                            colors.SetTuple3(pointIndex, (byte)((rgbInt >> 16) & 0xFF), (byte)((rgbInt >> 8) & 0xFF), (byte)(rgbInt & 0xFF));
                        }
                    }
                    else if (rIdx >= 0 && gIdx >= 0 && bIdx >= 0 && parts.Length > bIdx)
                    {
                        if (double.TryParse(parts[rIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double rv) &&
                            double.TryParse(parts[gIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double gv) &&
                            double.TryParse(parts[bIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double bv))
                            colors.SetTuple3(pointIndex, (byte)rv, (byte)gv, (byte)bv);
                    }
                }
                pointIndex++;
            }

            var polyData = vtkPolyData.New();
            polyData.SetPoints(points);
            var vertices = vtkCellArray.New();
            for (int i = 0; i < pointIndex; i++) { vertices.InsertNextCell(1); vertices.InsertCellPoint(i); }
            polyData.SetVerts(vertices);
            if (colors != null) polyData.GetPointData().SetScalars(colors);
            return polyData;
        }

        #endregion

        #region 示例点云生成

        private void GenerateSample_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckInitialized()) return;
            GenerateSamplePointCloud();
        }

        private void GenerateSamplePointCloud()
        {
            try
            {
                int pointCount = 5000;
                var random = new Random(42);
                int shapeIndex = SampleShapeCombo.SelectedIndex;

                var points = vtkPoints.New();
                points.SetNumberOfPoints(pointCount);
                var colors = vtkUnsignedCharArray.New();
                colors.SetNumberOfComponents(3);
                colors.SetNumberOfTuples(pointCount);
                colors.SetName("Colors");

                double radius = 50.0;
                for (int i = 0; i < pointCount; i++)
                {
                    double x, y, z;
                    switch (shapeIndex)
                    {
                        case 1:
                            x = (random.NextDouble() - 0.5) * 2 * radius;
                            y = (random.NextDouble() - 0.5) * 2 * radius;
                            z = (random.NextDouble() - 0.5) * 2 * radius;
                            break;
                        case 2:
                            {
                                double t = (double)i / pointCount * 8 * Math.PI;
                                double r = radius * (0.3 + 0.7 * (double)i / pointCount);
                                x = r * Math.Cos(t); y = r * Math.Sin(t); z = (double)i / pointCount * 2 * radius - radius;
                            }
                            break;
                        case 3:
                            x = (random.NextDouble() - 0.5) * 2 * radius;
                            y = (random.NextDouble() - 0.5) * 2 * radius;
                            z = (random.NextDouble() - 0.5) * 5;
                            break;
                        default:
                            {
                                double theta = 2 * Math.PI * random.NextDouble();
                                double phi = Math.Acos(2 * random.NextDouble() - 1);
                                double r = radius * (0.8 + 0.2 * random.NextDouble());
                                x = r * Math.Sin(phi) * Math.Cos(theta);
                                y = r * Math.Sin(phi) * Math.Sin(theta);
                                z = r * Math.Cos(phi);
                            }
                            break;
                    }
                    points.SetPoint(i, x, y, z);
                    var (rByte, gByte, bByte) = HeightToColor(0.5);
                    colors.SetTuple3(i, rByte, gByte, bByte);
                }

                var vertices = vtkCellArray.New();
                for (int i = 0; i < pointCount; i++) { vertices.InsertNextCell(1); vertices.InsertCellPoint(i); }

                var polyData = vtkPolyData.New();
                polyData.SetPoints(points);
                polyData.SetVerts(vertices);
                polyData.GetPointData().SetScalars(colors);

                AddCloud(polyData, $"示例-{GetShapeName(shapeIndex)}", "Sample", "");
                UpdateStatus($"已生成示例点云 | 点数: {pointCount}");
            }
            catch (Exception ex)
            {
                DebugWrite($"生成点云失败: {ex.Message}");
                MessageBox.Show($"生成点云失败: {ex.Message}");
            }
        }

        private static string GetShapeName(int index) => index switch
        {
            1 => "立方体", 2 => "螺旋", 3 => "平面", _ => "球体"
        };

        #endregion

        #region 颜色模式 / 点大小

        private void ColorMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_isInitialized || ColorModeCombo == null || _activeCloud == null) return;
            _activeCloud.ColorMode = (ColorMode)ColorModeCombo.SelectedIndex;
            ApplyItemVisual(_activeCloud);
            if (_activeCloud.ColorMode == ColorMode.Solid && _colorBarVisible)
                ToggleColorBarInternal(false);
            else if (_colorBarVisible) UpdateColorBarForActive();
        }

        private void PointSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PointSizeLabel != null) PointSizeLabel.Text = e.NewValue.ToString("F0");
            if (_activeCloud?.Actor != null)
            {
                _activeCloud.PointSize = e.NewValue;
                _activeCloud.Actor.GetProperty().SetPointSize((float)e.NewValue);
                if (_activeCloud.SliceActors != null)
                    foreach (var a in _activeCloud.SliceActors) a.GetProperty().SetPointSize((float)e.NewValue);
                RenderScene();
            }
        }

        private static (byte r, byte g, byte b) HeightToColor(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            if (t < 0.25)
            {
                double k = t / 0.25;
                return (0, (byte)(255 * k), (byte)255);
            }
            else if (t < 0.5)
            {
                double k = (t - 0.25) / 0.25;
                return (0, (byte)255, (byte)(255 * (1 - k)));
            }
            else if (t < 0.75)
            {
                double k = (t - 0.5) / 0.25;
                return ((byte)(255 * k), (byte)255, 0);
            }
            else
            {
                double k = (t - 0.75) / 0.25;
                return ((byte)255, (byte)(255 * (1 - k)), 0);
            }
        }

        private static void EnsureVerts(vtkPolyData polyData)
        {
            if (polyData.GetVerts() == null || polyData.GetVerts().GetNumberOfCells() == 0)
            {
                var vertices = vtkCellArray.New();
                long n = polyData.GetNumberOfPoints();
                for (int i = 0; i < (int)n; i++) { vertices.InsertNextCell(1); vertices.InsertCellPoint(i); }
                polyData.SetVerts(vertices);
            }
        }

        #endregion

        #region 显示切换（坐标轴 / 包围盒 / 颜色条 / 背景）

        private void ToggleAxes_Click(object sender, RoutedEventArgs e)
        {
            _axesVisible = !_axesVisible;
            ToggleAxesInternal(_axesVisible);
            UpdateToggleButtonStyle(BtnAxes, _axesVisible);
        }

        private void ToggleAxesInternal(bool visible)
        {
            try
            {
                if (visible) AddGlobalAxes();
                else if (_axesActor != null && _renderer != null)
                {
                    _renderer.RemoveActor(_axesActor);
                    _axesActor.Dispose();
                    _axesActor = null;
                }
                RenderScene();
            }
            catch (Exception ex) { DebugWrite($"坐标轴切换失败: {ex.Message}"); }
        }

        private void AddGlobalAxes()
        {
            if (_axesActor == null && _renderer != null)
            {
                _axesActor = vtkAxesActor.New();
                _renderer.AddActor(_axesActor);
            }
        }

        private void ToggleBoundingBox_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCloud == null) { MessageBox.Show("请先在点云列表中选择一个点云"); return; }
            _boundingBoxVisible = !_boundingBoxVisible;
            UpdateToggleButtonStyle(BtnBoundingBox, _boundingBoxVisible);
            if (_boundingBoxVisible) UpdateBoundingBox(); else RemoveBoundingBox();
            RenderScene();
        }

        private void UpdateBoundingBox()
        {
            if (_bbOwner != null && _bbOwner != _activeCloud)
            {
                RemoveBoundingBoxFrom(_bbOwner);
                _bbOwner = null;
            }
            if (!_boundingBoxVisible || _activeCloud == null || _renderer == null) return;
            var item = _activeCloud;
            RemoveBoundingBoxFrom(item);
            var outline = vtkOutlineFilter.New();
            outline.SetInputData(item.DisplayData);
            outline.Update();
            var outlineMapper = vtkPolyDataMapper.New();
            outlineMapper.SetInputConnection(outline.GetOutputPort());
            item.BoundingBoxActor = vtkActor.New();
            item.BoundingBoxActor.SetMapper(outlineMapper);
            item.BoundingBoxActor.GetProperty().SetColor(0.8, 0.8, 0.2);
            item.BoundingBoxActor.GetProperty().SetLineWidth(2);
            _renderer.AddActor(item.BoundingBoxActor);
            _bbOwner = item;
        }

        private void RemoveBoundingBox() => RemoveBoundingBoxFrom(_bbOwner);

        private void RemoveBoundingBoxFrom(PointCloudItem? item)
        {
            if (item?.BoundingBoxActor != null && _renderer != null)
            {
                _renderer.RemoveActor(item.BoundingBoxActor);
                item.BoundingBoxActor.Dispose();
            }
            if (item != null) item.BoundingBoxActor = null;
            if (item == _bbOwner) _bbOwner = null;
        }

        private void ToggleColorBar_Click(object sender, RoutedEventArgs e)
        {
            _colorBarVisible = !_colorBarVisible;
            UpdateToggleButtonStyle(BtnColorBar, _colorBarVisible);
            ToggleColorBarInternal(_colorBarVisible);
            RenderScene();
        }

        private void ToggleColorBarInternal(bool visible)
        {
            try
            {
                var item = _activeCloud;
                if (item == null || _renderer == null) return;
                if (visible)
                {
                    if (item.ScalarBarActor != null) { _renderer.RemoveActor(item.ScalarBarActor); item.ScalarBarActor.Dispose(); }
                    item.ScalarBarActor = vtkScalarBarActor.New();
                    item.ScalarBarActor.SetTitle("高度");
                    item.ScalarBarActor.SetNumberOfLabels(5);
                    var lut = vtkLookupTable.New();
                    lut.SetHueRange(0.667, 0.0);
                    lut.SetNumberOfTableValues(256);
                    lut.Build();
                    item.ScalarBarActor.SetLookupTable(lut);
                    item.ScalarBarActor.SetPosition(0.72, 0.15);
                    item.ScalarBarActor.SetWidth(0.12);
                    item.ScalarBarActor.SetHeight(0.7);
                    _renderer.AddActor(item.ScalarBarActor);
                    UpdateColorBarForActive();
                }
                else if (!visible && item.ScalarBarActor != null)
                {
                    _renderer.RemoveActor(item.ScalarBarActor);
                    item.ScalarBarActor.Dispose();
                    item.ScalarBarActor = null;
                }
            }
            catch (Exception ex) { DebugWrite($"颜色条切换失败: {ex.Message}"); }
        }

        private void UpdateColorBarForActive()
        {
            if (_activeCloud?.ScalarBarActor == null) return;
            var b = _activeCloud.DisplayData!.GetBounds();
            _activeCloud.ScalarBarActor.GetLookupTable().SetRange(b[4], b[5]);
        }

        private void BgColor_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_isInitialized || BgColorCombo == null) return;
            _bgColorIndex = BgColorCombo.SelectedIndex;
            ApplyBackgroundColor(_bgColorIndex);
            RenderScene();
        }

        private void ApplyBackgroundColor(int index)
        {
            _bgColorIndex = index;
            if (_renderer != null) ApplyBackgroundColorTo(_renderer);
            RenderScene();
        }

        private void ApplyBackgroundColorTo(vtkRenderer r)
        {
            switch (_bgColorIndex)
            {
                case 1: r.SetBackground(0.15, 0.15, 0.15); break;
                case 2: r.SetBackground(0.0, 0.0, 0.0); break;
                default: r.SetBackground(0.93, 0.93, 0.93); break;
            }
        }

        #endregion

        #region 体积计算

        private void ComputeVolume_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCloud == null) { MessageBox.Show("请先在点云列表中选择一个点云"); return; }
            if (!double.TryParse(VoxelSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double vs) || vs <= 0) vs = 0.1;
            try
            {
                var item = _activeCloud;
                double voxel = VolumeCalculator.ComputeVoxelVolume(item.OriginalData!, vs);
                double convex = VolumeCalculator.ComputeConvexVolume(item.OriginalData!);
                item.VoxelVolume = voxel;
                item.ConvexVolume = convex;
                item.VolumeComputed = true;
                VolumeResult.Text = $"体素体积: {FormatVolume(voxel)}\n凸包体积: {FormatVolume(convex)}";
                UpdateInfoPanel();
                RefreshCloudList();
                DatabaseHelper.UpdateVolume(item);
                AutoPublish();
                UpdateStatus($"体积计算完成: 体素 {FormatVolume(voxel)} / 凸包 {FormatVolume(convex)}");
            }
            catch (Exception ex)
            {
                DebugWrite($"体积计算失败: {ex.Message}");
                MessageBox.Show($"体积计算失败: {ex.Message}");
            }
        }

        private static string FormatVolume(double v)
        {
            if (v <= 0) return "0 m³";
            if (v >= 1e9) return $"{v / 1e9:F3} km³ ({v * 1e9:F0} mm³)";
            return $"{v:F3} m³ ({v * 1e9:F0} mm³)";
        }

        #endregion

        #region 裁剪

        private void ClipMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_isInitialized || ClipModeCombo == null) return;
            int idx = ClipModeCombo.SelectedIndex;
            BoxClipPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PlaneClipPanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (idx == 1 && _activeCloud != null) FillClipBoxDefaults(_activeCloud);
            if (idx == 2 && _activeCloud != null) FillClipPlaneDefaults(_activeCloud);
        }

        private void FillClipDefaults(PointCloudItem item)
        {
            FillClipBoxDefaults(item);
            FillClipPlaneDefaults(item);
        }

        private void FillClipBoxDefaults(PointCloudItem item)
        {
            var b = item.OriginalData!.GetBounds();
            ClipXmin.Text = b[0].ToString("F2"); ClipXmax.Text = b[1].ToString("F2");
            ClipYmin.Text = b[2].ToString("F2"); ClipYmax.Text = b[3].ToString("F2");
            ClipZmin.Text = b[4].ToString("F2"); ClipZmax.Text = b[5].ToString("F2");
        }

        private void FillClipPlaneDefaults(PointCloudItem item)
        {
            var b = item.OriginalData!.GetBounds();
            ClipOX.Text = ((b[0] + b[1]) / 2).ToString("F2");
            ClipOY.Text = ((b[2] + b[3]) / 2).ToString("F2");
            ClipOZ.Text = ((b[4] + b[5]) / 2).ToString("F2");
            ClipNX.Text = "0"; ClipNY.Text = "0"; ClipNZ.Text = "1";
        }

        private void ApplyClip_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCloud == null) { MessageBox.Show("请先在点云列表中选择一个点云"); return; }
            int idx = ClipModeCombo.SelectedIndex;
            var c = _activeCloud.Clip;
            if (idx == 1)
            {
                c.Mode = ClipMode.Box;
                c.BoxMinX = ParseDouble(ClipXmin.Text, c.BoxMinX);
                c.BoxMaxX = ParseDouble(ClipXmax.Text, c.BoxMaxX);
                c.BoxMinY = ParseDouble(ClipYmin.Text, c.BoxMinY);
                c.BoxMaxY = ParseDouble(ClipYmax.Text, c.BoxMaxY);
                c.BoxMinZ = ParseDouble(ClipZmin.Text, c.BoxMinZ);
                c.BoxMaxZ = ParseDouble(ClipZmax.Text, c.BoxMaxZ);
            }
            else if (idx == 2)
            {
                c.Mode = ClipMode.Plane;
                c.PlaneOriginX = ParseDouble(ClipOX.Text, 0);
                c.PlaneOriginY = ParseDouble(ClipOY.Text, 0);
                c.PlaneOriginZ = ParseDouble(ClipOZ.Text, 0);
                c.PlaneNormalX = ParseDouble(ClipNX.Text, 0);
                c.PlaneNormalY = ParseDouble(ClipNY.Text, 0);
                c.PlaneNormalZ = ParseDouble(ClipNZ.Text, 1);
            }
            else
            {
                c.Mode = ClipMode.None;
            }
            c.InsideOut = ClipInsideOutCheck.IsChecked == true;
            ApplyItemVisual(_activeCloud);
            UpdateInfoPanel();
            if (_boundingBoxVisible) UpdateBoundingBox();
            RefreshCloudList();
            AutoPublish();
            UpdateStatus($"已对 #{_activeCloud.ListIndex} {_activeCloud.Name} 应用裁剪");
        }

        private void ClearClip_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCloud == null) return;
            _activeCloud.Clip.Mode = ClipMode.None;
            ApplyItemVisual(_activeCloud);
            UpdateInfoPanel();
            if (_boundingBoxVisible) UpdateBoundingBox();
            RefreshCloudList();
            UpdateStatus($"已清除 #{_activeCloud.ListIndex} {_activeCloud.Name} 的裁剪");
        }

        private static double ParseDouble(string text, double fallback)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        #endregion

        #region 数据库面板

        private void RefreshDbList()
        {
            var records = DatabaseHelper.GetAll();
            _dbRows.Clear();
            foreach (var r in records)
            {
                _dbRows.Add(new DbRow
                {
                    Id = r.Id,
                    Detail = $"#{r.Id} {r.Name}  ({r.PointCount:N0}点)" +
                             (r.VolumeVoxel > 0 ? $"  V≈{FormatVolume(r.VolumeVoxel)}" : "")
                });
            }
            DbStatus.Text = $"数据库记录数: {records.Count}";
        }

        private void DbRefresh_Click(object sender, RoutedEventArgs e) => RefreshDbList();

        private void DbListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

        private void DbReload_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckInitialized()) return;
            if (DbListBox.SelectedItem is not DbRow row) { MessageBox.Show("请先在数据库列表中选择一条记录"); return; }
            var rec = DatabaseHelper.GetById(row.Id);
            if (rec == null) { MessageBox.Show("记录不存在"); return; }
            if (string.IsNullOrEmpty(rec.SourcePath) || !File.Exists(rec.SourcePath))
            {
                MessageBox.Show($"数据文件不存在: {rec.SourcePath}\n可能已被移动或删除。");
                return;
            }
            var poly = DatabaseHelper.LoadPly(rec.SourcePath);
            if (poly == null) { MessageBox.Show("载入点云失败"); return; }
            AddCloud(poly, rec.Name, "PLY", rec.SourcePath);
            UpdateStatus($"已从数据库重载: {rec.Name}");
        }

        private void DbDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DbListBox.SelectedItem is not DbRow row)
            {
                MessageBox.Show("请先在数据库列表中选择一条记录");
                return;
            }
            DatabaseHelper.Delete(row.Id);
            RefreshDbList();
            UpdateStatus($"已删除数据库记录 #{row.Id}");
        }

        #endregion

        #region MQTT 发布

        private async void MqttConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _mqtt ??= new MqttPublisher();
                _mqtt.StatusChanged -= OnMqttStatus;
                _mqtt.StatusChanged += OnMqttStatus;
                _mqtt.SetTopic(MqttTopic.Text);
                int port = int.TryParse(MqttPort.Text, out int p) ? p : 1883;
                await _mqtt.ConnectAsync(MqttHost.Text, port, MqttClientId.Text);
                MqttStatus.Text = "已连接";
            }
            catch (Exception ex)
            {
                MqttStatus.Text = $"连接失败: {ex.Message}";
            }
        }

        private async void MqttDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_mqtt == null) return;
            try { await _mqtt.DisconnectAsync(); MqttStatus.Text = "已断开"; }
            catch (Exception ex) { MqttStatus.Text = $"断开失败: {ex.Message}"; }
        }

        private async void MqttPublish_Click(object sender, RoutedEventArgs e)
        {
            if (_mqtt == null || !_mqtt.IsConnected)
            {
                MqttStatus.Text = "未连接，请先连接";
                return;
            }
            try
            {
                string json = BuildStatusJson();
                await _mqtt.PublishAsync(json);
                MqttStatus.Text = "已发布";
            }
            catch (Exception ex)
            {
                MqttStatus.Text = $"发布失败: {ex.Message}";
            }
        }

        private void OnMqttStatus(string msg)
        {
            Dispatcher.Invoke(() => { if (MqttStatus != null) MqttStatus.Text = msg; });
        }

        private void AutoPublish()
        {
            if (_mqtt != null && _mqtt.IsConnected)
            {
                try { _ = _mqtt.PublishAsync(BuildStatusJson()); } catch { }
            }
        }

        private string BuildStatusJson()
        {
            var clouds = _clouds.Select(c => new
            {
                index = c.ListIndex,
                name = c.Name,
                format = c.Format,
                points = c.PointCount,
                voxelVolume = c.VolumeComputed ? Math.Round(c.VoxelVolume, 3) : 0,
                convexVolume = c.VolumeComputed ? Math.Round(c.ConvexVolume, 3) : 0,
                bounds = new[] { c.Bounds[0], c.Bounds[1], c.Bounds[2], c.Bounds[3], c.Bounds[4], c.Bounds[5] },
                clipped = c.Clip.Mode != ClipMode.None
            }).ToList();
            var payload = new
            {
                timestamp = DateTime.Now.ToString("o"),
                cloudCount = _clouds.Count,
                clouds
            };
            return JsonSerializer.Serialize(payload);
        }

        #endregion

        #region 切片显示系统

        private int ParseSliceCount()
        {
            if (int.TryParse(SliceCountBox.Text, out int n)) return Math.Max(2, Math.Min(300, n));
            return 50;
        }

        /// <summary>按当前轴把点云切成 N 片，每片一个 Actor（初始按模式/进度决定可见性）</summary>
        private void BuildSlices(PointCloudItem item)
        {
            // 清理旧切片
            if (item.SliceActors != null && _renderer != null)
                foreach (var a in item.SliceActors) { _renderer.RemoveActor(a); a.Dispose(); }
            item.SliceActors = new List<vtkActor>();
            item.SliceData = new List<vtkPolyData>();

            var src = GetRenderData(item);
            long n = src.GetNumberOfPoints();
            if (n == 0) { UpdateSliceProgress(item); return; }

            int axis = item.SliceAxis; // 0=X,1=Y,2=Z
            var b = src.GetBounds();
            double min = b[axis * 2], max = b[axis * 2 + 1];
            if (max - min < 1e-9) max = min + 1;

            int slices = Math.Max(2, Math.Min(300, item.SliceCount));
            item.SliceCount = slices;

            var pts = src.GetPoints();
            var scalars = src.GetPointData().GetScalars();
            bool hasColor = scalars != null && scalars.GetNumberOfTuples() == n;

            // 分配每点到切片桶
            var buckets = new List<List<int>>(slices);
            for (int i = 0; i < slices; i++) buckets.Add(new List<int>());
            for (int i = 0; i < n; i++)
            {
                double[] p = pts.GetPoint(i);
                int idx = (int)((p[axis] - min) / (max - min) * slices);
                if (idx < 0) idx = 0;
                if (idx >= slices) idx = slices - 1;
                buckets[idx].Add(i);
            }

            for (int s = 0; s < slices; s++)
            {
                var bucket = buckets[s];
                int cnt = bucket.Count;
                if (cnt == 0) continue;
                var sp = vtkPoints.New();
                sp.SetNumberOfPoints(cnt);
                vtkUnsignedCharArray? sc = null;
                if (hasColor)
                {
                    sc = vtkUnsignedCharArray.New();
                    sc.SetNumberOfComponents(3);
                    sc.SetNumberOfTuples(cnt);
                    sc.SetName("Colors");
                }
                var sData = vtkPolyData.New();
                for (int k = 0; k < cnt; k++)
                {
                    int pi = bucket[k];
                    double[] p = pts.GetPoint(pi);
                    sp.SetPoint(k, p[0], p[1], p[2]);
                    if (sc != null && scalars != null)
                    {
                        var c3 = scalars.GetTuple3(pi);
                        sc.SetTuple3(k, (byte)c3[0], (byte)c3[1], (byte)c3[2]);
                    }
                }
                sData.SetPoints(sp);
                var verts = vtkCellArray.New();
                for (int k = 0; k < cnt; k++) { verts.InsertNextCell(1); verts.InsertCellPoint(k); }
                sData.SetVerts(verts);
                if (sc != null) sData.GetPointData().SetScalars(sc);
                item.SliceData.Add(sData);

                var mapper = vtkPolyDataMapper.New();
                mapper.SetInputData(sData);
                if (hasColor) mapper.SetScalarModeToUsePointData(); else mapper.SetScalarModeToDefault();
                var actor = vtkActor.New();
                actor.SetMapper(mapper);
                actor.GetProperty().SetPointSize((float)item.PointSize);
                actor.GetProperty().SetLighting(false);
                bool vis = item.SliceMode && s < item.ShownSlices;
                actor.SetVisibility(vis ? 1 : 0);
                _renderer?.AddActor(actor);
                item.SliceActors.Add(actor);
            }
            UpdateSliceProgress(item);
        }

        private void StartSlicePlayback(PointCloudItem? item = null)
        {
            var target = item ?? _activeCloud;
            if (target == null || target.SliceActors == null || _renderer == null) return;
            StopSliceTimer();
            target.SliceMode = true;
            if (target.ShownSlices >= target.SliceActors.Count) target.ShownSlices = 0;
            target.Actor?.SetVisibility(0);
            if (target.SliceActors.Count == 0) { UpdateSliceProgress(target); return; }
            _slicePlaying = true;
            _sliceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _sliceTimer.Tick += (s, e) => SliceTick(target);
            _sliceTimer.Start();
            UpdateSliceProgress(target);
            UpdateStatus($"切片显示中：{target.Name}");
        }

        private void SliceTick(PointCloudItem target)
        {
            if (target.SliceActors == null) { StopSliceTimer(); return; }
            if (target.ShownSlices < target.SliceActors.Count)
            {
                target.SliceActors[target.ShownSlices].SetVisibility(1);
                target.ShownSlices++;
                _renderWindow?.Render();
                UpdateSliceProgress(target);
            }
            else
            {
                StopSliceTimer();
                UpdateStatus($"切片显示完成：{target.Name}（{target.SliceActors.Count} 片）");
            }
        }

        private void StopSliceTimer()
        {
            if (_sliceTimer != null) { _sliceTimer.Stop(); _sliceTimer = null; }
            _slicePlaying = false;
        }

        private void SlicePlay_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCloud == null) { MessageBox.Show("请先选择或导入一个点云"); return; }
            if (_slicePlaying) { StopSliceTimer(); UpdateSliceProgress(_activeCloud); UpdateStatus("切片显示已暂停"); }
            else StartSlicePlayback(_activeCloud);
        }

        private void SliceRestart_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCloud == null) { MessageBox.Show("请先选择或导入一个点云"); return; }
            if (_activeCloud.SliceActors != null)
                foreach (var a in _activeCloud.SliceActors) a.SetVisibility(0);
            _activeCloud.ShownSlices = 0;
            _activeCloud.SliceMode = true;
            StartSlicePlayback(_activeCloud);
        }

        private void SliceFull_Click(object sender, RoutedEventArgs e)
        {
            if (_activeCloud == null) { MessageBox.Show("请先选择或导入一个点云"); return; }
            StopSliceTimer();
            _activeCloud.SliceMode = false;
            _activeCloud.ShownSlices = _activeCloud.SliceActors?.Count ?? 0;
            if (_activeCloud.SliceActors != null)
                foreach (var a in _activeCloud.SliceActors) a.SetVisibility(0);
            _activeCloud.Actor?.SetVisibility(_activeCloud.Visible ? 1 : 0);
            UpdateSliceProgress(_activeCloud);
            RenderScene();
            UpdateStatus($"已显示完整点云：{_activeCloud.Name}");
        }

        private void SliceAxis_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_activeCloud == null) return;
            _activeCloud.SliceAxis = SliceAxisCombo.SelectedIndex;
            _activeCloud.SliceMode = true;
            _activeCloud.ShownSlices = 0;
            if (_activeCloud.Actor != null) _activeCloud.Actor.SetVisibility(0);
            BuildSlices(_activeCloud);
            StartSlicePlayback(_activeCloud);
        }

        private void SliceCount_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_activeCloud == null) return;
            _activeCloud.SliceCount = ParseSliceCount();
            _activeCloud.SliceMode = true;
            _activeCloud.ShownSlices = 0;
            if (_activeCloud.Actor != null) _activeCloud.Actor.SetVisibility(0);
            BuildSlices(_activeCloud);
            StartSlicePlayback(_activeCloud);
        }

        private void UpdateSliceProgress(PointCloudItem? item)
        {
            if (item == null || item.SliceActors == null)
            {
                SliceProgress.Value = 0;
                SliceProgressText.Text = "未开始";
                return;
            }
            int total = item.SliceActors.Count;
            SliceProgress.Maximum = total;
            SliceProgress.Value = item.ShownSlices;
            if (item.SliceMode)
                SliceProgressText.Text = $"已显示 {item.ShownSlices}/{total} 片" + (_slicePlaying ? "（播放中）" : "（暂停）");
            else
                SliceProgressText.Text = $"完整显示（共 {total} 片）";
        }

        #endregion

        #region 信息面板 / 状态 / 导出

        private void UpdateInfoPanel()
        {
            if (_activeCloud == null)
            {
                InfoPointCount.Text = "—";
                InfoSource.Text = "—";
                InfoBoundsX.Text = "X: —"; InfoBoundsY.Text = "Y: —"; InfoBoundsZ.Text = "Z: —";
                InfoCenter.Text = "中心: —"; InfoSize.Text = "尺寸: —";
                InfoVolume.Text = "体积: —";
                return;
            }
            try
            {
                var c = _activeCloud;
                InfoPointCount.Text = c.PointCount.ToString("N0");
                InfoSource.Text = $"{c.Name} ({c.Format})";
                var b = c.DisplayData!.GetBounds();
                InfoBoundsX.Text = $"X: {b[0]:F2} ~ {b[1]:F2}";
                InfoBoundsY.Text = $"Y: {b[2]:F2} ~ {b[3]:F2}";
                InfoBoundsZ.Text = $"Z: {b[4]:F2} ~ {b[5]:F2}";
                InfoCenter.Text = $"中心: ({(b[0] + b[1]) / 2:F2}, {(b[2] + b[3]) / 2:F2}, {(b[4] + b[5]) / 2:F2})";
                InfoSize.Text = $"尺寸: {b[1] - b[0]:F2} × {b[3] - b[2]:F2} × {b[5] - b[4]:F2}";
                InfoVolume.Text = c.VolumeComputed
                    ? $"体积: 体素 {FormatVolume(c.VoxelVolume)}\n        凸包 {FormatVolume(c.ConvexVolume)}"
                    : "体积: 未计算";
            }
            catch (Exception ex) { DebugWrite($"更新信息面板失败: {ex.Message}"); }
        }

        private void ExportImage_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckInitialized()) return;
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|BMP 图片 (*.bmp)|*.bmp",
                    Title = "导出当前视图",
                    FileName = $"vtk_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    OverwritePrompt = true
                };
                if (dialog.ShowDialog() == true)
                {
                    if (_renderWindow == null)
                    {
                        MessageBox.Show("当前没有可导出的点云视口。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    var windowToImage = vtkWindowToImageFilter.New();
                    windowToImage.SetInput(_renderWindow);
                    windowToImage.SetReadFrontBuffer(0);
                    windowToImage.Update();
                    string ext = Path.GetExtension(dialog.FileName).ToLower();
                    if (ext == ".png")
                    {
                        var w = vtkPNGWriter.New(); w.SetFileName(dialog.FileName); w.SetInputConnection(windowToImage.GetOutputPort()); w.Write();
                    }
                    else if (ext == ".jpg" || ext == ".jpeg")
                    {
                        var w = vtkJPEGWriter.New(); w.SetFileName(dialog.FileName); w.SetInputConnection(windowToImage.GetOutputPort()); w.Write();
                    }
                    else
                    {
                        var w = vtkBMPWriter.New(); w.SetFileName(dialog.FileName); w.SetInputConnection(windowToImage.GetOutputPort()); w.Write();
                    }
                    var fi = new FileInfo(dialog.FileName);
                    if (fi.Exists && fi.Length > 0)
                    {
                        UpdateStatus($"已导出图片: {Path.GetFileName(dialog.FileName)}");
                        MessageBox.Show($"图片导出成功！\n\n保存位置: {dialog.FileName}\n文件大小: {fi.Length / 1024.0:F1} KB",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        UpdateStatus("图片导出失败：文件未生成");
                        MessageBox.Show($"图片导出失败：未能写入文件。\n\n目标路径: {dialog.FileName}\n请检查该目录是否存在且有写入权限。",
                            "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugWrite($"导出图片失败: {ex.Message}");
                MessageBox.Show($"导出图片失败: {ex.Message}");
            }
        }

        #region 切片动画导出（GIF / OGV）

        private int ParseGifDelay()
        {
            if (GifDelayBox != null && int.TryParse(GifDelayBox.Text, out int d))
            {
                if (d < 10) d = 10; else if (d > 2000) d = 2000;
                return d;
            }
            return 80;
        }

        /// <summary>
        /// 对 WPF GifBitmapEncoder 生成的 GIF 字节流做后处理：
        /// (1) 把每一帧的图形控制扩展(GCE)延时改写为指定值(单位 1/100 秒)；
        /// (2) 在全局颜色表之后注入 NETSCAPE2.0 应用扩展块以实现循环播放。
        /// 这样可绕过 GifBitmapEncoder 不支持编码器级全局元数据的限制。
        /// </summary>
        /// <param name="loopCount">循环次数，0 表示无限循环。</param>
        private static byte[] PatchGifDelaysAndLoop(byte[] gif, ushort delayCentiSec, ushort loopCount)
        {
            if (gif == null || gif.Length < 14) return gif ?? System.Array.Empty<byte>();

            // ---- 1) 改写所有 GCE 延时 ----
            // GCE 结构: 21 F9 04 <packed> <delayLo> <delayHi> <transIdx> 00
            for (int i = 0; i + 8 < gif.Length; i++)
            {
                if (gif[i] == 0x21 && gif[i + 1] == 0xF9 && gif[i + 2] == 0x04)
                {
                    gif[i + 4] = (byte)(delayCentiSec & 0xFF);
                    gif[i + 5] = (byte)((delayCentiSec >> 8) & 0xFF);
                }
            }

            // ---- 2) 计算全局颜色表结束位置(循环块插入点) ----
            // Header(6) + Logical Screen Descriptor(7)，其中 LSD 第 5 字节(索引10)为 packed。
            int insertAt = 13;
            byte packed = gif[10];
            if ((packed & 0x80) != 0)
            {
                int gctSize = 3 * (1 << ((packed & 0x07) + 1));
                insertAt += gctSize;
            }
            if (insertAt > gif.Length) return gif;

            byte[] loopBlock =
            {
                0x21, 0xFF, 0x0B,
                0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30, // "NETSCAPE2.0"
                0x03, 0x01,
                (byte)(loopCount & 0xFF), (byte)((loopCount >> 8) & 0xFF),
                0x00
            };

            byte[] outBytes = new byte[gif.Length + loopBlock.Length];
            System.Array.Copy(gif, 0, outBytes, 0, insertAt);
            System.Array.Copy(loopBlock, 0, outBytes, insertAt, loopBlock.Length);
            System.Array.Copy(gif, insertAt, outBytes, insertAt + loopBlock.Length, gif.Length - insertAt);
            return outBytes;
        }

        /// <summary>把当前渲染窗口抓取为 BitmapSource（与“截图”同一取图路径）</summary>
        private BitmapSource? CaptureFrame()
        {
            if (_renderWindow == null) return null;
            try
            {
                var w2i = vtkWindowToImageFilter.New();
                w2i.SetInput(_renderWindow);
                w2i.SetReadFrontBuffer(0);
                w2i.Update();
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vtk_frame_{Guid.NewGuid():N}.png");
                var w = vtkPNGWriter.New();
                w.SetInputConnection(w2i.GetOutputPort());
                w.SetFileName(tmp);
                w.Write();
                w2i.Dispose(); w.Dispose();
                if (!System.IO.File.Exists(tmp)) return null;
                var decoder = new PngBitmapDecoder(new Uri(tmp), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bs = decoder.Frames[0];
                var conv = new FormatConvertedBitmap(bs, PixelFormats.Bgr32, null, 0);
                conv.Freeze();
                try { System.IO.File.Delete(tmp); } catch { }
                return conv;
            }
            catch (Exception ex) { DebugWrite($"CaptureFrame 失败: {ex.Message}"); return null; }
        }

        /// <summary>导出完成后把点云恢复到导出前的显示状态</summary>
        private void RestoreCloudView(PointCloudItem item, int savedShown, bool savedMode, bool savedFullVis, bool wasPlaying)
        {
            if (item.SliceActors != null)
                foreach (var a in item.SliceActors) a.SetVisibility(0);
            if (item.Actor != null) item.Actor.SetVisibility(0);
            if (savedMode)
            {
                item.SliceMode = true;
                item.ShownSlices = Math.Min(savedShown, item.SliceActors?.Count ?? 0);
                if (item.SliceActors != null)
                    for (int i = 0; i < item.ShownSlices; i++) item.SliceActors[i].SetVisibility(1);
                if (wasPlaying) StartSlicePlayback(item);
            }
            else
            {
                item.SliceMode = false;
                item.ShownSlices = item.SliceActors?.Count ?? 0;
                if (item.Actor != null) item.Actor.SetVisibility(savedFullVis ? 1 : 0);
            }
            RenderScene();
        }

        private void ExportGif_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckInitialized()) return;
            var item = _activeCloud;
            if (item == null) { MessageBox.Show("请先导入或选择一个点云。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (item.SliceActors == null || item.SliceActors.Count == 0) { MessageBox.Show("当前点云尚未构建切片，无法导出动画。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "GIF 动图 (*.gif)|*.gif",
                Title = "导出切片动画（GIF）",
                FileName = $"slice_anim_{DateTime.Now:yyyyMMdd_HHmmss}.gif",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() != true) return;

            // 保存当前显示状态，导出结束后还原
            int savedShown = item.ShownSlices;
            bool savedMode = item.SliceMode;
            bool savedFullVis = item.Actor != null && item.Actor.GetVisibility() == 1;
            bool wasPlaying = _slicePlaying;
            StopSliceTimer();

            if (item.Actor != null) item.Actor.SetVisibility(0);
            foreach (var a in item.SliceActors) a.SetVisibility(0);
            item.SliceMode = true; item.ShownSlices = 0;
            FrameScene();

            int delayMs = ParseGifDelay();
            int n = item.SliceActors.Count;
            try
            {
                // 注意：WPF 的 GifBitmapEncoder 不支持编码器级(全局)元数据，
                // 设置 enc.Metadata 会抛“指定的 BitmapEncoder 不支持全局元数据”。
                // 因此这里只逐帧编码，循环块与帧延时统一在保存后做字节级注入。
                var enc = new GifBitmapEncoder();

                for (int i = 0; i < n; i++)
                {
                    item.SliceActors[i].SetVisibility(1);
                    item.ShownSlices = i + 1;
                    _renderWindow?.Render();
                    var bs = CaptureFrame();
                    if (bs != null)
                        enc.Frames.Add(BitmapFrame.Create(bs));
                    UpdateStatus($"导出动图：{i + 1}/{n}");
                }

                using (var ms = new System.IO.MemoryStream())
                {
                    enc.Save(ms);
                    byte[] gif = ms.ToArray();
                    // 字节级后处理：写入每帧延时 + NETSCAPE2.0 无限循环块
                    gif = PatchGifDelaysAndLoop(gif, (ushort)(delayMs / 10), 0);
                    System.IO.File.WriteAllBytes(dlg.FileName, gif);
                }

                var fi = new System.IO.FileInfo(dlg.FileName);
                if (fi.Exists && fi.Length > 0)
                {
                    UpdateStatus($"已导出动图: {System.IO.Path.GetFileName(dlg.FileName)}");
                    MessageBox.Show($"GIF 动图导出成功！\n\n帧数: {n}\n每帧间隔: {delayMs} ms\n保存位置: {dlg.FileName}\n文件大小: {fi.Length / 1024.0:F1} KB",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("GIF 导出失败：文件未生成。", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                DebugWrite($"导出 GIF 失败: {ex.Message}");
                MessageBox.Show($"导出 GIF 失败: {ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                RestoreCloudView(item, savedShown, savedMode, savedFullVis, wasPlaying);
            }
        }

        private void ExportVideo_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckInitialized()) return;
            var item = _activeCloud;
            if (item == null) { MessageBox.Show("请先导入或选择一个点云。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (item.SliceActors == null || item.SliceActors.Count == 0) { MessageBox.Show("当前点云尚未构建切片，无法导出动画。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Ogg 视频 (*.ogv)|*.ogv",
                Title = "导出切片动画（视频）",
                FileName = $"slice_anim_{DateTime.Now:yyyyMMdd_HHmmss}.ogv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() != true) return;

            int savedShown = item.ShownSlices;
            bool savedMode = item.SliceMode;
            bool savedFullVis = item.Actor != null && item.Actor.GetVisibility() == 1;
            bool wasPlaying = _slicePlaying;
            StopSliceTimer();
            if (item.Actor != null) item.Actor.SetVisibility(0);
            foreach (var a in item.SliceActors) a.SetVisibility(0);
            item.SliceMode = true; item.ShownSlices = 0;
            FrameScene();

            int delayMs = ParseGifDelay();
            int fps = delayMs > 0 ? Math.Max(1, 1000 / delayMs) : 12;
            int n = item.SliceActors.Count;
            try
            {
                var w2i = vtkWindowToImageFilter.New();
                w2i.SetInput(_renderWindow);
                w2i.SetReadFrontBuffer(0);
                var writer = vtkOggTheoraWriter.New();
                writer.SetFileName(dlg.FileName);
                writer.SetInputConnection(w2i.GetOutputPort());
                writer.SetRate(fps);
                writer.Start();
                for (int i = 0; i < n; i++)
                {
                    item.SliceActors[i].SetVisibility(1);
                    item.ShownSlices = i + 1;
                    _renderWindow?.Render();
                    w2i.Modified();
                    writer.Write();
                    UpdateStatus($"导出视频：{i + 1}/{n}");
                }
                writer.End();
                w2i.Dispose(); writer.Dispose();

                var fi = new System.IO.FileInfo(dlg.FileName);
                if (fi.Exists && fi.Length > 0)
                {
                    UpdateStatus($"已导出视频: {System.IO.Path.GetFileName(dlg.FileName)}");
                    MessageBox.Show($"视频导出成功！\n\n帧数: {n}\n帧率: {fps} fps\n保存位置: {dlg.FileName}\n文件大小: {fi.Length / 1024.0:F1} KB\n\n注：.ogv 需用 VLC 等支持 Ogg Theora 的播放器打开。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("视频导出失败：文件未生成（VTK 视频编码器可能不可用）。", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                DebugWrite($"导出视频失败: {ex.Message}");
                MessageBox.Show($"导出视频失败: {ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                RestoreCloudView(item, savedShown, savedMode, savedFullVis, wasPlaying);
            }
        }

        #endregion

        #endregion

        #region 基础操作

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            if (_renderer == null) { MessageBox.Show("请先在点云列表中选择一个点云"); return; }
            FrameScene();
            UpdateStatus("视角已重置");
        }

        private void UpdateStatus(string message) => StatusText.Text = message;

        private void UpdateToggleButtonStyle(System.Windows.Controls.Button btn, bool active)
        {
            if (active)
            {
                btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x99, 0xFF));
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.Bold;
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.Foreground = Brushes.Black;
                btn.FontWeight = FontWeights.Normal;
            }
        }

        private static void DebugWrite(string message) =>
            System.Diagnostics.Debug.WriteLine($"[WpfApp1] {message}");

        private bool CheckInitialized()
        {
            if (!_isInitialized)
            {
                MessageBox.Show("VTK 正在初始化中，请稍候片刻后重试。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        #endregion

        #region 清理

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) => Cleanup();

        private void Cleanup()
        {
            try
            {
                StopSliceTimer();
                foreach (var c in _clouds.ToList()) RemoveCloudInternal(c);
                _activeCloud = null;
                _bbOwner = null;
                if (_axesActor != null && _renderer != null) { _renderer.RemoveActor(_axesActor); _axesActor.Dispose(); _axesActor = null; }
                _mqtt?.Dispose();
                _mqtt = null;
            }
            catch (Exception ex) { DebugWrite($"清理资源失败: {ex.Message}"); }
        }

        #endregion
    }
}
