using Godot;
using DungeonCrawler.Core;

namespace DungeonCrawler.UI;

/// <summary>
/// Standalone overlay HUD: title bar, controls legend, session info,
/// pause menu, and reactive feedback. Registered as an autoload so it
/// works on top of any scene without modifying that scene's own script
/// or node tree. Reacts to raw Input polling only — never reads state
/// from other scripts, so it stays independent of gameplay code.
/// </summary>
public partial class GameHud : CanvasLayer
{
	private static readonly Color PanelBg = new Color(0.043f, 0.059f, 0.102f, 0.82f);
	private static readonly Color AccentCyan = Color.FromHtml("#4cc9f0");
	private static readonly Color AccentAmber = Color.FromHtml("#f59e0b");
	private static readonly Color TextPrimary = Color.FromHtml("#e2e8f0");
	private static readonly Color TextMuted = Color.FromHtml("#8b96ad");

	private Control _pauseLayer = null!;
	private PanelContainer _pausePanel = null!;
	private Label _timeLabel = null!;
	private Label _fpsLabel = null!;
	private Label _hpLabel = null!;
	private ProgressBar _hpBar = null!;
	private Label _scoreLabel = null!;
	private PanelContainer _toastPanel = null!;
	private Label _toastLabel = null!;
	private ColorRect _damageFlash = null!;

	private bool _isPaused;
	private bool _rWasDown;
	private double _elapsedSeconds;
	private int _lastKnownHp = -1;

	public override void _Ready()
	{
		Layer = 100;
		ProcessMode = ProcessModeEnum.Always;

		BuildVignette();
		BuildDamageFlash();
		BuildTopBar();
		BuildControlsLegend();
		BuildInfoPanel();
		BuildToast();
		BuildPauseMenu();
	}

	public override void _Process(double delta)
	{
		if (Input.IsActionJustPressed("ui_cancel"))
		{
			SetPaused(!_isPaused);
		}

		bool rDown = Input.IsKeyPressed(Key.R);
		if (rDown && !_rWasDown && !_isPaused)
		{
			ShowToast("REGENERATING DUNGEON...");
		}
		_rWasDown = rDown;

		if (!_isPaused)
		{
			_elapsedSeconds += delta;
		}

		UpdateInfoPanel();
	}

	private void SetPaused(bool paused)
	{
		_isPaused = paused;
		GetTree().Paused = paused;
		_pauseLayer.Visible = paused;

		if (paused)
		{
			Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
			Vector2 panelSize = _pausePanel.Size;
			Vector2 centerPos = (viewportSize - panelSize) / 2f;

			_pausePanel.Modulate = new Color(1, 1, 1, 0);
			_pausePanel.Position = new Vector2(centerPos.X, centerPos.Y - 16);

			Tween tween = CreateTween();
			tween.SetPauseMode(Tween.TweenPauseMode.Process);
			tween.SetParallel(true);
			tween.TweenProperty(_pausePanel, "modulate:a", 1.0, 0.18).SetEase(Tween.EaseType.Out);
			tween.TweenProperty(_pausePanel, "position:y", centerPos.Y, 0.18).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
		}
	}

	private static StyleBoxFlat MakePanelStyle(Color bg, Color borderColor, float borderWidth = 1.5f, float corner = 10f)
	{
		var style = new StyleBoxFlat
		{
			BgColor = bg,
			BorderColor = borderColor,
			BorderWidthTop = (int)borderWidth,
			BorderWidthBottom = (int)borderWidth,
			BorderWidthLeft = (int)borderWidth,
			BorderWidthRight = (int)borderWidth,
			CornerRadiusTopLeft = (int)corner,
			CornerRadiusTopRight = (int)corner,
			CornerRadiusBottomLeft = (int)corner,
			CornerRadiusBottomRight = (int)corner,
			ContentMarginLeft = 14,
			ContentMarginRight = 14,
			ContentMarginTop = 10,
			ContentMarginBottom = 10
		};
		return style;
	}

	private void BuildVignette()
	{
		var shader = new Shader
		{
			Code = """
shader_type canvas_item;

void fragment() {
	vec2 uv = UV - vec2(0.5);
	float dist = length(uv * vec2(1.15, 1.4));
	float vig = smoothstep(0.35, 0.95, dist);
	COLOR = vec4(0.0, 0.0, 0.01, vig * 0.5);
}
"""
		};

		var vignette = new ColorRect
		{
			Color = Colors.White,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Material = new ShaderMaterial { Shader = shader }
		};
		vignette.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(vignette);
	}

