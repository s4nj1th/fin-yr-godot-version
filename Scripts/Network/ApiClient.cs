using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using DungeonCrawler.Data;

namespace DungeonCrawler.Network;

public partial class ApiClient : Node
{
	// Matches the evolutionary-loop backend under Backend/ (run with
	// `uvicorn main:app --port 8000`, see Backend/README.md).
	[Export] public string BaseUrl { get; set; } = "http://localhost:8000";

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

	public async Task<NextCandidateResponse?> RequestNextGenomeAsync()
	{
		try
		{
			HttpResponseMessage response = await _client.PostAsync($"{BaseUrl}/api/next_candidate", null);
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
}
