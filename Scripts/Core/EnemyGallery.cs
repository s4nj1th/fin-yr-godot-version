using System.Collections.Generic;
using Godot;
using DungeonCrawler.AI;
using DungeonCrawler.Entities;

namespace DungeonCrawler.Core;

/// <summary>
/// Shows every enemy archetype side by side — the regular version in front,
/// the elite behind — with its name and how it attacks, plus the two floor
/// telegraphs. Used to brief study participants before they play and for
/// the paper's figure (Scenes/EnemyGallery.tscn). With
/// `-- --gallery-out=FILE.png` it saves one frame and quits.
/// </summary>
public partial class EnemyGallery : Node3D
{
	private static readonly Dictionary<string, string> Descriptions = new()
	{
		[EnemyArchetypes.MeleeChaser] = "walks straight at you",
		[EnemyArchetypes.MeleeHitRun] = "strikes, then backs off",
		[EnemyArchetypes.RangedKite] = "shoots, keeps its distance",
		[EnemyArchetypes.RangedStrafe] = "shoots while side-stepping",
		[EnemyArchetypes.MeleeCharger] = "telegraphed dash on a line",
		[EnemyArchetypes.RangedBomber] = "lobs a bomb at a 3x3 area",
		[EnemyArchetypes.MeleeShield] = "blocks hits from the front",
	};

	private const float ColumnSpacing = 2.6f;
	private readonly List<EnemyController> _enemies = new();
	private readonly List<TelegraphFx> _telegraphs = new();
	private readonly int[,] _grid = new int[9, 20];
	private string? _outPath;
	private SubViewport? _capture;
	private Camera3D _camera = null!;
	private int _frames;
	private double _time;

	public override void _Ready()
	{
		foreach (string arg in OS.GetCmdlineUserArgs())
		{
			if (arg.StartsWith("--gallery-out="))
			{
				_outPath = arg.Substring("--gallery-out=".Length);
			}
		}

		for (int y = 0; y < _grid.GetLength(0); y++)
		{
			for (int x = 0; x < _grid.GetLength(1); x++)
			{
				_grid[y, x] = 1;
			}
		}

		BuildStage();
		IReadOnlyList<string> behaviors = EnemyArchetypes.Behaviors;
		for (int i = 0; i < behaviors.Count; i++)
		{
			float x = (i - ((behaviors.Count - 1) / 2f)) * ColumnSpacing;
			Spawn(behaviors[i], elite: false, new Vector3(x, 0, 1.3f));
			Spawn(behaviors[i], elite: true, new Vector3(x, 0, -0.9f));
			ArchetypeDefinition archetype = EnemyArchetypes.Get(behaviors[i]);
			AddLabel(new Vector3(x, 0.02f, 2.55f), archetype.DisplayName, 60, Colors.White);
			AddLabel(new Vector3(x, 0.02f, 3.3f), Descriptions[behaviors[i]], 34, new Color(0.8f, 0.82f, 0.9f), wrapWidth: 380);
		}

		float left = -((behaviors.Count - 1) / 2f * ColumnSpacing) - 1.7f;
		AddLabel(new Vector3(left, 0.02f, 1.3f), "regular", 40, new Color(0.7f, 0.72f, 0.8f));
		AddLabel(new Vector3(left, 0.02f, -0.9f), "elite", 40, Color.FromHtml("#facc15"));

		// The two floor telegraphs, at rest, behind the line-up.
		var line = new List<Vector3>();
		for (int i = 0; i < 5; i++)
		{
			line.Add(new Vector3(-7.0f + i, -0.45f, -3.2f));
		}
		_telegraphs.Add(TelegraphFx.Create(this, line, Color.FromHtml("#ef4444")));
		var blast = new List<Vector3>();
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				blast.Add(new Vector3(5.5f + dx, -0.45f, -3.2f + dz));
			}
		}
		_telegraphs.Add(TelegraphFx.Create(this, blast, Color.FromHtml("#f97316")));
		AddLabel(new Vector3(-1.4f, 0.02f, -3.2f), "charge line", 40, Color.FromHtml("#fca5a5"));
		AddLabel(new Vector3(2.0f, 0.02f, -3.2f), "blast area", 40, Color.FromHtml("#fdba74"));
	}

	public override void _Process(double delta)
	{
		_time += delta;
		foreach (EnemyController enemy in _enemies)
		{
			// The player is far away, so nobody attacks; this just animates.
			enemy.Tick(delta, new Vector2I(-100, -100), _grid);
		}

		foreach (TelegraphFx telegraph in _telegraphs)
		{
			telegraph.SetProgress(0.65f, delta);
		}

		_frames++;
		if (_outPath == null)
		{
			return;
		}

		if (_frames == 5)
		{
			// Render off-screen at print resolution, without the HUD.
			foreach (Node node in GetTree().Root.GetChildren())
			{
				if (node is CanvasLayer layer)
				{
					layer.Visible = false;
				}
			}

			_capture = new SubViewport { Size = new Vector2I(2600, 1200), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, World3D = GetViewport().World3D };
			AddChild(_capture);
			var camera = (Camera3D)_camera.Duplicate();
			_capture.AddChild(camera);
			camera.Current = true;
		}

		if (_frames == 45)
		{
			_capture!.GetTexture().GetImage().SavePng(_outPath);
			GD.Print($"[Gallery] Saved {_outPath}");
			GetTree().Quit();
		}
	}

	private void Spawn(string behavior, bool elite, Vector3 position)
	{
		ArchetypeDefinition archetype = EnemyArchetypes.WithModifiers(EnemyArchetypes.Get(behavior), 1f, 1f, elite);
		var enemy = new EnemyController { ShowDebugBoard = false };
		AddChild(enemy);
		enemy.Initialize(new Vector2I(1, 1), archetype, elite);
		enemy.Position = position;
		enemy.Visuals.FaceDirection(new Vector2I(0, 1));
		_enemies.Add(enemy);
	}

	private void BuildStage()
	{
		AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(21f, 0.2f, 9.6f) },
			Position = new Vector3(0, -0.1f, 0),
			MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromHtml("#241a44"), Roughness = 0.9f }
		});
		AddChild(new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color,
				BackgroundColor = Color.FromHtml("#0b0718"),
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = Color.FromHtml("#8b80b8"),
				AmbientLightEnergy = 0.55f,
				TonemapMode = Godot.Environment.ToneMapper.Linear
			}
		});
		AddChild(new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-55, 30, 0),
			LightEnergy = 1.15f,
			ShadowEnabled = true,
			ShadowOpacity = 0.5f
		});
		_camera = new Camera3D
		{
			Position = new Vector3(0, 8.4f, 9.6f),
			RotationDegrees = new Vector3(-42, 0, 0),
			Fov = 50f,
			Current = true
		};
		AddChild(_camera);
	}

	private void AddLabel(Vector3 position, string text, int size, Color color, float wrapWidth = 0f)
	{
		AddChild(new Label3D
		{
			Text = text,
			Width = wrapWidth > 0f ? wrapWidth : 500f,
			AutowrapMode = wrapWidth > 0f ? TextServer.AutowrapMode.Word : TextServer.AutowrapMode.Off,
			FontSize = size,
			OutlineSize = 8,
			Modulate = color,
			OutlineModulate = new Color(0.02f, 0.02f, 0.06f),
			PixelSize = 0.006f,
			Position = position,
			RotationDegrees = new Vector3(-90, 0, 0)
		});
	}
}
