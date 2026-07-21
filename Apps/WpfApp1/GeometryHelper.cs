using Kitware.VTK;
using System;
using System.Collections.Generic;

namespace WpfApp1
{
    /// <summary>
    /// 体积计算：体素占据体积 + 凸包体积
    /// </summary>
    public static class VolumeCalculator
    {
        /// <summary>
        /// 体素占据体积：把点云体素化到三维网格，统计被占据的体素数 × 体素体积。
        /// 适用于任意点云（得到点所占据空间的体积估计）。
        /// </summary>
        public static double ComputeVoxelVolume(vtkPolyData poly, double voxelSize)
        {
            if (poly == null || poly.GetNumberOfPoints() == 0) return 0;
            if (voxelSize <= 0) voxelSize = 0.1;

            double inv = 1.0 / voxelSize;
            long n = poly.GetNumberOfPoints();
            // 列填充法：对每个 (ix,iy) 列，填充从最小到最大 iz 的体素，
            // 近似“实心”占据体积（对闭合曲面点云接近其包围体积，而非仅表层体素）。
            var cols = new System.Collections.Generic.Dictionary<(int, int), (int min, int max)>();
            for (int i = 0; i < n; i++)
            {
                double[] p = poly.GetPoint(i);
                int ix = (int)Math.Floor(p[0] * inv);
                int iy = (int)Math.Floor(p[1] * inv);
                int iz = (int)Math.Floor(p[2] * inv);
                if (cols.TryGetValue((ix, iy), out var mm))
                {
                    int mn = iz < mm.min ? iz : mm.min;
                    int mx = iz > mm.max ? iz : mm.max;
                    cols[(ix, iy)] = (mn, mx);
                }
                else
                    cols[(ix, iy)] = (iz, iz);
            }
            long filled = 0;
            foreach (var kv in cols) filled += (kv.Value.max - kv.Value.min + 1);
            double v = filled * voxelSize * voxelSize * voxelSize;
            return v;
        }

        /// <summary>
        /// 凸包体积：对点云计算凸包（vtkHull）后用 vtkMassProperties 求体积。
        /// 点数过大时先随机降采样到 maxPoints，以保证性能（结果为估计值）。
        /// </summary>
        public static double ComputeConvexVolume(vtkPolyData poly, int maxPoints = 200000)
        {
            if (poly == null || poly.GetNumberOfPoints() == 0) return 0;

            vtkPolyData work = poly;
            long n = poly.GetNumberOfPoints();
            bool downsampled = false;

            if (n > maxPoints)
            {
                var pts = vtkPoints.New();
                pts.SetNumberOfPoints(maxPoints);
                var rnd = new Random(12345);
                for (int i = 0; i < maxPoints; i++)
                {
                    int idx = rnd.Next((int)n);
                    double[] p = poly.GetPoint(idx);
                    pts.SetPoint(i, p[0], p[1], p[2]);
                }
                work = vtkPolyData.New();
                work.SetPoints(pts);
                downsampled = true;
            }

            try
            {
                // vtkHull.AddRecursiveSpherePlanes 生成的裁剪平面只包围单位球内的点。
                // 因此先把点云平移到中心并 uniform 缩放到单位球内，求凸包体积后再乘 scale^3 还原。
                double[] b = work.GetBounds();
                double cx = (b[0] + b[1]) / 2, cy = (b[2] + b[3]) / 2, cz = (b[4] + b[5]) / 2;
                double r = Math.Max(b[1] - b[0], Math.Max(b[3] - b[2], b[5] - b[4])) / 2.0;
                if (r <= 0) r = 1.0;
                double scale = 1.0 / r;

                var t = vtkTransform.New();
                t.Translate(-cx, -cy, -cz);
                t.Scale(scale, scale, scale);
                var tf = vtkTransformPolyDataFilter.New();
                tf.SetInputData(work);
                tf.SetTransform(t);
                tf.Update();
                var scaled = tf.GetOutput();

                // vtkHull.AddRecursiveSpherePlanes 在本绑定中无效（输出为空），
                // 改用显式“支撑平面”：对每个方向取点云在该方向的最大投影作为平面位置，
                // 这些半空间的交集即为凸包（凸包 = 所有方向支撑平面的交集）。
                var hull = vtkHull.New();
                hull.SetInputData(scaled);
                int m = 80;
                double golden = Math.PI * (3.0 - Math.Sqrt(5.0));
                long np = scaled.GetNumberOfPoints();
                double[] p0 = scaled.GetPoint(0);
                for (int i = 0; i < m; i++)
                {
                    double y = 1.0 - (i + 0.5) / m * 2.0;
                    double rad = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
                    double theta = golden * i;
                    double nx = Math.Cos(theta) * rad;
                    double ny = y;
                    double nz = Math.Sin(theta) * rad;
                    double maxv = nx * p0[0] + ny * p0[1] + nz * p0[2];
                    for (long k = 1; k < np; k++)
                    {
                        double[] p = scaled.GetPoint(k);
                        double d = nx * p[0] + ny * p[1] + nz * p[2];
                        if (d > maxv) maxv = d;
                    }
                    // vtkHull 保留 n·X + p >= 0 的一侧；要保留 n·X <= maxv，法线取 -n，p = maxv
                    hull.AddPlane(-nx, -ny, -nz, maxv);
                }
                hull.Update();

                var hullOut = hull.GetOutput();
                long hullPts = hullOut != null ? hullOut.GetNumberOfPoints() : -1;
                long hullCells = hullOut != null ? hullOut.GetNumberOfCells() : -1;

                // vtkMassProperties 只处理三角面；凸包输出为多边形，需先三角化
                var tri = vtkTriangleFilter.New();
                tri.SetInputConnection(hull.GetOutputPort());
                tri.PassLinesOff();
                tri.PassVertsOff();
                tri.Update();

                var triOut = tri.GetOutput();
                long triCells = triOut != null ? triOut.GetNumberOfCells() : -1;

                var mp = vtkMassProperties.New();
                mp.SetInputConnection(tri.GetOutputPort());
                double vScaled = Math.Abs(mp.GetVolume());
                double v = vScaled * r * r * r;

                return v;
            }
            finally
            {
                if (downsampled && work != null) work.Dispose();
            }
        }
    }

