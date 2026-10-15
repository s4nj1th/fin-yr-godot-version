using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using DungeonCrawler.Data;
using DungeonCrawler.UI;

namespace DungeonCrawler.Core;

/// <summary>
/// Study mode (`-- --study`, optionally `--study-minutes=N`): runs one
/// session of the human study (docs/HUMAN_STUDY_PROTOCOL.md). The backend
/// assigns the counterbalanced condition and the session's player id; the
/// participant plays for the session length (the dungeon in progress when
/// time runs out is finished); then replays a sample of their archive, whose
/// outcomes are recorded without changing it; then answers the
/// questionnaire. The adaptive tactical director is off in study mode, so
/// within-session adaptation cannot confound the comparison of conditions.
/// </summary>
public partial class GameManager
{
	private const int ReplayBlockSize = 4;
	private bool _studyMode;
	private double _studyMinutes = 30;
	private StudyFlow? _studyFlow;
	private string _studyParticipant = "";
	private int _studySession;
	private bool _studyRunning;
	private bool _studySessionOver;
	private bool _studyFinished;
	private double _studyElapsed;
	private bool _currentIsReplay;
	private readonly Queue<GenomeData> _replayQueue = new();
	private int _replaysTotal;

	/// <summary>Session status for the HUD, or null outside study mode.</summary>
	public string? StudyStatus
	{
		get
		{
			if (!_studyMode || !_studyRunning)
			{
				return null;
			}

			if (_studySessionOver)
			{
				return $"FINAL ROUND {_replaysTotal - _replayQueue.Count}/{_replaysTotal}";
			}

			int left = Math.Max(0, (int)((_studyMinutes * 60) - _studyElapsed));
			return $"SESSION {left / 60:D2}:{left % 60:D2} LEFT";
		}
	}

	/// <summary>Parses the study flags; returns true if the game should wait for the study screens.</summary>
	private bool SetUpStudyMode(string[] cmdlineArgs)
	{
		foreach (string arg in cmdlineArgs)
		{
			if (arg == "--study")
			{
				_studyMode = true;
			}
			else if (arg.StartsWith("--study-minutes=", StringComparison.Ordinal)
				&& double.TryParse(arg.AsSpan("--study-minutes=".Length), System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out double minutes))
			{
				_studyMinutes = Math.Max(0.1, minutes);
			}
		}

		if (!_studyMode)
		{
			return false;
		}

		_studyFlow = new StudyFlow();
		_studyFlow.SessionRequested += (participant, session) => _ = StartStudySessionAsync(participant, session);
		_studyFlow.QuestionnaireSubmitted += (answers, comment) => _ = SubmitStudyQuestionnaireAsync(answers, comment);
		AddChild(_studyFlow);
		return true;
	}

	private bool StudyBlocksGameplay => _studyMode && (!_studyRunning || _studyFinished);

	private async Task StartStudySessionAsync(string participant, int session)
	{
		StudySessionResponse? response = await _apiClient.StartStudySessionAsync(participant, session);
		if (response == null)
		{
			_studyFlow!.ShowMessage("Cannot reach the study server",
				"The backend is not running or not reachable. Please tell the researcher.");
			return;
		}

		_studyParticipant = participant;
		_studySession = session;
		PlayerId = response.PlayerId;
		_studyRunning = true;
		GD.Print($"[Study] {participant} session {session}: condition {response.Condition}, player id {PlayerId}, {_studyMinutes} min.");
		RequestNextGenomeAndRegenerateAsync();
	}

	/// <summary>
	/// Called after each trial is reported. Starts the replay block once the
	/// session time is up and ends the session after the last replay.
	/// Returns false when the session is over and no new dungeon should be built.
	/// </summary>
	private async Task<bool> PrepareNextStudyTrialAsync()
	{
		if (!_studySessionOver && _studyElapsed >= _studyMinutes * 60)
		{
			_studySessionOver = true;
			ReplaySample? sample = await _apiClient.GetReplaySampleAsync(PlayerId, ReplayBlockSize);
			foreach (GenomeData design in sample?.Designs ?? new List<GenomeData>())
			{
				_replayQueue.Enqueue(design);
			}

			_replaysTotal = _replayQueue.Count;
			GD.Print($"[Study] Session time is up; replaying {_replaysTotal} archived design(s).");
		}

		if (_studySessionOver && _replayQueue.Count == 0)
		{
			await FinishStudySessionAsync();
			return false;
		}

		return true;
	}

	/// <summary>Takes the next archived design for the replay block, if the block has started.</summary>
	private bool TryTakeReplayDesign()
	{
		_currentIsReplay = false;
		if (!_studyMode || !_studySessionOver || _replayQueue.Count == 0)
		{
			return false;
		}

		GenomeData design = _replayQueue.Dequeue();
		_currentGenome = design;
		_currentGenomeId = design.Id;
		_currentIsReplay = true;
		_backendAvailable = true;
		return true;
	}

	private async Task ReportReplayAsync(TelemetryMetrics telemetry)
	{
		bool recorded = await _apiClient.ReportReplayAsync(new ReplayOutcomeRequest
		{
			GenomeId = _currentGenomeId ?? "",
			PlayerId = PlayerId,
			Telemetry = telemetry
		});
		LastTrialResultMessage = recorded ? "FINAL ROUND RESULT RECORDED" : "BACKEND UNREACHABLE — REPLAY NOT RECORDED";
	}

	private async Task FinishStudySessionAsync()
	{
		_studyFinished = true;
		_enemyRoot?.QueueFree();
		_enemyRoot = null!;
		_enemies.Clear();
		Questionnaire? questionnaire = await _apiClient.GetQuestionnaireAsync();
		if (questionnaire == null)
		{
			_studyFlow!.ShowMessage("Session complete", "The questionnaire could not be loaded. Please tell the researcher.");
			return;
		}

		_studyFlow!.ShowQuestionnaire(questionnaire);
	}

	private async Task SubmitStudyQuestionnaireAsync(Dictionary<string, int> answers, string comment)
	{
		bool stored = await _apiClient.SubmitQuestionnaireAsync(new QuestionnaireSubmission
		{
			ParticipantId = _studyParticipant,
			SessionNumber = _studySession,
			PlayerId = PlayerId,
			Responses = answers,
			Comment = comment
		});
		_studyFlow!.ShowMessage("Session complete",
			stored ? "Thank you! Your session is complete. Please tell the researcher."
				: "Your answers could not be saved. Please tell the researcher before closing the game.");
		GD.Print($"[Study] Questionnaire {(stored ? "stored" : "NOT stored")}; session complete.");
		if (_studyFlow.IsAutomatic)
		{
			GetTree().Quit(stored ? 0 : 1);
		}
	}
}
