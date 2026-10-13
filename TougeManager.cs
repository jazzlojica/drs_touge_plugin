using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DrsTougePlugin;

public class Driver
{
    public byte Id;
    public string Name = "";
    public string SteamId = "";
    public ACTcpClient Client = null!;
    public Vector3 Pos, Vel;
    public float SpeedKmh;
    public double RosterAt1, RosterAt2;      // when to (re)send the full roster to this driver's UI
}

public sealed class Battle
{
    public readonly Driver[] P = new Driver[2];          // P[0] = challenger
    public readonly int[] Score = { 0, 0 };
    public int RoundsDone, Leader, Count = 3;
    public string State = "LINEUP";                      // LINEUP, COUNT, RUN, DONE
    public readonly HashSet<byte> Ready = new();
    public double Created, NextCount, GoTime, LastTick, PassT, EscapeT;
    public readonly string MatchId = Guid.NewGuid().ToString("N");
    public readonly Trail Trail = new();
    public Vector3 Heading = Vector3.UnitX;
    public Driver Lead => P[Leader];
    public Driver Chase => P[1 - Leader];
}

public record Challenge(byte From, string Role, double Expires);

/// <summary>Runs the 1v1 touge battles, feeds the in-game UI and reports results to the website.</summary>
public class TougeManager : BackgroundService
{
    private const string Ui = "@TG|";
    private static readonly Regex SteamRe = new(@"^\d{17}$", RegexOptions.Compiled);
    private readonly DrsTougeConfiguration _cfg;
    private readonly EntryCarManager _cars;
    private readonly RankingsClient _ranks;
    private readonly ResultPoster _poster;
    private readonly object _lock = new();
    private readonly Dictionary<byte, Driver> _players = new();
    private readonly Dictionary<byte, Battle> _battleOf = new();
    private readonly Dictionary<byte, Challenge> _challenges = new();       // key = challenged driver
    private readonly Queue<(byte? To, string Msg)> _outbox = new();
    private readonly List<(string A, string B, double T)> _history = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rng = new();
    private int _ranksSeen = -1;

