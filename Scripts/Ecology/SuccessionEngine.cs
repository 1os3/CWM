using Godot;
using CWM.Scripts.World;
using WorldTileData = CWM.Scripts.World.TileData;

namespace CWM.Scripts.Ecology;

public static class SuccessionEngine
{
    public static bool UpdateStage(ref WorldTileData tile, float climateTemperature)
    {
        if (tile.IsWater)
        {
            return false;
        }

        var previous = tile.Succession;
        var profile = BiomeClassifier.GetEcologyProfile(tile.Biome);
        var habitatSuitability = BiomeClassifier.GetHabitatSuitability(tile.Biome, climateTemperature, tile.SoilMoisture, tile.Moisture);
        var disturbancePressure = Mathf.Clamp(tile.Disturbance / 6.0f, 0.0f, 1.5f);
        var successionScore =
            (tile.OrganicMatter * 0.34f) +
            (tile.Nutrients * 0.18f) +
            (tile.FloraGrowth * 0.24f) +
            (habitatSuitability * 0.18f) +
            (Mathf.Lerp(tile.SoilMoisture, tile.Moisture, 0.35f) * 0.06f) -
            (disturbancePressure * 0.22f);

        var desiredStage = successionScore switch
        {
            < 0.16f => SuccessionStage.Bare,
            < 0.38f => SuccessionStage.Pioneer,
            < 0.68f => SuccessionStage.Intermediate,
            _ => SuccessionStage.Climax
        };
        tile.Succession = (SuccessionStage)Mathf.Min((int)desiredStage, (int)profile.SuccessionCap);

        if (tile.Succession == SuccessionStage.Bare && tile.FloraGrowth < 0.08f)
        {
            tile.Flora = FloraType.None;
        }

        return previous != tile.Succession;
    }

    public static bool RecoverDisturbance(ref WorldTileData tile, float climateTemperature, float deltaHours, RandomNumberGenerator rng)
    {
        if (tile.IsWater || tile.Disturbance <= 0)
        {
            return false;
        }

        var previous = tile.Disturbance;
        var profile = BiomeClassifier.GetEcologyProfile(tile.Biome);
        var habitatSuitability = BiomeClassifier.GetHabitatSuitability(tile.Biome, climateTemperature, tile.SoilMoisture, tile.Moisture);
        var recoveryPerHour =
            profile.DisturbanceRecoveryPerHour *
            Mathf.Lerp(0.35f, 1.0f, habitatSuitability) *
            Mathf.Lerp(0.6f, 1.0f, tile.OrganicMatter);
        var expectedRecovery = Mathf.Max(recoveryPerHour, 0.0f) * Mathf.Max(deltaHours, 0.001f);
        var wholeRecovery = Mathf.FloorToInt(expectedRecovery);
        if (wholeRecovery > 0)
        {
            tile.Disturbance = Mathf.Max(0, tile.Disturbance - wholeRecovery);
        }

        var fractionalRecovery = expectedRecovery - wholeRecovery;
        if (tile.Disturbance > 0 && rng.Randf() < fractionalRecovery)
        {
            tile.Disturbance--;
        }

        return previous != tile.Disturbance;
    }

    public static bool UpdateBiome(ref WorldTileData tile, float climateTemperature, int waterNeighbors, float worldGameHours)
    {
        if (tile.Biome == BiomeType.Farmland)
        {
            var desiredFarmlandFate = FarmSimulation.ResolveAbandonedFarmland(tile, climateTemperature, worldGameHours);
            if (desiredFarmlandFate == BiomeType.Farmland)
            {
                return false;
            }

            var previous = tile;
            ApplyBiomeTransition(ref tile, desiredFarmlandFate);
            UpdateStage(ref tile, climateTemperature);
            return !TilesEquivalent(previous, tile);
        }

        var desiredBiome = ResolveDynamicBiome(tile, climateTemperature, waterNeighbors);
        if (desiredBiome == tile.Biome)
        {
            return false;
        }

        var previousTile = tile;
        ApplyBiomeTransition(ref tile, desiredBiome);
        UpdateStage(ref tile, climateTemperature);
        return !TilesEquivalent(previousTile, tile);
    }

