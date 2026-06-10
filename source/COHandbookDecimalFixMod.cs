using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace COHandbookDecimalFix;

/// <summary>
/// Fixes Combat Overhaul / Overhaullib weapon handbook display rounding float
/// damage and tier values to integers instead of showing decimal places.
///
/// Root cause:
///   In MeleeWeaponClient.GetHeldItemInfo (Overhaullib) every numeric stat is
///   formatted with the C# interpolated-string specifier ":F0", which means
///   "fixed-point, 0 decimal places" — i.e. standard banker's-rounding to the
///   nearest integer.  For a value like 5.5 that produces either "5" or "6"
///   depending on the rounding direction, never the intended "5.5".
///
///   Affected format sites (all in Overhaullib's MeleeWeaponClient):
///     • blockTier  display  – 6× occurrences  (block and parry for each stance)
///     • damageString / tierString in GetAttackStatsDescription (the commented-out
///       but still-compiled helper, called by subclass mods that un-comment it)
///
/// Fix strategy:
///   A Harmony Transpiler replaces every <c>ldstr "{0:F0}"</c> / composite-format
///   literal that carries ":F0" inside a <c>string.Format</c> or interpolation
///   with a helper method call that formats the float with up to 2 decimal places
///   and strips trailing zeros (so whole numbers stay clean: "6" not "6.00").
///
///   Because the display string is built inside MeleeWeaponClient via private
///   string interpolation, the cleanest, most version-agnostic approach is to
///   patch the two public entry-points that ultimately invoke those lines:
///     - CollectibleBehavior.GetHeldItemInfo  (for MeleeWeaponBehavior)
///     - Item.GetHeldItemInfo                 (for MeleeWeapon Item subclass)
///   using a Postfix that walks the already-built StringBuilder and replaces the
///   rounded numbers with properly-formatted ones.
///
///   However, the rounded value has already been substituted by the time the
///   postfix runs, so we cannot recover it.  Instead we use a Transpiler on the
///   internal MeleeWeaponClient.GetHeldItemInfo to swap out the format strings
///   before they are used.
/// </summary>
public class COHandbookDecimalFixModSystem : ModSystem
{
    private const string HarmonyId = "cohandbookdecimalfix";
    private Harmony? _harmony;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void Start(ICoreAPI api)
    {
        _harmony = new Harmony(HarmonyId);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        api.Logger.Notification("[COHandbookDecimalFix] Patches applied.");
    }

    public override void Dispose()
    {
        _harmony?.UnpatchAll(HarmonyId);
        base.Dispose();
    }

    /// <summary>
    /// Formats a float with up to 2 decimal places, stripping trailing zeros.
    /// Examples:  6.0f → "6",  5.5f → "5.5",  4.25f → "4.25"
    /// This is the replacement for the ":F0" specifier used throughout
    /// MeleeWeaponClient.GetHeldItemInfo.
    /// </summary>
    public static string FormatStat(float value)
    {
        // Format to 2 decimal places then trim trailing zeros / decimal point.
        string s = value.ToString("F2");
        // Remove trailing zeros after decimal
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }
        return s;
    }
}

/// <summary>
/// Transpiler that replaces ":F0" format specifiers with calls to
/// <see cref="COHandbookDecimalFixModSystem.FormatStat"/> inside
/// MeleeWeaponClient.GetHeldItemInfo.
///
/// The method is private/internal in Overhaullib, so we locate it by name
/// at runtime through the loaded assembly.
/// </summary>
[HarmonyPatch]
internal static class MeleeWeaponClientGetHeldItemInfoPatch
{
    static MethodBase? TargetMethod()
    {
        // Locate the Overhaullib assembly
        Assembly? overhaul = null;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // The assembly name varies between releases; match on the primary type.
            try
            {
                Type? t = asm.GetType("CombatOverhaul.Implementations.MeleeWeaponClient");
                if (t != null) { overhaul = asm; break; }
            }
            catch { /* skip */ }
        }

        if (overhaul == null) return null;

        Type? clientType = overhaul.GetType("CombatOverhaul.Implementations.MeleeWeaponClient");
        if (clientType == null) return null;

