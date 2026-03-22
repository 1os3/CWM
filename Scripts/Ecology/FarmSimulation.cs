using Godot;
using CWM.Scripts.World;
using WorldTileData = CWM.Scripts.World.TileData;

namespace CWM.Scripts.Ecology;

/// <summary>耕地作物生长、杂草与撂荒演替（与 <see cref="FloraManager"/> 顺序配合）。</summary>
public static class FarmSimulation
{
    public const float MinHarvestCropGrowth = 0.78f;

    public static void SimulateFarmlandTick(
        ref WorldTileData tile,
        float stepHours,
        EcologyUpdateContext context,
        float effectiveTemperature,
        float worldGameHours,
        RandomNumberGenerator rng)
    {
        if (tile.Biome != BiomeType.Farmland)
        {
            return;
        }

        var hoursSinceCare = ComputeHoursSinceCare(tile, worldGameHours);
        var neglect = Mathf.Clamp((hoursSinceCare - 18.0f) / 220.0f, 0.0f, 1.0f);

        if (tile.CropGrowth > 0.04f && tile.CropGrowth < 0.999f)
        {
            var moistureFactor = Mathf.Lerp(0.15f, 1.15f, tile.SoilMoisture);
            var nutrientFactor = Mathf.Lerp(0.35f, 1.1f, tile.Nutrients);
            var lightFactor = Mathf.Lerp(0.55f, 1.05f, tile.Sunlight);
            var rainFactor = Mathf.Lerp(0.45f, 1.1f, context.RainContribution * 3.5f);
            var season = Mathf.Lerp(0.55f, 1.05f, context.SeasonGrowthModifier);
            var heat = 1.0f - Mathf.Abs(effectiveTemperature - 0.52f) * 1.35f;
            heat = Mathf.Clamp(heat, 0.25f, 1.0f);
            var weedPenalty = 1.0f - Mathf.Clamp(tile.WeedPressure * 0.78f, 0.0f, 0.82f);

            var growthPerHour =
                0.022f *
                moistureFactor *
                nutrientFactor *
                lightFactor *
                rainFactor *
                season *
                heat *
                weedPenalty;

            tile.CropGrowth = Mathf.Clamp(tile.CropGrowth + growthPerHour * stepHours, 0.0f, 1.0f);
        }

        var weedGain =
            (neglect * 0.055f) +
            (tile.SoilMoisture > 0.72f ? 0.012f : 0.0f) +
            (context.RainContribution > 0.08f ? 0.008f : 0.0f);
        tile.WeedPressure = Mathf.Clamp(
            tile.WeedPressure + weedGain * stepHours + rng.Randf() * 0.0015f * stepHours,
            0.0f,
            1.0f);

        if (tile.CropGrowth > 0.12f)
        {
            tile.SoilMoisture = Mathf.Clamp(
                tile.SoilMoisture - 0.014f * stepHours * Mathf.Lerp(0.4f, 1.0f, tile.CropGrowth),
                0.0f,
                1.0f);
        }

        if (tile.WeedPressure > 0.38f)
        {
            tile.Flora = FloraType.Grass;
            tile.FloraGrowth = Mathf.Max(tile.FloraGrowth, tile.WeedPressure * 0.62f);
        }
        else if (tile.CropGrowth > 0.07f)
        {
            tile.Flora = FloraType.Wildflower;
            tile.FloraGrowth = Mathf.Clamp(tile.CropGrowth * 0.9f, 0.1f, 0.95f);
        }
        else if (tile.CropGrowth <= 0.02f)
        {
            tile.Flora = FloraType.None;
            tile.FloraGrowth = Mathf.Min(tile.FloraGrowth, 0.06f);
        }

        tile.OrganicMatter = Mathf.Clamp(
            tile.OrganicMatter + (tile.CropGrowth * 0.008f - tile.WeedPressure * 0.004f) * stepHours,
            0.0f,
            1.0f);
    }

    public static BiomeType ResolveAbandonedFarmland(WorldTileData tile, float climateTemperature, float worldGameHours)
    {
        var hoursSinceCare = ComputeHoursSinceCare(tile, worldGameHours);
        var veryNeglected = hoursSinceCare > 140.0f && tile.CropGrowth < 0.18f;
        var weedTakeover = tile.WeedPressure > 0.82f && tile.CropGrowth < 0.28f;
        var midNeglectWeeds = hoursSinceCare > 90.0f && tile.WeedPressure > 0.62f && tile.CropGrowth < 0.2f;

        if (!veryNeglected && !weedTakeover && !midNeglectWeeds)
        {
            return BiomeType.Farmland;
        }

        if (tile.SoilMoisture > 0.78f && climateTemperature > 0.42f && tile.WeedPressure > 0.55f)
        {
            return BiomeType.Swamp;
        }

        return BiomeType.Grassland;
    }

    public static float ComputeHarvestYield(WorldTileData tile)
    {
        if (tile.Biome != BiomeType.Farmland || tile.CropGrowth < MinHarvestCropGrowth)
        {
            return 0.0f;
        }

        var quality =
            tile.CropGrowth *
            tile.CropGrowth *
            (1.0f - Mathf.Clamp(tile.WeedPressure, 0.0f, 1.0f) * 0.62f) *
            Mathf.Lerp(0.45f, 1.15f, tile.Nutrients) *
            Mathf.Lerp(0.5f, 1.05f, tile.SoilMoisture) *
            Mathf.Lerp(0.55f, 1.1f, tile.Sunlight);

        // 单格收获量（曾偏大，约下调 100 倍）
        return Mathf.Max(0.0f, quality * 1.2f);
    }

    public static float ComputeHoursSinceCare(WorldTileData tile, float worldGameHours)
    {
        if (tile.LastFarmCareGameHours <= 0.0001f)
        {
            return 10_000.0f;
        }

        return Mathf.Max(0.0f, worldGameHours - tile.LastFarmCareGameHours);
    }
}
