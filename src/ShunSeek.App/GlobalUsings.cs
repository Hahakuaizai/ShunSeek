// 本项目同时开了 WPF 和 WinForms（托盘图标用 NotifyIcon），两边有一堆同名类型，
// 隐式 using 会把它们都塞进全局作用域。这里统一把"WPF 那一套"钉成别名，
// 免得每个文件里都要写全限定名（Application 因为要写 System.Windows.Application，单独处理）。
global using RadioButton = System.Windows.Controls.RadioButton;
global using Brush = System.Windows.Media.Brush;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Clipboard = System.Windows.Clipboard;
global using Directory = System.IO.Directory;
global using File = System.IO.File;
global using Path = System.IO.Path;
