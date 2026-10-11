using System;

namespace ScreenTranslator.Models;

/// <summary>Прямоугольник в физических пикселях экрана (виртуальный рабочий стол).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public bool Contains(PixelRect r) => r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;

    public PixelRect Intersect(PixelRect r)
    {
        int left = Math.Max(X, r.X);
        int top = Math.Max(Y, r.Y);
        int right = Math.Min(Right, r.Right);
        int bottom = Math.Min(Bottom, r.Bottom);
        return right > left && bottom > top ? FromLTRB(left, top, right, bottom) : default;
    }

    public PixelRect Union(PixelRect r)
    {
        if (IsEmpty) return r;
        if (r.IsEmpty) return this;
        return FromLTRB(Math.Min(X, r.X), Math.Min(Y, r.Y), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));
    }

    public static PixelRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public override string ToString() => $"{Width}×{Height} ({X}, {Y})";
}
