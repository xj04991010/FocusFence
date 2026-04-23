// Resolve WPF vs WinForms namespace collisions caused by ImplicitUsings.
// Only WinForms types we actually use: NotifyIcon, ContextMenuStrip, ToolStripMenuItem
// Everything else should resolve to WPF (System.Windows.*).

global using DataFormats = System.Windows.DataFormats;
global using DragDropEffects = System.Windows.DragDropEffects;
global using DragEventArgs = System.Windows.DragEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using Point = System.Windows.Point;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Application = System.Windows.Application;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using Cursors = System.Windows.Input.Cursors;
global using Brushes = System.Windows.Media.Brushes;
global using FontFamily = System.Windows.Media.FontFamily;
global using CheckBox = System.Windows.Controls.CheckBox;
global using Button = System.Windows.Controls.Button;