    public static bool UpdateMorphology(
        ref WorldTileData tile,
        int waterNeighbors,
        float deltaHours)
    {
        var previous = tile;
        var stepHours = Mathf.Max(deltaHours, 0.001f);
        var clampedWaterNeighbors = Mathf.Clamp(waterNeighbors, 0, 4);
        var landNeighbors = 4 - clampedWaterNeighbors;
        var coastalExposure = tile.IsWater
            ? landNeighbors / 4.0f
            : clampedWaterNeighbors / 4.0f;
        var vegetationProtection = Mathf.Lerp(1.0f, 0.35f, Mathf.Clamp(tile.FloraGrowth, 0.0f, 1.0f));
        var disturbanceFactor = Mathf.Lerp(1.0f, 1.8f, Mathf.Clamp(tile.Disturbance / 8.0f, 0.0f, 1.0f));

        if (tile.IsWater)
        {
            var depositionRatePerHour = tile.Biome switch
            {
                BiomeType.ShallowWater => landNeighbors * 0.00045f,
                BiomeType.DeepWater => landNeighbors * 0.00012f,
                _ => landNeighbors * 0.0002f
            };
            var scourRatePerHour = clampedWaterNeighbors * 0.00005f;
            tile.Elevation = Mathf.Clamp(tile.Elevation + ((depositionRatePerHour - scourRatePerHour) * stepHours), 0.0f, 1.0f);
            return !TilesEquivalent(previous, tile);
        }

        var lowlandPeatAccretionPerHour = tile.Elevation < 0.32f && tile.SoilMoisture > 0.80f && tile.OrganicMatter > 0.50f
            ? 0.00014f
            : 0.0f;
        var vegetationSedimentRetentionPerHour = tile.FloraGrowth * tile.OrganicMatter * 0.00008f * (1.0f - coastalExposure);
        var coastalErosionPerHour = coastalExposure * 0.00025f * vegetationProtection * disturbanceFactor;
        var hydraulicErosionPerHour = Mathf.Max(tile.SoilMoisture - 0.70f, 0.0f) * 0.00018f * vegetationProtection;
        var alpineWeatheringPerHour = tile.Elevation > 0.72f
            ? Mathf.Lerp(0.00001f, 0.00010f, tile.SnowCover)
            : 0.0f;

        tile.Elevation = Mathf.Clamp(
            tile.Elevation + ((lowlandPeatAccretionPerHour + vegetationSedimentRetentionPerHour - coastalErosionPerHour - hydraulicErosionPerHour - alpineWeatheringPerHour) * stepHours),
            0.0f,
            1.0f);

        return !TilesEquivalent(previous, tile);
    }

    public static bool ApplyNaturalDisturbance(ref WorldTileData tile, float damage)
    {
        if (tile.IsWater || damage <= 0.0f)
        {
            return false;
        }

        var previous = tile;
        tile.FloraGrowth = Mathf.Clamp(tile.FloraGrowth - damage, 0.0f, 1.0f);
        tile.OrganicMatter = Mathf.Clamp(tile.OrganicMatter - (damage * 0.25f), 0.0f, 1.0f);
        tile.Disturbance += Mathf.CeilToInt(damage * 5.0f);
        if (tile.FloraGrowth < 0.12f)
        {
            tile.Flora = FloraType.None;
        }

        UpdateStage(ref tile, tile.Temperature);
        return !TilesEquivalent(previous, tile);
    }

