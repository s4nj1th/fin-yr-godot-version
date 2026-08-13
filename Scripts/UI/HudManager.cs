using Godot;
using DungeonCrawler.Data;

namespace DungeonCrawler.UI;

public partial class HudManager : CanvasLayer
{
	[Signal] public delegate void AdaptivePromptSubmittedEventHandler(string userPrompt);

	private Label? _hudLevelLabel;
	private Label? _hudHpLabel;
	private ProgressBar? _hudHpBar;
	private Label? _hudTimeLabel;
	private Label? _hudScoreLabel;
	private Label? _hudThemeLabel;
	private Panel? _narrativePanel;
	private Label? _narrativeTitle;
	private Label? _narrativeLore;
	private Label? _playstyleText;
	private LineEdit? _promptInput;
	private Button? _continueButton;

	public override void _Ready()
	{
		BuildUiHierarchy();
	}

	private void BuildUiHierarchy()
	{
		// Top Header HUD Panel
		var topPanel = new PanelContainer();
		topPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide, Control.LayoutPresetMode.KeepSize, 10);
		AddChild(topPanel);

		var topHBox = new HBoxContainer();
		topPanel.AddChild(topHBox);

		_hudLevelLabel = new Label { Text = "Level 1" };
		_hudThemeLabel = new Label { Text = "Theme: Dark Obsidian" };
		_hudHpLabel = new Label { Text = "HP: 100/100" };
		_hudHpBar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 100, CustomMinimumSize = new Vector2(150, 20) };
		_hudTimeLabel = new Label { Text = "Time: 0.0s" };
		_hudScoreLabel = new Label { Text = "Score: 0" };

		topHBox.AddChild(_hudLevelLabel);
		topHBox.AddChild(new VSeparator());
		topHBox.AddChild(_hudThemeLabel);
		topHBox.AddChild(new VSeparator());
		topHBox.AddChild(_hudHpLabel);
		topHBox.AddChild(_hudHpBar);
		topHBox.AddChild(new VSeparator());
		topHBox.AddChild(_hudTimeLabel);
		topHBox.AddChild(new VSeparator());
		topHBox.AddChild(_hudScoreLabel);

		// LLM Narrative Popup Modal (Hidden by default)
		_narrativePanel = new Panel { Visible = false, CustomMinimumSize = new Vector2(500, 320) };
		_narrativePanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
		AddChild(_narrativePanel);

		var vBox = new VBoxContainer();
		vBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 15);
		_narrativePanel.AddChild(vBox);

		_narrativeTitle = new Label { Text = "Act II: The Corrupted Abyss", HorizontalAlignment = HorizontalAlignment.Center };
		_narrativeLore = new Label { Text = "Dark void magic erodes the sanctuary pillars...", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_playstyleText = new Label { Text = "Playstyle Detected: Blitz Krieger", HorizontalAlignment = HorizontalAlignment.Center };
		
		_promptInput = new LineEdit { PlaceholderText = "Enter preference for next floor (e.g. 'Make it harder')..." };
		_continueButton = new Button { Text = "Proceed to Next Level" };
		_continueButton.Pressed += OnContinuePressed;

		vBox.AddChild(_narrativeTitle);
		vBox.AddChild(new HSeparator());
		vBox.AddChild(_playstyleText);
		vBox.AddChild(_narrativeLore);
		vBox.AddChild(new HSeparator());
		vBox.AddChild(_promptInput);
		vBox.AddChild(_continueButton);
	}

	public void UpdateStats(int level, string themeName, int currentHp, int maxHp, int score, double timeSec)
	{
		if (_hudLevelLabel != null) _hudLevelLabel.Text = $"Level {level}";
		if (_hudThemeLabel != null) _hudThemeLabel.Text = $"Theme: {themeName}";
		if (_hudHpLabel != null) _hudHpLabel.Text = $"HP: {currentHp}/{maxHp}";
		if (_hudHpBar != null)
		{
			_hudHpBar.MaxValue = maxHp;
			_hudHpBar.Value = currentHp;
		}
		if (_hudScoreLabel != null) _hudScoreLabel.Text = $"Score: {score}";
		if (_hudTimeLabel != null) _hudTimeLabel.Text = $"Time: {timeSec:F1}s";
	}

	public void ShowAdaptationModal(LLMAdaptationResponse adaptation)
	{
		if (_narrativePanel == null) return;

		if (_narrativeTitle != null) _narrativeTitle.Text = adaptation.NarrativeIsomorphism.Title;
		if (_narrativeLore != null) _narrativeLore.Text = adaptation.NarrativeIsomorphism.Lore;
		if (_playstyleText != null) _playstyleText.Text = $"LLM Analysis: {adaptation.PlaystyleAssessment}";

		_narrativePanel.Visible = true;
	}

	private void OnContinuePressed()
	{
		string prompt = _promptInput?.Text ?? "";
		if (_promptInput != null) _promptInput.Text = "";
		if (_narrativePanel != null) _narrativePanel.Visible = false;

		EmitSignal(SignalName.AdaptivePromptSubmitted, prompt);
	}
}
