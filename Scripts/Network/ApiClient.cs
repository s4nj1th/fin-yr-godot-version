using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using DungeonCrawler.Data;

namespace DungeonCrawler.Network;

public partial class ApiClient : Node
{
	[Export] public string BaseUrl { get; set; } = "http://localhost:8080";
	
	private readonly System.Net.Http.HttpClient _client = new();

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
}
