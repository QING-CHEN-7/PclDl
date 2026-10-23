using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PclDl.Plugin.Ui
{
    /// <summary>用 PCL 自带的控件（MyButton / MyCard）构建界面，保证与原版视觉一致。</summary>
    public sealed class PclUi
    {
        private readonly Assembly _pcl;
        private readonly Type _buttonType;
        private readonly Type _cardType;
        private readonly Type _cardColorState;

        public PclUi(Assembly pclAssembly)
        {
            _pcl = pclAssembly;
            _buttonType = _pcl.GetType("PCL.MyButton", false);
            _cardType = _pcl.GetType("PCL.MyCard", false);
            _cardColorState = _buttonType == null ? null : _buttonType.GetNestedType("ColorState");
        }

        public bool HasNativeButton { get { return _buttonType != null; } }
        public bool HasNativeCard { get { return _cardType != null; } }

        /// <summary>原生按钮；找不到 PCL 控件时退回标准按钮。</summary>
        public FrameworkElement Button(string text, bool highlight, Action onClick, double height = 30, Thickness? margin = null)
        {
            if (_buttonType != null)
            {
                try
                {
                    var button = (FrameworkElement)Activator.CreateInstance(_buttonType);
                    _buttonType.GetProperty("Text").SetValue(button, text, null);
                    if (_cardColorState != null)
                    {
                        var state = Enum.Parse(_cardColorState, highlight ? "Highlight" : "Normal");
                        _buttonType.GetProperty("ColorType").SetValue(button, state, null);
                    }
                    button.Height = height;
                    button.Margin = margin ?? new Thickness(0);
                    button.VerticalAlignment = VerticalAlignment.Center;

                    var clickEvent = _buttonType.GetEvent("Click");
                    if (clickEvent != null && onClick != null)
                    {
                        var handler = Delegate.CreateDelegate(clickEvent.EventHandlerType, new ClickRelay(onClick), "Handle");
                        clickEvent.AddEventHandler(button, handler);
                    }
                    return button;
                }
                catch { }
            }

            var fallback = new Button { Content = text, Height = height, Margin = margin ?? new Thickness(0), Padding = new Thickness(14, 0, 14, 0) };
            if (onClick != null) fallback.Click += (s, e) => onClick();
            return fallback;
        }

        private sealed class ClickRelay
        {
            private readonly Action _action;
            public ClickRelay(Action action) { _action = action; }
            public void Handle(object sender, MouseButtonEventArgs e) { _action(); }
        }

        /// <summary>原生卡片；标题用 PCL 的 Inlines，内容挂在 BorderChild。</summary>
        public FrameworkElement Card(string title, UIElement content)
        {
            if (_cardType != null)
            {
                try
                {
                    var card = (FrameworkElement)Activator.CreateInstance(_cardType);
                    card.Margin = new Thickness(0, 0, 0, 15);

                    if (!string.IsNullOrEmpty(title))
                    {
                        var titleProp = _cardType.GetProperty("Title");
                        if (titleProp != null) titleProp.SetValue(card, title, null);
                        else
                        {
                            var inlines = _cardType.GetProperty("Inlines").GetValue(card, null) as InlineCollection;
                            if (inlines != null) inlines.Add(new Run(title));
                        }

                        // PCL 原生写法：<MyCard Title="x"><Grid Margin="21,40,21,16">…</Grid></MyCard>
                        var element = content as FrameworkElement;
                        if (element != null) element.Margin = new Thickness(21, 40, 21, 16);
                    }

                    var childProp = _cardType.GetProperty("BorderChild");
                    if (childProp != null) childProp.SetValue(card, content, null);
                    return card;
                }
                catch { }
            }

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(18, 14, 18, 16),
                Child = content
            };
            Theme.Apply(border, Border.BackgroundProperty, "ColorBrush7", new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)));
            if (!string.IsNullOrEmpty(title))
            {
                var panel = new StackPanel();
                var header = new TextBlock { Text = title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) };
                Theme.Apply(header, TextBlock.ForegroundProperty, "ColorBrush1", Brushes.Black);
                panel.Children.Add(header);
                panel.Children.Add(content);
                border.Child = panel;
            }
            return border;
        }

        /// <summary>原生列表项（MyListItem）：标题 + 副文本，点击回调。</summary>
        public FrameworkElement ListItem(string title, string info, Action onClick, string logo = null)
        {
            var type = _pcl == null ? null : _pcl.GetType("PCL.MyListItem", false);
            if (type != null)
            {
                try
                {
                    var item = (FrameworkElement)Activator.CreateInstance(type);
                    type.GetProperty("Title").SetValue(item, title, null);
                    if (!string.IsNullOrEmpty(info))
                    {
                        var infoProp = type.GetProperty("Info");
                        if (infoProp != null) infoProp.SetValue(item, info, null);
                    }

                    // 与 PCL 版本列表一致：42 高、Clickable 类型、带图标
                    item.Height = 42;
                    item.Margin = new Thickness(0, 1, 0, 1);
                    var typeProp = type.GetProperty("Type");
                    if (typeProp != null)
                    {
                        var checkType = type.GetNestedType("CheckType", System.Reflection.BindingFlags.Public);
                        if (checkType != null)
                        {
                            try { typeProp.SetValue(item, Enum.Parse(checkType, "Clickable"), null); }
                            catch { }
                        }
                    }

                    if (!string.IsNullOrEmpty(logo))
                    {
                        try { type.GetProperty("Logo").SetValue(item, logo, null); }
                        catch { }
                    }

                    var clickEvent = type.GetEvent("Click");
                    if (clickEvent != null && onClick != null)
                    {
                        var handler = Delegate.CreateDelegate(clickEvent.EventHandlerType,
                            new ClickRelay(() => { Diag("listitem click " + title); onClick(); }), "Handle");
                        clickEvent.AddEventHandler(item, handler);
                    }
                    

                    // 兜底：直接挂预览鼠标抬起，避免 MyListItem 内部命中判定差异
                    if (onClick != null)
                    {
                        var element = item as UIElement;
                        if (element != null)
                            element.PreviewMouseLeftButtonUp += (sender, args) => onClick();
                    }
                    return item;
                }
                catch { }
            }

            var fallback = new Button
            {
                Content = string.IsNullOrEmpty(info) ? title : title + "    " + info,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 34,
                Margin = new Thickness(0, 1, 0, 1)
            };
            if (onClick != null) fallback.Click += (x, y) => onClick();
            return fallback;
        }

        /// <summary>版本图标（PCL 内置图片资源）。</summary>
        public static string VersionLogo(string type)
        {
            var name = type == "release" ? "Grass.png" : type == "snapshot" ? "CommandBlock.png" : "CobbleStone.png";
            return "pack://application:,,,/Plain Craft Launcher 2;component/Images/Blocks/" + name;
        }

        /// <summary>列表分组标题。</summary>
        public FrameworkElement Section(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 10, 0, 4)
            };
            Theme.Apply(block, TextBlock.ForegroundProperty, "ColorBrush2", Brushes.DimGray);
            return block;
        }

        /// <summary>把内核日志接到插件日志文件，方便定位卡住的位置。</summary>
        internal sealed class DiagLog : PclDl.Core.Logging.ILog
        {
            public void Write(PclDl.Core.Logging.LogLevel level, string message)
            {
                Diag("[" + level.ToString().ToLowerInvariant() + "] " + message);
            }
        }

        internal static void Diag(string message)
        {
            try
            {
                var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL2Plugins");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "ui.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        public FrameworkElement TextBox(string text, double width)
        {
            var box = new TextBox
            {
                Text = text ?? "",
                Width = width,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 4, 6, 4)
            };
            return box;
        }

        public TextBlock Label(string text, double fontSize = 13, bool bold = false)
        {
            var label = new TextBlock { Text = text, FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center };
            if (bold) label.FontWeight = FontWeights.Bold;
            Theme.Apply(label, TextBlock.ForegroundProperty, "ColorBrush1", Brushes.Black);
            return label;
        }

        public TextBlock Hint(string text)
        {
            var hint = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Theme.Apply(hint, TextBlock.ForegroundProperty, "ColorBrush2", Brushes.DimGray);
            return hint;
        }
    }
}
