using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

// The stock recovery in RecoverStuckBot acts when the engine sets IsStuck or when a bot stays
// under 5 units/s. The engine's stuck check, as in the CS:S bot source this AI descends from,
// sets IsStuck only when the bot's average speed drops under 20 units/s. A bot grinding along a
// wall or a corner moves faster than that and gets nowhere, so neither path catches it. Such a
// bot is now pushed sideways past the corner for a quarter second and then re-pathed.
public partial class BotState
{
    public readonly FakeConVar<bool> CornerUnstick = new("sb_corner_unstick",
        "Bots that keep moving without getting anywhere are steered sideways and re-pathed (0 = off, 1 = on)", true);

    // A bot that moved less than NoProgressDistance in NoProgressSeconds, while its average
    // speed stayed above GrindMinSpeed, is grinding against something.
    private const float NoProgressSeconds = 2.5f;
    private const float NoProgressDistance = 40f;
    private const float GrindMinSpeed = 30f;
    // A bot this close to its move goal is adjusting at it, not stuck.
    private const float AtGoalDistance = 64f;
    private const float NudgeSeconds = 0.25f;
    private const float NudgeSpeed = 200f;
    private const float NudgeTurnDegrees = 70f;
    // Give up after this many nudges without progress; the bot is then left to the engine.
    private const int MaxNudgesWithoutProgress = 3;

    private struct ProgressWindow
    {
        public float X, Y;
        public float Since;
        public float SpeedSum;
        public int Samples;
        public int Nudges;
    }

    private struct Nudge
    {
        public float DirX, DirY;
        public float Until;
    }

    private readonly Dictionary<int, ProgressWindow> _progress = new();
    private readonly Dictionary<int, Nudge> _nudges = new();

    // Returns true when it moved the bot this tick.
    private bool UpdateCornerUnstick(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now, bool curIsAttacking)
    {
        if (!CornerUnstick.Value)
            return false;
        var origin = pawn.AbsOrigin;
        if (origin == null)
            return false;

        // The stock recovery owns IsStuck; fights, defuses and a smoke-edge hold stop a bot on
        // purpose. Checked before every nudge write too, so a fight cancels a nudge under way.
        if (bot.IsStuck || curIsAttacking || bot.IsEnemyVisible || pawn.IsDefusing
            || (_lastSmokeHold.TryGetValue(idx, out float heldAt) && now - heldAt < 1f))
        {
            _nudges.Remove(idx);
            _progress.Remove(idx);
            return false;
        }

        if (_nudges.TryGetValue(idx, out var nudge))
        {
            if (now < nudge.Until)
            {
                SetHorizontalVelocity(pawn, nudge.DirX, nudge.DirY, NudgeSpeed);
                return true;
            }
            _nudges.Remove(idx);
            ForceRepath(bot);
        }

        float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
        float speed = MathF.Sqrt(vx * vx + vy * vy);
        if (!_progress.TryGetValue(idx, out var window))
        {
            _progress[idx] = new ProgressWindow { X = origin.X, Y = origin.Y, Since = now };
            return false;
        }

        window.SpeedSum += speed;
        window.Samples++;
        float mx = origin.X - window.X, my = origin.Y - window.Y;
        if (mx * mx + my * my > NoProgressDistance * NoProgressDistance)
        {
            // Progress: start a new window and forget earlier nudges.
            _progress[idx] = new ProgressWindow { X = origin.X, Y = origin.Y, Since = now };
            return false;
        }
        if (now - window.Since < NoProgressSeconds)
        {
            _progress[idx] = window;
            return false;
        }

        // The window is full. Whatever happens next, the following window starts here.
        var next = new ProgressWindow { X = origin.X, Y = origin.Y, Since = now, Nudges = window.Nudges };
        var goal = bot.GoalPosition;
        bool goalKnown = goal.X != 0f || goal.Y != 0f || goal.Z != 0f;
        float gx = goal.X - origin.X, gy = goal.Y - origin.Y;
        float goalDist = MathF.Sqrt(gx * gx + gy * gy);
        if (window.SpeedSum / window.Samples < GrindMinSpeed
            || (goalKnown && goalDist < AtGoalDistance)
            || window.Nudges >= MaxNudgesWithoutProgress)
        {
            _progress[idx] = next;
            return false;
        }

        // Head for the goal if the engine reports one, else keep the current heading, and
        // turn off it; alternate sides on each try.
        float baseX, baseY;
        if (goalKnown && goalDist > 1f)
        {
            baseX = gx / goalDist;
            baseY = gy / goalDist;
        }
        else if (speed > 1f)
        {
            baseX = vx / speed;
            baseY = vy / speed;
        }
        else
        {
            float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
            baseX = MathF.Cos(yaw);
            baseY = MathF.Sin(yaw);
        }

        float side = window.Nudges % 2 == 0 ? 1f : -1f;
        float dirX = 0f, dirY = 0f;
        bool found = false;
        foreach (float sign in (ReadOnlySpan<float>)[side, -side])
        {
            float rad = sign * NudgeTurnDegrees * MathF.PI / 180f;
            dirX = baseX * MathF.Cos(rad) - baseY * MathF.Sin(rad);
            dirY = baseX * MathF.Sin(rad) + baseY * MathF.Cos(rad);
            if (IsPathClear(pawn, origin, dirX, dirY, 64f))
            {
                found = true;
                break;
            }
        }
        next.Nudges = window.Nudges + 1;
        _progress[idx] = next;
        if (!found)
        {
            ForceRepath(bot);
            DebugBehavior($"[Smarter-Bot/Unstick] {player.PlayerName} grinding at ({origin.X:F0},{origin.Y:F0}), no open side; re-pathing");
            return false;
        }

        _nudges[idx] = new Nudge { DirX = dirX, DirY = dirY, Until = now + NudgeSeconds };
        DebugBehavior(
            $"[Smarter-Bot/Unstick] {player.PlayerName} moved {MathF.Sqrt(mx * mx + my * my):F0} units in {NoProgressSeconds:F1} s " +
            $"at ({origin.X:F0},{origin.Y:F0}); nudge {next.Nudges} of {MaxNudgesWithoutProgress}");
        SetHorizontalVelocity(pawn, dirX, dirY, NudgeSpeed);
        return true;
    }

    private void ResetCornerUnstickState()
    {
        _progress.Clear();
        _nudges.Clear();
    }
}
