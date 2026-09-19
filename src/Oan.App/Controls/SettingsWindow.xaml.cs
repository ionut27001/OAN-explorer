using System.Windows;

namespace Oan.App.Controls;

/// <summary>
/// The settings that are not a gesture.
///
/// It deliberately does not hold everything: the view size, sort and the filters stay on the
/// toolbar, because they are things you change while looking at a folder, not things you set
/// once. What is here is what you decide and forget — and it is the same view model the toolbar
/// binds to, so the two can never disagree.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(object model, Window owner)
    {
        InitializeComponent();
        DataContext = model;
        Owner = owner;
    }
}
