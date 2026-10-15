using System.Collections.Generic;
using Godot;

namespace DungeonCrawler.Entities;

/// <summary>
/// Floor markings that warn the player of a telegraphed attack (the
/// charger's dash line and the bomber's blast area), plus the one-shot
/// effects of those attacks landing. A marking fills up as the wind-up
/// runs out, so the player can see how long they have to step out of it.
/// Lives in world space (parented to the enemy root), not on the enemy, so
/// it stays put while the enemy moves.
/// </summary>
public partial class TelegraphFx : Node3D
{
	// Just above the tallest floor tile (special tiles top out at y = 0.43).
	private const float FloorY = 0.45f;

	private readonly List<(MeshInstance3D Fill, StandardMaterial3D Material)> _fills = new();
	private readonly List<StandardMaterial3D> _outlines = new();
	private double _time;

	public static TelegraphFx Create(Node parent, IEnumerable<Vector3> tileCentres, Color color)
	{
		var fx = new TelegraphFx();
		parent.AddChild(fx);
		foreach (Vector3 centre in tileCentres)
		{
			fx.AddTile(centre, color);
		}

		return fx;
	}

	private void AddTile(Vector3 centre, Color color)
	{
		var outline = new StandardMaterial3D
		{
			AlbedoColor = new Color(color, 0.28f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled
		};
		_outlines.Add(outline);
		AddChild(new MeshInstance3D
		{
			Mesh = new PlaneMesh { Size = new Vector2(0.94f, 0.94f) },
			MaterialOverride = outline,
			Position = new Vector3(centre.X, FloorY, centre.Z),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		});

		var fillMaterial = new StandardMaterial3D
		{
			AlbedoColor = new Color(color, 0.55f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			RenderPriority = 1
		};
		var fill = new MeshInstance3D
		{
			Mesh = new PlaneMesh { Size = new Vector2(0.94f, 0.94f) },
			MaterialOverride = fillMaterial,
			Position = new Vector3(centre.X, FloorY + 0.005f, centre.Z),
			Scale = new Vector3(0.05f, 1, 0.05f),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		AddChild(fill);
		_fills.Add((fill, fillMaterial));
	}

	/// <summary>Wind-up progress in [0, 1]: the inner square grows to fill the tile.</summary>
	public void SetProgress(float progress, double delta)
	{
		_time += delta;
		float p = Mathf.Clamp(progress, 0f, 1f);
		float pulse = 0.22f + (0.12f * Mathf.Sin((float)(_time * (8.0 + (14.0 * p)))));
		foreach (StandardMaterial3D outline in _outlines)
		{
			outline.AlbedoColor = new Color(outline.AlbedoColor, pulse);
		}

		foreach ((MeshInstance3D fill, StandardMaterial3D material) in _fills)
		{
			fill.Scale = new Vector3(Mathf.Max(0.05f, p), 1, Mathf.Max(0.05f, p));
			material.AlbedoColor = new Color(material.AlbedoColor, 0.35f + (0.4f * p));
		}
	}

	/// <summary>A quick expanding flash over the marked tiles, then frees the marking.</summary>
	public void Release()
	{
		Tween tween = CreateTween().SetParallel();
		foreach ((MeshInstance3D fill, StandardMaterial3D material) in _fills)
		{
			fill.Scale = Vector3.One;
			material.AlbedoColor = new Color(1f, 0.95f, 0.85f, 0.9f);
			tween.TweenProperty(material, "albedo_color:a", 0f, 0.25);
		}

		foreach (StandardMaterial3D outline in _outlines)
		{
			tween.TweenProperty(outline, "albedo_color:a", 0f, 0.2);
		}

		tween.Chain().TweenCallback(Callable.From(QueueFree));
	}

	/// <summary>An expanding, fading sphere — the bomber's explosion.</summary>
	public static void Explosion(Node parent, Vector3 centre, float radius, Color color)
	{
		var material = new StandardMaterial3D
		{
			AlbedoColor = new Color(color, 0.75f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha
		};
		var sphere = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = 0.5f, Height = 1f },
			MaterialOverride = material,
			Position = new Vector3(centre.X, FloorY + 0.2f, centre.Z),
			Scale = Vector3.One * 0.3f,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		parent.AddChild(sphere);
		Tween tween = sphere.CreateTween().SetParallel();
		tween.TweenProperty(sphere, "scale", Vector3.One * (radius * 2f + 1f), 0.22).SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(material, "albedo_color:a", 0f, 0.3);
		tween.Chain().TweenCallback(Callable.From(sphere.QueueFree));
	}

	/// <summary>A floating number that rises and fades (damage dealt, or "BLOCK").</summary>
	public static void FloatingText(Node parent, Vector3 position, string text, Color color)
	{
		var label = new Label3D
		{
			Text = text,
			FontSize = 44,
			OutlineSize = 10,
			Modulate = color,
			OutlineModulate = new Color(0.02f, 0.02f, 0.06f),
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			NoDepthTest = true,
			PixelSize = 0.006f,
			Position = position + new Vector3(0, 1.0f, 0)
		};
		parent.AddChild(label);
		Tween tween = label.CreateTween().SetParallel();
		tween.TweenProperty(label, "position:y", position.Y + 1.6f, 0.6).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(label, "modulate:a", 0f, 0.6).SetDelay(0.15);
		tween.TweenProperty(label, "outline_modulate:a", 0f, 0.6).SetDelay(0.15);
		tween.Chain().TweenCallback(Callable.From(label.QueueFree));
	}
}
