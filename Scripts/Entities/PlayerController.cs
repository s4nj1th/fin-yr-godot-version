using System;
using Godot;
using DungeonCrawler.Core;

namespace DungeonCrawler.Entities;

public partial class PlayerController : Node2D
{
    [Signal] public delegate void PlayerMovedEventHandler(Vector2I gridPos);
    [Signal] public delegate void StatsChangedEventHandler(int currentHp, int maxHp, int score);
    [Signal] public delegate void ChestOpenedEventHandler();
    [Signal] public delegate void TrapTriggeredEventHandler();
    [Signal] public delegate void ExitReachedEventHandler();
    [Signal] public delegate void PlayerDiedEventHandler();

    [Export] public int MaxHp { get; set; } = 100;
    [Export] public int VisionRadius { get; set; } = 5;

    public int CurrentHp { get; private set; }
    public int Score { get; private set; }
    public Vector2I GridPos { get; private set; }
    public int DamageTaken { get; private set; }
    public int ChestsOpened { get; private set; }
    public int TrapsTriggered { get; private set; }

    private DungeonRenderer? _renderer;
    private double _moveTimer = 0;
    private const double MoveDelay = 0.12; // Controls movement step speed

    public void SpawnAt(Vector2I startGridPos, DungeonRenderer renderer, string visibilityMode)
    {
        _renderer = renderer;
        GridPos = startGridPos;
        Position = new Vector2(GridPos.X * renderer.TileSize, GridPos.Y * renderer.TileSize);
        
        CurrentHp = MaxHp;
        Score = 0;
        DamageTaken = 0;
        ChestsOpened = 0;
        TrapsTriggered = 0;

        VisionRadius = visibilityMode == "low" ? 3 : 6;

        _renderer.UpdateVisibility(GridPos, VisionRadius);
        EmitSignal(SignalName.StatsChanged, CurrentHp, MaxHp, Score);
    }

    public override void _Process(double delta)
    {
        if (_renderer == null || CurrentHp <= 0) return;

        _moveTimer += delta;
        if (_moveTimer < MoveDelay) return;

        Vector2I dir = Vector2I.Zero;
        if (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D)) dir.X += 1;
        else if (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A)) dir.X -= 1;
        else if (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S)) dir.Y += 1;
        else if (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W)) dir.Y -= 1;

        if (dir != Vector2I.Zero)
        {
            TryMove(dir);
            _moveTimer = 0;
        }
    }

    private void TryMove(Vector2I dir)
    {
        if (_renderer == null) return;

        Vector2I targetPos = GridPos + dir;
        if (!_renderer.IsWalkable(targetPos.X, targetPos.Y)) return;

        GridPos = targetPos;
        Position = new Vector2(GridPos.X * _renderer.TileSize, GridPos.Y * _renderer.TileSize);

        _renderer.UpdateVisibility(GridPos, VisionRadius);
        EmitSignal(SignalName.PlayerMoved, GridPos);

        int tileType = _renderer.GetTileType(GridPos.X, GridPos.Y);
        HandleTileInteraction(tileType);
    }

    private void HandleTileInteraction(int tileType)
    {
        if (_renderer == null) return;

        switch (tileType)
        {
            case 4: // TRAP
                TakeDamage(15);
                TrapsTriggered++;
                EmitSignal(SignalName.TrapTriggered);
                _renderer.ClearTile(GridPos.X, GridPos.Y);
                break;

            case 6: // CHEST
                Score += 25;
                ChestsOpened++;
                EmitSignal(SignalName.ChestOpened);
                _renderer.ClearTile(GridPos.X, GridPos.Y);
                EmitSignal(SignalName.StatsChanged, CurrentHp, MaxHp, Score);
                break;

            case 8: // EXIT
                EmitSignal(SignalName.ExitReached);
                break;
        }
    }

    public void TakeDamage(int damage)
    {
        CurrentHp = Math.Max(0, CurrentHp - damage);
        DamageTaken += damage;
        EmitSignal(SignalName.StatsChanged, CurrentHp, MaxHp, Score);

        if (CurrentHp <= 0)
        {
            EmitSignal(SignalName.PlayerDied);
        }
    }

    public override void _Draw()
    {
        if (_renderer == null) return;
        // Draw player as a cyan circle primitive
        float size = _renderer.TileSize;
        DrawCircle(new Vector2(size / 2, size / 2), size / 2.5f, Colors.Cyan);
    }
}
