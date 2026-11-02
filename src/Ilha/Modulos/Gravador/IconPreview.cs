using System;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Ilha.Recorder;

/// <summary>
/// Draws the recorder's two menu tiles (each icon on the menu's gray ball, idle and recording) into a PNG with no window,
/// with the same drawing code the menu uses, so the icons can be checked without opening the island.
/// </summary>
internal static class IconPreview
{
    public static void Render(string path, float scale = 6)
    {
        const float BALL = 54, CELL = 64;
        int w = (int)(CELL * 4 * scale), h = (int)(CELL * scale);
        var levels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_0 };
        using var d3d = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, levels);
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        using var f = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        using var dev = f.CreateDevice(dxgi);
        using var dc = dev.CreateDeviceContext(DeviceContextOptions.None);
        var fmt = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(fmt, 96 * scale, 96 * scale, BitmapOptions.Target));
        dc.Target = target;
        dc.SetDpi(96 * scale, 96 * scale);
        dc.BeginDraw();
        dc.Clear(new Color4(0, 0, 0, 1));
        for (int i = 0; i < 4; i++)
        {
            var center = new Vector2(CELL * i + CELL / 2, CELL / 2);
            Icone.Circulo(dc, center, BALL / 2, new Color4(1, 1, 1, 0.06f));
            var box = new System.Drawing.RectangleF(center.X - 12, center.Y - 12, 24, 24);
            // the menu draws icons for 24 DIP and scales them by 28/24
            dc.Transform = Matrix3x2.CreateScale(28f / 24f, center);
            if (i < 2) Icons.Camera(dc, f, box, live: i == 1);
            else Icons.MicTile(dc, f, box, live: i == 3);
            dc.Transform = Matrix3x2.Identity;
        }
        dc.EndDraw();
        using var cpu = dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(fmt, 96 * scale, 96 * scale, BitmapOptions.CannotDraw | BitmapOptions.CpuRead));
        cpu.CopyFromBitmap(target);
        var map = cpu.Map(MapOptions.Read);
        try
        {
            using var bmp = new System.Drawing.Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(map.Bits + y * (int)map.Pitch, row, 0, row.Length);
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
            bmp.UnlockBits(data);
            bmp.Save(path, ImageFormat.Png);
        }
        finally { cpu.Unmap(); }
    }
}
