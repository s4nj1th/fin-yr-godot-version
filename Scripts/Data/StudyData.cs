using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DungeonCrawler.Data;

// Wire format of the human-study endpoints (Backend/main.py, Backend/study.py).

public class StudySessionRequest
{
	[JsonPropertyName("participant_id")] public string ParticipantId { get; set; } = "";
	[JsonPropertyName("session_number")] public int SessionNumber { get; set; } = 1;
}

public class StudySessionResponse
{
	[JsonPropertyName("participant_id")] public string ParticipantId { get; set; } = "";
	[JsonPropertyName("session_number")] public int SessionNumber { get; set; }
	[JsonPropertyName("condition")] public string Condition { get; set; } = "";
	[JsonPropertyName("player_id")] public string PlayerId { get; set; } = "";
}

public class QuestionnaireScale
{
	[JsonPropertyName("min")] public int Min { get; set; } = 1;
	[JsonPropertyName("max")] public int Max { get; set; } = 7;
	[JsonPropertyName("low_label")] public string LowLabel { get; set; } = "";
	[JsonPropertyName("high_label")] public string HighLabel { get; set; } = "";
}

public class QuestionnaireItem
{
	[JsonPropertyName("id")] public string Id { get; set; } = "";
	[JsonPropertyName("text")] public string Text { get; set; } = "";
}

public class Questionnaire
{
	[JsonPropertyName("scale")] public QuestionnaireScale Scale { get; set; } = new();
	[JsonPropertyName("items")] public List<QuestionnaireItem> Items { get; set; } = new();
}

public class QuestionnaireSubmission
{
	[JsonPropertyName("participant_id")] public string ParticipantId { get; set; } = "";
	[JsonPropertyName("session_number")] public int SessionNumber { get; set; }
	[JsonPropertyName("player_id")] public string PlayerId { get; set; } = "";
	[JsonPropertyName("responses")] public Dictionary<string, int> Responses { get; set; } = new();
	[JsonPropertyName("comment")] public string Comment { get; set; } = "";
}

public class ReplaySample
{
	[JsonPropertyName("player_id")] public string PlayerId { get; set; } = "";
	[JsonPropertyName("designs")] public List<GenomeData> Designs { get; set; } = new();
}

public class ReplayOutcomeRequest
{
	[JsonPropertyName("genome_id")] public string GenomeId { get; set; } = "";
	[JsonPropertyName("player_id")] public string PlayerId { get; set; } = "";
	[JsonPropertyName("telemetry")] public TelemetryMetrics Telemetry { get; set; } = new();
}
