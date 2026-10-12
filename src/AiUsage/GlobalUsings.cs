// UseWPF drops System.IO from the SDK's implicit usings (it stays implicit for a plain console/
// library project, but not a WPF one) - every provider/storage file needs File/Directory/Path,
// so it is restored globally here instead of repeating "using System.IO;" everywhere.
global using System.IO;

// The SDK's implicit usings never cover System.Windows/System.Windows.Controls (WPF gets no special
// treatment there, unlike System.IO above) - Application, MessageBox, Clipboard and UserControl are
// used unqualified throughout the views, so both namespaces are restored globally here instead of
// repeating "using System.Windows;"/"using System.Windows.Controls;" everywhere.
global using System.Windows;
global using System.Windows.Controls;

// System.Drawing shares these type names with WPF's media types; the WPF ones win project-wide, and
// the few System.Drawing types in use are qualified where they appear.
global using Point = System.Windows.Point;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Pen = System.Windows.Media.Pen;