    public TougeManager(DrsTougeConfiguration cfg, EntryCarManager cars, RankingsClient ranks, ResultPoster poster)
    {
        _cfg = cfg; _cars = cars; _ranks = ranks; _poster = poster;
        _poster.OnDelivered = _ranks.Poke;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("[DRS touge] plugin started (site: {Site})", string.IsNullOrWhiteSpace(_cfg.SiteUrl) ? "NOT SET" : _cfg.SiteUrl);
        var background = Task.WhenAll(_ranks.RunAsync(stoppingToken), _poster.RunAsync(stoppingToken));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { lock (_lock) Tick(); }
                catch (Exception e) { Log.Error(e, "[DRS touge] tick failed"); }
            }
        }
        catch (OperationCanceledException) { }
        await background.ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- main loop
    private void Tick()
    {
        var now = Now;
        SyncPlayers(now);
        if (_ranks.Version != _ranksSeen) { _ranksSeen = _ranks.Version; foreach (var p in _players.Values) Broadcast(Entry(p)); }
        foreach (var d in _players.Values)
        {
            if (d.RosterAt1 > 0 && now >= d.RosterAt1) { d.RosterAt1 = 0; SendRoster(d); }
            else if (d.RosterAt2 > 0 && now >= d.RosterAt2) { d.RosterAt2 = 0; SendRoster(d); }
        }
        foreach (var kv in _challenges.Where(k => k.Value.Expires < now).ToList()) { _challenges.Remove(kv.Key); ToDriver(kv.Key, "CX"); }
        foreach (var b in _battleOf.Values.Distinct().ToList())
        {
            switch (b.State)
            {
                case "LINEUP" when now - b.Created > _cfg.ReadyTimeoutSeconds * (b.RoundsDone + 1):
                    Abort(b, "Battle timed out waiting for both drivers. No result recorded."); break;
                case "COUNT": TickCount(b, now); break;
                case "RUN": Judge(b, now); break;
            }
        }
        for (var i = 0; i < 6 && _outbox.Count > 0; i++) Flush(_outbox.Dequeue());
    }

    private void SyncPlayers(double now)
    {
        var seen = new HashSet<byte>();
        foreach (var ec in _cars.EntryCars)
        {
            var c = ec.Client;
            if (c == null || !c.HasSentFirstUpdate) continue;
            var id = ec.SessionId;
            seen.Add(id);
            var sid = c.Guid.ToString();
            if (!SteamRe.IsMatch(sid)) continue;
            if (!_players.TryGetValue(id, out var d) || d.SteamId != sid)
            {
                d = new Driver { Id = id, SteamId = sid, RosterAt1 = now + 8, RosterAt2 = now + 25 };
                _players[id] = d;
                d.Name = c.Name ?? $"Car {id}";
                Log.Information("[DRS touge] joined: car {Id} {Name} ({Sid})", id, d.Name, sid);
                Broadcast(Entry(d));
            }
            d.Client = c;
            d.Name = c.Name ?? d.Name;
            d.Pos = ec.Status.Position;
            d.Vel = ec.Status.Velocity;
            d.SpeedKmh = d.Vel.Length() * 3.6f;
        }
        foreach (var id in _players.Keys.Where(k => !seen.Contains(k)).ToList()) Left(id);
    }

    private void Left(byte id)
    {
        if (!_players.Remove(id, out var p)) return;
        Broadcast($"X|{id}");
        if (_battleOf.TryGetValue(id, out var b) && b.State != "DONE")
        {
            var other = b.P[0] == p ? b.P[1] : b.P[0];
            Tell(other, $"{p.Name} disconnected. You win the battle by forfeit.");
            Finish(b, other, p, "disconnect");
        }
        _challenges.Remove(id);
        foreach (var kv in _challenges.Where(k => k.Value.From == id).ToList()) { _challenges.Remove(kv.Key); ToDriver(kv.Key, "CX"); }
    }

    // ---------------------------------------------------------------- commands (called from chat)
    public void CmdBattle(ACTcpClient? client, string args)
    {
        lock (_lock)
        {
            if (!Me(client, out var me)) return;
            var role = "random";
            args = (args ?? "").Trim();
            var sp = args.LastIndexOf(' ');
            var last = sp >= 0 ? args[(sp + 1)..].ToLowerInvariant() : args.ToLowerInvariant();
            if (last is "leader" or "chaser" or "random") { role = last; args = sp >= 0 ? args[..sp].Trim() : ""; }
            if (_battleOf.ContainsKey(me.Id)) { Tell(me, "You are already in a battle (/forfeit to quit)."); return; }
            if (args.Length == 0) { Tell(me, "Usage: /battle <driver name> [leader|chaser|random]"); return; }
            var hits = Find(args).Where(h => h.Id != me.Id).ToList();
            if (hits.Count == 0) { Tell(me, "No driver matches that name."); return; }
            if (hits.Count > 1) { Tell(me, "Several drivers match; type more of the name."); return; }
            var tgt = hits[0];
            if (_battleOf.ContainsKey(tgt.Id)) { Tell(me, $"{tgt.Name} is already in a battle."); return; }
            if (tgt.SteamId == me.SteamId) { Tell(me, "That is you."); return; }
            _challenges[tgt.Id] = new Challenge(me.Id, role, Now + _cfg.ChallengeTimeoutSeconds);
            Tell(me, $"Challenge sent to {tgt.Name}.");
            Tell(tgt, $"{me.Name} challenges you to a touge battle! Type /accept or /decline.");
            ToDriver(tgt.Id, $"C|{me.Id}|{role}");
        }
    }

    public void CmdAccept(ACTcpClient? client)
    {
        lock (_lock)
        {
            if (!Me(client, out var me)) return;
            if (!_challenges.Remove(me.Id, out var ch) || ch.Expires < Now) { Tell(me, "No open challenge."); return; }
            if (!_players.TryGetValue(ch.From, out var a) || _battleOf.ContainsKey(a.Id) || _battleOf.ContainsKey(me.Id))
            { Tell(me, "Challenge is no longer valid."); return; }
            var b = new Battle { Created = Now };
            b.P[0] = a; b.P[1] = me;
            b.Leader = ch.Role == "leader" ? 0 : ch.Role == "chaser" ? 1 : _rng.Next(2);
            _battleOf[a.Id] = _battleOf[me.Id] = b;
            var rated = Rated(a, me);
            TellBoth(b, $"Battle on: {a.Name} vs {me.Name}. Best of 2 rounds, roles swap. " + (rated ? "Rated." : "UNRATED (too many recent battles between you two)."));
            foreach (var p in b.P) { ToDriver(p.Id, "CX"); Broadcast(Entry(p)); }
            AnnounceLineup(b);
        }
    }

    public void CmdDecline(ACTcpClient? client)
    {
        lock (_lock)
        {
            if (!Me(client, out var me)) return;
            if (_challenges.Remove(me.Id, out var ch))
            {
                if (_players.TryGetValue(ch.From, out var from)) { Tell(from, $"{me.Name} declined."); ToDriver(from.Id, $"D|{me.Id}"); }
                ToDriver(me.Id, "CX"); Tell(me, "Challenge declined."); return;
            }
            foreach (var kv in _challenges.Where(k => k.Value.From == me.Id).ToList())
            { _challenges.Remove(kv.Key); ToDriver(kv.Key, "CX"); Tell(me, "Challenge cancelled."); return; }
            if (_battleOf.TryGetValue(me.Id, out var b) && b.State == "LINEUP" && b.RoundsDone == 0) Abort(b, $"{me.Name} cancelled the battle.");
        }
    }

    public void CmdReady(ACTcpClient? client)
    {
        lock (_lock)
        {
            if (!Me(client, out var me)) return;
            if (!_battleOf.TryGetValue(me.Id, out var b) || b.State != "LINEUP") return;
            if (Vector3.Distance(b.Lead.Pos, b.Chase.Pos) > _cfg.StartMaxDistanceMeters)
            { Tell(me, $"Line up first: {b.Chase.Name} within {(int)_cfg.StartMaxDistanceMeters} m of {b.Lead.Name}."); return; }
            b.Ready.Add(me.Id);
            if (b.Ready.Count == 2) { b.State = "COUNT"; b.Count = 3; b.NextCount = Now; UiBattle(b, "COUNT"); }
            else TellBoth(b, $"{me.Name} is ready.");
        }
    }

    public void CmdForfeit(ACTcpClient? client)
    {
        lock (_lock)
        {
            if (!Me(client, out var me) || !_battleOf.TryGetValue(me.Id, out var b)) return;
            var other = b.P[0] == me ? b.P[1] : b.P[0];
            TellBoth(b, $"{me.Name} forfeits.");
            Finish(b, other, me, "forfeit");
        }
    }

    // ---------------------------------------------------------------- battle flow
    private void AnnounceLineup(Battle b)
    {
        TellBoth(b, $"Round {b.RoundsDone + 1}: {b.Lead.Name} LEADS, {b.Chase.Name} CHASES. Line up, then both type /ready (or press READY).");
        UiBattle(b, "LINEUP");
    }

    private void TickCount(Battle b, double now)
    {
        foreach (var p in b.P)
        {
            if (p.SpeedKmh > _cfg.JumpStartKmh)
            {
                b.State = "LINEUP"; b.Ready.Clear();
                foreach (var q in b.P) ToDriver(q.Id, $"J|{p.Id}");
                UiBattle(b, "LINEUP");
                TellBoth(b, $"JUMP START by {p.Name}! Back to the line, then both ready up.");
                return;
            }
        }
        if (Vector3.Distance(b.Lead.Pos, b.Chase.Pos) > _cfg.StartMaxDistanceMeters * 1.5f)
        {
            b.State = "LINEUP"; b.Ready.Clear(); UiBattle(b, "LINEUP");
            TellBoth(b, "Countdown cancelled: keep the cars lined up. Ready up again."); return;
        }
        if (now < b.NextCount) return;
        if (b.Count > 0)
        {
            TellBoth(b, b.Count.ToString());
            foreach (var p in b.P) ToDriver(p.Id, $"N|{b.Count}");
            b.Count--; b.NextCount = now + 1.0;
        }
        else
        {
            b.State = "RUN"; b.GoTime = b.LastTick = now; b.PassT = b.EscapeT = 0;
            b.Trail.Reset(b.Lead.Pos); b.Heading = Vector3.UnitX;
            TellBoth(b, "GO!"); UiBattle(b, "RUN");
        }
    }

    /// <summary>Metres the leader is ahead of the chaser along the road (negative once the chaser is in front).</summary>
    private (float Lead, bool ChaserAhead) Measure(Battle b)
    {
        var L = b.Lead; var C = b.Chase;
        var v = new Vector3(L.Vel.X, 0, L.Vel.Z);
        if (v.Length() > 3f) b.Heading = Vector3.Normalize(v);
        b.Trail.Add(L.Pos);
        var rel = new Vector3(C.Pos.X - L.Pos.X, 0, C.Pos.Z - L.Pos.Z);
        var dot = Vector3.Dot(rel, b.Heading);
        var i = b.Trail.Nearest(C.Pos);
        var nearHead = i >= b.Trail.Count - 3;                 // chaser is level with the leader, not on an older part of the road
        var lead = nearHead ? -dot : b.Trail.PathLead(i) + Vector3.Distance(C.Pos, b.Trail.Point(i));
        return (lead, nearHead && dot > _cfg.PassMarginMeters);
    }

    private void Judge(Battle b, double now)
    {
        var dt = now - b.LastTick; b.LastTick = now;
        var (lead, ahead) = Measure(b);                        // keeps the trail growing even during the grace period
        var elapsed = now - b.GoTime;
        if (elapsed >= _cfg.RoundTimeoutSeconds) { RoundEnd(b, null, "time limit"); return; }
        if (elapsed < _cfg.GraceSeconds) return;
        b.PassT = ahead ? b.PassT + dt : 0;
        b.EscapeT = lead > _cfg.EscapeMeters ? b.EscapeT + dt : 0;
        if (b.PassT >= _cfg.PassHoldSeconds) RoundEnd(b, 1 - b.Leader, "overtake");
        else if (b.EscapeT >= _cfg.EscapeHoldSeconds) RoundEnd(b, b.Leader, "gap");
    }

    private void RoundEnd(Battle b, int? winner, string reason)
    {
        if (winner.HasValue) b.Score[winner.Value]++;
        b.RoundsDone++;
        var res = winner.HasValue ? $"{b.P[winner.Value].Name} wins the round ({reason})." : $"Round drawn ({reason}).";
        TellBoth(b, $"{res} Score {b.P[0].Name} {b.Score[0]} - {b.Score[1]} {b.P[1].Name}");
        if (b.RoundsDone % 2 == 0 && b.Score[0] != b.Score[1])
        {
            var w = b.Score[0] > b.Score[1] ? 0 : 1;
            Finish(b, b.P[w], b.P[1 - w], "score"); return;
        }
        if (b.RoundsDone >= _cfg.MaxRounds) { TellBoth(b, "Battle drawn. No rating change."); Abort(b, "Battle drawn. No rating change."); return; }
        b.Leader ^= 1;
        b.State = "LINEUP"; b.Ready.Clear(); b.Created = Now; b.PassT = b.EscapeT = 0;
        AnnounceLineup(b);
    }

    private void Abort(Battle b, string msg)
    {
        TellBoth(b, msg);
        b.State = "DONE";
        foreach (var p in b.P) _battleOf.Remove(p.Id);
        UiEnd(b, null, msg);
    }

    private void Finish(Battle b, Driver winner, Driver loser, string reason)
    {
        if (b.State == "DONE") return;
        b.State = "DONE";
        foreach (var p in b.P) _battleOf.Remove(p.Id);
        UiEnd(b, winner, reason);
        if (!Rated(winner, loser)) { TellBoth(b, $"{winner.Name} wins, but this pairing is unrated for now."); return; }
        _history.Add((Key(winner, loser), "", Now));
        Tell(winner, "You win the battle! Your rating update is on its way to the website.");
        Tell(loser, $"{winner.Name} wins the battle.");
        _poster.Submit(new
        {
            match_id = b.MatchId,
            winner_steam_id = winner.SteamId, loser_steam_id = loser.SteamId,
            winner_name = winner.Name, loser_name = loser.Name,
            track = "", layout = "", reason, score = b.Score,
        });
        Log.Information("[DRS touge] {W} beat {L} ({Reason})", winner.Name, loser.Name, reason);
    }

    private static string Key(Driver a, Driver b) => string.CompareOrdinal(a.SteamId, b.SteamId) < 0 ? a.SteamId + "|" + b.SteamId : b.SteamId + "|" + a.SteamId;

    private bool Rated(Driver a, Driver b)
    {
        var cutoff = Now - 3600; var key = Key(a, b);
        _history.RemoveAll(h => h.T <= cutoff);
        return _history.Count(h => h.A == key) < _cfg.PairLimitPerHour;
    }

    // ---------------------------------------------------------------- helpers: players, chat, UI feed
    private bool Me(ACTcpClient? client, out Driver me)
    {
        me = null!;
        if (client == null) return false;
        foreach (var d in _players.Values) if (ReferenceEquals(d.Client, client)) { me = d; return true; }
        client.SendChatMessage("Your Steam account could not be verified, so you cannot battle.");
        return false;
    }

    private IEnumerable<Driver> Find(string q)
    {
        var exact = _players.Values.Where(p => p.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : _players.Values.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string Tier(int elo) => elo < 400 ? "D" : elo < 800 ? "C" : elo < 1200 ? "B" : elo < 1700 ? "A" : "S";

    private string Entry(Driver p)
    {
        var info = _ranks.Get(p.SteamId);
        var elo = info?.Elo ?? 1;
        return $"R|{p.Id}|{elo}|{Tier(elo)}|{info?.Pos ?? 0}|{(_battleOf.ContainsKey(p.Id) ? 1 : 0)}|{(info != null ? 1 : 0)}";
    }

    private void SendRoster(Driver to)
    {
        foreach (var p in _players.Values) ToDriver(to.Id, Entry(p));
        if (_battleOf.TryGetValue(to.Id, out var b))
            UiBattle(b, b.State == "COUNT" ? "COUNT" : b.State == "RUN" ? "RUN" : "LINEUP", to.Id);
    }

    private void UiBattle(Battle b, string state, byte? only = null)
    {
        var total = b.RoundsDone < 2 ? 2 : 4;
        for (var i = 0; i < 2; i++)
        {
            var p = b.P[i];
            if (only.HasValue && p.Id != only.Value) continue;
            var o = b.P[1 - i];
            ToDriver(p.Id, $"B|{o.Id}|{(b.Leader == i ? "L" : "C")}|{b.RoundsDone + 1}|{total}|{state}|{(int)_cfg.EscapeMeters}|{b.Score[i]}-{b.Score[1 - i]}");
        }
    }

    private void UiEnd(Battle b, Driver? winner, string text)
    {
        foreach (var p in b.P)
        {
            if (!_players.ContainsKey(p.Id)) continue;
            ToDriver(p.Id, $"E|{(winner == null ? "D" : ReferenceEquals(p, winner) ? "W" : "L")}|{text}");
            Broadcast(Entry(p));
        }
    }

    private void ToDriver(byte id, string payload) => _outbox.Enqueue((id, Ui + payload));
    private void Broadcast(string payload) => _outbox.Enqueue((null, Ui + payload));
    private static void Tell(Driver d, string msg) { try { d.Client.SendChatMessage(msg); } catch { /* client gone */ } }
    private static void TellBoth(Battle b, string msg) { foreach (var p in b.P) Tell(p, msg); }

    private void Flush((byte? To, string Msg) m)
    {
        try
        {
            if (m.To.HasValue) { if (_players.TryGetValue(m.To.Value, out var d)) d.Client.SendChatMessage(m.Msg); }
            else foreach (var d in _players.Values) d.Client.SendChatMessage(m.Msg);
        }
        catch (Exception e) { Log.Debug("[DRS touge] chat send failed: {Msg}", e.Message); }
    }
}
