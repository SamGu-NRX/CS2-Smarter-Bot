using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

// Native bots take molotov damage and keep following their path, so they stand in fire. A bot
// that takes fire damage walks back to where it stood a second or two earlier, which it reached
// on foot; with no such spot it walks straight away from the centre of the nearest fire.
public partial class BotState
{
    public readonly FakeConVar<bool> FireEscape = new("sb_fire_escape",
        "Bots that take molotov or incendiary damage walk out of the fire (0 = off, 1 = on)", true);

    // A bot counts as standing in fire until this long after its last fire damage. There is no
    // measured CS2 damage interval; 0.5 s tolerates one missed damage tick.
    private const float BurnGraceSeconds = 0.5f;
    // The retreat target is a position sampled 1-2 s before the first burn.
    private const float TrailSampleSeconds = 1.0f;
    private const float MinRetreatDistance = 48f;
    private const float MaxRetreatDistance = 400f;
    // Rifles run at 215-225 units/s, so this does not make a bot faster than its weapon allows.
    private const float FireSteerSpeed = 220f;
    // A bot still burning after this long is cornered; the stock stuck recovery takes over.
    private const float FireEscapeMaxSeconds = 3.0f;
    private const float FireSearchRadius = 500f;

    private struct PositionTrail
    {
        public float NewX, NewY, NewZ;
        public float OldX, OldY, OldZ;
        public float SampledAt;
        public bool HasNew, HasOld;
    }

    private struct FireEscapeState
    {
        public float TargetX, TargetY;
        public bool HasTarget;
        public float DirX, DirY;
        public float StartedAt;
        public float NextCheckAt;
    }

    // Inferno entity index -> where it started burning, from inferno_startburn.
    private readonly Dictionary<int, (float X, float Y, float Z)> _infernoCenters = new();
    // Bot controller index -> server time of its last fire damage.
    private readonly Dictionary<int, float> _lastBurnTime = new();
    private readonly Dictionary<int, PositionTrail> _trails = new();
    private readonly Dictionary<int, FireEscapeState> _fireEscapes = new();

    private HookResult OnInfernoStartburn(EventInfernoStartburn @event, GameEventInfo info)
    {
        _infernoCenters[@event.Entityid] = (@event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    private HookResult OnInfernoExpire(EventInfernoExpire @event, GameEventInfo info)
    {
        _infernoCenters.Remove(@event.Entityid);
        return HookResult.Continue;
    }

    private HookResult OnInfernoExtinguish(EventInfernoExtinguish @event, GameEventInfo info)
    {
        _infernoCenters.Remove(@event.Entityid);
        return HookResult.Continue;
    }

    // CS2 reports molotov and incendiary fire damage with the weapon name "inferno".
    private HookResult OnBehaviorPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim != null && victim.IsValid && victim.IsBot && @event.Weapon == "inferno")
            _lastBurnTime[(int)victim.Index] = Server.CurrentTime;
        return HookResult.Continue;
    }

    // Returns true when it moved the bot this tick.
    private bool UpdateFireEscape(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now)
    {
        if (!FireEscape.Value)
            return false;
        var origin = pawn.AbsOrigin;
        if (origin == null)
            return false;

        if (!IsBurning(bot, idx, now))
        {
            _fireEscapes.Remove(idx);
            RecordTrail(idx, origin, now);
            return false;
        }

        if (pawn.IsDefusing || IsHoldingC4(pawn) || IsRetakingNearBomb(player, origin))
            return false;

        if (!_fireEscapes.TryGetValue(idx, out var escape))
        {
            escape = StartFireEscape(pawn, idx, origin, now);
            _fireEscapes[idx] = escape;
            DebugBehavior(
                $"[Smarter-Bot/Fire] {player.PlayerName} burning at ({origin.X:F0},{origin.Y:F0}); " +
                (escape.HasTarget ? $"retreating to ({escape.TargetX:F0},{escape.TargetY:F0})" : $"leaving along ({escape.DirX:F2},{escape.DirY:F2})"));
        }

        if (now - escape.StartedAt > FireEscapeMaxSeconds)
            return false;

        float dirX = escape.DirX, dirY = escape.DirY;
        if (escape.HasTarget)
        {
            float dx = escape.TargetX - origin.X, dy = escape.TargetY - origin.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            // Reached or passed the target and still burning: keep going the same way from here.
            if (dist <= 24f || dx * escape.DirX + dy * escape.DirY <= 0f)
            {
                escape.HasTarget = false;
            }
            else
            {
                dirX = dx / dist;
                dirY = dy / dist;
            }
        }

        // Check the way ahead every eighth of a second; a wall there means a new direction.
        if (now >= escape.NextCheckAt)
        {
            escape.NextCheckAt = now + 0.125f;
            if (!IsPathClear(pawn, origin, dirX, dirY, 40f))
            {
                escape.HasTarget = false;
                (escape.DirX, escape.DirY) = PickOpenDirection(pawn, origin, dirX, dirY);
                dirX = escape.DirX;
                dirY = escape.DirY;
            }
        }
        _fireEscapes[idx] = escape;

        SetHorizontalVelocity(pawn, dirX, dirY, FireSteerSpeed);
        return true;
    }

