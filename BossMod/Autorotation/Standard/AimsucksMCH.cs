using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

// ReSharper disable once CheckNamespace
namespace BossMod.Autorotation;

/*
 * Half the logic here is shamelessly copied from Kage (@evil3yes) on the XIVSlothCombo team.
 * This was all possible due to the help of veyn (@veyn), croizat (@croizat), and xan (@xan_0).
 */

/*
 * Reference materials:
 * Opener, burst, and Queen usage: https://www.thebalanceffxiv.com/jobs/ranged/machinist/basic-guide/
 * Actions: https://www.thebalanceffxiv.com/jobs/ranged/machinist/skills-overview/
 * https://discord.com/channels/1001823907193552978/1191076246860349450/1282433792304222281
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
        DelayedCombo = 350,
        FlexibleCombo = 400,
        ForcedCombo = 880,

        // GCD cooldown priorities
        SecondDrill = 590,
        FullMetalField = 600,
        OpenerSecondDrill = 610,
        Excavator = 620,
        ChainSaw = 630,
        Drill = 640,
        AirAnchor = 650,

        // Assign a very high priority for Blazing Shot
        BlazingShot = 900
    }

    private enum OGCDPriority
    {
        None = 0, // All "none" actions are not cast

        // Same priorities for our two main oGCD actions
        DoubleCheckOrCheckmate = 500,

        Reassemble = 600,
        BarrelStabilizer = 650,
        Wildfire = 700,
        Hypercharge = 800
    }

    // Variable to record the combat timer
    private float CombatTimer;

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

    // Variables to get the amount "left" on a buff
    private float FullMetalMachinistLeft;
    private float ExcavatorReadyLeft;
    private float OverheatedLeft;
    private float ReassembledLeft;
    private float HyperchargedLeft;

    // Variables to get the cooldowns of actions
    private float WildfireCD;

    // Functions to check if an action is unlocked
    private bool Unlocked(MCH.AID aid) => ActionUnlocked(ActionID.MakeSpell(aid));
    private bool Unlocked(MCH.TraitID tid) => TraitUnlocked((uint)tid);

    // Functions to check action cooldowns
    private float CD(MCH.AID aid) =>
        World.Client.Cooldowns[ActionDefinitions.Instance.Spell(aid)!.MainCooldownGroup].Remaining;

    // Functions to calculate the time until max charges of an action
    // and the time until the action's next charge is available
    private float NextChargeIn(MCH.AID aid) => Unlocked(aid)
        ? ActionDefinitions.Instance.Spell(aid)!.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions)
        : float.MaxValue;

    private float MaxChargesIn(MCH.AID aid) => Unlocked(aid)
        ? ActionDefinitions.Instance.Spell(aid)!.ChargeCapIn(World.Client.Cooldowns, World.Client.DutyActions, Player.Level)
        : float.MaxValue;


    // Functions to check lowest of 4 tool cooldowns for Hypercharge
    // and the time until the next available tool for Reassemble
    private float NextToolIn =>
        MathF.Min(NextChargeIn(MCH.AID.AirAnchor),
            MathF.Min(NextChargeIn(MCH.AID.Drill),
                NextChargeIn(MCH.AID.ChainSaw)));

    private float CappedToolIn =>
        MathF.Min(MaxChargesIn(MCH.AID.AirAnchor),
            MathF.Min(MaxChargesIn(MCH.AID.Drill),
                MaxChargesIn(MCH.AID.ChainSaw)));

    // Function to check if a GCD can be fit in a certain time window
    private bool CanFitGCD(float deadline, int extraGCDs = 0) => GCD + GCDLength * extraGCDs < deadline;

    // Function to provide the AID of the previous combo action
    private MCH.AID PrevCombo => (MCH.AID)World.Client.ComboState.Action;

    // Function to check if the action is the first GCD cast in combat
    private bool IsFirstGCD() => !Player.InCombat || (World.CurrentTime - Manager.CombatStart).TotalSeconds < 0.1f;

    /*
     * Execute Rotation
     * Note: We branch a lot of the logic out to individual functions
     *
     * TODO: Queen, Hypercharge, and making sure we don't use Reassemble until buff window in opener
     */
    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay,
        float forceMovementIn, bool isMoving)
    {
        // Update the combat timer
        CombatTimer = (float)(World.CurrentTime - Manager.CombatStart).TotalSeconds;

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
        ReassembledLeft = SelfStatusLeft(MCH.SID.Reassembled);
        HyperchargedLeft = SelfStatusLeft(MCH.SID.Hypercharged);

        // Check our cooldowns and update how long we have left on them
        WildfireCD = CD(MCH.AID.Wildfire);

        // Set our next GCD and next GCD priorities so they can be overwritten by QueueGCD()
        NextGCD = MCH.AID.None;
        NextGCDPrio = GCDPriority.None;

        // Begin processing actions

        // oGCD actions
        UseHypercharge(primaryTarget);
        UseWildfire(primaryTarget);
        UseBarrelStabilizer(primaryTarget);
        UseReassemble(primaryTarget);
        UseDoubleCheckOrCheckmate(primaryTarget);

        // GCD actions
        UseBlazingShot(primaryTarget);
        UseFullMetalField(primaryTarget);
        UseSingleTargetTools(primaryTarget);

        UseComboSingleTarget(primaryTarget);

        // Service.Log(string.Format("GCD: {0:N2}", GCD));
    }

    /*
     * Single-Target 1-2-3 Combo (Split Shot, Slug Shot, Clean Shot)
     * Note: Covers heated variants automatically!
     */
    private void UseComboSingleTarget(Actor? target)
    {
        var ComboAction = PrevCombo switch
        {
            MCH.AID.SlugShot => MCH.AID.CleanShot,
            MCH.AID.SplitShot => MCH.AID.SlugShot,
            _ => MCH.AID.SplitShot
        };

        QueueGCD(ComboAction, target, GCDPriority.FlexibleCombo);
    }

    /*
     * Tools (Air Anchor, Drill, Chain Saw, Excavator)
     * 1. Queue each with the following priorities:
     *   a. Air Anchor
     *   b. Drill 1
     *   c. Chain Saw
     *   d. Excavator (check for Excavator Ready buff as well)
     *   e. Drill 2
     */
    private void UseSingleTargetTools(Actor? target)
    {
        QueueGCD(MCH.AID.AirAnchor, target, GCDPriority.AirAnchor);

        // TODO: Make this work without reading the combat timer
        // Opener usage
        if (CombatTimer < 20) QueueGCD(MCH.AID.Drill, target, CD(MCH.AID.Drill) > 0 ? GCDPriority.OpenerSecondDrill : GCDPriority.Drill);

        // Change the priority depending on if we have 1 or 2 charges (determined by if Drill's CD > 0)
        if (CombatTimer > 20) QueueGCD(MCH.AID.Drill, target, CD(MCH.AID.Drill) > 0 ? GCDPriority.SecondDrill : GCDPriority.Drill);

        // Change the priority depending on if we have 1 or 2 charges (determined by if Drill's CD > 0)
        // QueueGCD(MCH.AID.Drill, target, CD(MCH.AID.Drill) > 0 ? GCDPriority.SecondDrill : GCDPriority.Drill);

        QueueGCD(MCH.AID.ChainSaw, target, GCDPriority.ChainSaw);

        // Check if Excavator Ready buff is on us before queueing Excavator
        if (ExcavatorReadyLeft > 0) QueueGCD(MCH.AID.Excavator, target, GCDPriority.Excavator);
    }

    /*
     * Full Metal Field
     * 1. Check that we have the Full Metal Machinist buff (from Barrel Stabilizer)
     * 2. In 2-minute burst, use FMF right before Wildfire by looking at Wildfire's CD and comparing it to a GCD length
     * 3. If our buff is about to expire, cast FMF in an emergency
     */
    private void UseFullMetalField(Actor? target)
    {
        // We cannot use Full Metal Field with no Full Metal Machinist buff
        if (FullMetalMachinistLeft == 0) return;

        if (CombatTimer < 20 && NextToolIn > GCDLength
            || WildfireCD <= GCDLength
            || FullMetalMachinistLeft <= 6)
            QueueGCD(MCH.AID.FullMetalField, target, GCDPriority.FullMetalField);
    }

    /*
     * Blazing Shot
     * 1. Check that we're overheated
     */
    private void UseBlazingShot(Actor? target)
    {
        // We cannot use Blazing Shot with no stacks of Overheated
        if (OverheatedLeft == 0) return;

        QueueGCD(MCH.AID.BlazingShot, target, GCDPriority.BlazingShot);
    }

    /*
     * Double Check and Checkmate
     * 1. Always make sure we alternate between Double Check and Checkmate depending on shortest CD
     */
    private void UseDoubleCheckOrCheckmate(Actor? target)
    {
        // Prevent use of the action if we are in a pre-pull state or during downtime
        if (!Player.InCombat || target == null || target.IsAlly)
            return;

        // Get cooldowns for both actions
        var DoubleCheckIn = CD(MCH.AID.DoubleCheck);
        var CheckmateIn = CD(MCH.AID.Checkmate);

        // Compare the cooldowns of the two abilities and queue the one with a longer cooldown
        MCH.AID NextCheck;
        NextCheck = DoubleCheckIn > CheckmateIn ? MCH.AID.Checkmate : MCH.AID.DoubleCheck;

        // Queue the next "Check" ability we want to cast
        QueueOGCD(NextCheck, target, OGCDPriority.DoubleCheckOrCheckmate);
    }

    /*
     * Reassemble
     * 1. Do not use it inside a Hypercharge burst window
     * 2. Always use when the next GCD is a tool
     * 3. Always use ~5s before the fight starts
     */
    private void UseReassemble(Actor? target)
    {
        // If we are in a burst window or already have the buff, do not use it
        if (OverheatedLeft > 0 || ReassembledLeft > 0) return;

        // MCH.AID[] Tools = { MCH.AID.AirAnchor, MCH.AID.Drill, MCH.AID.ChainSaw, MCH.AID.Excavator };
        // if(Tools.Contains(NextGCD)) QueueOGCD(MCH.AID.Reassemble, target, OGCDPriority.Reassemble);

        // Check if a tool will be available within 1 GCD
        // Alternatively, use it before the countdown timer finishes, determined by Reassemble's EffectApplicationDelay below
        if (NextToolIn <= GCDLength
            || World.Client.CountdownRemaining is > 0 and < 5)
            QueueOGCD(MCH.AID.Reassemble, target, OGCDPriority.Reassemble);
    }

    /*
     * Barrel Stabilizer
     * 1. Do not use it inside a Hypercharge burst window
     * 2. Because this is a 2-minute cast, always use when Drill has a cooldown to ensure it's cast after Drill
     *
     * Note: This accounts for the very beginning of the fight, and since we will *always* have Drill on CD
     * during the fight, it should just use Barrel Stabilizer every 2 minutes
     *
     * TODO: Figure out how to account for fight downtime if it becomes an issue
     */
    private void UseBarrelStabilizer(Actor? target)
    {
        // Prevent use of the action if we are in a pre-pull state or during downtime
        if (!Player.InCombat || target == null || target.IsAlly)
            return;

        // If we are in a burst window, do not use it
        if (OverheatedLeft > 0) return;

        // Use when Drill is on cooldown (so after the very first drill)
        if (CD(MCH.AID.Drill) > 0) QueueOGCD(MCH.AID.BarrelStabilizer, target, OGCDPriority.BarrelStabilizer);
    }

    /*
     * Wildfire
     * 1. Do not use it inside a Hypercharge burst window
     * 2. If in the opener, use it after all the tools have been used but before FMF has been used
     * 3. If in a 2-minute burst window, use it right after Full Metal Machinist expires, which coincides with when
     *     we use Full Metal Field
     *
     * TODO: Remove the requirement for the combat timer here
     * TODO: Try to late weave Wildfire so it's the second oGCD
     */
    private void UseWildfire(Actor? target)
    {
        // Prevent use of the action if we are in a pre-pull state or during downtime
        if (!Player.InCombat || target == null || target.IsAlly)
            return;

        // If we are in a burst window, do not use it
        // if (OverheatedLeft > 0) return;

        // Opener usage
        if (CombatTimer < 60)
        {
            // Use before FMF and after all the tools have been used
            if (FullMetalMachinistLeft > 0 && NextToolIn > GCDLength)
                QueueOGCD(MCH.AID.Wildfire, target, OGCDPriority.Wildfire);
        }


        // 2-minute burst window usage
        else if (CombatTimer > 60)
        {
            // Use after Full Metal Field and Hypercharge
            if (FullMetalMachinistLeft == 0 && OverheatedLeft > 0)
                QueueOGCD(MCH.AID.Wildfire, target, OGCDPriority.Wildfire);
        }
    }

    /*
     * Hypercharge
     * 1. Do not use while Overheated
     * 2. Do not use while heat is under 50 unless we have the Hypercharged buff
     * 3. Do not use if a tool will cap within a Hypercharge window
     * 4. Always use after FMF
     */
    private void UseHypercharge(Actor? target)
    {
        // Prevent use of the action if we are in a pre-pull state or during downtime
        if (!Player.InCombat || target == null || target.IsAlly)
            return;

        // Check multiple conditions to prevent action misuse
        if (Heat < 50 && HyperchargedLeft == 0
            || OverheatedLeft > 0
            || ReassembledLeft > GCDLength) return;

        // Use it after FMF has been used
        // TODO: Try to see if we can late weave Hypercharge after FMF
        if (FullMetalMachinistLeft > 0) return;

        if (CappedToolIn > GCD + 7.5f)
            QueueOGCD(MCH.AID.Hypercharge, target, OGCDPriority.Hypercharge);
    }

    /*
     * Hypercharge
     * 1. Make sure the next tool charge is in > 8 seconds before you queue Hypercharge
     */

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
     * Effect Application Delay
     * Note: This provides values for how long it takes actions to actually "hit" the target, which
     * are used primarily for pre-pull actions
     *
     * Source: https://docs.google.com/spreadsheets/d/1Emevsz5_oJdmkXy23hZQUXimirZQaoo5BejSzL3hZ9I
     */
    private float EffectApplicationDelay(MCH.AID aid) => aid switch
    {
        // Special case for Reassemble to make sure it's used at ~4s before combat (it's instant otherwise)
        MCH.AID.Reassemble => 4.0f,

        // Sourced from spreadsheet linked above
        MCH.AID.Detonator => 0.62f,
        MCH.AID.Dismantle => 0.62f,
        MCH.AID.Tactician => 0.62f,
        MCH.AID.HeatBlast => 0.62f,
        MCH.AID.Ricochet => 0.62f,
        MCH.AID.Wildfire => 0.67f,
        MCH.AID.Checkmate => 0.71f,
        MCH.AID.DoubleCheck => 0.71f,
        MCH.AID.HeatedSplitShot => 0.80f,
        MCH.AID.GaussRound => 0.80f,
        MCH.AID.HeatedCleanShot => 0.80f,
        MCH.AID.HeatedSlugShot => 0.80f,
        MCH.AID.BlazingShot => 0.85f,
        MCH.AID.AutoCrossbow => 0.89f,
        MCH.AID.Flamethrower => 0.89f,
        MCH.AID.Bioblaster => 0.97f,
        MCH.AID.FullMetalField => 1.02f,
        MCH.AID.ChainSaw => 1.03f,
        MCH.AID.Excavator => 1.07f,
        MCH.AID.Scattergun => 1.15f,
        MCH.AID.Drill => 1.15f,
        MCH.AID.AirAnchor => 1.15f,
        MCH.AID.SatelliteBeam => 3.16f,
        MCH.AID.BarrelStabilizer => 0,
        MCH.AID.Hypercharge => 0,
        // MCH.AID.Reassemble => 0,
        _ => 0
    };

    /*
     * Queue GCD helper function
     * 1. Make sure priority is not set to "None"
     * 2. Set delay to the length of the countdown minus the EffectApplicationDelay to properly use pre-pull actions
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
     * Queue oGCD helper function
     * 1. Make sure priority is not set to "None"
     * 2. Set delay to the length of the countdown minus the EffectApplicationDelay to properly use pre-pull actions
     */
    private void QueueOGCD(MCH.AID aid, Actor? target, OGCDPriority prio, float basePrio = ActionQueue.Priority.Low)
    {
        if (prio != OGCDPriority.None)
        {
            var delay = !Player.InCombat && World.Client.CountdownRemaining > 0
                ? Math.Max(0, World.Client.CountdownRemaining.Value - EffectApplicationDelay(aid))
                : 0;
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), target, basePrio + (int)prio,
                delay: delay);
        }
    }
}
