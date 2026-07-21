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
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(245, 247, 250))));

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

        /// <summary>将本容器的 Connection 下发给内部尚未显式设置连接的 PLC 子控件。</summary>
        private void PropagateConnection()
        {
            if (PanelContent == null) return;
            IConnectionManager? conn = ConnectionManager;
            foreach (var child in FindVisualChildren<PlcControlBase>(PanelContent))
            {
                // 仅下发未显式（本地）设置连接的子控件，尊重子控件的独立绑定
                if (child.ReadLocalValue(ConnectionProperty) == DependencyProperty.UnsetValue)
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
