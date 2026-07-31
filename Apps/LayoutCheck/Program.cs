using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;

namespace LayoutCheck
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var w = new MeasureWindow();
            w.ApplyTemplate();

            // 递归 ApplyTemplate，确保各控件（PlcPanel/PlcRangeIndicator/PlcSelectButton/PlcNumericInput）已生成可视子树
            WalkApplyTemplate(w.Content as DependencyObject);

            const double W = 1900, H = 640;
            var content = (UIElement)w.Content;
            content.Measure(new Size(W, H));
            content.Arrange(new Rect(0, 0, W, H));
            content.UpdateLayout();

            // 强制再 ApplyTemplate 一次，避免 Arrange 后新建的可视子节点未就位
            WalkApplyTemplate(w.Content as DependencyObject);
            content.UpdateLayout();

            int pxW = (int)Math.Ceiling(W);
            int pxH = (int)Math.Ceiling(H);
            var bmp = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(content);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            string outPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "layout.png");
            using var fs = System.IO.File.Create(outPath);
            enc.Save(fs);
            Console.WriteLine("RENDERED: " + outPath);

            // 顺便打印每个号位文本块的实际渲染坐标，客观判断对齐
            DumpTextPositions(w.Content as DependencyObject);
        }

        private static void WalkApplyTemplate(DependencyObject? d)
        {
            if (d == null) return;
            if (d is FrameworkElement fe) fe.ApplyTemplate();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                WalkApplyTemplate(VisualTreeHelper.GetChild(d, i));
        }

        private static void DumpTextPositions(DependencyObject? d)
        {
            if (d == null) return;
            if (d is TextBlock tb && tb.Text.Contains("号位"))
            {
                var rt = tb.TransformToAncestor(((FrameworkElement)d).Parent as Visual ?? (Visual)d);
                try
                {
                    var p = rt.Transform(new Point(0, 0));
                    Console.WriteLine($"TEXT {tb.Text,-6} -> x={p.X:F0}, y={p.Y:F0}, w={tb.ActualWidth:F0}, h={tb.ActualHeight:F0}");
                }
                catch { }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                DumpTextPositions(VisualTreeHelper.GetChild(d, i));
        }
    }
}
