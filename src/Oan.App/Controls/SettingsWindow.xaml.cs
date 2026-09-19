using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Oan.App.Platform;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// The settings that are not a gesture.
///
/// It deliberately does not hold everything: the view size, the sort order and the filters stay
/// on the toolbar, because they are things you change while looking at a folder rather than
/// things you set once. What is here is what you decide and forget — and it is the same view
/// model the toolbar binds to, so the two can never disagree.
///
/// It wears the window's material as well, and follows it while it is open. A settings window
/// that stayed flat grey behind glass would be the one place the setting it offers does not apply.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppearanceViewModel _look;

    public SettingsWindow(MainViewModel model, Window owner)
    {
        InitializeComponent();
        DataContext = model;
        Owner = owner;
        _look = model.Look;

        SourceInitialized += (_, _) => Dress();
        _look.PropertyChanged += OnLookChanged;
        Closed += (_, _) => _look.PropertyChanged -= OnLookChanged;
    }

    private void OnLookChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(AppearanceViewModel.Backdrop)) Dress();
    }

    private void Dress()
    {
        var applied = WindowEffects.TryBackdrop(this, _look.Backdrop);

        if (applied && _look.Backdrop != Backdrop.Solid) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, "WindowBg");

        WindowEffects.UseDarkTitleBar(this, Theme.IsDark);
    }
}
