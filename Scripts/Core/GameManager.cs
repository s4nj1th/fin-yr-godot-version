using System;
using System.Collections.Generic;
using Godot;
using DungeonCrawler.Data;
using DungeonCrawler.Network;
using DungeonCrawler.Entities;
using DungeonCrawler.UI;

namespace DungeonCrawler.Core;

public partial class GameManager : Node2D
{
    private ApiClient _apiClient = null!;
    private DungeonRenderer _renderer = null!;
    private PlayerController _player = null!;
    private HudManager _hudManager = null!;
    private readonly List<EnemyController> _activeEnemies = new();

    private DungeonMapData? _currentMapData;
    private DungeonConfig _currentConfig = new();
    private int _currentLevel = 1;
    private double _levelTime = 0;
    private bool _levelActive = false;

    public override void _Ready()
    {
        InitializeComponents();
        StartInitialLevel();
    }

    private void InitializeComponents()
    {
        _apiClient = new ApiClient();
        AddChild(_apiClient);

        _renderer = new DungeonRenderer();
        AddChild(_renderer);

        _player = new PlayerController();
        AddChild(_player);

        _hudManager = new HudManager();
        AddChild(_hudManager);

        _player.ExitReached += OnPlayerExitReached;
        _player.PlayerDied += OnPlayerDied;
        _player.StatsChanged += OnPlayerStatsChanged;
        _hudManager.AdaptivePromptSubmitted += OnAdaptivePromptSubmitted;
    }

    public override void _Process(double delta)
    {
        if (_levelActive)
        {
            _levelTime += delta;
            _hudManager.UpdateStats(
                _currentLevel,
                _currentMapData?.ThemeName ?? "Dark Obsidian",
                _player.CurrentHp,
                _player.MaxHp,
                _player.Score,
                _levelTime
            );
        }
    }

    private async void StartInitialLevel()
    {
        GD.Print("[GameManager] Requesting Level 1 map from backend server...");
        _currentMapData = await _apiClient.GenerateMapAsync("Generate a dark Obsidian starting dungeon", _currentConfig);
        
        if (_currentMapData == null)
        {
            GD.PrintErr("[GameManager] Failed to load initial map data!");
            return;
        }

        LoadLevel(_currentMapData);
    }

    private void LoadLevel(DungeonMapData mapData)
    {
        _currentMapData = mapData;
        _currentConfig = mapData.Config;
        _levelTime = 0;
        _levelActive = true;

        // Clear existing enemies
        foreach (var enemy in _activeEnemies)
        {
            enemy.QueueFree();
        }
        _activeEnemies.Clear();

        // Render dungeon map
        _renderer.Initialize(mapData);

        // Spawn player at START tile
        var startPos = new Vector2I(mapData.Start.X, mapData.Start.Y);
        _player.SpawnAt(startPos, _renderer, mapData.Config.Visibility);

        // Spawn enemies
        foreach (var enemyInfo in mapData.Enemies)
        {
            var enemy = new EnemyController();
            AddChild(enemy);
            enemy.Initialize(new Vector2I(enemyInfo.X, enemyInfo.Y), enemyInfo.Type, _renderer, _player);
            enemy.EnemyAttackedPlayer += (damage) => _player.TakeDamage(damage);
            _activeEnemies.Add(enemy);
        }

        GD.Print($"[GameManager] Level {_currentLevel} loaded successfully! Enemies spawned: {_activeEnemies.Count}");
    }

    private async void OnPlayerExitReached()
    {
        _levelActive = false;
        GD.Print("[GameManager] Exit reached! Gathering telemetry for LLM closed-loop feedback...");

        float explorationPercent = _renderer.CalculateExplorationPercent();

        var telemetryReq = new TelemetryRequest
        {
            LevelNumber = _currentLevel,
            Telemetry = new TelemetryMetrics
            {
                TimeTakenSec = (float)_levelTime,
                DamageTaken = _player.DamageTaken,
                HealthRemaining = _player.CurrentHp,
                DeathsRetries = 0,
                EnemiesKilled = 0, // Simplified metric
                TotalEnemies = _activeEnemies.Count,
                ChestsOpened = _player.ChestsOpened,
                TotalChests = 4,
                TrapsTriggered = _player.TrapsTriggered,
                ExplorationPercent = explorationPercent
            },
            UserPreference = "",
            PreviousConfig = _currentConfig
        };

        GD.Print("[GameManager] Sending telemetry to /api/adapt...");
        var adaptationResponse = await _apiClient.SendTelemetryAndAdaptAsync(telemetryReq);

        if (adaptationResponse != null)
        {
            _hudManager.ShowAdaptationModal(adaptationResponse);
        }
        else
        {
            // Fallback: Proceed to next level directly
            _currentLevel++;
            StartInitialLevel();
        }
    }

    private async void OnAdaptivePromptSubmitted(string userPrompt)
    {
        _currentLevel++;
        GD.Print($"[GameManager] Generating Level {_currentLevel} with prompt: '{userPrompt}'");

        var mapData = await _apiClient.GenerateMapAsync(userPrompt, _currentConfig);
        if (mapData != null)
        {
            LoadLevel(mapData);
        }
    }

    private void OnPlayerStatsChanged(int currentHp, int maxHp, int score)
    {
        _hudManager.UpdateStats(
            _currentLevel,
            _currentMapData?.ThemeName ?? "Dark Obsidian",
            currentHp,
            maxHp,
            score,
            _levelTime
        );
    }

    private void OnPlayerDied()
    {
        _levelActive = false;
        GD.Print("[GameManager] Player died! Restarting level...");
        if (_currentMapData != null)
        {
            LoadLevel(_currentMapData);
        }
    }
}
