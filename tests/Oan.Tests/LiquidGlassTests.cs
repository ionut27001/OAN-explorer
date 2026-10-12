using System.IO.Packaging;
using System.Windows;
using Oan.App.Controls;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The glass shader is bytecode compiled by hand and shipped as a resource. Nothing in the build
/// checks that it is there, or that it is a shader — a missing or stale file shows up as slabs
/// that draw nothing, at run time, on someone else's machine.
/// </summary>
[Collection(nameof(DiskBound))]
public class LiquidGlassTests
{
    [Fact]
    public void The_compiled_shader_ships_with_the_app_and_loads()
    {
        OnSta.Run(() =>
        {
            // Registers the pack: scheme, which a process with no Application has not done yet.
            _ = PackUriHelper.UriSchemePack;
            if (Application.ResourceAssembly is null) Application.ResourceAssembly = typeof(LiquidGlassEffect).Assembly;

            var effect = new LiquidGlassEffect { Size = new Point(400, 300), Radius = 4, Edge = 18, Bend = 26, Shine = 0.5 };

            Assert.Equal(26, effect.Bend);
            using var bytecode = Application.GetResourceStream(LiquidGlassEffect.Bytecode).Stream;

            // ps_3_0 bytecode opens with its version token: 0xFFFF0300, little-endian.
            var head = new byte[4];
            bytecode.ReadExactly(head);
            Assert.Equal([0x00, 0x03, 0xFF, 0xFF], head);
        });
    }
}
