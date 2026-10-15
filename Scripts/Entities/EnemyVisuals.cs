using System;
using System.Collections.Generic;
using Godot;
using DungeonCrawler.AI;

namespace DungeonCrawler.Entities;

/// <summary>
/// The look and animation of one enemy: a model built from primitive meshes
/// per archetype (no external art assets), an elite aura and crown, a health
/// bar, and the idle, turn, wind-up, hit, stun, spawn and death animations.
/// Purely cosmetic — nothing here affects combat. Owned by
/// <see cref="EnemyController"/>, which calls <see cref="Process"/> every frame.
/// </summary>
public partial class EnemyVisuals : Node3D
{
	private const float HealthBarWidth = 0.58f;
	private static readonly Dictionary<string, StandardMaterial3D> SharedMaterials = new();

	private readonly List<StandardMaterial3D> _bodyMaterials = new();
	private readonly List<Color> _bodyBaseColors = new();
	private Node3D _model = null!;
	private Node3D? _spinner;
	private MeshInstance3D? _spark;
	private Node3D? _stunStars;
	private MeshInstance3D? _eliteRing;
	private Node3D _healthBar = null!;
	private MeshInstance3D _healthFill = null!;
	private StandardMaterial3D _healthFillMaterial = null!;
	private float _bobPhase;
	private float _bobAmplitude = 0.025f;
	private float _bobHeight;
	private float _targetYaw;
	private double _time;
	private float _windup;
	private bool _enraged;
	private Tween? _flashTween;
	private Tween? _jiggleTween;

	public Color ClassColor { get; private set; } = Colors.White;
	public float ModelScale { get; private set; } = 1f;

	public static Color ColorFor(string behavior) => behavior switch
	{
		EnemyArchetypes.MeleeChaser => Color.FromHtml("#ef4444"),
		EnemyArchetypes.MeleeHitRun => Color.FromHtml("#fb923c"),
		EnemyArchetypes.RangedKite => Color.FromHtml("#a78bfa"),
		EnemyArchetypes.RangedStrafe => Color.FromHtml("#2dd4bf"),
		EnemyArchetypes.MeleeCharger => Color.FromHtml("#e879f9"),
		EnemyArchetypes.RangedBomber => Color.FromHtml("#a3e635"),
		EnemyArchetypes.MeleeShield => Color.FromHtml("#60a5fa"),
		_ => Color.FromHtml("#f43f5e")
	};

	public void Build(string behavior, bool elite, int seed)
	{
		ClassColor = ColorFor(behavior);
		_bobPhase = (seed % 997) * 0.37f;
		_model = new Node3D();
		AddChild(_model);

		switch (behavior)
		{
			case EnemyArchetypes.MeleeHitRun: BuildSkirmisher(); break;
			case EnemyArchetypes.RangedKite: BuildArcher(); break;
			case EnemyArchetypes.RangedStrafe: BuildSentinel(); break;
			case EnemyArchetypes.MeleeCharger: BuildCharger(); break;
			case EnemyArchetypes.RangedBomber: BuildBomber(); break;
			case EnemyArchetypes.MeleeShield: BuildShieldbearer(); break;
			default: BuildBrute(); break;
		}

		if (elite)
		{
			BuildEliteMarks();
		}

		BuildHealthBar(elite ? 1.25f : 1.05f);
		PlaySpawn();
	}

	// --- Models -------------------------------------------------------------