    /// <summary>
    /// 裁剪几何：基于 vtkClipPolyData 的框选 / 平面裁剪
    /// </summary>
    public static class ClipHelper
    {
        /// <summary>框选裁剪：保留框内（或框外，当 insideOut=true）的点</summary>
        public static vtkPolyData? ApplyBox(vtkPolyData src, double xMin, double xMax,
            double yMin, double yMax, double zMin, double zMax, bool insideOut)
        {
            if (src == null) return null;
            var box = vtkBox.New();
            box.SetBounds(xMin, xMax, yMin, yMax, zMin, zMax);

            var clip = vtkClipPolyData.New();
            clip.SetInputData(src);
            clip.SetClipFunction(box);
            clip.SetInsideOut(insideOut ? 1 : 0);
            clip.Update();

            var outp = clip.GetOutput();
            if (outp == null || outp.GetNumberOfPoints() == 0) return null;
            // 复制一份独立数据，避免与原始数据共享
            var result = vtkPolyData.New();
            result.DeepCopy(outp);
            result.SetVerts(BuildVerts(result.GetNumberOfPoints()));
            return result;
        }

        /// <summary>平面裁剪：保留法线指向侧（或反侧，当 insideOut=true）的点</summary>
        public static vtkPolyData? ApplyPlane(vtkPolyData src,
            double ox, double oy, double oz, double nx, double ny, double nz, bool insideOut)
        {
            if (src == null) return null;
            var plane = vtkPlane.New();
            plane.SetOrigin(ox, oy, oz);
            plane.SetNormal(nx, ny, nz);

            var clip = vtkClipPolyData.New();
            clip.SetInputData(src);
            clip.SetClipFunction(plane);
            clip.SetInsideOut(insideOut ? 1 : 0);
            clip.Update();

            var outp = clip.GetOutput();
            if (outp == null || outp.GetNumberOfPoints() == 0) return null;
            var result = vtkPolyData.New();
            result.DeepCopy(outp);
            result.SetVerts(BuildVerts(result.GetNumberOfPoints()));
            return result;
        }

        private static vtkCellArray BuildVerts(long count)
        {
            var verts = vtkCellArray.New();
            for (int i = 0; i < count; i++)
            {
                verts.InsertNextCell(1);
                verts.InsertCellPoint(i);
            }
            return verts;
        }
    }
}