	private void BuildTopBar()
	{
		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide, Control.LayoutPresetMode.KeepSize, 0);
		panel.OffsetTop = 14;
		panel.OffsetLeft = 20;
		panel.OffsetRight = -220;
		panel.AddThemeStyleboxOverride("panel", MakePanelStyle(PanelBg, new Color(AccentCyan, 0.35f)));
		AddChild(panel);

		var vbox = new VBoxContainer();
		panel.AddChild(vbox);

		var title = new Label
		{
			Text = "CLOSED-LOOP LLM PCG DUNGEON CRAWLER",
			HorizontalAlignment = HorizontalAlignment.Left
		};
		title.AddThemeColorOverride("font_color", TextPrimary);
		title.AddThemeFontSizeOverride("font_size", 18);
		vbox.AddChild(title);

		var subtitle = new Label
		{
			Text = "prototype build — press ESC to pause",
			HorizontalAlignment = HorizontalAlignment.Left
		};
		subtitle.AddThemeColorOverride("font_color", TextMuted);
		subtitle.AddThemeFontSizeOverride("font_size", 12);
		vbox.AddChild(subtitle);
	}

	private void BuildInfoPanel()
	{
		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", MakePanelStyle(PanelBg, new Color(AccentAmber, 0.3f)));
		AddChild(panel);

		var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(110, 0) };
		panel.AddChild(vbox);

		_hpLabel = new Label { Text = "HP --/--", HorizontalAlignment = HorizontalAlignment.Right };
		_hpLabel.AddThemeColorOverride("font_color", Color.FromHtml("#f43f5e"));
		_hpLabel.AddThemeFontSizeOverride("font_size", 13);
		vbox.AddChild(_hpLabel);

		_hpBar = new ProgressBar
		{
			MinValue = 0,
			MaxValue = 100,
			Value = 100,
			ShowPercentage = false,
			CustomMinimumSize = new Vector2(0, 10)
		};
		var hpBarBg = new StyleBoxFlat { BgColor = new Color(0.15f, 0.03f, 0.03f), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
		var hpBarFill = new StyleBoxFlat { BgColor = Color.FromHtml("#f43f5e"), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
		_hpBar.AddThemeStyleboxOverride("background", hpBarBg);
		_hpBar.AddThemeStyleboxOverride("fill", hpBarFill);
		vbox.AddChild(_hpBar);

		_scoreLabel = new Label { Text = "SCORE 0", HorizontalAlignment = HorizontalAlignment.Right };
		_scoreLabel.AddThemeColorOverride("font_color", AccentAmber);
		_scoreLabel.AddThemeFontSizeOverride("font_size", 13);
		vbox.AddChild(_scoreLabel);

		_timeLabel = new Label { Text = "TIME 00:00", HorizontalAlignment = HorizontalAlignment.Right };
		_timeLabel.AddThemeColorOverride("font_color", TextPrimary);
		_timeLabel.AddThemeFontSizeOverride("font_size", 13);
		vbox.AddChild(_timeLabel);

		_fpsLabel = new Label { Text = "FPS 0", HorizontalAlignment = HorizontalAlignment.Right };
		_fpsLabel.AddThemeColorOverride("font_color", TextMuted);
		_fpsLabel.AddThemeFontSizeOverride("font_size", 12);
		vbox.AddChild(_fpsLabel);

		Vector2 size = panel.GetCombinedMinimumSize();
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		panel.Position = new Vector2(viewportSize.X - 20 - size.X, 14);
	}

	private void UpdateInfoPanel()
	{
		int totalSeconds = (int)_elapsedSeconds;
		int minutes = totalSeconds / 60;
		int seconds = totalSeconds % 60;
		_timeLabel.Text = $"TIME {minutes:D2}:{seconds:D2}";
		_fpsLabel.Text = $"FPS {Engine.GetFramesPerSecond():F0}";

		if (GetTree().CurrentScene is GameManager gameManager)
		{
			int currentHp = gameManager.PlayerCurrentHp;
			int maxHp = gameManager.PlayerMaxHp;

			_hpLabel.Text = $"HP {currentHp}/{maxHp}";
			_hpBar.MaxValue = maxHp;
			_hpBar.Value = currentHp;
			_scoreLabel.Text = $"SCORE {gameManager.Score}";
			_hpLabel.Visible = true;
			_hpBar.Visible = true;
			_scoreLabel.Visible = true;

			if (_lastKnownHp >= 0 && currentHp < _lastKnownHp)
			{
				TriggerDamageFlash();
			}
			_lastKnownHp = currentHp;
		}
		else
		{
			_hpLabel.Visible = false;
			_hpBar.Visible = false;
			_scoreLabel.Visible = false;
			_lastKnownHp = -1;
		}
	}

	private void BuildDamageFlash()
	{
		_damageFlash = new ColorRect
		{
			Color = new Color(0.9f, 0.05f, 0.05f, 0.0f),
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_damageFlash.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(_damageFlash);
	}

	private void TriggerDamageFlash()
	{
		_damageFlash.Color = new Color(0.9f, 0.05f, 0.05f, 0.35f);
		Tween tween = CreateTween();
		tween.TweenProperty(_damageFlash, "color:a", 0.0f, 0.3);
	}

	private void BuildToast()
	{
		_toastPanel = new PanelContainer { Modulate = new Color(1, 1, 1, 0) };
		_toastPanel.AddThemeStyleboxOverride("panel", MakePanelStyle(new Color(0.043f, 0.059f, 0.102f, 0.92f), new Color(AccentAmber, 0.55f), 1.5f, 8f));
		AddChild(_toastPanel);

		_toastLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(260, 0) };
		_toastLabel.AddThemeColorOverride("font_color", AccentAmber);
		_toastLabel.AddThemeFontSizeOverride("font_size", 14);
		_toastPanel.AddChild(_toastLabel);
	}

	private void ShowToast(string text)
	{
		_toastLabel.Text = text;

		Vector2 size = _toastPanel.GetCombinedMinimumSize();
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		_toastPanel.Position = new Vector2((viewportSize.X - size.X) / 2f, 90);

		Tween tween = CreateTween();
		tween.TweenProperty(_toastPanel, "modulate:a", 1.0, 0.12);
		tween.TweenInterval(0.9);
		tween.TweenProperty(_toastPanel, "modulate:a", 0.0, 0.35);
	}

	private void AddLegendRow(VBoxContainer parent, string key, string description)
	{
		var row = new HBoxContainer();
		parent.AddChild(row);

		var keyBadge = new PanelContainer { CustomMinimumSize = new Vector2(52, 0) };
		keyBadge.AddThemeStyleboxOverride("panel", MakePanelStyle(new Color(AccentCyan, 0.16f), new Color(AccentCyan, 0.45f), 1f, 6f));
		var keyLabel = new Label
		{
			Text = key,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		keyLabel.AddThemeColorOverride("font_color", AccentCyan);
		keyLabel.AddThemeFontSizeOverride("font_size", 13);
		keyBadge.AddChild(keyLabel);
		row.AddChild(keyBadge);

		var descLabel = new Label
		{
			Text = description,
			VerticalAlignment = VerticalAlignment.Center
		};
		descLabel.AddThemeColorOverride("font_color", TextPrimary);
		descLabel.AddThemeFontSizeOverride("font_size", 13);
		row.AddChild(descLabel);
	}

	private void BuildControlsLegend()
	{
		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", MakePanelStyle(PanelBg, new Color(AccentAmber, 0.3f)));
		AddChild(panel);

		var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(190, 0) };
		panel.AddChild(vbox);

		AddLegendRow(vbox, "WASD", "Move");
		AddLegendRow(vbox, "SPACE", "Attack");
		AddLegendRow(vbox, "R", "Regenerate dungeon");
		AddLegendRow(vbox, "ESC", "Pause");

		Vector2 size = panel.GetCombinedMinimumSize();
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		panel.Position = new Vector2(20, viewportSize.Y - 18 - size.Y);
	}

	private void BuildPauseMenu()
	{
		_pauseLayer = new Control { Visible = false, ProcessMode = ProcessModeEnum.Always };
		_pauseLayer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(_pauseLayer);

		var dim = new ColorRect { Color = new Color(0.01f, 0.015f, 0.03f, 0.72f) };
		dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_pauseLayer.AddChild(dim);

		_pausePanel = new PanelContainer { CustomMinimumSize = new Vector2(280, 0), ProcessMode = ProcessModeEnum.Always };
		_pausePanel.AddThemeStyleboxOverride("panel", MakePanelStyle(new Color(0.043f, 0.059f, 0.102f, 0.97f), new Color(AccentCyan, 0.5f), 2f, 14f));
		_pauseLayer.AddChild(_pausePanel);

		var vbox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		vbox.AddThemeConstantOverride("separation", 14);
		_pausePanel.AddChild(vbox);

		var title = new Label { Text = "PAUSED", HorizontalAlignment = HorizontalAlignment.Center };
		title.AddThemeColorOverride("font_color", TextPrimary);
		title.AddThemeFontSizeOverride("font_size", 22);
		vbox.AddChild(title);

		var resumeButton = new Button { Text = "Resume", ProcessMode = ProcessModeEnum.Always };
		resumeButton.Pressed += () => SetPaused(false);
		vbox.AddChild(resumeButton);

		var quitButton = new Button { Text = "Quit", ProcessMode = ProcessModeEnum.Always };
		quitButton.Pressed += () => GetTree().Quit();
		vbox.AddChild(quitButton);
	}
}
