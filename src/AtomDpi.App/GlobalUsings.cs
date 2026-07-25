// Enabling Windows Forms puts System.Windows.Forms into this project's implicit
// global usings, which makes names it shares with WPF ambiguous. Windows Forms is
// only here for the notification area icon - it has no first party WPF equivalent -
// so these aliases settle every such name in favour of WPF, once, for the whole
// project. Anything genuinely from Windows Forms is named explicitly at its use site.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