	private void BuildBrute()
	{
		Body(new BoxMesh { Size = new Vector3(0.5f, 0.42f, 0.38f) }, new Vector3(0, 0.42f, 0));
		Body(new BoxMesh { Size = new Vector3(0.15f, 0.22f, 0.17f) }, new Vector3(-0.13f, 0.11f, 0));
		Body(new BoxMesh { Size = new Vector3(0.15f, 0.22f, 0.17f) }, new Vector3(0.13f, 0.11f, 0));
		Body(new SphereMesh { Radius = 0.15f, Height = 0.3f }, new Vector3(0, 0.74f, -0.03f));
		Body(new SphereMesh { Radius = 0.1f, Height = 0.2f }, new Vector3(-0.32f, 0.36f, -0.08f));
		Body(new SphereMesh { Radius = 0.1f, Height = 0.2f }, new Vector3(0.32f, 0.36f, -0.08f));
		Detail(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.045f, Height = 0.18f }, new Vector3(-0.11f, 0.87f, 0), Shared("#fef3c7"), new Vector3(0, 0, 35));
		Detail(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.045f, Height = 0.18f }, new Vector3(0.11f, 0.87f, 0), Shared("#fef3c7"), new Vector3(0, 0, -35));
		Eyes(0.74f, -0.14f, 0.055f, "#fde047");
	}

	private void BuildSkirmisher()
	{
		Body(new CapsuleMesh { Radius = 0.15f, Height = 0.55f }, new Vector3(0, 0.33f, 0));
		Body(new SphereMesh { Radius = 0.12f, Height = 0.24f }, new Vector3(0, 0.7f, 0));
		Detail(new BoxMesh { Size = new Vector3(0.24f, 0.05f, 0.05f) }, new Vector3(0, 0.62f, -0.06f), Shared("#7c2d12"));
		Detail(new BoxMesh { Size = new Vector3(0.03f, 0.03f, 0.34f) }, new Vector3(-0.2f, 0.36f, -0.14f), Shared("#e5e7eb", 0.6f));
		Detail(new BoxMesh { Size = new Vector3(0.03f, 0.03f, 0.34f) }, new Vector3(0.2f, 0.36f, -0.14f), Shared("#e5e7eb", 0.6f));
		Eyes(0.72f, -0.11f, 0.045f, "#fef08a");
		_bobAmplitude = 0.035f;
	}

	private void BuildArcher()
	{
		Body(new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.27f, Height = 0.6f }, new Vector3(0, 0.3f, 0));
		Body(new SphereMesh { Radius = 0.15f, Height = 0.3f }, new Vector3(0, 0.68f, 0));
		Detail(new SphereMesh { Radius = 0.09f, Height = 0.18f }, new Vector3(0, 0.66f, -0.08f), Shared("#1e1b4b"));
		var bow = Detail(new TorusMesh { InnerRadius = 0.2f, OuterRadius = 0.225f }, new Vector3(0.22f, 0.45f, -0.12f), Shared("#facc15", 0.4f), new Vector3(90, 0, 0));
		bow.Scale = new Vector3(0.45f, 1f, 1f);
		Eyes(0.68f, -0.13f, 0.04f, "#c4b5fd");
	}

	private void BuildSentinel()
	{
		Detail(new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.16f, Height = 0.02f }, new Vector3(0, 0.01f, 0), Shared("#0b1020"));
		Body(new SphereMesh { Radius = 0.2f, Height = 0.4f }, new Vector3(0, 0.6f, 0));
		_spinner = new Node3D { Position = new Vector3(0, 0.6f, 0) };
		_model.AddChild(_spinner);
		var ring = new MeshInstance3D
		{
			Mesh = new TorusMesh { InnerRadius = 0.27f, OuterRadius = 0.31f },
			MaterialOverride = Shared("#99f6e4", 0.8f),
			RotationDegrees = new Vector3(70, 0, 0),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		_spinner.AddChild(ring);
		Detail(new SphereMesh { Radius = 0.07f, Height = 0.14f }, new Vector3(0, 0.6f, -0.18f), Shared("#ecfeff", 2.5f));
		_bobAmplitude = 0.06f;
		_bobHeight = 0.04f;
	}

	private void BuildCharger()
	{
		Body(new BoxMesh { Size = new Vector3(0.42f, 0.34f, 0.66f) }, new Vector3(0, 0.3f, 0.04f));
		Body(new BoxMesh { Size = new Vector3(0.34f, 0.28f, 0.2f) }, new Vector3(0, 0.33f, -0.36f));
		Detail(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.05f, Height = 0.3f }, new Vector3(-0.12f, 0.42f, -0.52f), Shared("#fdf4ff"), new Vector3(-90, 0, 0));
		Detail(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.05f, Height = 0.3f }, new Vector3(0.12f, 0.42f, -0.52f), Shared("#fdf4ff"), new Vector3(-90, 0, 0));
		for (int i = 0; i < 3; i++)
		{
			Detail(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.05f, Height = 0.14f }, new Vector3(0, 0.53f, 0.22f - (i * 0.18f)), Shared("#86198f"));
		}

		foreach (float x in new[] { -0.15f, 0.15f })
		{
			foreach (float z in new[] { -0.2f, 0.26f })
			{
				Body(new BoxMesh { Size = new Vector3(0.1f, 0.14f, 0.1f) }, new Vector3(x, 0.07f, z));
			}
		}

		Eyes(0.38f, -0.47f, 0.07f, "#fde047");
	}

	private void BuildBomber()
	{
		Body(new SphereMesh { Radius = 0.26f, Height = 0.52f }, new Vector3(0, 0.3f, 0));
		Detail(new TorusMesh { InnerRadius = 0.25f, OuterRadius = 0.285f }, new Vector3(0, 0.3f, 0), Shared("#3f6212"));
		Detail(new CylinderMesh { TopRadius = 0.025f, BottomRadius = 0.025f, Height = 0.16f }, new Vector3(0, 0.62f, 0), Shared("#1c1917"));
		_spark = Detail(new SphereMesh { Radius = 0.05f, Height = 0.1f }, new Vector3(0, 0.71f, 0), new StandardMaterial3D
		{
			AlbedoColor = Color.FromHtml("#fb923c"),
			EmissionEnabled = true,
			Emission = Color.FromHtml("#fb923c"),
			EmissionEnergyMultiplier = 3f
		});
		Body(new SphereMesh { Radius = 0.07f, Height = 0.14f }, new Vector3(-0.12f, 0.04f, 0));
		Body(new SphereMesh { Radius = 0.07f, Height = 0.14f }, new Vector3(0.12f, 0.04f, 0));
		Eyes(0.36f, -0.24f, 0.07f, "#fefce8");
	}

	private void BuildShieldbearer()
	{
		Body(new CapsuleMesh { Radius = 0.2f, Height = 0.7f }, new Vector3(0, 0.38f, 0.04f));
		Body(new SphereMesh { Radius = 0.13f, Height = 0.26f }, new Vector3(0, 0.82f, 0.04f));
		Detail(new CylinderMesh { TopRadius = 0.11f, BottomRadius = 0.15f, Height = 0.1f }, new Vector3(0, 0.9f, 0.04f), Shared("#cbd5e1"));
		Detail(new BoxMesh { Size = new Vector3(0.56f, 0.62f, 0.07f) }, new Vector3(0, 0.42f, -0.3f), Shared("#94a3b8", 0f, 0.6f));
		Detail(new BoxMesh { Size = new Vector3(0.08f, 0.44f, 0.02f) }, new Vector3(0, 0.44f, -0.345f), Shared("#fde68a", 0.5f));
		Detail(new BoxMesh { Size = new Vector3(0.36f, 0.08f, 0.02f) }, new Vector3(0, 0.52f, -0.345f), Shared("#fde68a", 0.5f));
	}

	private void BuildEliteMarks()
	{
		ModelScale = 1.2f;
		_model.Scale = Vector3.One * ModelScale;
		_eliteRing = new MeshInstance3D
		{
			Mesh = new TorusMesh { InnerRadius = 0.36f, OuterRadius = 0.42f },
			MaterialOverride = Shared("#facc15", 2.2f),
			Position = new Vector3(0, 0.03f, 0),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		AddChild(_eliteRing);

		var crown = new Node3D { Position = new Vector3(0, 1.02f, 0) };
		_model.AddChild(crown);
		crown.AddChild(new MeshInstance3D
		{
			Mesh = new TorusMesh { InnerRadius = 0.07f, OuterRadius = 0.1f },
			MaterialOverride = Shared("#facc15", 1.4f),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		});
		for (int i = 0; i < 3; i++)
		{
			float angle = i * Mathf.Tau / 3f;
			crown.AddChild(new MeshInstance3D
			{
				Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.03f, Height = 0.08f },
				MaterialOverride = Shared("#facc15", 1.4f),
				Position = new Vector3(Mathf.Cos(angle) * 0.085f, 0.05f, Mathf.Sin(angle) * 0.085f),
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
			});
		}
	}

	private void BuildHealthBar(float height)
	{
		_healthBar = new Node3D { Position = new Vector3(0, height, 0), Visible = false };
		AddChild(_healthBar);
		_healthBar.AddChild(new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(0.62f, 0.09f) },
			MaterialOverride = BillboardMaterial(new Color(0.03f, 0.03f, 0.08f, 0.85f)),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		});
		_healthFillMaterial = BillboardMaterial(Color.FromHtml("#4ade80"));
		_healthFillMaterial.RenderPriority = 1;
		_healthFill = new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(HealthBarWidth, 0.06f) },
			MaterialOverride = _healthFillMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		_healthBar.AddChild(_healthFill);
	}

	// --- Per-frame animation --------------------------------------------------

	public void Process(double delta)
	{
		_time += delta;
		float bob = _bobHeight + (_bobAmplitude * Mathf.Sin((float)(_time * 3.2) + _bobPhase));
		_model.Position = new Vector3(_model.Position.X, bob, _model.Position.Z);

		float yaw = Mathf.LerpAngle(_model.Rotation.Y, _targetYaw, Mathf.Clamp((float)delta * 12f, 0f, 1f));
		_model.Rotation = new Vector3(0, yaw, 0);

		if (_spinner != null)
		{
			_spinner.RotateY((float)delta * 2.4f);
		}

		if (_spark?.MaterialOverride is StandardMaterial3D spark)
		{
			spark.EmissionEnergyMultiplier = 2f + (2.5f * _windup) + (0.8f * Mathf.Sin((float)(_time * 23.0)));
		}

		if (_eliteRing != null)
		{
			_eliteRing.Scale = Vector3.One * (1f + (0.06f * Mathf.Sin((float)(_time * 4.0))));
		}

		_stunStars?.RotateY((float)delta * 6f);

		float glow = Mathf.Max(_windup * 2.4f, _enraged ? 0.5f + (0.4f * Mathf.Sin((float)(_time * 9.0))) : 0f);
		for (int i = 0; i < _bodyMaterials.Count; i++)
		{
			StandardMaterial3D material = _bodyMaterials[i];
			material.EmissionEnabled = glow > 0.01f;
			material.Emission = _enraged ? Color.FromHtml("#ef4444") : ClassColor;
			material.EmissionEnergyMultiplier = glow;
		}

		float squash = _windup * 0.12f;
		_model.Scale = new Vector3(ModelScale * (1f + squash), ModelScale * (1f - squash), ModelScale * (1f + squash));
	}

	/// <summary>Turn the model toward a grid direction (smoothly, in <see cref="Process"/>).</summary>
	public void FaceDirection(Vector2I direction)
	{
		if (direction == Vector2I.Zero)
		{
			return;
		}

		_targetYaw = Mathf.Atan2(-direction.X, -direction.Y);
	}

	/// <summary>Wind-up progress in [0, 1]; 0 when not winding up.</summary>
	public void SetWindup(float progress) => _windup = Mathf.Clamp(progress, 0f, 1f);

	public void SetEnraged(bool enraged) => _enraged = enraged;

	public void SetHealth(int current, int max)
	{
		float ratio = max > 0 ? Mathf.Clamp((float)current / max, 0f, 1f) : 0f;
		_healthBar.Visible = ratio < 0.999f && ratio > 0f;
		// Billboarded quads ignore the node's scale, so the fill is resized
		// in mesh space and kept left-aligned with the mesh's centre offset.
		var fill = (QuadMesh)_healthFill.Mesh;
		fill.Size = new Vector2(Mathf.Max(ratio, 0.001f) * HealthBarWidth, 0.06f);
		fill.CenterOffset = new Vector3(-(1f - ratio) * HealthBarWidth * 0.5f, 0, 0);
		_healthFillMaterial.AlbedoColor = Color.FromHtml("#ef4444").Lerp(Color.FromHtml("#4ade80"), ratio);
	}

	public void PlayHit(Vector3 awayFromAttacker, bool blocked)
	{
		_flashTween?.Kill();
		Color flash = blocked ? Color.FromHtml("#bfdbfe") : Colors.White;
		for (int i = 0; i < _bodyMaterials.Count; i++)
		{
			_bodyMaterials[i].AlbedoColor = flash;
		}

		_flashTween = CreateTween().SetParallel();
		for (int i = 0; i < _bodyMaterials.Count; i++)
		{
			_flashTween.TweenProperty(_bodyMaterials[i], "albedo_color", _bodyBaseColors[i], 0.18).SetDelay(0.04);
		}

		_jiggleTween?.Kill();
		Vector3 push = awayFromAttacker.LengthSquared() > 0.0001f ? awayFromAttacker.Normalized() * (blocked ? 0.05f : 0.14f) : Vector3.Zero;
		_jiggleTween = CreateTween();
		_jiggleTween.TweenProperty(this, "position", push, 0.05).SetTrans(Tween.TransitionType.Sine);
		_jiggleTween.TweenProperty(this, "position", Vector3.Zero, 0.14).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
	}

	public void PlayLunge(Vector3 direction, float distance)
	{
		Vector3 offset = direction.LengthSquared() > 0.0001f ? direction.Normalized() * distance : Vector3.Zero;
		Tween tween = CreateTween();
		tween.TweenProperty(this, "position", offset, 0.07).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(this, "position", Vector3.Zero, 0.13).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
	}

	public void SetStunned(bool stunned)
	{
		if (stunned && _stunStars == null)
		{
			_stunStars = new Node3D { Position = new Vector3(0, 0.95f * ModelScale, 0) };
			AddChild(_stunStars);
			for (int i = 0; i < 3; i++)
			{
				float angle = i * Mathf.Tau / 3f;
				_stunStars.AddChild(new MeshInstance3D
				{
					Mesh = new SphereMesh { Radius = 0.045f, Height = 0.09f },
					MaterialOverride = Shared("#fde047", 2f),
					Position = new Vector3(Mathf.Cos(angle) * 0.18f, 0, Mathf.Sin(angle) * 0.18f),
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
				});
			}
		}
		else if (!stunned && _stunStars != null)
		{
			_stunStars.QueueFree();
			_stunStars = null;
		}
	}

	private void PlaySpawn()
	{
		Position = new Vector3(0, -0.4f, 0);
		Scale = new Vector3(0.2f, 0.2f, 0.2f);
		Tween tween = CreateTween().SetParallel();
		tween.TweenProperty(this, "scale", Vector3.One, 0.3).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(this, "position:y", 0f, 0.3).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
	}

	/// <summary>Topples, sinks and shrinks; the caller frees the enemy after <paramref name="seconds"/>.</summary>
	public void PlayDeath(double seconds)
	{
		_healthBar.Visible = false;
		SetStunned(false);
		Tween tween = CreateTween().SetParallel();
		tween.TweenProperty(_model, "rotation_degrees:x", 80f, seconds * 0.6).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.TweenProperty(this, "position:y", -0.3f, seconds).SetDelay(seconds * 0.3);
		tween.TweenProperty(this, "scale", Vector3.One * 0.05f, seconds * 0.5).SetDelay(seconds * 0.5);

		var burst = new CpuParticles3D
		{
			Emitting = true,
			OneShot = true,
			Amount = 14,
			Lifetime = 0.45,
			Explosiveness = 1f,
			Direction = Vector3.Up,
			Spread = 70f,
			InitialVelocityMin = 1.2f,
			InitialVelocityMax = 2.4f,
			Gravity = new Vector3(0, -6f, 0),
			ScaleAmountMin = 0.6f,
			ScaleAmountMax = 1.1f,
			Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.07f, 0.07f) },
			Position = new Vector3(0, 0.4f, 0)
		};
		burst.MaterialOverride = Shared(ClassColor.ToHtml(false), 1.2f);
		AddChild(burst);
	}

	// --- Helpers ----------------------------------------------------------------

	private MeshInstance3D Body(Mesh mesh, Vector3 position)
	{
		var material = new StandardMaterial3D { AlbedoColor = ClassColor, Metallic = 0.15f, Roughness = 0.45f };
		_bodyMaterials.Add(material);
		_bodyBaseColors.Add(ClassColor);
		var instance = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = position };
		_model.AddChild(instance);
		return instance;
	}

	private MeshInstance3D Detail(Mesh mesh, Vector3 position, StandardMaterial3D material, Vector3? rotationDegrees = null)
	{
		var instance = new MeshInstance3D
		{
			Mesh = mesh,
			MaterialOverride = material,
			Position = position,
			RotationDegrees = rotationDegrees ?? Vector3.Zero,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		_model.AddChild(instance);
		return instance;
	}

	private void Eyes(float height, float forward, float spacing, string color)
	{
		StandardMaterial3D material = Shared(color, 2.2f);
		foreach (float x in new[] { -spacing, spacing })
		{
			Detail(new SphereMesh { Radius = 0.026f, Height = 0.052f }, new Vector3(x, height, forward), material);
		}
	}

	internal static StandardMaterial3D Shared(string color, float emission = 0f, float metallic = 0.1f)
	{
		string key = $"{color}|{emission}|{metallic}";
		if (!SharedMaterials.TryGetValue(key, out StandardMaterial3D? material))
		{
			Color albedo = Color.FromHtml(color);
			material = new StandardMaterial3D
			{
				AlbedoColor = albedo,
				Metallic = metallic,
				Roughness = 0.4f,
				EmissionEnabled = emission > 0f,
				Emission = albedo,
				EmissionEnergyMultiplier = emission
			};
			SharedMaterials[key] = material;
		}

		return material;
	}

	private static StandardMaterial3D BillboardMaterial(Color color) => new()
	{
		AlbedoColor = color,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
		NoDepthTest = true
	};
}
