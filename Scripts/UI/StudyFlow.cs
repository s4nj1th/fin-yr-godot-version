using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using DungeonCrawler.AI;
using DungeonCrawler.Data;
using DungeonCrawler.Entities;

namespace DungeonCrawler.UI;

/// <summary>
/// The screens around a human-study session (docs/HUMAN_STUDY_PROTOCOL.md):
/// participant id and session number, the information sheet and consent,
/// a briefing on the controls and the enemies, then — after play — the
/// questionnaire and a closing screen. The session itself is run by
/// GameManager (Scripts/Core/GameManager.Study.cs).
///
/// For an unattended end-to-end test, `--study-auto=P01:1` fills in the
/// participant, consents, skips the briefing and answers the questionnaire
/// with the scale midpoint.
/// </summary>
public partial class StudyFlow : CanvasLayer
{
	public event Action<string, int>? SessionRequested;
	public event Action<Dictionary<string, int>, string>? QuestionnaireSubmitted;

	private static readonly Color PanelBg = new(0.043f, 0.059f, 0.102f, 0.97f);
	private static readonly Color Accent = Color.FromHtml("#4cc9f0");
	private static readonly Color TextPrimary = Color.FromHtml("#e2e8f0");
	private static readonly Color TextMuted = Color.FromHtml("#94a3b8");

	private const string InformationSheetPath = "res://study/participant_information.txt";

	private Control _root = null!;
	private PanelContainer _panel = null!;
	private VBoxContainer _content = null!;
	private string? _autoParticipant;
	private int _autoSession;

	public bool IsAutomatic => _autoParticipant != null;

	public override void _Ready()
	{
		Layer = 120;
		ProcessMode = ProcessModeEnum.Always;
		foreach (string arg in OS.GetCmdlineUserArgs())
		{
			if (arg.StartsWith("--study-auto=", StringComparison.Ordinal))
			{
				string[] parts = arg.Substring("--study-auto=".Length).Split(':');
				_autoParticipant = parts[0];
				_autoSession = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 1;
			}
		}

		_root = new Control();
		_root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(_root);
		var dim = new ColorRect { Color = new Color(0.01f, 0.012f, 0.03f, 0.94f) };
		dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_root.AddChild(dim);

		var center = new CenterContainer();
		center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_root.AddChild(center);
		_panel = new PanelContainer { CustomMinimumSize = new Vector2(720, 0) };
		_panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
		{
			BgColor = PanelBg, BorderColor = new Color(Accent, 0.5f),
			BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
			CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
			ContentMarginLeft = 28, ContentMarginRight = 28, ContentMarginTop = 22, ContentMarginBottom = 22
		});
		center.AddChild(_panel);
		_content = new VBoxContainer();
		_content.AddThemeConstantOverride("separation", 14);
		_panel.AddChild(_content);

		if (IsAutomatic)
		{
			GD.Print($"[Study] Automatic run as {_autoParticipant}, session {_autoSession}.");
			Hide();
			CallDeferred(nameof(EmitAutomaticSession));
			return;
		}

