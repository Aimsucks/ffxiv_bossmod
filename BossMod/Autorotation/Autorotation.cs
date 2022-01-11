﻿using Dalamud.Hooking;
using System;

namespace BossMod
{
    class Autorotation : IDisposable
    {
        private delegate ulong OnGetIconDelegate(byte param1, uint param2);
        private Hook<OnGetIconDelegate> _iconHook;
        private unsafe float* _comboTimeLeft = null;
        private unsafe WARRotation.AID* _comboLastMove = null;

        public unsafe float ComboTimeLeft => *_comboTimeLeft;
        public unsafe WARRotation.AID ComboLastMove => *_comboLastMove;

        public WARActions WarActions { get; init; } = new();

        public unsafe Autorotation()
        {
            IntPtr comboPtr = Service.SigScanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? 80 7E 21 00", 0x178);
            _comboTimeLeft = (float*)comboPtr;
            _comboLastMove = (WARRotation.AID*)(comboPtr + 0x4);

            var getIcon = Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 8B F8 3B DF"); // 5.4
            _iconHook = new(getIcon, new OnGetIconDelegate(GetIconDetour));
            _iconHook.Enable();
        }

        public void Dispose()
        {
            _iconHook.Dispose();
        }

        public void Update()
        {
            WarActions.Update(ComboLastMove, ComboTimeLeft);
        }

        public void Draw()
        {
            WarActions.DrawActionHint(false);
        }

        /// <summary>
        ///     Replace an ability with another ability
        ///     actionID is the original ability to be "used"
        ///     Return either actionID (itself) or a new Action table ID as the
        ///     ability to take its place.
        ///     I tend to make the "combo chain" button be the last move in the combo
        ///     For example, Souleater combo on DRK happens by dragging Souleater
        ///     onto your bar and mashing it.
        /// </summary>
        private ulong GetIconDetour(byte self, uint actionID)
        {
            if (Service.ClientState.LocalPlayer == null)
                return _iconHook.Original(self, actionID);

            //Service.Log($"[AR] Detour: {(WarriorActions.AID)actionID}");
            switch (actionID)
            {
                case (uint)WARRotation.AID.HeavySwing:
                    return (uint)WarActions.NextBestAction;
                case (uint)WARRotation.AID.StormEye:
                    return (uint)WARRotation.GetNextStormEyeComboAction(WarActions.State);
                case (uint)WARRotation.AID.StormPath:
                    return (uint)WARRotation.GetNextStormPathComboAction(WarActions.State);
                case (uint)WARRotation.AID.MythrilTempest:
                    return (uint)WARRotation.GetNextAOEComboAction(WarActions.State);
                default:
                    return _iconHook.Original(self, actionID);
            }
        }
    }
}