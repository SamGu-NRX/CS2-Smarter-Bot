using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;

namespace BotState;

// "Freeze with bomb" has no confirmed cause. Two candidates in this plugin:
//  - UpdateBotBehavior skips the teammate-wait fix for the carrier, so the native wait behind a
//    friend (IsWaitingBehindFriend, PoliteTimer) can hold it behind a teammate who stands still.
//  - UpdateBotCombat forces IsAttacking while IsAimingAtEnemy, and the stock idle repath skips
//    attacking bots, so a carrier aiming at an enemy it cannot see may never be re-pathed.
// A carrier that has not moved for CarrierFrozenSeconds outside freeze time is logged with the
// fields that tell these apart, then released from the friend wait and re-pathed.
public partial class BotState
{
    public readonly FakeConVar<bool> CarrierWatchdog = new("sb_carrier_watchdog",
        "Log and re-path a bomb carrier that has not moved for 8 seconds (0 = off, 1 = on)", true);

    // Longer than the stock 5 s idle repath, so this fires only when that did not help.
    private const float CarrierFrozenSeconds = 8f;
    private const float CarrierStillSpeed = 5f;
    private const int MaxCarrierLogsPerRound = 4;

    private int _watchedCarrierSlot = -1;
    private float _carrierStillSince;
    private int _carrierLogsThisRound;

    // Never moves the bot itself; it only resets native timers.
    private void UpdateCarrierWatchdog(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (!CarrierWatchdog.Value || player.Slot != _bombCarrierSlot || _plantedBombPos != null)
        {
            if (player.Slot == _watchedCarrierSlot)
                _watchedCarrierSlot = -1;
            return;
        }

        float vx = pawn.AbsVelocity.X, vy = pawn.AbsVelocity.Y;
        // A real fight is a reason to stand still.
        if (vx * vx + vy * vy >= CarrierStillSpeed * CarrierStillSpeed || bot.IsEnemyVisible
            || _watchedCarrierSlot != player.Slot)
        {
            _watchedCarrierSlot = player.Slot;
            _carrierStillSince = now;
            return;
        }
        if (now - _carrierStillSince < CarrierFrozenSeconds)
            return;
        _carrierStillSince = now;

        // Holding the C4 means the bot is planting or about to; log it, but do not interfere.
        bool planting = IsHoldingC4(pawn);
        if (_carrierLogsThisRound < MaxCarrierLogsPerRound)
        {
            _carrierLogsThisRound++;
            var origin = pawn.AbsOrigin;
            var goal = bot.GoalPosition;
            float goalDist = origin == null ? -1f
                : MathF.Sqrt((goal.X - origin.X) * (goal.X - origin.X) + (goal.Y - origin.Y) * (goal.Y - origin.Y));
            Logger.LogWarning(
                "[Smarter-Bot/CarrierWatchdog] {Name} has held the C4 still for {Seconds:F0} s at ({X:F0},{Y:F0},{Z:F0}). " +
                "waitingBehindFriend={Waiting} politeLeft={Polite:F1}s attacking={Attacking} aimingAtEnemy={Aiming} " +
                "enemyVisible={Visible} engineStuck={Stuck} following={Following} inBombZone={InZone} " +
                "goalDist={GoalDist:F0} activeWeapon={Weapon} action={Action}",
                player.PlayerName, CarrierFrozenSeconds, origin?.X ?? 0f, origin?.Y ?? 0f, origin?.Z ?? 0f,
                bot.IsWaitingBehindFriend, bot.PoliteTimer.Timestamp - now, bot.IsAttacking, bot.IsAimingAtEnemy,
                bot.IsEnemyVisible, bot.IsStuck, bot.IsFollowing, pawn.InBombZone,
                goalDist, pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName ?? "none",
                planting ? "none (holding C4)" : "released friend wait, re-pathed");
        }
        if (planting)
            return;

        // The same reset UpdateBotBehavior applies to every other bot.
        ref bool waitingBehindFriend = ref bot.IsWaitingBehindFriend;
        waitingBehindFriend = false;
        CountdownTimer politeTimer = bot.PoliteTimer;
        ref float politeDuration = ref politeTimer.Duration;
        politeDuration = 0f;
        ref float politeTimestamp = ref politeTimer.Timestamp;
        politeTimestamp = 0f;

        ForceRepath(bot);
        ResetLookAroundForBot(player);
    }

    private void ResetCarrierWatchdogState()
    {
        _watchedCarrierSlot = -1;
        _carrierLogsThisRound = 0;
    }
}
