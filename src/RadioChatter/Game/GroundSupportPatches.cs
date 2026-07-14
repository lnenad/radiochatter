using System;
using HarmonyLib;
using RadioChatter.Comms;
using UnityEngine;

namespace RadioChatter.Game
{
    /// <summary>Positive damage is the authoritative fallback for ground-support requests.
    /// The dealer id survives blast/fragment damage and lets us reject friendly fire.</summary>
    [HarmonyPatch(typeof(global::Unit), "RecordDamage", new Type[] { typeof(global::PersistentID), typeof(float) })]
    internal static class UnitRecordDamageGroundSupportPatch
    {
        private static void Postfix(global::Unit __instance, global::PersistentID lastDamagedBy, float damageAmount)
        {
            if (__instance == null || damageAmount <= 0f)
                return;

            global::Unit attacker;
            if (!lastDamagedBy.TryGetUnit(out attacker))
                return;

            GroundSupportAttackPatchHelpers.TryEnqueue(__instance, attacker);
        }
    }

    /// <summary>Turrets and guns pass their selected target through WeaponStation.Fire. This
    /// catches a group being fired upon even when the first rounds miss.</summary>
    [HarmonyPatch(typeof(global::WeaponStation), "Fire", new Type[] { typeof(global::Unit), typeof(global::Unit) })]
    internal static class WeaponStationFireGroundSupportPatch
    {
        private static void Postfix(global::Unit owner, global::Unit target)
        {
            GroundSupportAttackPatchHelpers.TryEnqueue(target, owner);
        }
    }

    /// <summary>Missile/bomb-style stations use LaunchMount rather than Fire.</summary>
    [HarmonyPatch(typeof(global::WeaponStation), "LaunchMount", new Type[] { typeof(global::Unit), typeof(global::Unit), typeof(global::GlobalPosition) })]
    internal static class WeaponStationLaunchGroundSupportPatch
    {
        private static void Postfix(global::Unit owner, global::Unit target)
        {
            GroundSupportAttackPatchHelpers.TryEnqueue(target, owner);
        }
    }

    internal static class GroundSupportAttackPatchHelpers
    {
        // Automatic weapons call WeaponStation.Fire repeatedly. The director performs the
        // longer group-level suppression; this short throttle keeps the event bus itself quiet.
        private const float PerVehicleEventSeconds = 2f;
        // Throttle entries are useless after PerVehicleEventSeconds, but nothing else ever
        // removes them, so sweep once the table grows past a bound to keep long hosted
        // sessions from accumulating one entry per victim/attacker pair forever.
        private const int SweepThreshold = 256;
        private static readonly System.Collections.Generic.Dictionary<ulong, float> LastEventAt =
            new System.Collections.Generic.Dictionary<ulong, float>(64);
        private static readonly System.Collections.Generic.List<ulong> StaleKeys =
            new System.Collections.Generic.List<ulong>(64);

        private static void EvictStale(float now)
        {
            if (LastEventAt.Count < SweepThreshold)
                return;

            StaleKeys.Clear();
            foreach (System.Collections.Generic.KeyValuePair<ulong, float> pair in LastEventAt)
            {
                if (now - pair.Value >= PerVehicleEventSeconds)
                    StaleKeys.Add(pair.Key);
            }

            for (int i = 0; i < StaleKeys.Count; i++)
                LastEventAt.Remove(StaleKeys[i]);
        }

        public static void TryEnqueue(global::Unit victim, global::Unit attacker)
        {
            if (victim == null || attacker == null || victim == attacker ||
                !(victim is global::GroundVehicle) || victim.disabled ||
                Plugin.Cfg == null || !Plugin.Cfg.GroundSupportRequests.Value ||
                !Plugin.Cfg.VoiceCommandsEnabled.Value)
            {
                return;
            }

            try
            {
                global::FactionHQ localHq;
                if (!global::GameManager.GetLocalHQ(out localHq) || localHq == null ||
                    victim.NetworkHQ != localHq || attacker.NetworkHQ == localHq)
                {
                    return;
                }

                uint victimId = GameAdapter.PersistentId(victim);
                uint attackerId = attacker is global::GroundVehicle
                    ? GameAdapter.PersistentId(attacker)
                    : 0;
                ulong attackKey = ((ulong)victimId << 32) | attackerId;
                float now = Time.unscaledTime;
                EvictStale(now);
                float last;
                if (victimId == 0 ||
                    (LastEventAt.TryGetValue(attackKey, out last) && now - last < PerVehicleEventSeconds))
                {
                    return;
                }

                LastEventAt[attackKey] = now;
                RadioEventBus.Enqueue(new RadioEvent
                {
                    Type = RadioEventType.GroundUnitUnderAttack,
                    SubjectId = victimId,
                    SubjectName = GameAdapter.RadioName(victim),
                    SubjectIsFriendly = true,
                    Position = GameAdapter.PositionOf(victim),
                    AttackerId = attackerId,
                    Text = GameAdapter.RadioName(attacker)
                });
            }
            catch
            {
                // Combat hooks must never disrupt the game's own weapon/damage path.
            }
        }
    }
}