    private static BiomeType ResolveDynamicBiome(WorldTileData tile, float climateTemperature, int waterNeighbors)
    {
        var classifiedBiome = BiomeClassifier.Classify(tile.Elevation, tile.Moisture, climateTemperature);
        var coastal = tile.Elevation < 0.26f && waterNeighbors >= 1;
        var lowland = tile.Elevation < 0.42f;
        var upland = tile.Elevation > 0.62f;
        var alpine = tile.Elevation > 0.78f;
        var cold = climateTemperature < 0.18f;
        var cool = climateTemperature < 0.34f;
        var hot = climateTemperature > 0.72f;
        var veryWet = tile.SoilMoisture > 0.82f;
        var wet = tile.SoilMoisture > 0.62f;
        var moderateWet = tile.SoilMoisture > 0.34f;
        var dry = tile.SoilMoisture < 0.18f;
        var veryDry = tile.SoilMoisture < 0.10f;
        var organicRich = tile.OrganicMatter > 0.45f;
        var lush = tile.OrganicMatter > 0.62f && tile.FloraGrowth > 0.56f && tile.Succession == SuccessionStage.Climax;
        var organicPoor = tile.OrganicMatter < 0.16f;
        var disturbed = tile.Disturbance >= 4;
        var highSnow = tile.SnowCover > 0.48f;

        switch (classifiedBiome)
        {
            case BiomeType.DeepWater:
                return BiomeType.DeepWater;
            case BiomeType.ShallowWater:
                if (tile.Elevation > 0.13f && waterNeighbors <= 2 && tile.OrganicMatter > 0.18f)
                {
                    return BiomeType.Beach;
                }

                return BiomeType.ShallowWater;
            case BiomeType.Beach:
                if (veryWet && organicRich && tile.Succession >= SuccessionStage.Intermediate && climateTemperature > 0.40f)
                {
                    return BiomeType.Swamp;
                }

                if (tile.Succession >= SuccessionStage.Pioneer &&
                    tile.FloraGrowth > 0.22f &&
                    tile.SoilMoisture > 0.22f &&
                    waterNeighbors <= 2)
                {
                    return BiomeType.Grassland;
                }

                return BiomeType.Beach;
            case BiomeType.Forest when wet && tile.OrganicMatter > 0.46f && tile.FloraGrowth > 0.40f && tile.Succession >= SuccessionStage.Intermediate:
                return BiomeType.Forest;
            case BiomeType.ConiferForest when cool && moderateWet && tile.OrganicMatter > 0.28f && tile.Succession >= SuccessionStage.Pioneer:
                return BiomeType.ConiferForest;
            case BiomeType.Swamp when lowland && tile.SoilMoisture > 0.74f && tile.OrganicMatter > 0.35f:
                return BiomeType.Swamp;
            case BiomeType.Desert when hot && tile.SoilMoisture < 0.22f && tile.OrganicMatter < 0.26f:
                return BiomeType.Desert;
            case BiomeType.AlpineMeadow when upland && cool && tile.SoilMoisture > 0.26f && tile.OrganicMatter > 0.18f:
                return BiomeType.AlpineMeadow;
            case BiomeType.Snow when (cold && upland) || tile.SnowCover > 0.55f:
                return BiomeType.Snow;
            case BiomeType.RockyHighlands when tile.Elevation > 0.55f && (dry || organicPoor || disturbed):
                return BiomeType.RockyHighlands;
        }

        if (alpine)
        {
            if (cold || highSnow)
            {
                return BiomeType.Snow;
            }

            if (cool && moderateWet && tile.Succession >= SuccessionStage.Intermediate && tile.OrganicMatter > 0.22f)
            {
                return BiomeType.AlpineMeadow;
            }

            if (tile.OrganicMatter < 0.12f || tile.Nutrients < 0.12f || disturbed)
            {
                return BiomeType.BareRock;
            }

            return BiomeType.RockyHighlands;
        }

        if (upland)
        {
            if (cold && (highSnow || tile.SoilMoisture > 0.42f))
            {
                return BiomeType.Snow;
            }

            if (cool && wet && tile.Succession >= SuccessionStage.Intermediate && tile.OrganicMatter > 0.34f)
            {
                return BiomeType.ConiferForest;
            }

            if (cool && moderateWet && tile.Succession >= SuccessionStage.Pioneer && tile.OrganicMatter > 0.24f)
            {
                return BiomeType.AlpineMeadow;
            }

            if (organicPoor || disturbed || dry)
            {
                return BiomeType.RockyHighlands;
            }
        }

        if (lowland && veryWet && organicRich && tile.Succession >= SuccessionStage.Intermediate && climateTemperature > 0.42f)
        {
            return BiomeType.Swamp;
        }

        if (hot && veryDry && organicPoor)
        {
            return BiomeType.Desert;
        }

        if (cool)
        {
            if (wet && tile.Succession >= SuccessionStage.Intermediate && tile.OrganicMatter > 0.34f)
            {
                return BiomeType.ConiferForest;
            }

            if (moderateWet && tile.Succession >= SuccessionStage.Pioneer && tile.OrganicMatter > 0.18f)
            {
                return tile.Elevation > 0.45f
                    ? BiomeType.AlpineMeadow
                    : BiomeType.Grassland;
            }
        }

        if (lush && wet)
        {
            return BiomeType.Forest;
        }

        if (tile.Succession >= SuccessionStage.Intermediate && moderateWet && tile.OrganicMatter > 0.22f)
        {
            return BiomeType.Grassland;
        }

        if (dry && hot)
        {
            return BiomeType.Desert;
        }

        if (coastal && classifiedBiome != BiomeType.Desert)
        {
            return BiomeType.Beach;
        }

        if (tile.Elevation > 0.48f && organicPoor)
        {
            return BiomeType.RockyHighlands;
        }

        return classifiedBiome;
    }

