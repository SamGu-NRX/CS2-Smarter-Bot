using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

// Native bots have no "move after being seen" behaviour; the stock code only nudges AWP and
// SSG 08 users sideways after a shot. A bot that has held still for a second and then gets a
// kill, or is spotted by an enemy, now moves to a nearby spot, preferring one the threat cannot
// see. The game does not expose its hiding spots, so the spot comes from hull traces around
// the bot, and the native AI may later walk the bot back to the spot it chose before.
public partial class BotState
{
    public readonly FakeConVar<bool> Reposition = new("sb_reposition",
        "Bots holding an angle move to a nearby spot after a kill or after an enemy spots them (0 = off, 1 = on)", true);

    // A bot moving slower than this for HoldMinSeconds is holding an angle.
    private const float HoldSpeed = 20f;
    private const float HoldMinSeconds = 1.0f;
    // A trigger waits up to this long for the fight to end before it is dropped.
    private const float RepositionPendingSeconds = 1.5f;
    private const float RepositionCooldownSeconds = 6f;
    private const float RepositionMaxSeconds = 0.9f;
    private const float RepositionSpeed = 220f;
    private const float EyeHeight = 64f;

    private struct PendingReposition
    {
        public float ThreatX, ThreatY, ThreatZ;
        public float Since;
        public string Reason;
    }

    private struct RepositionMove
    {
        public float TargetX, TargetY;
        public float StartedAt;
    }

    private readonly Dictionary<int, float> _stillSince = new();
    private readonly HashSet<int> _spottedLastTick = new();
    private readonly Dictionary<int, PendingReposition> _pendingRepositions = new();
    private readonly Dictionary<int, RepositionMove> _repositionMoves = new();
    private readonly Dictionary<int, float> _nextRepositionAt = new();

