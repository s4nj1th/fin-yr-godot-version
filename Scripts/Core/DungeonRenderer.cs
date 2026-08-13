using System;
using System.Collections.Generic;
using Godot;
using DungeonCrawler.Data;

namespace DungeonCrawler.Core;

public partial class DungeonRenderer : Node2D
{
	[Export] public int TileSize { get; set; } = 16;

	private DungeonMapData? _mapData;
	private bool[,] _exploredGrid = new bool[0, 0];
	private bool[,] _visibleGrid = new bool[0, 0];
	private Dictionary<string, Color> _paletteColors = new();

	public int MapWidth => _mapData?.Width ?? 0;
	public int MapHeight => _mapData?.Height ?? 0;

	public void Initialize(DungeonMapData mapData)
	{
		_mapData = mapData;
		_exploredGrid = new bool[MapHeight, MapWidth];
		_visibleGrid = new bool[MapHeight, MapWidth];

		ParsePalette(mapData.Palette);
		QueueRedraw();
	}

	private void ParsePalette(PaletteData palette)
	{
		_paletteColors["background"] = Color.FromHtml(palette.Background);
		_paletteColors["wall"] = Color.FromHtml(palette.Wall);
		_paletteColors["floor"] = Color.FromHtml(palette.Floor);
		_paletteColors["corridor"] = Color.FromHtml(palette.Corridor);
		_paletteColors["door"] = Color.FromHtml(palette.Door);
		_paletteColors["trap"] = Color.FromHtml(palette.Trap);
		_paletteColors["enemy"] = Color.FromHtml(palette.Enemy);
		_paletteColors["chest"] = Color.FromHtml(palette.Chest);
		_paletteColors["start"] = Color.FromHtml(palette.Start);
		_paletteColors["exit"] = Color.FromHtml(palette.Exit);
		_paletteColors["fog"] = Color.FromHtml(palette.Fog);
	}

	public void UpdateVisibility(Vector2I playerGridPos, int visionRadius)
	{
		if (_mapData == null) return;

		// Reset visibility grid for current frame
		Array.Clear(_visibleGrid, 0, _visibleGrid.Length);

		int startX = Math.Max(0, playerGridPos.X - visionRadius);
		int endX = Math.Min(MapWidth - 1, playerGridPos.X + visionRadius);
		int startY = Math.Max(0, playerGridPos.Y - visionRadius);
		int endY = Math.Min(MapHeight - 1, playerGridPos.Y + visionRadius);

		for (int y = startY; y <= endY; y++)
		{
			for (int x = startX; x <= endX; x++)
			{
				float distSq = (x - playerGridPos.X) * (x - playerGridPos.X) + (y - playerGridPos.Y) * (y - playerGridPos.Y);
				if (distSq <= visionRadius * visionRadius)
				{
					_visibleGrid[y, x] = true;
					_exploredGrid[y, x] = true;
				}
			}
		}

		QueueRedraw();
	}

	public float CalculateExplorationPercent()
	{
		if (_mapData == null || MapWidth == 0 || MapHeight == 0) return 0.0f;

		int totalWalkable = 0;
		int exploredWalkable = 0;

		for (int y = 0; y < MapHeight; y++)
		{
			for (int x = 0; x < MapWidth; x++)
			{
				int tileType = _mapData.Grid[y][x];
				if (tileType != 0) // Walkable tile
				{
					totalWalkable++;
					if (_exploredGrid[y, x]) exploredWalkable++;
				}
			}
		}

		return totalWalkable == 0 ? 0.0f : ((float)exploredWalkable / totalWalkable) * 100.0f;
	}

	public bool IsWalkable(int x, int y)
	{
		if (_mapData == null || x < 0 || x >= MapWidth || y < 0 || y >= MapHeight)
			return false;

		int tileType = _mapData.Grid[y][x];
		return tileType != 0; // 0 is WALL
	}

	public int GetTileType(int x, int y)
	{
		if (_mapData == null || x < 0 || x >= MapWidth || y < 0 || y >= MapHeight)
			return 0;

		return _mapData.Grid[y][x];
	}

	public void ClearTile(int x, int y)
	{
		if (_mapData != null && x >= 0 && x < MapWidth && y >= 0 && y < MapHeight)
		{
			_mapData.Grid[y][x] = 1; // Replace tile (e.g. chest/trap) with floor
			QueueRedraw();
		}
	}

	public override void _Draw()
	{
		if (_mapData == null) return;

		// Draw background
		DrawRect(new Rect2(0, 0, MapWidth * TileSize, MapHeight * TileSize), _paletteColors["background"]);

		for (int y = 0; y < MapHeight; y++)
		{
			for (int x = 0; x < MapWidth; x++)
			{
				if (!_exploredGrid[y, x])
				{
					// Unexplored tile -> draw dark fog
					DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize, TileSize), _paletteColors["fog"]);
					continue;
				}

				int tileType = _mapData.Grid[y][x];
				Color tileColor = tileType switch
				{
					0 => _paletteColors["wall"],
					1 => _paletteColors["floor"],
					2 => _paletteColors["corridor"],
					3 => _paletteColors["door"],
					4 => _paletteColors["trap"],
					5 => _paletteColors["enemy"],
					6 => _paletteColors["chest"],
					7 => _paletteColors["start"],
					8 => _paletteColors["exit"],
					_ => _paletteColors["floor"]
				};

				// Dim unexplored/explored non-visible tiles
				if (!_visibleGrid[y, x])
				{
					tileColor = tileColor.Darkened(0.5f);
				}

				DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize, TileSize), tileColor);
				DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize, TileSize), Colors.Black.Darkened(0.8f), false); // Tile outline
			}
		}
	}
}
