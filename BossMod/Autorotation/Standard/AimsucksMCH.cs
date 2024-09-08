using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

// ReSharper disable once CheckNamespace
namespace BossMod.Autorotation;

/*
 * Half the logic here is shamelessly copied from Kage (@evil3yes) on the XIVSlothCombo team.
 * This was all possible due to the help of veyn (@veyn), croizat (@croizat), and xan (@xan_0).
 */

/*
 * Reference materials:
 * BossMod/BossMod/ActionQueue/ActionDefinition.cs (specifically ChargeCapIn) if we need to figure out if we've used a Drill charge
 */

public sealed class AimsucksMCH(RotationModuleManager manager, Actor player) : RotationModule(manager, player)
{
    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition(
            "Aimsucks MCH",
            "Developed based off of XIVSlothCombo's Machinist rotation",
            "Aimsucks",
            RotationModuleQuality.WIP,
            BitMask.Build((int)Class.MCH),
            100);

        // This is where strategies would go... IF I HAD ONE!

        return res;
    }

    private enum GCDPriority
    {
        None = 0, // All "none" actions are not cast

        // Higher priorities will bump actions higher in the queue
        DelayCombo = 350,
        FlexibleCombo = 400,
        ForcedCombo = 880,

        // GCD cooldown priorities
        SecondDrill = 590,
        FullMetalField = 600,
        Excavator = 610,
        ChainSaw = 620,
        Drill = 630,
        AirAnchor = 640,

        // Assign a very high priority for Blazing Shot
        BlazingShot = 900
    }

    private enum OGCDPriority
    {
        None = 0, // All "none" actions are not cast

        // Same priorities for our two main oGCD actions
        DoubleCheckOrCheckmate = 500
    }

    // Variables to read both of Machinist's gauges, summons, and status
    private int Heat;
    private int Battery;
    private float OverheatedRemainingTime;
    private float QueenRemainingTime;
    private int LastQueenBatteryUsed;

    // Variables to read information pertaining to GCDs
    private float GCDLength;
    private MCH.AID NextGCD;
    private GCDPriority NextGCDPrio;

    // Variable for the last used "Check" ability, either Double Check or Checkmate
    private MCH.AID PrevCheck;

    // Variables to get the amount "left" on a buff
    private float FullMetalMachinistLeft;
    private float ExcavatorReadyLeft;
    private float OverheatedLeft;

    // Variables to get the cooldowns of actions
    private float WildfireCD;

    // Functions to check if an action is unlocked
    private bool Unlocked(MCH.AID aid) => ActionUnlocked(ActionID.MakeSpell(aid));
    private bool Unlocked(MCH.TraitID tid) => TraitUnlocked((uint)tid);

    // Functions to check action cooldowns
    private float CD(MCH.AID aid) =>
        World.Client.Cooldowns[ActionDefinitions.Instance.Spell(aid)!.MainCooldownGroup].Remaining;

    // Function to check if a GCD can be fit in a certain time window
    private bool CanFitGCD(float deadline, int extraGCDs = 0) => GCD + GCDLength * extraGCDs < deadline;

    // Function to provide the AID of the previous combo action
    private MCH.AID PrevCombo => (MCH.AID)World.Client.ComboState.Action;

    // Function to check if the action is the first GCD cast in combat
    private bool IsFirstGCD() => !Player.InCombat || (World.CurrentTime - Manager.CombatStart).TotalSeconds < 0.1f;

    /*
     * Execute is what runs the rotation
     * We branch a lot of the logic out to individual functions
     */
    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay,
        float forceMovementIn, bool isMoving)
    {
        // Update our two gauges
        Heat = GetGauge<MachinistGauge>().Heat;
        Battery = GetGauge<MachinistGauge>().Battery;

        // Get various stats Dalamud exposes for our rotation
        OverheatedRemainingTime = GetGauge<MachinistGauge>().OverheatTimeRemaining;
        QueenRemainingTime = GetGauge<MachinistGauge>().SummonTimeRemaining;
        LastQueenBatteryUsed = GetGauge<MachinistGauge>().LastSummonBatteryPower;

        // Get our GCD length depending on stats
        GCDLength = ActionSpeed.GCDRounded(World.Client.PlayerStats.SkillSpeed, World.Client.PlayerStats.Haste,
            Player.Level);

        // Check our buffs and update how long we have left on them
        FullMetalMachinistLeft = SelfStatusLeft(MCH.SID.FullMetalMachinist);
        ExcavatorReadyLeft = SelfStatusLeft(MCH.SID.ExcavatorReady);
        OverheatedLeft = SelfStatusLeft(MCH.SID.Overheated);

        // Check our cooldowns and update how long we have left on them
        WildfireCD = CD(MCH.AID.Wildfire);

        // ------------------------
        // Begin processing actions
        // ------------------------
        UseBlazingShot(primaryTarget);
        UseFullMetalField(primaryTarget);
        UseSingleTargetTools(primaryTarget);

        // Essentially our lowest priority: always be casting our 1-2-3 combo
        QueueGCD(NextComboSingleTarget(), primaryTarget, GCDPriority.FlexibleCombo);
    }

    /*
     * Determine the next action for our 1-2-3 combo
     */
    private MCH.AID NextComboSingleTarget() => PrevCombo switch
    {
        MCH.AID.SlugShot => MCH.AID.CleanShot,
        MCH.AID.SplitShot => MCH.AID.SlugShot,
        _ => MCH.AID.SplitShot
    };

    /*
     * Determine the next action for our tools
     */
    private void UseSingleTargetTools(Actor? target)
    {
        QueueGCD(MCH.AID.AirAnchor, target, GCDPriority.AirAnchor);

        // Change the priority depending on if we have 1 or 2 charges (determined by if Drill's CD > 0)
        QueueGCD(MCH.AID.Drill, target, CD(MCH.AID.Drill) > 0 ? GCDPriority.SecondDrill : GCDPriority.Drill);

        QueueGCD(MCH.AID.ChainSaw, target, GCDPriority.ChainSaw);

        // Check if Excavator Ready buff is on us before queueing Excavator
        if (ExcavatorReadyLeft > 0) QueueGCD(MCH.AID.Excavator, target, GCDPriority.Excavator);
    }

    /*
     * Always make sure we alternate between Double Check and Checkmate
     * TODO: Change this to "use whichever has a lower cooldown" to make it more even
     */
    private void UseDoubleCheckOrCheckmate(Actor? target)
    {
        // Figure out what our next cast is going to be based on the previous "Check" ability
        MCH.AID NextCheck = PrevCheck == MCH.AID.DoubleCheck ? MCH.AID.Checkmate : MCH.AID.DoubleCheck;

        // Queue the next "Check" ability we want to cast
        QueueOGCD(NextCheck, target, OGCDPriority.DoubleCheckOrCheckmate);

        // Set "previous" ability to the one we just queued
        PrevCheck = NextCheck;
    }

    private void UseFullMetalField(Actor? target)
    {
        // We cannot use Full Metal Field with no Full Metal Machinist buff
        if (FullMetalMachinistLeft == 0) return;

        // Condition 1: Always use Full Metal Field right before Wildfire
        // Condition 2: In an emergency, use Full Metal Field before the buff expires
        // Full Metal Field's priority is less than the rest of the tools, so it should happen after
        if (WildfireCD <= GCDLength || FullMetalMachinistLeft <= 6)
            QueueGCD(MCH.AID.FullMetalField, target, GCDPriority.FullMetalField);
    }

    private void UseBlazingShot(Actor? target)
    {
        // We cannot use Blazing Shot with no stacks of Overheated
        if (OverheatedLeft == 0) return;

        QueueGCD(MCH.AID.BlazingShot, target, GCDPriority.BlazingShot);
    }

    /*
     * Determine the next action for our 1-2-3 combo
     */
    // private (MCH.AID, GCDPriority) ComboActionPriority()
    // {
    //     // Report how many steps are left in our 1-2-3 single-target combo
    //     var comboStepsRemaining = PrevCombo switch
    //     {
    //         MCH.AID.SplitShot => Unlocked(MCH.AID.CleanShot) ? 2 : Unlocked(MCH.AID.SlugShot) ? 1 : 0,
    //         MCH.AID.SlugShot => Unlocked(MCH.AID.CleanShot) ? 1 : 0,
    //         MCH.AID.CleanShot => 0,
    //         _ => 0
    //     };
    // }

    // private bool ShouldUseQueen()
    // {

        /*
         * Every non-opener 2 minute burst we use queen, we should check for the following conditions:
         * 1. Wildfire has a short cooldown (ideally a few seconds)
         * 2. Air Anchor has just been used to get us from 80 Battery to 100
         */

    //     return;
    // }

    /*
     * Provide values for how long it takes actions to actually "hit" the target
     * Used primarily for pre-pull actions
     */
    private float EffectApplicationDelay(MCH.AID aid) => aid switch
    {
        MCH.AID.AirAnchor => 0.50f,
        _ => 0
    };

    /*
     * Helper function to queue GCD action
     */
    private void QueueGCD(MCH.AID aid, Actor? target, GCDPriority prio)
    {
        if (prio != GCDPriority.None)
        {
            var delay = !Player.InCombat && World.Client.CountdownRemaining > 0
                ? Math.Max(0, World.Client.CountdownRemaining.Value - EffectApplicationDelay(aid))
                : 0;
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), target, ActionQueue.Priority.High + (int)prio,
                delay: delay);
            if (prio > NextGCDPrio)
            {
                NextGCD = aid;
                NextGCDPrio = prio;
            }
        }
    }

    /*
     * Helper function to queue oGCD action
     */
    private void QueueOGCD(MCH.AID aid, Actor? target, OGCDPriority prio, float basePrio = ActionQueue.Priority.Low)
    {
        if (prio != OGCDPriority.None)
        {
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), target, basePrio + (int)prio);
        }
    }
}
