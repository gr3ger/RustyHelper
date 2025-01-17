using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;
using TMPro;

namespace RustyHelper;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    private static ConfigEntry<int> configSplunkPercentageFree;
    private static ConfigEntry<bool> configAlternativeProgress;

    private void Awake()
    {
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        
        configSplunkPercentageFree = Config.Bind("Splunk",
            "SplunkAiPercentageFree",
            75,
            "A number describing which fields Splunk should target, ranges from 1 (%) to 100 (%), default is 75");

        configAlternativeProgress = Config.Bind("UI", 
            "AlternativeProgress",
            false,
            "Whether or not to show an alternative progress text as \"percent done, (seeds that need to be planted to reach goal)\", instead of the original that shows \"harvests/neededHarvests\"");
        
        var hm = Harmony.CreateAndPatchAll(typeof(Plugin));
        
        // Skip patching splunk altogether if we have the default value
        if(configSplunkPercentageFree.Value != 75){
            hm.PatchAll(typeof(SeederWorkerAI_GetAllEmptyCropPatches_Patch));
        }

        var patchedMethods = string.Join(", " , hm.GetPatchedMethods().Select(x => x.Name).ToList());
        Logger.LogInfo($"Patched methods: {patchedMethods}");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SeederWorkerAI), "GetAllEmptyCropPatches")]
    static void GetAllEmptyCropPatches(SeederWorkerAI __instance)
    {
        // Ugly fix since there's a small bug that can accur if you've set splunk to plant on fields with only one free slot.
        // in GetAllEmptyCropPatches where it considers fossils as plantable,
        // this causes splunk to get caught in a loop if the closest field is full except for a fossil
        
        // Removes all crop patches from the list where none of its slots has the state CropSlot.State.Empty
        // Setting a cutoff value of 1/4 (25%), since that's the highest percentage where the bug can occur on a 4x4 patch
        if(configSplunkPercentageFree.Value <= 25){
            __instance.availableCropPatches.RemoveAll(x => x.cropSlots.All(y => y.state != CropSlot.State.Empty));
        }
    }

    /// <summary>
    /// Patches Splunk's AI to change the default limitation of 75% free slots to 1% instead
    /// </summary>
    [HarmonyPatch(typeof(SeederWorkerAI), "GetAllEmptyCropPatches")]
    public static class SeederWorkerAI_GetAllEmptyCropPatches_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var cm = new CodeMatcher(instructions).MatchForward(true, // Set position to the end of the match
                new CodeMatch(OpCodes.Ldloc_2),
                new CodeMatch(OpCodes.Conv_R4),
                new CodeMatch(OpCodes.Ldloc_1),
                new CodeMatch(OpCodes.Conv_R4),
                new CodeMatch(OpCodes.Ldc_R4));

            if (cm.IsValid)
            {
                var newInt = Math.Clamp(configSplunkPercentageFree.Value, 0, 100);
                var newFloat = newInt / 100f;
                Logger.LogInfo($"Patching Splunk's AI to plant when there's at least {newInt}% ({newFloat}) free slots instead of 75%");
                cm.SetOperandAndAdvance(newFloat);
            }
            else
            {
                Logger.LogError("Failed to patch Splunk's AI");
            }

            return cm.InstructionEnumeration();
        }
    }
    
    /// <summary>
    /// This method will be executed *after* the original SetRequirement function in CropInfoPanel
    /// That way we can modify the resulting amount text without worrying about it getting overwritten
    /// </summary>
    /// <param name="index">Index of the required crop in the panel</param>
    /// <param name="cropRequirement">The crop object in question</param>
    /// <param name="cropRequirementAmount">The amount of the crop we need to harvest</param>
    /// <param name="totalCropsHarvested">The amount of the crop we have already harvested</param>
    /// <param name="__instance">The current CropInfoPanel instance acquired through HarmonyX</param>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CropInfoPanel), nameof(CropInfoPanel.SetRequirement))]
    static void SetRequirement(
        int index,
        CropSO cropRequirement,
        int cropRequirementAmount,
        int totalCropsHarvested, CropInfoPanel __instance)
    {
        // Skip execution if we don't want the changed requirement display
        if(!configAlternativeProgress.Value)
            return;
        
        CropManager.GMO gmo = GameManager.ins.getGMO(cropRequirement);
        
        // Get the total harvest amount for current crop type, taking any modifiers (GMO) into account
        int harvestYieldPerPlant = cropRequirement.harvestMultiplier + (gmo.tier != CropManager.GmoTier.None ? gmo.harvest : 0);
        
        // Sum all the yields for the current crop type already planted, and account for it in the calculation for how many seeds we need to plant
        int yieldsInGround = GameManager.ins.cropSlots.Where(x => x.cropType == cropRequirement.cropType).Sum(cropSlot => cropSlot._CropMultiplier);
        
        // Calculate how many seeds we need to plant based on how many yields are needed (cropRequirementAmount),
        // how many we already harvested (totalCropsHarvested),
        // and how many yields are expected from what is already planted (yieldsInGround)
        int cropsNeeded = Math.Max(cropRequirementAmount - totalCropsHarvested - yieldsInGround, 0);
        
        int cropsHarvestedPercent = (int)Math.Clamp(Math.Floor(((float)totalCropsHarvested / (float)cropRequirementAmount)*100f), 0f, 100f);
        
        // Number of seeds we need to plant to reach our goal
        int numberNewPlantsNeeded = (int)Math.Ceiling(cropsNeeded / (float)harvestYieldPerPlant);
        
        // Get the private class member "cropsHarvestedDenominator" from our CropInfoPanel instance so we can modify it
        TMP_Text[] cropsHarvestedDenominator = Traverse.Create(__instance).Field("cropsHarvestedDenominator").GetValue() as TMP_Text[];
        
        if(numberNewPlantsNeeded > 0){
            cropsHarvestedDenominator[index].text = "<color=#303030>" + cropsHarvestedPercent + "%</color> (" + numberNewPlantsNeeded+ ")";
        }else{
            cropsHarvestedDenominator[index].text = "<color=#303030>" + cropsHarvestedPercent + "%</color>";
        }
    }
}