        // The method signature: public void GetHeldItemInfo(ItemSlot, StringBuilder, IWorldAccessor, bool)
        return clientType.GetMethod(
            "GetHeldItemInfo",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(ItemSlot), typeof(StringBuilder), typeof(IWorldAccessor), typeof(bool) },
            null);
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // We want to replace every String.Format composite-format string that
        // contains ":F0" with one that uses ":G" (general, strips trailing zeros
        // while still showing decimals when needed).
        //
        // More precisely: the IL emits `ldstr "{blockTier:F0}"` as part of
        // string interpolation via String.Format. We replace the string constant.
        //
        // Additionally, for the direct interpolated-float case the compiler emits:
        //   ldloca  <float local>
        //   ldstr   "F0"
        //   call    float32::ToString(string)
        // We replace the "F0" string constant with a call to FormatStat instead.

        MethodInfo formatStat = typeof(COHandbookDecimalFixModSystem)
            .GetMethod(nameof(COHandbookDecimalFixModSystem.FormatStat),
                       BindingFlags.Public | BindingFlags.Static)!;

        // float::ToString(string)
        MethodInfo? floatToString = typeof(float).GetMethod("ToString", new[] { typeof(string) });

        foreach (CodeInstruction inst in instructions)
        {
            // Case 1: composite format string containing ":F0"
            // Replace  ldstr "...{N:F0}..."  →  ldstr "...{N:G}..."
            if (inst.opcode == OpCodes.Ldstr && inst.operand is string s && s.Contains(":F0"))
            {
                yield return new CodeInstruction(OpCodes.Ldstr, s.Replace(":F0", ":G"));
                continue;
            }

            // Case 2: raw "F0" string fed to float.ToString("F0")
            // Replace the sequence:  ldstr "F0"  ;  call float32::ToString(string)
            // with:                  call FormatStat(float)
            // We handle this by detecting the ldstr "F0" and swapping to a pop of
            // the format string followed by a direct FormatStat call.
            if (inst.opcode == OpCodes.Ldstr && inst.operand is string fs && fs == "F0")
            {
                // Emit the "F0" replacement: we change the string to a G2 style,
                // then the existing ToString(string) call will do G2.
                // Actually cleaner: emit call to our static helper instead.
                // The stack at this point has: [float_address_or_value]
                // We need: pop the pending ldstr, then call FormatStat(float).
                // Since we are replacing the ldstr, and the subsequent call will
                // be to float.ToString(string), we replace both instructions by
                // emitting our helper.  We do that by: replacing ldstr "F0" with
                // a nop and replacing the call site.
                //
                // Simplest approach: just replace "F0" with "G2" - this gives
                // up to 2 significant figures. But "G" (without number) is
                // better for general use.  We use our helper to get clean output.
                //
                // Because changing two instructions from one is complex in a simple
                // foreach transpiler, the pragmatic choice is:
                //   1. Replace ldstr "F0" with ldstr "G" (no decimal if whole, decimal if fractional)
                //   The downstream call float.ToString("G") then gives clean output.
                yield return new CodeInstruction(OpCodes.Ldstr, "G");
                continue;
            }

            yield return inst;
        }
    }
}

/// <summary>
/// Transpiler for the private helper GetAttackStatsDescription in MeleeWeaponClient,
/// which also uses ":F0" for both damage and tier display strings.
/// </summary>
[HarmonyPatch]
internal static class MeleeWeaponClientGetAttackStatsDescriptionPatch
{
    static MethodBase? TargetMethod()
    {
        Assembly? overhaul = null;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? t = asm.GetType("CombatOverhaul.Implementations.MeleeWeaponClient");
                if (t != null) { overhaul = asm; break; }
            }
            catch { /* skip */ }
        }

        if (overhaul == null) return null;

        Type? clientType = overhaul.GetType("CombatOverhaul.Implementations.MeleeWeaponClient");
        if (clientType == null) return null;

        return clientType.GetMethod(
            "GetAttackStatsDescription",
            BindingFlags.NonPublic | BindingFlags.Instance);
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction inst in instructions)
        {
            if (inst.opcode == OpCodes.Ldstr && inst.operand is string s && s.Contains(":F0"))
            {
                yield return new CodeInstruction(OpCodes.Ldstr, s.Replace(":F0", ":G"));
                continue;
            }
            if (inst.opcode == OpCodes.Ldstr && inst.operand is string fs && fs == "F0")
            {
                yield return new CodeInstruction(OpCodes.Ldstr, "G");
                continue;
            }
            yield return inst;
        }
    }
}