		ShowSetup();
	}

	private void EmitAutomaticSession() => SessionRequested?.Invoke(_autoParticipant!, _autoSession);

	// --- Screens -------------------------------------------------------------

	private void ShowSetup(string? error = null)
	{
		Clear();
		Title("Study session");
		Text("Researcher: enter the participant's pseudonymous id (for example P07) and the session number. " +
			"Do not enter a name.");
		var id = new LineEdit { PlaceholderText = "P01", MaxLength = 4 };
		_content.AddChild(id);
		var session = new OptionButton();
		session.AddItem("Session 1", 1);
		session.AddItem("Session 2", 2);
		_content.AddChild(session);
		if (error != null)
		{
			Text(error, Color.FromHtml("#f87171"));
		}

		Button("Continue", () =>
		{
			string participant = id.Text.Trim().ToUpperInvariant();
			if (!Regex.IsMatch(participant, "^P[0-9]{2,3}$"))
			{
				ShowSetup("The id must be P followed by two or three digits.");
				return;
			}

			ShowConsent(participant, session.GetSelectedId());
		});
	}

	private void ShowConsent(string participant, int sessionNumber)
	{
		Clear();
		Title("Information for participants");
		string sheet = Godot.FileAccess.FileExists(InformationSheetPath)
			? Godot.FileAccess.GetFileAsString(InformationSheetPath)
			: "The information sheet (study/participant_information.txt) is missing. Please ask the researcher.";
		var text = new RichTextLabel
		{
			Text = sheet,
			FitContent = false,
			ScrollActive = true,
			CustomMinimumSize = new Vector2(660, 300),
			SelectionEnabled = false
		};
		text.AddThemeColorOverride("default_color", TextPrimary);
		_content.AddChild(text);
		var agree = new CheckBox { Text = "I have read this information and agree to take part." };
		_content.AddChild(agree);
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		_content.AddChild(row);
		var decline = new Button { Text = "I do not want to take part" };
		decline.Pressed += ShowDeclined;
		row.AddChild(decline);
		var proceed = new Button { Text = "Continue", Disabled = true };
		agree.Toggled += on => proceed.Disabled = !on;
		proceed.Pressed += () => ShowBriefing(participant, sessionNumber);
		row.AddChild(proceed);
	}

	private void ShowDeclined()
	{
		Clear();
		Title("Thank you");
		Text("No data has been recorded. Please tell the researcher.");
	}

	private void ShowBriefing(string participant, int sessionNumber)
	{
		Clear();
		Title("How to play");
		Text("Reach the green exit. Move with WASD or the arrow keys; attack the enemy next to you with SPACE. " +
			"If you lose all your health you go back to the start. Each dungeon has the same layout; the enemies change " +
			"from dungeon to dungeon. Please play normally — there are no right or wrong answers.");
		Title("The enemies", 18);
		var grid = new GridContainer { Columns = 2 };
		grid.AddThemeConstantOverride("h_separation", 14);
		grid.AddThemeConstantOverride("v_separation", 6);
		_content.AddChild(grid);
		foreach (ArchetypeDefinition archetype in EnemyArchetypes.All)
		{
			var swatch = new ColorRect { Color = EnemyVisuals.ColorFor(archetype.Behavior), CustomMinimumSize = new Vector2(18, 18) };
			var name = new Label { Text = $"{archetype.DisplayName} — {Describe(archetype)}" };
			name.AddThemeColorOverride("font_color", TextPrimary);
			grid.AddChild(swatch);
			grid.AddChild(name);
		}

		Text("Red or orange squares on the floor show where an attack is about to land — step out of them. " +
			"Enemies with a gold ring are elites: tougher, and faster once hurt.", TextMuted);
		Button("Start playing", () =>
		{
			Hide();
			SessionRequested?.Invoke(participant, sessionNumber);
		});
	}

	private static string Describe(ArchetypeDefinition archetype) => archetype.Behavior switch
	{
		EnemyArchetypes.MeleeChaser => "walks straight at you and hits hard",
		EnemyArchetypes.MeleeHitRun => "darts in, strikes, then backs off",
		EnemyArchetypes.RangedKite => "shoots from a distance and keeps away",
		EnemyArchetypes.RangedStrafe => "shoots while stepping sideways",
		EnemyArchetypes.MeleeCharger => "charges along a straight line — sidestep the red line",
		EnemyArchetypes.RangedBomber => "throws a bomb at an orange square — get out of it",
		EnemyArchetypes.MeleeShield => "blocks hits from the front — attack it from the side",
		_ => ""
	};

	/// <summary>Shown by GameManager when the session ends; collects the answers.</summary>
	public void ShowQuestionnaire(Questionnaire questionnaire)
	{
		if (IsAutomatic)
		{
			var midpoint = new Dictionary<string, int>();
			foreach (QuestionnaireItem item in questionnaire.Items)
			{
				midpoint[item.Id] = (questionnaire.Scale.Min + questionnaire.Scale.Max) / 2;
			}

			QuestionnaireSubmitted?.Invoke(midpoint, "automatic test run");
			return;
		}

		Show();
		Clear();
		Title("A few questions about this session");
		Text($"1 = {questionnaire.Scale.LowLabel}, {questionnaire.Scale.Max} = {questionnaire.Scale.HighLabel}", TextMuted);
		var answers = new Dictionary<string, int>();
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(660, 360) };
		_content.AddChild(scroll);
		var list = new VBoxContainer();
		list.AddThemeConstantOverride("separation", 10);
		scroll.AddChild(list);
		foreach (QuestionnaireItem item in questionnaire.Items)
		{
			var label = new Label { Text = item.Text, AutowrapMode = TextServer.AutowrapMode.Word, CustomMinimumSize = new Vector2(620, 0) };
			label.AddThemeColorOverride("font_color", TextPrimary);
			list.AddChild(label);
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 10);
			list.AddChild(row);
			var group = new ButtonGroup();
			for (int value = questionnaire.Scale.Min; value <= questionnaire.Scale.Max; value++)
			{
				int chosen = value;
				var option = new CheckBox { Text = value.ToString(), ButtonGroup = group };
				option.Pressed += () => answers[item.Id] = chosen;
				row.AddChild(option);
			}
		}

		var comment = new TextEdit { PlaceholderText = "Anything else you would like to tell us? (optional)", CustomMinimumSize = new Vector2(660, 70) };
		_content.AddChild(comment);
		Label warning = Text("", Color.FromHtml("#f87171"));
		Button("Submit", () =>
		{
			if (answers.Count < questionnaire.Items.Count)
			{
				warning.Text = "Please answer every question.";
				return;
			}

			QuestionnaireSubmitted?.Invoke(answers, comment.Text);
		});
	}

	/// <summary>A message screen (session finished, backend unreachable, ...).</summary>
	public void ShowMessage(string title, string message)
	{
		if (IsAutomatic)
		{
			GD.Print($"[Study] {title}: {message}");
			return;
		}

		Show();
		Clear();
		Title(title);
		Text(message);
	}

	// --- Helpers -------------------------------------------------------------------

	private void Clear()
	{
		foreach (Node child in _content.GetChildren())
		{
			child.QueueFree();
		}
	}

	private void Title(string text, int size = 24)
	{
		var label = new Label { Text = text };
		label.AddThemeColorOverride("font_color", Accent);
		label.AddThemeFontSizeOverride("font_size", size);
		_content.AddChild(label);
	}

	private Label Text(string text, Color? color = null)
	{
		var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.Word, CustomMinimumSize = new Vector2(660, 0) };
		label.AddThemeColorOverride("font_color", color ?? TextPrimary);
		_content.AddChild(label);
		return label;
	}

	private void Button(string text, Action onPressed)
	{
		var button = new Button { Text = text, CustomMinimumSize = new Vector2(200, 36) };
		button.Pressed += onPressed;
		_content.AddChild(button);
	}
}
