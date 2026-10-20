using MyPLCManagerLibrary.Interfaces;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 通用容器控件：带标题栏的分组面板，并在连接建立时自动把 <see cref="Connection"/>
    /// 透传给内部所有 <see cref="PlcControlBase"/> 子控件，避免在每个子控件上重复绑定连接。
    /// </summary>
    public partial class PlcPanel : PlcControlBase
    {
        public PlcPanel()
        {
            InitializeComponent();
            Loaded += (s, e) => PropagateConnection();
        }

        #region 依赖属性

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(PlcPanel), new PropertyMetadata(string.Empty));

        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly DependencyProperty ShowHeaderProperty =
            DependencyProperty.Register(nameof(ShowHeader), typeof(bool), typeof(PlcPanel), new PropertyMetadata(true));

        public bool ShowHeader
        {
            get => (bool)GetValue(ShowHeaderProperty);
            set => SetValue(ShowHeaderProperty, value);
        }

        public static readonly DependencyProperty HeaderBrushProperty =
            DependencyProperty.Register(nameof(HeaderBrush), typeof(Brush), typeof(PlcPanel),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(38, 38, 38))));

        public Brush HeaderBrush
        {
            get => (Brush)GetValue(HeaderBrushProperty);
            set => SetValue(HeaderBrushProperty, value);
        }

        public static readonly DependencyProperty PanelContentProperty =
            DependencyProperty.Register(nameof(PanelContent), typeof(UIElement), typeof(PlcPanel),
                new PropertyMetadata(null, (d, e) => ((PlcPanel)d).PropagateConnection()));

        /// <summary>面板内承载的子控件（通常是一个 Grid/StackPanel，里面放若干 PLC 控件）。</summary>
        public UIElement? PanelContent
        {
            get => (UIElement?)GetValue(PanelContentProperty);
            set => SetValue(PanelContentProperty, value);
        }

        #endregion

        #region 连接透传

        protected override void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            PropagateConnection();
        }

        /// <summary>
        /// 将本容器的 Connection 下发给内部尚未取得有效连接的 PLC 子控件。
        /// </summary>
        /// <remarks>
        /// ⚠️ 判定条件必须是「子控件当前 <b>有效</b> 连接为空」，而不是「本地值未设置」。
        /// 原因（现场缺陷复盘）：子控件若显式写了
        /// <c>Connection="{Binding RelativeSource={RelativeSource AncestorType=myplc:PlcPanel}, Path=Connection}"</c>，
        /// 该绑定表达式会作为本地值存在，<see cref="DependencyObject.ReadLocalValue"/> 返回的是
        /// BindingExpression 而**不是** <see cref="DependencyProperty.UnsetValue"/>，于是被旧判断当成
        /// "已显式设置连接"而跳过下发。偏偏这类祖先绑定在「窗口已构造但尚未 Show」（开机自启动
        /// --background 进托盘、稍后才从托盘打开主窗口）时没有可视化树可走，解析结果为 null，
        /// 且显示之后不会再自愈 —— 结果这些子控件永远拿不到连接：不订阅标签、不响应写入。
        /// 实报症状即为此：模式设定四个 PlcSelectButton 点击无效/不高亮 + 16 根导轨条不闪烁。
        /// 改为按有效值判断后，下发会把这些"绑了个空"的控件就地补上连接（SetValue 会顺带清掉
        /// 那个坏绑定），而真正显式绑定了**有效**连接的子控件（如接了另一个 IConnectionManager）
        /// 仍然被尊重、不会被覆盖。
        /// </remarks>
        private void PropagateConnection()
        {
            if (PanelContent == null) return;
            IConnectionManager? conn = ConnectionManager;
            if (conn == null) return;   // 本面板自身还没连上：不下发（避免把子控件写成本地面板空值）
            foreach (var child in FindVisualChildren<PlcControlBase>(PanelContent))
            {
                if (child.Connection == null)
                    child.Connection = conn;
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject dep) where T : DependencyObject
        {
            if (dep == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(dep);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(dep, i);
                if (child is T t) yield return t;
                foreach (var c in FindVisualChildren<T>(child))
                    yield return c;
            }
        }

        #endregion

        #region PlcControlBase 钩子（容器本身不订阅数值标签）

        protected override bool ShouldSubscribe => false;
        protected override MyPLCManagerLibrary.Model.PLCTag? CreateDefaultTag() => null;
        protected override void OnPlcValueReceived(object? value) { }

        #endregion
    }
}
