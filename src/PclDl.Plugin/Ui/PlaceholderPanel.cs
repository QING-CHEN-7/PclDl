using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PclDl.Core.Layout;

namespace PclDl.Plugin.Ui
{
    /// <summary>M3 占位面板：证明壳挂载成功，并提供"扫描已安装版本"的离线动作。</summary>
    public sealed class PlaceholderPanel : UserControl
    {
        private readonly TextBlock _status;

        public PlaceholderPanel(string gameDir)
        {
            Background = Brushes.Transparent;
            var panel = new StackPanel { Margin = new Thickness(40, 30, 40, 30) };

            var title = new TextBlock
            {
                Text = "PCL2 下载插件",
                FontSize = 26,
                FontWeight = FontWeights.Bold
            };
            // 用 PCL 的主题画刷，浅色/深色主题都不会变成"白字白底"
            Theme.Apply(title, TextBlock.ForegroundProperty, "ColorBrush1", Brushes.Black);
            panel.Children.Add(title);

            var subtitle = new TextBlock
            {
                Text = "内核已就绪 · M3 挂载验证面板（降级页面）",
                FontSize = 14,
                Margin = new Thickness(0, 6, 0, 18)
            };
            Theme.Apply(subtitle, TextBlock.ForegroundProperty, "ColorBrush2", Brushes.DimGray);
            panel.Children.Add(subtitle);

            var button = new Button
            {
                Content = "扫描已安装版本",
                Padding = new Thickness(16, 6, 16, 6),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += (s, e) => Scan(gameDir);
            panel.Children.Add(button);

            _status = new TextBlock
            {
                Margin = new Thickness(0, 14, 0, 0),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };
            Theme.Apply(_status, TextBlock.ForegroundProperty, "ColorBrush1", Brushes.Black);
            panel.Children.Add(_status);

            Content = panel;
        }

        private void Scan(string gameDir)
        {
            try
            {
                var layout = new DataLayout(gameDir);
                if (!Directory.Exists(layout.VersionsDir)) { _status.Text = "没有 versions 目录：" + layout.VersionsDir; return; }

                var lines = new System.Collections.Generic.List<string>();
                foreach (var dir in Directory.GetDirectories(layout.VersionsDir))
                {
                    var id = Path.GetFileName(dir);
                    var json = Path.Combine(dir, id + ".json");
                    var jar = Path.Combine(dir, id + ".jar");
                    lines.Add(id + (File.Exists(json) ? "  [json]" : "") + (File.Exists(jar) ? "  [jar]" : ""));
                }
                _status.Text = lines.Count == 0 ? "未发现已安装版本" : ("已安装版本（" + lines.Count + "）：" + Environment.NewLine + string.Join(Environment.NewLine, lines));
            }
            catch (Exception ex)
            {
                _status.Text = "扫描失败：" + ex.Message;
            }
        }
    }
}
