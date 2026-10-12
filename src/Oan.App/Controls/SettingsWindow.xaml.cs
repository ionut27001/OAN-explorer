using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Oan.App.Platform;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// The settings you decide and forget, on the same view model the toolbar binds to.
/// Wears the window's material and follows it while open.
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

    /// <summary>Both ways in, the logo menu and Ctrl+comma, go through here, so there is never a second one.</summary>
    public static void Open(Window? owner)
    {
        if (owner?.DataContext is not MainViewModel model) return;

        new SettingsWindow(model, owner).ShowDialog();
    }

    private void OnLookChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(AppearanceViewModel.Backdrop) or nameof(AppearanceViewModel.Glass)) Dress();
    }

    private void Dress()
    {
        // The same material as the window it belongs to, which under Glass is acrylic.
        var material = _look.Glass ? Backdrop.Acrylic : _look.Backdrop;
        var applied = WindowEffects.TryBackdrop(this, material);

        if (applied && material != Backdrop.Solid) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, "WindowBg");

        WindowEffects.UseDarkTitleBar(this, Theme.IsDark);
    }
}
