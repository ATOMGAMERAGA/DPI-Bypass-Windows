// Enabling Windows Forms puts System.Windows.Forms and System.Drawing into this
// project's implicit global usings, which makes the names they share with WPF
// ambiguous. Windows Forms is only here for the notification area icon - WPF has no
// first party equivalent - so these aliases settle each shared name in favour of WPF,
// once, for the whole project. Anything genuinely from Windows Forms or System.Drawing
// is named explicitly at its use site (see Infrastructure/TrayIcon.cs).
global using Application = System.Windows.Application;
global using Brushes = System.Windows.Media.Brushes;
global using MessageBox = System.Windows.MessageBox;
