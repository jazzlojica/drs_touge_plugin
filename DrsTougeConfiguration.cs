namespace DrsTougePlugin;

/// <summary>
/// Settings. AssettoServer creates a config file for this plugin in the server's cfg folder on first start
/// (look for a new file with "touge" in its name) and fills it with these defaults.
/// </summary>
public class DrsTougeConfiguration
{
    /// <summary>Your website, e.g. https://your-site.netlify.app (no trailing slash).</summary>
    public string SiteUrl { get; set; } = "";
    /// <summary>Same value as SERVER_SECRET_KEY on Netlify.</summary>
    public string SecretKey { get; set; } = "";

    public double GraceSeconds { get; set; } = 6.0;          // no judging right after GO
    public double PassHoldSeconds { get; set; } = 1.5;       // chaser must stay ahead this long = pass
    public double PassMarginMeters { get; set; } = 2.0;      // ...by at least this many metres
    public double EscapeMeters { get; set; } = 120.0;        // leader this far ahead (along the road) ...
    public double EscapeHoldSeconds { get; set; } = 5.0;     // ... for this long wins the round
    public double RoundTimeoutSeconds { get; set; } = 300.0; // no decision = drawn round
    public double StartMaxDistanceMeters { get; set; } = 40.0;
    public double JumpStartKmh { get; set; } = 8.0;          // moving faster than this during the countdown
    public int MaxRounds { get; set; } = 4;                  // 2 rounds + up to 2 tiebreak rounds
    public double ReadyTimeoutSeconds { get; set; } = 300.0;
    public double ChallengeTimeoutSeconds { get; set; } = 60.0;
    public int PairLimitPerHour { get; set; } = 3;           // more rated battles between the same two = unrated
    public double RankingsRefreshSeconds { get; set; } = 30.0;
}
