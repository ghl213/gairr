// ArchNodePickerDialog.cs —— 重新关联架构节点：为已建编排计划选择挂靠的功能节点
// 选叶子=直接关联该功能；选分组/分支=在其下新增以编排标题命名的功能子叶。返回选中节点 NodePath。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GAIRR;

/// <summary>架构节点选择对话框：树形列出功能架构全部节点，选中即返回其 NodePath。</summary>
public class ArchNodePickerDialog : Window
{
 /// <summary>选中节点的 NodePath（未确认时为 null）。</summary>
 public string? ResultPath { get; private set; }

 public ArchNodePickerDialog(ArchNode funcRoot, string planTitle)
 {
 WindowStartupLocation = WindowStartupLocation.CenterOwner;
 Width = 520;
 Height = 520;
 WindowStyle = WindowStyle.None;
 ResizeMode = ResizeMode.CanResizeWithGrip;
 Title = "重新关联架构节点";
 Background = new SolidColorBrush(Color.FromRgb(0x07, 0x07, 0x0F));
 Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF6));
 FontFamily = new FontFamily("Microsoft YaHei, PingFang SC, sans-serif");

 var grid = new Grid { Margin = new Thickness(14) };
 grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
 grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
 grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

 var head = new TextBlock
 {
 Text = "为计划「" + planTitle + "」选择要挂靠的功能节点：选叶子=直接关联该功能；选分组/分支=在其下新增以编排标题命名的功能子叶。",
 TextWrapping = TextWrapping.Wrap,
 Margin = new Thickness(0, 0, 0, 10),
 };
 Grid.SetRow(head, 0);
 grid.Children.Add(head);

 var list = new ListBox
 {
 Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1F)),
 Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF6)),
 BorderBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x1D, 0x29)),
 BorderThickness = new Thickness(1),
 };
 var paths = new List<string>();
 void Walk(ArchNode n, int depth)
 {
 paths.Add(n.NodePath);
 list.Items.Add(new string(' ', depth * 3) + n.Name + (n.IsLeaf ? " （叶子）" : ""));
 foreach (var c in n.Children) Walk(c, depth + 1);
 }
 foreach (var c in funcRoot.Children) Walk(c, 0);
 if (paths.Count == 0)
 {
 paths.Add(funcRoot.NodePath);
 list.Items.Add(funcRoot.Name + " （根）");
 }
 Grid.SetRow(list, 1);
 grid.Children.Add(list);

 var btnRow = new StackPanel
 {
 Orientation = Orientation.Horizontal,
 HorizontalAlignment = HorizontalAlignment.Right,
 Margin = new Thickness(0, 10, 0, 0),
 };
 var ok = new Button { Content = "确定关联", Width = 88, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
 var cancel = new Button { Content = "取消", Width = 72, Height = 28 };
 ok.Click += (s, args) =>
 {
 if (list.SelectedIndex < 0 || list.SelectedIndex >= paths.Count)
 {
 MessageBox.Show(this, "请先选择一个节点", "重新关联");
 return;
 }
 ResultPath = paths[list.SelectedIndex];
 DialogResult = true;
 };
 cancel.Click += (s, args) => DialogResult = false;
 list.MouseDoubleClick += (s, args) =>
 {
 if (list.SelectedIndex >= 0 && list.SelectedIndex < paths.Count)
 {
 ResultPath = paths[list.SelectedIndex];
 DialogResult = true;
 }
 };
 btnRow.Children.Add(ok);
 btnRow.Children.Add(cancel);
 Grid.SetRow(btnRow, 2);
 grid.Children.Add(btnRow);

 Content = grid;
 }
}