    private HookResult OnRepositionPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!Reposition.Value)
            return HookResult.Continue;
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (attacker == null || !attacker.IsValid || !attacker.IsBot || !attacker.PawnIsAlive
            || attacker.HasBeenControlledByPlayerThisRound
            || victim == null || !victim.IsValid || victim.TeamNum == attacker.TeamNum)
            return HookResult.Continue;

        int idx = (int)attacker.Index;
        float now = Server.CurrentTime;
        var victimOrigin = victim.PlayerPawn?.Value?.AbsOrigin;
        if (victimOrigin == null || !IsHoldingAngle(idx, now))
            return HookResult.Continue;

        _pendingRepositions[idx] = new PendingReposition
        {
            ThreatX = victimOrigin.X,
            ThreatY = victimOrigin.Y,
            ThreatZ = victimOrigin.Z,
            Since = now,
            Reason = "kill",
        };
        return HookResult.Continue;
    }

    // Returns true when it moved the bot this tick.
    private bool UpdateReposition(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now, bool curIsAttacking)
    {
        if (!Reposition.Value)
            return false;
        var origin = pawn.AbsOrigin;
        if (origin == null)
            return false;
        bool fighting = curIsAttacking || bot.IsEnemyVisible;
        // Objectives come first, both for starting a move and for one under way.
        bool busy = pawn.IsDefusing || IsHoldingC4(pawn) || IsRetakingNearBomb(player, origin);

        if (_repositionMoves.TryGetValue(idx, out var move))
        {
            float mx = move.TargetX - origin.X, my = move.TargetY - origin.Y;
            float left = MathF.Sqrt(mx * mx + my * my);
            if (fighting || busy || left < 20f || now - move.StartedAt > RepositionMaxSeconds)
            {
                _repositionMoves.Remove(idx);
                // Look around from the new spot instead of staring down the old angle.
                ResetLookAroundForBot(player);
                return false;
            }
            SetHorizontalVelocity(pawn, mx / left, my / left, RepositionSpeed);
            return true;
        }

        float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
        if (vx * vx + vy * vy < HoldSpeed * HoldSpeed)
            _stillSince.TryAdd(idx, now);
        else
            _stillSince.Remove(idx);

        // m_bSpotted is the radar flag: at least one enemy can see this bot.
        bool spotted = pawn.EntitySpottedState.Spotted;
        bool newlySpotted = spotted && !_spottedLastTick.Contains(idx);
        if (spotted) _spottedLastTick.Add(idx); else _spottedLastTick.Remove(idx);
        if (newlySpotted && IsHoldingAngle(idx, now) && !_pendingRepositions.ContainsKey(idx)
            && TryNearestEnemy(player, origin, out float tx, out float ty, out float tz))
        {
            _pendingRepositions[idx] = new PendingReposition
            {
                ThreatX = tx, ThreatY = ty, ThreatZ = tz, Since = now, Reason = "spotted",
            };
        }

        if (!_pendingRepositions.TryGetValue(idx, out var pending))
            return false;
        if (now - pending.Since > RepositionPendingSeconds)
        {
            _pendingRepositions.Remove(idx);
            return false;
        }
        // Finish the fight first; the trigger keeps for RepositionPendingSeconds.
        if (fighting)
            return false;
        _pendingRepositions.Remove(idx);
        if (now < _nextRepositionAt.GetValueOrDefault(idx) || busy)
            return false;

        if (!TryPickRepositionSpot(pawn, origin, pending, out float spotX, out float spotY, out bool hidden))
        {
            DebugBehavior($"[Smarter-Bot/Reposition] {player.PlayerName} ({pending.Reason}) found no open spot");
            return false;
        }

        _repositionMoves[idx] = new RepositionMove { TargetX = spotX, TargetY = spotY, StartedAt = now };
        _nextRepositionAt[idx] = now + RepositionCooldownSeconds;
        _stillSince.Remove(idx);
        DebugBehavior(
            $"[Smarter-Bot/Reposition] {player.PlayerName} ({pending.Reason}) moving to ({spotX:F0},{spotY:F0})" +
            (hidden ? ", out of the threat's sight" : ", threat can still see it"));

        float sx = spotX - origin.X, sy = spotY - origin.Y;
        float len = MathF.Sqrt(sx * sx + sy * sy);
        SetHorizontalVelocity(pawn, sx / len, sy / len, RepositionSpeed);
        return true;
    }

    private bool IsHoldingAngle(int idx, float now)
        => _stillSince.TryGetValue(idx, out float since) && now - since >= HoldMinSeconds;

    // Nearest living enemy by distance. Runs only when a bot is newly spotted.
    private static bool TryNearestEnemy(CCSPlayerController player, Vector origin, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        float best = float.MaxValue;
        for (int slot = 0, maxPlayers = NativeAPI.GetMaxClients(); slot < maxPlayers; slot++)
        {
            var other = Utilities.GetPlayerFromSlot(slot);
            if (other == null || !other.IsValid || !other.PawnIsAlive
                || other.TeamNum == player.TeamNum
                || (other.TeamNum != (byte)CsTeam.Terrorist && other.TeamNum != (byte)CsTeam.CounterTerrorist))
                continue;
            var pos = other.PlayerPawn?.Value?.AbsOrigin;
            if (pos == null)
                continue;
            float dx = pos.X - origin.X, dy = pos.Y - origin.Y, dz = pos.Z - origin.Z;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 >= best)
                continue;
            best = d2;
            x = pos.X;
            y = pos.Y;
            z = pos.Z;
        }
        return best < float.MaxValue;
    }

    // Tries back-diagonal, sideways and straight-back spots 128 and 80 units away. A spot needs
    // an open path and ground under it; one the threat cannot see wins, otherwise the first open
    // one is used, because a sideways step still changes the angle.
    private bool TryPickRepositionSpot(CCSPlayerPawn pawn, Vector origin, PendingReposition threat,
        out float spotX, out float spotY, out bool hidden)
    {
        spotX = spotY = 0f;
        hidden = false;

        float awayX = origin.X - threat.ThreatX, awayY = origin.Y - threat.ThreatY;
        float len = MathF.Sqrt(awayX * awayX + awayY * awayY);
        if (len < 1f)
        {
            float yaw = (pawn.EyeAngles.Y + 180f) * MathF.PI / 180f;
            awayX = MathF.Cos(yaw);
            awayY = MathF.Sin(yaw);
        }
        else
        {
            awayX /= len;
            awayY /= len;
        }

        // Pick the first side at random so a bot does not always step the same way.
        float side = _random.Next(2) == 0 ? 1f : -1f;
        ReadOnlySpan<float> turns = [50f, -50f, 90f, -90f, 0f];
        ReadOnlySpan<float> distances = [128f, 80f];
        bool haveFallback = false;
        foreach (float distance in distances)
        {
            foreach (float baseTurn in turns)
            {
                float rad = side * baseTurn * MathF.PI / 180f;
                float dirX = awayX * MathF.Cos(rad) - awayY * MathF.Sin(rad);
                float dirY = awayX * MathF.Sin(rad) + awayY * MathF.Cos(rad);
                float cx = origin.X + dirX * distance, cy = origin.Y + dirY * distance;
                if (!IsPathClear(pawn, origin, dirX, dirY, distance) || !HasGroundAt(pawn, cx, cy, origin.Z))
                    continue;

                if (!IsLineOpen(pawn, cx, cy, origin.Z + EyeHeight, threat.ThreatX, threat.ThreatY, threat.ThreatZ + EyeHeight))
                {
                    spotX = cx;
                    spotY = cy;
                    hidden = true;
                    return true;
                }
                if (!haveFallback)
                {
                    spotX = cx;
                    spotY = cy;
                    haveFallback = true;
                }
            }
        }
        return haveFallback;
    }

    // True when something solid lies within 58 units below the spot, so the bot does not step
    // off a ledge or a box.
    private bool HasGroundAt(CCSPlayerPawn pawn, float x, float y, float z)
    {
        _traceFrom ??= new Vector();
        _traceTo ??= new Vector();
        _traceFrom.X = x; _traceFrom.Y = y; _traceFrom.Z = z + 18f;
        _traceTo.X = x; _traceTo.Y = y; _traceTo.Z = z - 40f;
        try
        {
            var result = Trace.TraceEndShape(_traceFrom, _traceTo, pawn,
                new TraceOptions { InteractsWith = Masks.PlayerSolid });
            return result.Fraction < 1f;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // True when no world geometry blocks the line, using the same mask as the flash LOS check.
    private bool IsLineOpen(CCSPlayerPawn pawn, float x1, float y1, float z1, float x2, float y2, float z2)
    {
        _traceFrom ??= new Vector();
        _traceTo ??= new Vector();
        _traceFrom.X = x1; _traceFrom.Y = y1; _traceFrom.Z = z1;
        _traceTo.X = x2; _traceTo.Y = y2; _traceTo.Z = z2;
        try
        {
            var result = Trace.TraceEndShape(_traceFrom, _traceTo, pawn,
                new TraceOptions { InteractsWith = Masks.SolidBrushOnly });
            return result.Fraction >= 0.999f;
        }
        catch (Exception)
        {
            // Unknown visibility: report it as open so the spot only counts as a fallback.
            return true;
        }
    }

    private void ResetRepositionState()
    {
        _stillSince.Clear();
        _spottedLastTick.Clear();
        _pendingRepositions.Clear();
        _repositionMoves.Clear();
        _nextRepositionAt.Clear();
    }
}
