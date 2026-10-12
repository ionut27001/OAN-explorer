using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Oan.App.Controls;

/// <summary>LiquidGlass.hlsl. It bends whatever its element has drawn, so that element must hold the picture.</summary>
internal sealed class LiquidGlassEffect : ShaderEffect
{
    internal static readonly Uri Bytecode = new("pack://application:,,,/Oan;component/Controls/LiquidGlass.ps");

    private static readonly PixelShader Shader = new() { UriSource = Bytecode };

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(LiquidGlassEffect), 0);

    public static readonly DependencyProperty SizeProperty = Constant<Point>(nameof(Size), 0);
    public static readonly DependencyProperty RadiusProperty = Constant<double>(nameof(Radius), 1);
    public static readonly DependencyProperty EdgeProperty = Constant<double>(nameof(Edge), 2);
    public static readonly DependencyProperty BendProperty = Constant<double>(nameof(Bend), 3);
    public static readonly DependencyProperty ShineProperty = Constant<double>(nameof(Shine), 4);

    public LiquidGlassEffect()
    {
        PixelShader = Shader;
        foreach (var property in new[] { InputProperty, SizeProperty, RadiusProperty, EdgeProperty, BendProperty, ShineProperty })
            UpdateShaderValue(property);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    /// <summary>In pixels, so the edge is as wide on a bar as on a pane.</summary>
    public Point Size { get => (Point)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double Edge { get => (double)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
    public double Bend { get => (double)GetValue(BendProperty); set => SetValue(BendProperty, value); }
    public double Shine { get => (double)GetValue(ShineProperty); set => SetValue(ShineProperty, value); }

    private static DependencyProperty Constant<T>(string name, int register) =>
        DependencyProperty.Register(name, typeof(T), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(default(T), PixelShaderConstantCallback(register)));
}
