using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

// Shared plumbing for the movement behaviours added after the stock fixes: one registration
// call from Load, one per-bot call from OnTick, and the helpers they share. Each behaviour has
// its own sb_* convar, settable from cfg/gamemode_competitive_server.cfg.
public partial class BotState
{
    private bool _debugBehavior;
    // Where the bomb was planted this round, or null. Set from the planter's position.
    private (float X, float Y, float Z)? _plantedBombPos;

    // Toggles or explicitly sets the movement-behaviour diagnostics.
    [ConsoleCommand("css_botstate_behaviordebug", "Toggle Smarter-Bot movement behaviour debug log")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnBehaviorDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _debugBehavior = arg == "1"
                          || arg.Equals("true", StringComparison.OrdinalIgnoreCase)
                          || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _debugBehavior = !_debugBehavior;
        }

        cmd.ReplyToCommand($"[Smarter-Bot] behaviour debug = {_debugBehavior}");
        Console.WriteLine($"[Smarter-Bot] behaviour debug = {_debugBehavior}");
    }

    // Called from Load. Only registers callbacks; nothing here touches entities.
    private void RegisterBotBehaviorFixes()
    {
        RegisterEventHandler<EventPlayerHurt>(OnBehaviorPlayerHurt);
        RegisterEventHandler<EventInfernoStartburn>(OnInfernoStartburn);
        RegisterEventHandler<EventInfernoExpire>(OnInfernoExpire);
        RegisterEventHandler<EventInfernoExtinguish>(OnInfernoExtinguish);
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonate);
        RegisterEventHandler<EventSmokegrenadeExpired>(OnSmokeExpired);
        RegisterEventHandler<EventBombPlanted>(OnBehaviorBombPlanted);
        RegisterEventHandler<EventPlayerDeath>(OnRepositionPlayerDeath);
        RegisterEventHandler<EventRoundStart>(OnBehaviorRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnBehaviorPlayerSpawn);
        RegisterListener<Listeners.OnMapStart>(_ => ResetBotBehaviorFixes());
    }

    // Runs after the stock movement fixes. The first behaviour that moves the bot this tick wins.
    private void UpdateBotBehaviorFixes(
        CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now, bool curIsAttacking)
    {
        if (_isFreezeTime || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            return;
        // Air and ladder movement belong to UpdateBotMovement.
        if (_cachedInAir.GetValueOrDefault(idx) || _cachedNearLadder.GetValueOrDefault(idx))
            return;
        // NadeSystem lineups and other BotController replays drive the bot themselves.
        if (_botController != null && BotControllerBridge.IsViewControlled(_botController, player.Slot))
            return;

        if (UpdateFireEscape(player, pawn, bot, idx, now)
            || UpdateReposition(player, pawn, bot, idx, now, curIsAttacking))
            return;
        HoldAtSmokeEdge(player, pawn, bot, idx, now, curIsAttacking);
    }

    private HookResult OnBehaviorRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetBotBehaviorFixes();
        return HookResult.Continue;
    }

    // A respawned bot, or a new bot in a reused slot, must not inherit the old pawn's state.
    private HookResult OnBehaviorPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
            ForgetBot((int)player.Index);
        return HookResult.Continue;
    }

    private void ForgetBot(int idx)
    {
        _lastBurnTime.Remove(idx);
        _trails.Remove(idx);
        _fireEscapes.Remove(idx);
        foreach (var key in _smokeHoldStart.Keys.Where(k => k.Bot == idx).ToList())
            _smokeHoldStart.Remove(key);
        _stillSince.Remove(idx);
        _spottedLastTick.Remove(idx);
        _pendingRepositions.Remove(idx);
        _repositionMoves.Remove(idx);
        _nextRepositionAt.Remove(idx);
    }

    private HookResult OnBehaviorBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        var origin = @event.Userid?.PlayerPawn?.Value?.AbsOrigin;
        if (origin != null)
            _plantedBombPos = (origin.X, origin.Y, origin.Z);
        return HookResult.Continue;
    }

    private void ResetBotBehaviorFixes()
    {
        _plantedBombPos = null;
        ResetFireEscapeState();
        ResetSmokeHoldState();
        ResetRepositionState();
    }

    // Sets horizontal velocity along a unit direction and keeps vertical speed, the same way the
    // stock stuck recovery and sniper peek steer bots.
    private static void SetHorizontalVelocity(CCSPlayerPawn pawn, float dirX, float dirY, float speed)
    {
        pawn.AbsVelocity.X = dirX * speed;
        pawn.AbsVelocity.Y = dirY * speed;
    }

    // Makes the native path follower plan a new path on its next update, as the stock stuck
    // recovery does.
    private static void ForceRepath(CCSBot bot)
    {
        CountdownTimer repathTimer = bot.RepathTimer;

        ref float duration = ref repathTimer.Duration;
        duration = 0.0f;

        ref float timestamp = ref repathTimer.Timestamp;
        timestamp = Server.CurrentTime;

        ref float timescale = ref repathTimer.Timescale;
        timescale = 1.0f;
    }

    // Scratch vectors for traces, created on first use so Load never allocates native memory.
    private Vector? _traceFrom, _traceTo, _hullMins, _hullMaxs;
    private bool _traceErrorLogged;

    // True when a standing player's hull can travel `distance` units from origin along a unit
    // direction without hitting world geometry, props or players.
    private bool IsPathClear(CCSPlayerPawn pawn, Vector origin, float dirX, float dirY, float distance)
    {
        _traceFrom ??= new Vector();
        _traceTo ??= new Vector();
        _hullMins ??= new Vector(-16f, -16f, 0f);
        _hullMaxs ??= new Vector(16f, 16f, 54f);

        // Start above step height so stairs and kerbs do not count as walls.
        _traceFrom.X = origin.X;
        _traceFrom.Y = origin.Y;
        _traceFrom.Z = origin.Z + 18f;
        _traceTo.X = origin.X + dirX * distance;
        _traceTo.Y = origin.Y + dirY * distance;
        _traceTo.Z = _traceFrom.Z;

        try
        {
            var result = Trace.TraceHullShape(_traceFrom, _traceTo, _hullMins, _hullMaxs, pawn,
                new TraceOptions { InteractsWith = Masks.PlayerSolid });
            return result.Fraction >= 0.99f;
        }
        catch (Exception ex)
        {
            // A failed trace must not stop the bot moving; treat the path as open.
            if (!_traceErrorLogged)
            {
                _traceErrorLogged = true;
                Console.WriteLine($"[Smarter-Bot] hull trace failed, treating paths as clear: {ex.Message}");
            }
            return true;
        }
    }

    private static bool IsHoldingC4(CCSPlayerPawn pawn)
        => pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName == "weapon_c4";

    // A CT near the planted bomb is retaking or defusing; leave its movement to the engine.
    private bool IsRetakingNearBomb(CCSPlayerController player, Vector origin)
    {
        if (_plantedBombPos is not { } bomb || player.TeamNum != (byte)CsTeam.CounterTerrorist)
            return false;
        float dx = origin.X - bomb.X, dy = origin.Y - bomb.Y;
        return dx * dx + dy * dy < 200f * 200f;
    }

    private void DebugBehavior(string message)
    {
        if (_debugBehavior)
            BroadcastDebug(message);
    }
}
