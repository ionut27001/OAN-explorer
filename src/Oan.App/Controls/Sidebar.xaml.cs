using System.Windows.Controls;

namespace Oan.App.Controls;

/// <summary>Quick access, the drives, and whatever OAN has mounted. Its data context is the
/// window's, inherited — the pane has no state of its own.</summary>
public partial class Sidebar : UserControl
{
    public Sidebar() => InitializeComponent();
}
