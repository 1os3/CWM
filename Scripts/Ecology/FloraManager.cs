using Godot;
using CWM.Scripts.World;
using CWM.Scripts.Weather;
using WorldTileData = CWM.Scripts.World.TileData;

namespace CWM.Scripts.Ecology;

public readonly record struct EcologyUpdateContext(
    float DaylightFactor,
    float SeasonGrowthModifier,
    float RainContribution,
    float EvaporationModifier,
    float StormDamageChance);

public static class FloraManager
{
    public static bool UpdateTile(
        ref WorldTileData tile,
        int neighboringFlora,
        int waterNeighbors,
        EcologyUpdateContext context,
        float effectiveTemperature,
        float deltaHours,
        RandomNumberGenerator rng)
    {
        var stepHours = Mathf.Max(deltaHours, 0.001f);
        if (tile.IsWater)
        {
            tile.SnowCover = Mathf.Clamp(tile.SnowCover + (context.RainContribution * 0.04f * stepHours), 0.0f, 1.0f);
            return false;
        }

        var previous = tile;
        var profile = BiomeClassifier.GetEcologyProfile(tile.Biome);
        var ambientSoilTarget = BiomeClassifier.GetAmbientSoilMoistureTarget(
            tile.Biome,
            Mathf.Lerp(tile.Moisture, tile.Rainfall, 0.35f));
        var waterAdjacencyPerHour = waterNeighbors * 0.02f;
        var rainfallPerHour = (context.RainContribution * Mathf.Lerp(0.35f, 1.35f, tile.Rainfall)) + waterAdjacencyPerHour;
        var evaporationPerHour =
            (0.02f + (effectiveTemperature * 0.035f)) *
            context.EvaporationModifier *
            Mathf.Lerp(0.68f, 1.32f, tile.Sunlight);
        var hydrologyRelaxationPerHour = (ambientSoilTarget - tile.SoilMoisture) * 0.18f;
        tile.SoilMoisture = Mathf.Clamp(
            tile.SoilMoisture + ((rainfallPerHour - evaporationPerHour + hydrologyRelaxationPerHour) * stepHours),
            0.0f,
            1.0f);

        var habitatSuitability = BiomeClassifier.GetHabitatSuitability(tile.Biome, effectiveTemperature, tile.SoilMoisture, tile.Moisture);
        var carryingCapacity = Mathf.Clamp((tile.Nutrients * 0.42f) + (tile.SoilMoisture * 0.32f) + (tile.OrganicMatter * 0.26f), 0.05f, 1.0f);
        var seasonalBoost =
            context.SeasonGrowthModifier *
            Mathf.Lerp(0.55f, 1.0f, context.DaylightFactor) *
            Mathf.Lerp(0.72f, 1.18f, tile.Sunlight);

        if (tile.Flora == FloraType.None && tile.Biome != BiomeType.Farmland)
        {
            var colonizationRatePerHour =
                ((neighboringFlora * 0.10f) + (tile.OrganicMatter * 0.12f) + 0.02f) *
                habitatSuitability *
                Mathf.Lerp(0.4f, 1.0f, carryingCapacity);
            if (rng.Randf() < ProbabilityFromRate(colonizationRatePerHour, stepHours))
            {
                tile.Flora = GetFallbackFlora(tile.Biome);
                tile.FloraGrowth = Mathf.Lerp(0.08f, 0.18f, habitatSuitability);
            }
        }
        else if (tile.Biome != BiomeType.Farmland)
        {
            var growthRatePerHour =
                0.18f *
                seasonalBoost *
                habitatSuitability *
                Mathf.Lerp(0.35f, 1.0f, carryingCapacity) *
                Mathf.Lerp(0.35f, 1.0f, profile.VegetationPotential);
            var logisticGrowth = growthRatePerHour * Mathf.Max(tile.FloraGrowth, 0.12f) * (1.0f - tile.FloraGrowth);
            var climateStressPerHour = (1.0f - habitatSuitability) * 0.10f;
            var droughtStressPerHour = tile.SoilMoisture < 0.12f
                ? 0.18f * ((0.12f - tile.SoilMoisture) / 0.12f)
                : 0.0f;

            tile.FloraGrowth = Mathf.Clamp(
                tile.FloraGrowth + ((logisticGrowth - climateStressPerHour - droughtStressPerHour) * stepHours),
                0.0f,
                1.0f);
        }

        var baseOrganicMatter = BiomeClassifier.GetBaseOrganicMatter(tile.Biome);
        var baseNutrients = BiomeClassifier.GetBaseNutrients(tile.Biome);
        var organicDeltaPerHour =
            (tile.FloraGrowth * habitatSuitability * 0.06f) -
            ((1.0f - habitatSuitability) * 0.015f) +
            ((baseOrganicMatter - tile.OrganicMatter) * 0.02f);
        tile.OrganicMatter = Mathf.Clamp(tile.OrganicMatter + (organicDeltaPerHour * stepHours), 0.0f, 1.0f);

        var nutrientReplenishmentPerHour =
            ((baseNutrients - tile.Nutrients) * 0.04f) +
            (tile.OrganicMatter * 0.02f);
        var nutrientUsePerHour = tile.FloraGrowth * 0.035f * Mathf.Lerp(0.6f, 1.2f, habitatSuitability);
        tile.Nutrients = Mathf.Clamp(
            tile.Nutrients + ((nutrientReplenishmentPerHour - nutrientUsePerHour) * stepHours),
            0.0f,
            1.0f);

        var stormEventRatePerHour = context.StormDamageChance * Mathf.Lerp(0.25f, 1.0f, tile.FloraGrowth);
        if (context.StormDamageChance > 0.0f && rng.Randf() < ProbabilityFromRate(stormEventRatePerHour, stepHours))
        {
            var damage = rng.RandfRange(0.05f, 0.14f);
            tile.FloraGrowth = Mathf.Clamp(tile.FloraGrowth - damage, 0.0f, 1.0f);
            tile.Disturbance += Mathf.CeilToInt(damage * 10.0f);
        }

        if (tile.Biome != BiomeType.Farmland && (tile.FloraGrowth <= 0.06f || habitatSuitability <= 0.04f))
        {
            tile.Flora = FloraType.None;
            tile.FloraGrowth = Mathf.Min(tile.FloraGrowth, 0.04f);
        }

        return !TileMostlyEqual(previous, tile);
    }

