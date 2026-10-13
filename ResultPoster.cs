using System.Net;
using System.Text;
using System.Text.Json;
using Serilog;

namespace DrsTougePlugin;

/// <summary>
/// Sends finished battles to POST {SiteUrl}/api/telemetry/battle. Results are kept in a file until the website
/// confirms them, so a website outage or a server restart never loses a result (match_id makes retries safe).
/// </summary>
public class ResultPoster
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly DrsTougeConfiguration _cfg;
    private readonly List<string> _queue = new();
    private readonly object _lock = new();
    private readonly string _file = Path.Combine("cfg", "drs_touge_pending_results.jsonl");

    public ResultPoster(DrsTougeConfiguration cfg)
    {
        _cfg = cfg;
        try { if (File.Exists(_file)) _queue.AddRange(File.ReadAllLines(_file).Where(l => !string.IsNullOrWhiteSpace(l))); }
        catch (Exception e) { Log.Warning("[DRS touge] could not read pending results: {Msg}", e.Message); }
    }

    public Action? OnDelivered { get; set; }

    public void Submit(object result)
    {
        var line = JsonSerializer.Serialize(result);
        lock (_lock) { _queue.Add(line); Save(); }
    }

    private void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_file)!); File.WriteAllLines(_file, _queue); }
        catch (Exception e) { Log.Warning("[DRS touge] could not save pending results: {Msg}", e.Message); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            string? item;
            lock (_lock) item = _queue.Count > 0 ? _queue[0] : null;
            if (item == null) { try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { } continue; }
            if (string.IsNullOrWhiteSpace(_cfg.SiteUrl) || string.IsNullOrWhiteSpace(_cfg.SecretKey))
            {
                Log.Error("[DRS touge] SiteUrl / SecretKey not configured, {Count} result(s) are waiting", _queue.Count);
                try { await Task.Delay(60000, ct); } catch (OperationCanceledException) { }
                continue;
            }
            int code;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.SiteUrl.TrimEnd('/') + "/api/telemetry/battle")
                {
                    Content = new StringContent(item, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-api-key", _cfg.SecretKey);
                using var res = await Http.SendAsync(req, ct);
                code = (int)res.StatusCode;
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Warning("[DRS touge] POST failed: {Msg}", e.Message); code = 0; }
            catch (OperationCanceledException) { return; }

            if (code is >= 200 and < 300 || code is 400 or 409 or 422)   // delivered, or permanently rejected
            {
                if (code >= 400) Log.Error("[DRS touge] dropping result rejected with HTTP {Code}: {Item}", code, item);
                else Log.Information("[DRS touge] result delivered");
                lock (_lock) { if (_queue.Count > 0) _queue.RemoveAt(0); Save(); }
                if (code < 400) OnDelivered?.Invoke();
                backoff = 2;
            }
            else
            {
                Log.Warning("[DRS touge] website answered HTTP {Code}, retrying in {Sec}s", code, backoff);
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); } catch (OperationCanceledException) { return; }
                backoff = Math.Min(backoff * 2, 120);
            }
        }
    }
}
