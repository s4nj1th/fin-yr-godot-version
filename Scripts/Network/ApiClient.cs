using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using DungeonCrawler.Data;

namespace DungeonCrawler.Network;

public partial class ApiClient : Node
{
	// Matches the evolutionary-loop backend under Backend/ (run with
	// `uvicorn main:app --port 8001`, see Backend/README.md).
	// NOTE: Port 8001 is used instead of 8000 because port 8000 was already
	// occupied by an unrelated pre-existing process on the development machine.
	// Run the backend with: cd Backend && uvicorn main:app --port 8001
	[Export] public string BaseUrl { get; set; } = "http://localhost:8001";

	private readonly System.Net.Http.HttpClient _client = new()
	{
		Timeout = TimeSpan.FromSeconds(5)
	};

	public async Task<DungeonMapData?> GenerateMapAsync(string prompt = "", DungeonConfig? config = null)
	{
		try
		{
			var payload = new
			{
				prompt = prompt,
				config = config ?? new DungeonConfig()
			};

			string jsonString = JsonSerializer.Serialize(payload);
			var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

			HttpResponseMessage response = await _client.PostAsync($"{BaseUrl}/api/generate", content);
			response.EnsureSuccessStatusCode();

			string responseBody = await response.Content.ReadAsStringAsync();
			return JsonSerializer.Deserialize<DungeonMapData>(responseBody);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[ApiClient] Error calling /api/generate: {ex.Message}");
			return null;
		}
	}

	public async Task<LLMAdaptationResponse?> SendTelemetryAndAdaptAsync(TelemetryRequest request)
	{
		try
		{
			string jsonString = JsonSerializer.Serialize(request);
			var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

			HttpResponseMessage response = await _client.PostAsync($"{BaseUrl}/api/adapt", content);
			response.EnsureSuccessStatusCode();

			string responseBody = await response.Content.ReadAsStringAsync();
			return JsonSerializer.Deserialize<LLMAdaptationResponse>(responseBody);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[ApiClient] Error calling /api/adapt: {ex.Message}");
			return null;
		}
	}

	// --- Evolutionary-loop backend (Backend/main.py) ---

	public async Task<NextCandidateResponse?> RequestNextGenomeAsync(string playerId)
	{
		try
		{
			string url = $"{BaseUrl}/api/next_candidate?player_id={Uri.EscapeDataString(playerId)}";
			HttpResponseMessage response = await _client.PostAsync(url, null);
			response.EnsureSuccessStatusCode();

			string responseBody = await response.Content.ReadAsStringAsync();
			return JsonSerializer.Deserialize<NextCandidateResponse>(responseBody);
		}
		catch (Exception ex)
		{
			GD.Print($"[ApiClient] Evolution backend unavailable for /api/next_candidate ({ex.Message}) — falling back to local default genome.");
			return null;
		}
	}

	public async Task<ReportOutcomeResponse?> ReportOutcomeAsync(ReportOutcomeRequest request)
	{
		try
		{
			string jsonString = JsonSerializer.Serialize(request);
			var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

			HttpResponseMessage response = await _client.PostAsync($"{BaseUrl}/api/report_outcome", content);
			response.EnsureSuccessStatusCode();

			string responseBody = await response.Content.ReadAsStringAsync();
			return JsonSerializer.Deserialize<ReportOutcomeResponse>(responseBody);
		}
		catch (Exception ex)
		{
			GD.Print($"[ApiClient] Evolution backend unavailable for /api/report_outcome ({ex.Message}) — trial outcome not recorded.");
			return null;
		}
	}

	// --- Human study (Backend/study.py) ---

	private async Task<T?> PostJsonAsync<T>(string path, object? body) where T : class
	{
		try
		{
			HttpContent? content = body == null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
			HttpResponseMessage response = await _client.PostAsync($"{BaseUrl}{path}", content);
			response.EnsureSuccessStatusCode();
			return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync());
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[ApiClient] Study request {path} failed: {ex.Message}");
			return null;
		}
	}

	public Task<StudySessionResponse?> StartStudySessionAsync(string participantId, int sessionNumber) =>
		PostJsonAsync<StudySessionResponse>("/api/study/session", new StudySessionRequest { ParticipantId = participantId, SessionNumber = sessionNumber });

	public async Task<Questionnaire?> GetQuestionnaireAsync()
	{
		try
		{
			string body = await _client.GetStringAsync($"{BaseUrl}/api/study/questionnaire");
			return JsonSerializer.Deserialize<Questionnaire>(body);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[ApiClient] Could not load the questionnaire: {ex.Message}");
			return null;
		}
	}

	public async Task<bool> SubmitQuestionnaireAsync(QuestionnaireSubmission submission) =>
		await PostJsonAsync<Dictionary<string, int>>("/api/study/questionnaire", submission) != null;

	public Task<ReplaySample?> GetReplaySampleAsync(string playerId, int count) =>
		PostJsonAsync<ReplaySample>($"/api/study/replay_sample?player_id={Uri.EscapeDataString(playerId)}&n={count}", null);

	public async Task<bool> ReportReplayAsync(ReplayOutcomeRequest request) =>
		await PostJsonAsync<Dictionary<string, bool>>("/api/study/replay_outcome", request) != null;
}