    private static float ProbabilityFromRate(float ratePerHour, float deltaHours)
    {
        return 1.0f - Mathf.Exp(-Mathf.Max(ratePerHour, 0.0f) * deltaHours);
    }

    private static FloraType GetFallbackFlora(BiomeType biome)
    {
        var initialFlora = BiomeClassifier.GetInitialFlora(biome);
        if (initialFlora != FloraType.None)
        {
            return initialFlora;
        }

        return biome switch
        {
            BiomeType.Farmland => FloraType.Grass,
            BiomeType.Beach => FloraType.Grass,
            BiomeType.RockyHighlands => FloraType.Shrub,
            BiomeType.BareRock => FloraType.Moss,
            _ => FloraType.Grass
        };
    }

    private static bool TileMostlyEqual(WorldTileData a, WorldTileData b)
    {
        return a.Flora == b.Flora &&
               a.Succession == b.Succession &&
               Mathf.Abs(a.FloraGrowth - b.FloraGrowth) < 0.0001f &&
               Mathf.Abs(a.SoilMoisture - b.SoilMoisture) < 0.0001f &&
               Mathf.Abs(a.Nutrients - b.Nutrients) < 0.0001f &&
               Mathf.Abs(a.OrganicMatter - b.OrganicMatter) < 0.0001f &&
               a.Disturbance == b.Disturbance;
    }
}
