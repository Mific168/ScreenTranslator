namespace ScreenTranslator.Models;

/// <summary>Монитор: границы и рабочая область в физических пикселях, эффективный DPI.</summary>
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, uint Dpi, bool IsPrimary)
{
    public double Scale => Dpi / 96.0;
}
