using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

// Native path planning has no smoke cost, so a bot whose route crosses an enemy smoke walks
// straight into it. A bot reaching the edge of an enemy smoke now stops there, and goes through
// only after waiting SmokeHoldMaxSeconds. Smokes the bot's own team threw are treated as part of
// its own play (an execute) and never hold it.
public partial class BotState
{
    public readonly FakeConVar<bool> SmokeHold = new("sb_smoke_hold",
        "Bots wait at the edge of an enemy smoke instead of walking into it (0 = off, 1 = on)", true);

    // CS2 smokes fill the space around them, so no single radius is exact. No measured value
    // exists; 144 units is a guess near a fully grown smoke on open ground.
    private const float SmokeRadius = 144f;
    // Bots inside this band outside the smoke are held.
    private const float SmokeEdgeBand = 48f;
    // A held bot is pushed back out at this speed. The engine accelerates a running bot by about
    // 21 units/s each tick, so a small outward push cancels the creep instead of only zeroing it.
    private const float SmokeEdgePushBack = 30f;
    private const float SmokeHoldMaxSeconds = 5f;
    // Drop a smoke whose smokegrenade_expired never arrived. CS2 smokes last about 20 s.
    private const float SmokeMaxLifetime = 25f;

    private struct ActiveSmoke
    {
        public float X, Y, Z;
        // Team of the thrower when it burst; 0 when the thrower had already left.
        public byte Team;
        public float DetonatedAt;
    }

    // Smoke projectile entity index -> the smoke it produced.
    private readonly Dictionary<int, ActiveSmoke> _activeSmokes = new();
    // (bot controller index, smoke entity index) -> when the bot was first held at that smoke.
    private readonly Dictionary<(int Bot, int Smoke), float> _smokeHoldStart = new();

    private HookResult OnSmokeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        var thrower = @event.Userid;
        _activeSmokes[@event.Entityid] = new ActiveSmoke
        {
            X = @event.X,
            Y = @event.Y,
            Z = @event.Z,
            Team = thrower != null && thrower.IsValid ? thrower.TeamNum : (byte)0,
            DetonatedAt = Server.CurrentTime,
        };
        return HookResult.Continue;
    }

    private HookResult OnSmokeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        RemoveSmoke(@event.Entityid);
        return HookResult.Continue;
    }

    private void RemoveSmoke(int smokeId)
    {
        _activeSmokes.Remove(smokeId);
        foreach (var key in _smokeHoldStart.Keys.Where(k => k.Smoke == smokeId).ToList())
            _smokeHoldStart.Remove(key);
    }

    // Returns true when it changed the bot's velocity this tick.
    private bool HoldAtSmokeEdge(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now, bool curIsAttacking)
    {
        if (!SmokeHold.Value || _activeSmokes.Count == 0)
            return false;
        // A fight, a planted bomb (retake or post-plant) or a bomb carrier's plant run decides
        // the route; holding at a smoke then costs the round.
        if (curIsAttacking || bot.IsEnemyVisible || _plantedBombPos != null || IsHoldingC4(pawn))
            return false;
        var origin = pawn.AbsOrigin;
        if (origin == null)
            return false;

        int? expired = null;
        foreach (var (smokeId, smoke) in _activeSmokes)
        {
            if (now - smoke.DetonatedAt > SmokeMaxLifetime)
            {
                expired = smokeId;
                continue;
            }
            if (smoke.Team == player.TeamNum || MathF.Abs(smoke.Z - origin.Z) > 128f)
                continue;

            float dx = smoke.X - origin.X, dy = smoke.Y - origin.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            var key = (idx, smokeId);
            if (dist > SmokeRadius + SmokeEdgeBand)
            {
                // Walked well away: a later approach gets a fresh wait.
                if (dist > SmokeRadius + SmokeEdgeBand + 64f)
                    _smokeHoldStart.Remove(key);
                continue;
            }
            // Already inside, for example when the smoke burst on the bot: let it walk out.
            if (dist < SmokeRadius)
                continue;

            float inX = dx / dist, inY = dy / dist;
            float inward = pawn.AbsVelocity.X * inX + pawn.AbsVelocity.Y * inY;
            if (inward <= 0f)
                continue;

            if (!_smokeHoldStart.TryGetValue(key, out float heldSince))
            {
                _smokeHoldStart[key] = now;
                heldSince = now;
                DebugBehavior($"[Smarter-Bot/Smoke] {player.PlayerName} holding at smoke #{smokeId} edge ({dist:F0} units from centre)");
            }
            if (now - heldSince > SmokeHoldMaxSeconds)
                continue;

            // Keep the sideways part of the motion so the bot can still walk around the smoke.
            pawn.AbsVelocity.X -= (inward + SmokeEdgePushBack) * inX;
            pawn.AbsVelocity.Y -= (inward + SmokeEdgePushBack) * inY;
            if (expired is int stale)
                RemoveSmoke(stale);
            return true;
        }

        if (expired is int old)
            RemoveSmoke(old);
        return false;
    }

    private void ResetSmokeHoldState()
    {
        _activeSmokes.Clear();
        _smokeHoldStart.Clear();
    }
}
