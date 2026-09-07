namespace DungeonCrawler.AI;

/// <summary>
/// Engine-free stand-in for Godot's Vector2I, used throughout Scripts/AI so
/// this whole namespace stays pure C# with no dependency on the Godot
/// runtime being loaded — GodotSharp's own types require the actual engine
/// process to function and crash in a plain `dotnet test` host. Deliberately
/// has NO conversion operators to/from Godot.Vector2I here (even an unused
/// reference to that type from this namespace is enough to risk the same
/// crash) — GameManager.cs/EnemyController.cs each carry a tiny private
/// `ToGridPoint`/`ToVector2I` pair to convert at the boundary instead.
/// </summary>
public readonly record struct GridPoint(int X, int Y)
{
    public static readonly GridPoint Zero = new(0, 0);

    public static GridPoint operator +(GridPoint a, GridPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static GridPoint operator -(GridPoint a, GridPoint b) => new(a.X - b.X, a.Y - b.Y);
}
