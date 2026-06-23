using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HyperVStatusTray.UI;

internal static class IconFactory
{
    public static Icon CreateTrayIcon(IReadOnlyList<IndicatorState> states)
    {
        if (states.Count is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(states), "Tray icon supports one or two indicator states.");
        }

        using Bitmap bitmap = new(32, 32, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        if (states.Count == 1)
        {
            DrawDot(graphics, new RectangleF(7, 7, 18, 18), states[0]);
        }
        else
        {
            DrawDot(graphics, new RectangleF(9, 2, 14, 14), states[0]);
            DrawDot(graphics, new RectangleF(9, 17, 14, 14), states[1]);
        }

        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(iconHandle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(iconHandle);
        }
    }

    public static Bitmap CreateMenuImage(IndicatorState state)
    {
        Bitmap bitmap = new(16, 16, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        DrawDot(graphics, new RectangleF(2, 2, 12, 12), state);
        return bitmap;
    }

    private static void DrawDot(Graphics graphics, RectangleF bounds, IndicatorState state)
    {
        Color fill = state switch
        {
            IndicatorState.Off => Color.FromArgb(128, 128, 128),
            IndicatorState.Starting => Color.FromArgb(255, 191, 0),
            IndicatorState.Ready => Color.FromArgb(34, 177, 76),
            IndicatorState.Fault => Color.FromArgb(220, 35, 50),
            IndicatorState.Unknown => Color.FromArgb(45, 125, 210),
            _ => Color.Gray
        };

        using SolidBrush shadow = new(Color.FromArgb(90, Color.Black));
        using SolidBrush brush = new(fill);
        using Pen outline = new(Color.FromArgb(220, 28, 28, 28), Math.Max(1.0f, bounds.Width / 9f));

        RectangleF shadowBounds = bounds;
        shadowBounds.Offset(0.8f, 0.8f);
        graphics.FillEllipse(shadow, shadowBounds);
        graphics.FillEllipse(brush, bounds);
        graphics.DrawEllipse(outline, bounds);

        if (state == IndicatorState.Unknown)
        {
            using Pen slash = new(Color.White, Math.Max(1.2f, bounds.Width / 6f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(
                slash,
                bounds.Left + bounds.Width * 0.28f,
                bounds.Bottom - bounds.Height * 0.28f,
                bounds.Right - bounds.Width * 0.28f,
                bounds.Top + bounds.Height * 0.28f);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
