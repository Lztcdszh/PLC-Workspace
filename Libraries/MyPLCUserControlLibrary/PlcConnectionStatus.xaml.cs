using MyPLCManagerLibrary.Interfaces;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 连接状态指示：绑定 <see cref="Connection"/>，利用 IConnectionManager 的 IsConnected /
    /// ConnectionStatusText 实时显示彩色状态点与文本。本身不订阅任何 PLC 标签。
    /// </summary>
    public partial class PlcConnectionStatus : PlcControlBase
    {
        public PlcConnectionStatus()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                PART_Dot.Fill = ConnectedColor;
                PART_StatusText.Text = "已连接";
                return;
            }
        }

        #region 依赖属性

        public static readonly DependencyProperty ShowTextProperty =
            DependencyProperty.Register(nameof(ShowText), typeof(bool), typeof(PlcConnectionStatus), new PropertyMetadata(true, (d, e) => ((PlcConnectionStatus)d).UpdateLayoutVisibility()));

        public bool ShowText
        {
            get => (bool)GetValue(ShowTextProperty);
            set => SetValue(ShowTextProperty, value);
        }

        public static readonly DependencyProperty DotSizeProperty =
            DependencyProperty.Register(nameof(DotSize), typeof(double), typeof(PlcConnectionStatus), new PropertyMetadata(12d, (d, e) => ((PlcConnectionStatus)d).ApplyDotSize()));

        public double DotSize
        {
            get => (double)GetValue(DotSizeProperty);
            set => SetValue(DotSizeProperty, value);
        }

        public static readonly DependencyProperty ConnectedColorProperty =
            DependencyProperty.Register(nameof(ConnectedColor), typeof(Brush), typeof(PlcConnectionStatus),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(103, 194, 58)), (d, e) => ((PlcConnectionStatus)d).RefreshStatus()));

        public Brush ConnectedColor
        {
            get => (Brush)GetValue(ConnectedColorProperty);
            set => SetValue(ConnectedColorProperty, value);
        }

        public static readonly DependencyProperty DisconnectedColorProperty =
            DependencyProperty.Register(nameof(DisconnectedColor), typeof(Brush), typeof(PlcConnectionStatus),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(245, 108, 108)), (d, e) => ((PlcConnectionStatus)d).RefreshStatus()));

        public Brush DisconnectedColor
        {
            get => (Brush)GetValue(DisconnectedColorProperty);
            set => SetValue(DisconnectedColorProperty, value);
        }

        public static readonly DependencyProperty ConnectingColorProperty =
            DependencyProperty.Register(nameof(ConnectingColor), typeof(Brush), typeof(PlcConnectionStatus),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(230, 162, 60)), (d, e) => ((PlcConnectionStatus)d).RefreshStatus()));

        public Brush ConnectingColor
        {
            get => (Brush)GetValue(ConnectingColorProperty);
            set => SetValue(ConnectingColorProperty, value);
        }

        #endregion

        #region PlcControlBase 钩子（不订阅数值标签）

        protected override bool ShouldSubscribe => false;
        protected override MyPLCManagerLibrary.Model.PLCTag? CreateDefaultTag() => null;
        protected override void OnPlcValueReceived(object? value) { }

        protected override void OnConnectionChangedInternal(IConnectionManager? oldConn, IConnectionManager? newConn)
        {
            // 取消旧连接的完整属性订阅
            if (oldConn is INotifyPropertyChanged oldNotify)
                oldNotify.PropertyChanged -= Connection_AnyStatusChanged;

            // 订阅新连接的完整属性变更：基类只监听 IsConnected，
            // 但连接状态还可能仅由 ConnectionStatus / ConnectionStatusText 回摆
            // （例如底层瞬时重连），此时 IsConnected 布尔未变，基类不会刷新，
            // 导致界面卡在"连接中"。这里补齐订阅，任一相关属性变化都刷新。
            if (newConn is INotifyPropertyChanged newNotify)
                newNotify.PropertyChanged += Connection_AnyStatusChanged;

            RefreshStatus();
        }

        protected override void OnConnectionStateChanged(bool isConnected)
        {
            RefreshStatus();
        }

        protected override void OnUnloadedInternal()
        {
            if (ConnectionManager is INotifyPropertyChanged notify)
                notify.PropertyChanged -= Connection_AnyStatusChanged;
        }

        private void Connection_AnyStatusChanged(object? sender, PropertyChangedEventArgs e)
        {
            // 只关心连接状态相关属性
            if (e.PropertyName is not (null or ""
                or nameof(IConnectionManager.IsConnected)
                or nameof(IConnectionManager.IsConnecting)
                or nameof(IConnectionManager.ConnectionStatusText)))
            {
                // 允许通过字符串名匹配（防不同接口成员名差异）
                if (e.PropertyName != "ConnectionStatus" && e.PropertyName != "IsConnected"
                    && e.PropertyName != "IsConnecting" && e.PropertyName != "ConnectionStatusText")
                    return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshStatus));
                return;
            }
            RefreshStatus();
        }

        #endregion

        #region 状态刷新

        private void RefreshStatus()
        {
            if (PART_Dot == null || PART_StatusText == null) return;
            var conn = ConnectionManager;
            if (conn == null)
            {
                PART_Dot.Fill = DisconnectedColor;
                PART_StatusText.Text = "未连接";
                return;
            }
            if (conn.IsConnected)
            {
                // IsConnected 是连接真值，权威优先：一旦为 true 即"已连接"，
                // 不再引用 ConnectionStatusText —— 该文本来自 ConnectionStatus 枚举，
                // 底层瞬时重连时可能滞后停留在"连接中/重连中"，导致绿灯却显示连接中。
                PART_Dot.Fill = ConnectedColor;
                PART_StatusText.Text = "已连接";
            }
            else if (conn.IsConnecting)
            {
                PART_Dot.Fill = ConnectingColor;
                PART_StatusText.Text = !string.IsNullOrEmpty(conn.ConnectionStatusText) ? conn.ConnectionStatusText : "连接中";
            }
            else
            {
                PART_Dot.Fill = DisconnectedColor;
                PART_StatusText.Text = !string.IsNullOrEmpty(conn.ConnectionStatusText) ? conn.ConnectionStatusText : "未连接";
            }
        }

        private void UpdateLayoutVisibility()
        {
            if (PART_StatusText != null)
                PART_StatusText.Visibility = ShowText ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyDotSize()
        {
            if (PART_Dot != null)
            {
                PART_Dot.Width = DotSize;
                PART_Dot.Height = DotSize;
            }
        }

        #endregion
    }
}