    // Two sources, either is enough: this plugin's own player_hurt record, and the native
    // CCSBot burn timer (m_burnedByFlamesTimer, the time of the last fire damage).
    private bool IsBurning(CCSBot bot, int idx, float now)
    {
        if (_lastBurnTime.TryGetValue(idx, out float burnedAt) && now - burnedAt < BurnGraceSeconds)
            return true;
        float nativeBurnedAt = bot.BurnedByFlamesTimer.Timestamp;
        return nativeBurnedAt > 0f && now - nativeBurnedAt >= 0f && now - nativeBurnedAt < BurnGraceSeconds;
    }

    // Keeps two samples a second apart; the older one is where the bot stood 1-2 s ago.
    private void RecordTrail(int idx, Vector origin, float now)
    {
        _trails.TryGetValue(idx, out var trail);
        if (trail.HasNew && now - trail.SampledAt < TrailSampleSeconds)
            return;

        if (trail.HasNew)
        {
            trail.OldX = trail.NewX;
            trail.OldY = trail.NewY;
            trail.OldZ = trail.NewZ;
            trail.HasOld = true;
        }
        trail.NewX = origin.X;
        trail.NewY = origin.Y;
        trail.NewZ = origin.Z;
        trail.HasNew = true;
        trail.SampledAt = now;
        _trails[idx] = trail;
    }

    private FireEscapeState StartFireEscape(CCSPlayerPawn pawn, int idx, Vector origin, float now)
    {
        var escape = new FireEscapeState { StartedAt = now };

        // Walk back the way the bot came, if it moved and the straight line back is open.
        if (_trails.TryGetValue(idx, out var trail) && trail.HasOld)
        {
            float dx = trail.OldX - origin.X, dy = trail.OldY - origin.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist >= MinRetreatDistance && dist <= MaxRetreatDistance
                && MathF.Abs(trail.OldZ - origin.Z) < 64f
                && IsPathClear(pawn, origin, dx / dist, dy / dist, dist))
            {
                escape.HasTarget = true;
                escape.TargetX = trail.OldX;
                escape.TargetY = trail.OldY;
                escape.DirX = dx / dist;
                escape.DirY = dy / dist;
                return escape;
            }
        }

        // Otherwise leave straight away from the nearest fire's centre, or backwards if the bot
        // stands on the centre or no fire is known.
        float awayX, awayY;
        if (TryNearestInfernoCenter(origin, out float cx, out float cy)
            && (origin.X - cx) * (origin.X - cx) + (origin.Y - cy) * (origin.Y - cy) > 16f * 16f)
        {
            float len = MathF.Sqrt((origin.X - cx) * (origin.X - cx) + (origin.Y - cy) * (origin.Y - cy));
            awayX = (origin.X - cx) / len;
            awayY = (origin.Y - cy) / len;
        }
        else
        {
            float yaw = (pawn.EyeAngles.Y + 180f) * MathF.PI / 180f;
            awayX = MathF.Cos(yaw);
            awayY = MathF.Sin(yaw);
        }

        (escape.DirX, escape.DirY) = PickOpenDirection(pawn, origin, awayX, awayY);
        return escape;
    }

    // Turns off a wall: tries the given direction, then 45, 90 and 135 degrees to either side.
    // Returns the given direction when every one is blocked.
    private (float X, float Y) PickOpenDirection(CCSPlayerPawn pawn, Vector origin, float dirX, float dirY)
    {
        ReadOnlySpan<float> turns = [0f, 45f, -45f, 90f, -90f, 135f, -135f];
        foreach (float turn in turns)
        {
            float rad = turn * MathF.PI / 180f;
            float x = dirX * MathF.Cos(rad) - dirY * MathF.Sin(rad);
            float y = dirX * MathF.Sin(rad) + dirY * MathF.Cos(rad);
            if (IsPathClear(pawn, origin, x, y, 96f))
                return (x, y);
        }
        return (dirX, dirY);
    }

    private bool TryNearestInfernoCenter(Vector origin, out float x, out float y)
    {
        x = y = 0f;
        float best = FireSearchRadius * FireSearchRadius;
        bool found = false;
        foreach (var center in _infernoCenters.Values)
        {
            if (MathF.Abs(center.Z - origin.Z) > 128f)
                continue;
            float dx = center.X - origin.X, dy = center.Y - origin.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 >= best)
                continue;
            best = d2;
            x = center.X;
            y = center.Y;
            found = true;
        }
        return found;
    }

    private void ResetFireEscapeState()
    {
        _infernoCenters.Clear();
        _lastBurnTime.Clear();
        _trails.Clear();
        _fireEscapes.Clear();
    }
}
