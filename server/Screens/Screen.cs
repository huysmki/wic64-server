namespace Wic64Server.Screens;

/// <summary>A 40x25 text screen that is sent to the C64 as 1000 screen codes.</summary>
public sealed class Screen
{
    public const int Columns = 40;
    public const int Rows = 25;

    public byte[] Data { get; } = Enumerable.Repeat(ScreenCodes.Space, Columns * Rows).ToArray();

    public void Print(int row, int column, string text, int width, bool reverse = false) =>
        ScreenCodes.Write(Data.AsSpan(row * Columns + column, width), text, reverse);

    public void Print(int row, int column, string text, bool reverse = false) =>
        Print(row, column, text, Math.Min(text.Length, Columns - column), reverse);
}