    private static void ApplyBiomeTransition(ref WorldTileData tile, BiomeType desiredBiome)
    {
        var wasFarmland = tile.Biome == BiomeType.Farmland;
        tile.Biome = desiredBiome;
        if (wasFarmland && desiredBiome != BiomeType.Farmland)
        {
            tile.CropGrowth = 0.0f;
            tile.WeedPressure = 0.0f;
            tile.LastFarmCareGameHours = 0.0f;
        }
        if (desiredBiome is BiomeType.DeepWater or BiomeType.ShallowWater)
        {
            tile.SoilMoisture = 1.0f;
            tile.Nutrients = 0.0f;
            tile.OrganicMatter = Mathf.Min(tile.OrganicMatter, 0.08f);
            tile.Succession = SuccessionStage.Bare;
            tile.Flora = FloraType.None;
            tile.FloraGrowth = 0.0f;
            tile.SnowCover *= 0.4f;
            return;
        }

        tile.SoilMoisture = Mathf.Clamp(
            Mathf.Lerp(tile.SoilMoisture, BiomeClassifier.GetAmbientSoilMoistureTarget(desiredBiome, tile.Moisture), 0.18f),
            0.0f,
            1.0f);
        tile.Nutrients = Mathf.Clamp(
            Mathf.Lerp(tile.Nutrients, BiomeClassifier.GetBaseNutrients(desiredBiome), 0.14f),
            0.0f,
            1.0f);
        tile.OrganicMatter = Mathf.Clamp(
            Mathf.Lerp(tile.OrganicMatter, BiomeClassifier.GetBaseOrganicMatter(desiredBiome), 0.12f),
            0.0f,
            1.0f);

        var profile = BiomeClassifier.GetEcologyProfile(desiredBiome);
        tile.Succession = (SuccessionStage)Mathf.Min((int)tile.Succession, (int)profile.SuccessionCap);

        tile.Flora = GetTransitionFlora(desiredBiome, tile.FloraGrowth, tile.OrganicMatter);
        tile.FloraGrowth = desiredBiome switch
        {
            BiomeType.Beach => Mathf.Min(tile.FloraGrowth, 0.24f),
            BiomeType.Desert => Mathf.Min(tile.FloraGrowth, 0.20f),
            BiomeType.RockyHighlands => Mathf.Min(tile.FloraGrowth, 0.18f),
            BiomeType.BareRock => Mathf.Min(tile.FloraGrowth, 0.08f),
            BiomeType.Snow => Mathf.Min(tile.FloraGrowth, 0.16f),
            _ => tile.Flora == FloraType.None ? Mathf.Min(tile.FloraGrowth, 0.08f) : Mathf.Clamp(tile.FloraGrowth, 0.10f, 1.0f)
        };

        if (desiredBiome == BiomeType.Snow)
        {
            tile.SnowCover = Mathf.Max(tile.SnowCover, 0.25f);
        }
        else if (desiredBiome is BiomeType.Desert or BiomeType.Beach or BiomeType.BareRock)
        {
            tile.SnowCover *= 0.6f;
        }
    }

    private static FloraType GetTransitionFlora(BiomeType biome, float floraGrowth, float organicMatter)
    {
        if (floraGrowth <= 0.05f)
        {
            return FloraType.None;
        }

        var initialFlora = BiomeClassifier.GetInitialFlora(biome);
        if (initialFlora != FloraType.None)
        {
            return initialFlora;
        }

        return biome switch
        {
            BiomeType.Beach when floraGrowth > 0.18f => FloraType.Grass,
            BiomeType.RockyHighlands when floraGrowth > 0.10f => FloraType.Shrub,
            BiomeType.BareRock when organicMatter > 0.08f && floraGrowth > 0.04f => FloraType.Moss,
            _ => FloraType.None
        };
    }

    private static bool TilesEquivalent(WorldTileData a, WorldTileData b)
    {
        return a.Biome == b.Biome &&
               a.Flora == b.Flora &&
               a.Succession == b.Succession &&
               Mathf.IsEqualApprox(a.Elevation, b.Elevation) &&
               Mathf.IsEqualApprox(a.Moisture, b.Moisture) &&
               Mathf.IsEqualApprox(a.Temperature, b.Temperature) &&
               Mathf.IsEqualApprox(a.FloraGrowth, b.FloraGrowth) &&
               Mathf.IsEqualApprox(a.SoilMoisture, b.SoilMoisture) &&
               Mathf.IsEqualApprox(a.OrganicMatter, b.OrganicMatter) &&
               Mathf.IsEqualApprox(a.Nutrients, b.Nutrients) &&
               a.Disturbance == b.Disturbance &&
               Mathf.IsEqualApprox(a.CropGrowth, b.CropGrowth) &&
               Mathf.IsEqualApprox(a.WeedPressure, b.WeedPressure) &&
               Mathf.IsEqualApprox(a.LastFarmCareGameHours, b.LastFarmCareGameHours);
    }
}
