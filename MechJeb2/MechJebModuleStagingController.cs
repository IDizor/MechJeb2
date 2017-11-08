﻿using System.Collections.Generic;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace MuMech
{
    public class MechJebModuleStagingController : ComputerModule
    {
        public MechJebModuleStagingController(MechJebCore core)
            : base(core)
        {
            priority = 1000;
        }

        //adjustable parameters:
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble autostagePreDelay = 0.5;
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble autostagePostDelay = 1.0;
        [Persistent(pass = (int)Pass.Type)]
        public EditableInt autostageLimit = 0;
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDoubleMult fairingMaxDynamicPressure = new EditableDoubleMult(5000, 1000);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDoubleMult fairingMinAltitude = new EditableDoubleMult(50000, 1000);
        [Persistent(pass = (int)Pass.Type)]
        public EditableDouble clampAutoStageThrustPct = 0.95;
        [Persistent(pass = (int)Pass.Type)]
        public EditableDoubleMult fairingMaxAerothermalFlux = new EditableDoubleMult(1135, 1);

        public bool autostagingOnce = false;

        public bool waitingForFirstStaging = false;

        public override void OnStart(PartModule.StartState state)
        {
            if (vessel.situation == Vessel.Situations.PRELAUNCH)
                waitingForFirstStaging = true;

            GameEvents.onStageActivate.Add(stageActivate);
        }

        public override void OnDestroy()
        {
            GameEvents.onStageActivate.Remove(stageActivate);
        }

        private void stageActivate(int data)
        {
            waitingForFirstStaging = false;
        }

        public void AutostageOnce(object user)
        {
            users.Add(user);
            autostagingOnce = true;
        }

        public override void OnModuleDisabled()
        {
            autostagingOnce = false;
        }

        [GeneralInfoItem("Autostaging settings", InfoItem.Category.Misc)]
        public void AutostageSettingsInfoItem()
        {
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Delays: pre:", GUILayout.ExpandWidth(false));
            autostagePreDelay.text = GUILayout.TextField(autostagePreDelay.text, GUILayout.Width(35));
            GUILayout.Label("s  post:", GUILayout.ExpandWidth(false));
            autostagePostDelay.text = GUILayout.TextField(autostagePostDelay.text, GUILayout.Width(35));
            GUILayout.Label("s", GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.Label("Stage fairings when:");
            GuiUtils.SimpleTextBox("  dynamic pressure <", fairingMaxDynamicPressure, "kPa", 50);
            GuiUtils.SimpleTextBox("  altitude >", fairingMinAltitude, "km", 50);
            GuiUtils.SimpleTextBox("  aerothermal flux <", fairingMaxAerothermalFlux, "W/m²", 50);

            GuiUtils.SimpleTextBox("Stop at stage #", autostageLimit, "");

            GUILayout.EndVertical();
        }

        [ValueInfoItem("Autostaging status", InfoItem.Category.Misc)]
        public string AutostageStatus()
        {
            if (!this.enabled) return "Autostaging off";
            if (autostagingOnce) return "Will autostage next stage only";
            return "Autostaging until stage #" + (int)autostageLimit;
        }

        //internal state:
        double lastStageTime = 0;

        bool countingDown = false;
        double stageCountdownStart = 0;

        public override void OnUpdate()
        {
            if (!vessel.isActiveVessel)
                return;

            //if autostage enabled, and if we've already staged at least once, and if there are stages left,
            //and if we are allowed to continue staging, and if we didn't just fire the previous stage
            if (waitingForFirstStaging || StageManager.CurrentStage <= 0 || StageManager.CurrentStage <= autostageLimit
               || vesselState.time - lastStageTime < autostagePostDelay)
                return;

            //don't decouple active or idle engines or tanks
            List<ModuleEngines> activeModuleEngines = FindActiveModuleEngines();
            List<int> burnedResources = FindBurnedResources(activeModuleEngines);
            if (InverseStageDecouplesActiveOrIdleEngineOrTank(StageManager.CurrentStage - 1, vessel, burnedResources, activeModuleEngines))
                return;

            //Don't fire a stage that will activate a parachute, unless that parachute gets decoupled:
            if (HasStayingChutes(StageManager.CurrentStage - 1, vessel))
                return;

            //Always drop deactivated engines or tanks
            if (!InverseStageDecouplesDeactivatedEngineOrTank(StageManager.CurrentStage - 1, vessel))
            {
                //only decouple fairings if the dynamic pressure, altitude, and aerothermal flux conditions are respected
                if ((core.vesselState.dynamicPressure > fairingMaxDynamicPressure || core.vesselState.altitudeASL < fairingMinAltitude || core.vesselState.freeMolecularAerothermalFlux > fairingMaxAerothermalFlux) &&
                    HasFairing(StageManager.CurrentStage - 1, vessel))
                    return;

                //only release launch clamps if we're at nearly full thrust
                if (vesselState.thrustCurrent / vesselState.thrustAvailable < 0.99 &&
                    InverseStageReleasesClamps(StageManager.CurrentStage - 1, vessel))
                    return;
            }

            //When we find that we're allowed to stage, start a countdown (with a
            //length given by autostagePreDelay) and only stage once that countdown finishes,
            if (countingDown)
            {
                if (vesselState.time - stageCountdownStart > autostagePreDelay)
                {
                    if (InverseStageFiresDecoupler(StageManager.CurrentStage - 1, vessel))
                    {
                        //if we decouple things, delay the next stage a bit to avoid exploding the debris
                        lastStageTime = vesselState.time;
                    }

                    StageManager.ActivateNextStage();
                    countingDown = false;

                    if (autostagingOnce)
                        users.Clear();
                }
            }
            else
            {
                countingDown = true;
                stageCountdownStart = vesselState.time;
            }
        }

        //determine whether it's safe to activate inverseStage
        public static bool InverseStageDecouplesActiveOrIdleEngineOrTank(int inverseStage, Vessel v, List<int> tankResources, List<ModuleEngines> activeModuleEngines)
        {
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p.inverseStage == inverseStage && p.IsUnfiredDecoupler() && HasActiveOrIdleEngineOrTankDescendant(p, tankResources, activeModuleEngines))
                {
                    return true;
                }
            }
            return false;
        }

        public List<ModuleEngines> FindActiveModuleEngines()
        {
            var activeEngines = vessel.parts.Where(p => p.inverseStage >= StageManager.CurrentStage && p.IsEngine() && !p.IsSepratron() &&
                                                        !p.IsDecoupledInStage(StageManager.CurrentStage - 1));
            var engineModules = activeEngines.Select(p => p.Modules.OfType<ModuleEngines>().First(e => e.isEnabled));
            return engineModules.ToList();
        }

        // Find resources burned by engines that will remain after staging (so we wait until tanks are empty before releasing drop tanks)
        public List<int> FindBurnedResources(List<ModuleEngines> activeModuleEngines)
        {
            var burnedPropellants = activeModuleEngines.SelectMany(eng => eng.propellants);
            List<int> propellantIDs = burnedPropellants.Select(prop => prop.id).ToList();
            return propellantIDs;
        }

        //detect if a part is above an active or idle engine in the part tree
        public static bool HasActiveOrIdleEngineOrTankDescendant(Part p, List<int> tankResources, List<ModuleEngines> activeModuleEngines)
        {
            if ((p.State == PartStates.ACTIVE || p.State == PartStates.IDLE)
                && p.IsEngine() && !p.IsSepratron() && p.EngineHasFuel())
            {
                return true; // TODO: properly check if ModuleEngines is active
            }
            if (!p.IsSepratron())
            {
                for (int i = 0; i < p.Resources.Count; i++)
                {
                    PartResource r = p.Resources[i];
                    if (r.amount > p.resourceRequestRemainingThreshold && r.info.id != PartResourceLibrary.ElectricityHashcode && tankResources.Contains(r.info.id))
                    {
                        foreach (ModuleEngines engine in activeModuleEngines)
                        {
                            if (engine.part.crossfeedPartSet.ContainsPart(p))
                                return true;
                        }
                    }
                }
            }
            for (int i = 0; i < p.children.Count; i++)
            {
                if (HasActiveOrIdleEngineOrTankDescendant(p.children[i], tankResources, activeModuleEngines))
                {
                    return true;
                }
            }
            return false;
        }

        //determine whether activating inverseStage will fire any sort of decoupler. This
        //is used to tell whether we should delay activating the next stage after activating inverseStage
        public static bool InverseStageFiresDecoupler(int inverseStage, Vessel v)
        {
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p.inverseStage == inverseStage && p.IsUnfiredDecoupler())
                {
                    return true;
                }
            }
            return false;
        }

        //determine whether activating inverseStage will release launch clamps
        public static bool InverseStageReleasesClamps(int inverseStage, Vessel v)
        {
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p.inverseStage == inverseStage && p.IsLaunchClamp())
                {
                    return true;
                }
            }
            return false;
        }

        //determine whether inverseStage sheds a dead engine
        public static bool InverseStageDecouplesDeactivatedEngineOrTank(int inverseStage, Vessel v)
        {
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p.inverseStage == inverseStage && p.IsUnfiredDecoupler() && HasDeactivatedEngineOrTankDescendant(p))
                {
                    return true;
                }
            }
            return false;
        }

        //detect if a part is above a deactivated engine or fuel tank
        public static bool HasDeactivatedEngineOrTankDescendant(Part p)
        {
            if ((p.State == PartStates.DEACTIVATED) && (p.IsEngine()) && !p.IsSepratron())
            {
                return true; // TODO: yet more ModuleEngine lazy checks
            }

            //check if this is a new-style fuel tank that's run out of resources:
            bool hadResources = false;
            bool hasResources = false;
            for (int i = 0; i < p.Resources.Count; i++)
            {
                PartResource r = p.Resources[i];
                if (r.info.id == PartResourceLibrary.ElectricityHashcode) continue;
                if (r.maxAmount > p.resourceRequestRemainingThreshold) hadResources = true;
                if (r.amount > p.resourceRequestRemainingThreshold) hasResources = true;
            }
            if (hadResources && !hasResources) return true;

            if (p.IsEngine() && !p.EngineHasFuel()) return true;

            for (int i = 0; i < p.children.Count; i++)
            {
                if (HasDeactivatedEngineOrTankDescendant(p.children[i]))
                {
                    return true;
                }
            }
            return false;
        }

        //determine if there are chutes being fired that wouldn't also get decoupled
        public static bool HasStayingChutes(int inverseStage, Vessel v)
        {
            var chutes = v.parts.FindAll(p => p.inverseStage == inverseStage && p.IsParachute());

            for (int i = 0; i < chutes.Count; i++)
            {
                Part p = chutes[i];
                if (!p.IsDecoupledInStage(inverseStage))
                {
                    return true;
                }
            }

            return false;
        }

        // determine if there is a fairing to be deployed
        public static bool HasFairing(int inverseStage, Vessel v)
        {
            return v.parts.Any(p => p.inverseStage == inverseStage && (p.HasModule<ModuleProceduralFairing>() || (VesselState.isLoadedProceduralFairing && p.Modules.Contains("ProceduralFairingDecoupler"))));
        }
    }
}
