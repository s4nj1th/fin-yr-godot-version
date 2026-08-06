using System;
using Godot;
using DungeonCrawler.Core;

namespace DungeonCrawler.Entities;

public partial class EnemyController : Node2D
{
    [Signal] public delegate void EnemyAttackedPlayerEventHandler(int damage);

    public Vector2I GridPos { get; private set; }
    public string BehaviorType { get; private set; } = "patrol";

    private DungeonRenderer? _renderer;
    private PlayerController? _player;
    private double _aiTimer = 0;
    private const double AiStepDelay = 0.5; // Enemy moves every 0.5 sec

    public void Initialize(Vector2I startGridPos, string behaviorType, DungeonRenderer renderer, PlayerController player)
    {
        GridPos = startGridPos;
        BehaviorType = behaviorType;
        _renderer = renderer;
        _player = player;
        Position = new Vector2(GridPos.X * renderer.TileSize, GridPos.Y * renderer.TileSize);
    }

    public override void _Process(double delta)
    {
        if (_renderer == null || _player == null || _player.CurrentHp <= 0) return;

        _aiTimer += delta;
        if (_aiTimer < AiStepDelay) return;

        _aiTimer = 0;
        ExecuteAiStep();
    }

    private void ExecuteAiStep()
    {
        if (_renderer == null || _player == null) return;

        Vector2I playerPos = _player.GridPos;
        float distToPlayer = GridPos.DistanceTo(playerPos);

        if (distToPlayer <= 1.5f)
        {
            // Attack player
            EmitSignal(SignalName.EnemyAttackedPlayer, 10);
            return;
        }

        Vector2I nextStep = Vector2I.Zero;

        if (BehaviorType == "ambush" && distToPlayer <= 6.0f)
        {
            // Chase player aggressively
            int dx = Math.Sign(playerPos.X - GridPos.X);
            int dy = Math.Sign(playerPos.Y - GridPos.Y);

            if (dx != 0 && _renderer.IsWalkable(GridPos.X + dx, GridPos.Y))
                nextStep = new Vector2I(dx, 0);
            else if (dy != 0 && _renderer.IsWalkable(GridPos.X, GridPos.Y + dy))
                nextStep = new Vector2I(0, dy);
        }
        else
        {
            // Random patrol move
            var random = new Random();
            int dirIndex = random.Next(4);
            Vector2I[] directions = { Vector2I.Up, Vector2I.Down, Vector2I.Left, Vector2I.Right };
            Vector2I candidate = directions[dirIndex];

            if (_renderer.IsWalkable(GridPos.X + candidate.X, GridPos.Y + candidate.Y))
            {
                nextStep = candidate;
            }
        }

        if (nextStep != Vector2I.Zero)
        {
            GridPos += nextStep;
            Position = new Vector2(GridPos.X * _renderer.TileSize, GridPos.Y * _renderer.TileSize);
        }
    }

    public override void _Draw()
    {
        if (_renderer == null) return;
        float size = _renderer.TileSize;
        Color enemyColor = BehaviorType == "ambush" ? Colors.Crimson : Colors.OrangeRed;
        DrawRect(new Rect2(2, 2, size - 4, size - 4), enemyColor);
    }
}
