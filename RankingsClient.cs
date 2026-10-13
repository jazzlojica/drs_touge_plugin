using System.Text.Json;
using Serilog;

namespace DrsTougePlugin;

public record RankInfo(int Elo, int Pos);

/// <summary>Keeps a copy of GET /api/rankings so the in-game UI can show each driver's website points and rank.</summary>
public class RankingsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly DrsTougeConfiguration _cfg;
    private volatile Dictionary<string, RankInfo> _data = new();
    private readonly SemaphoreSlim _wake = new(0);
    public int Version { get; private set; }
    /// <summary>Refresh soon (called after a battle result was delivered).</summary>
    public void Poke() { if (_wake.CurrentCount == 0) _wake.Release(); }

    public RankingsClient(DrsTougeConfiguration cfg) { _cfg = cfg; }

    public RankInfo? Get(string steamId) => _data.TryGetValue(steamId, out var r) ? r : null;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(_cfg.SiteUrl))
            {
                try
                {
                    var json = await Http.GetStringAsync(_cfg.SiteUrl.TrimEnd('/') + "/api/rankings", ct);
                    using var doc = JsonDocument.Parse(json);
                    var fresh = new Dictionary<string, RankInfo>();
                    foreach (var d in doc.RootElement.GetProperty("drivers").EnumerateArray())
                        fresh[d.GetProperty("steam_id").GetString()!] = new RankInfo(d.GetProperty("elo").GetInt32(), d.GetProperty("rank").GetInt32());
                    if (!SameAs(fresh)) { _data = fresh; Version++; }
                }
                catch (Exception e) when (e is not OperationCanceledException) { Log.Warning("[DRS touge] rankings fetch failed: {Msg}", e.Message); }
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Max(5, _cfg.RankingsRefreshSeconds)), ct); } catch (OperationCanceledException) { }
        }
    }

    private bool SameAs(Dictionary<string, RankInfo> other) =>
        other.Count == _data.Count && other.All(kv => _data.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
