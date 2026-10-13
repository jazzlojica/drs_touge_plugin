using AssettoServer.Commands;
using Qmmands;

namespace DrsTougePlugin;

/// <summary>Chat commands. The in-game UI sends these for the player, typing them by hand works too.</summary>
public class TougeCommands : ACModuleBase
{
    private readonly TougeManager _touge;
    public TougeCommands(TougeManager touge) { _touge = touge; }

    [Command("battle", "challenge")]
    public void Battle([Remainder] string args = "") => _touge.CmdBattle(Context.Client, args);

    [Command("accept")]
    public void Accept() => _touge.CmdAccept(Context.Client);

    [Command("decline", "cancel")]
    public void Decline() => _touge.CmdDecline(Context.Client);

    [Command("ready")]
    public void Ready() => _touge.CmdReady(Context.Client);

    [Command("forfeit", "ff")]
    public void Forfeit() => _touge.CmdForfeit(Context.Client);

    [Command("tghelp")]
    public void Help() => Reply("/battle <name> [leader|chaser|random]  /accept  /decline  /ready  /forfeit");
}
