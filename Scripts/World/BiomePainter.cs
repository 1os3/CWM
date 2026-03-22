using Godot;

namespace CWM.Scripts.World;

public static class BiomePainter
{
    public static bool ApplyBiome(ref TileData tile, BiomeType biome)
    {
        var previous = tile;
        var (elevation, moisture, temperature, rainfall, sunlight, soilMoisture, snowCover, floraGrowth) = GetSignature(biome);

        tile.Biome = biome;
        tile.Elevation = elevation;
        tile.Moisture = moisture;
        tile.Temperature = temperature;
        tile.Rainfall = rainfall;
        tile.Sunlight = sunlight;
        tile.SoilMoisture = soilMoisture;
        tile.Nutrients = BiomeClassifier.GetBaseNutrients(biome);
        tile.OrganicMatter = BiomeClassifier.GetBaseOrganicMatter(biome);
        tile.Succession = BiomeClassifier.GetDefaultSuccession(biome);
        tile.Flora = GetPaintFlora(biome);
        tile.FloraGrowth = floraGrowth;
        tile.SnowCover = snowCover;
        tile.Disturbance = 0;

        return !Equivalent(previous, tile);
    }

    private static (float Elevation, float Moisture, float Temperature, float Rainfall, float Sunlight, float SoilMoisture, float SnowCover, float FloraGrowth) GetSignature(BiomeType biome)
    {
        return biome switch
        {
            BiomeType.DeepWater => (0.02f, 1.0f, 0.50f, 0.95f, 0.48f, 1.0f, 0.0f, 0.0f),
            BiomeType.ShallowWater => (0.10f, 0.98f, 0.55f, 0.88f, 0.55f, 1.0f, 0.0f, 0.0f),
            BiomeType.Beach => (0.18f, 0.22f, 0.72f, 0.24f, 0.86f, 0.22f, 0.0f, 0.12f),
            BiomeType.Grassland => (0.48f, 0.52f, 0.66f, 0.52f, 0.74f, 0.55f, 0.0f, 0.95f),
            BiomeType.Forest => (0.56f, 0.82f, 0.58f, 0.78f, 0.56f, 0.70f, 0.0f, 1.0f),
            BiomeType.ConiferForest => (0.70f, 0.58f, 0.30f, 0.60f, 0.42f, 0.60f, 0.12f, 1.0f),
            BiomeType.Swamp => (0.26f, 0.94f, 0.62f, 0.92f, 0.52f, 0.95f, 0.0f, 0.90f),
            BiomeType.Desert => (0.42f, 0.08f, 0.86f, 0.08f, 0.96f, 0.08f, 0.0f, 0.65f),
            BiomeType.Snow => (0.84f, 0.48f, 0.10f, 0.44f, 0.28f, 0.36f, 1.0f, 0.72f),
            BiomeType.RockyHighlands => (0.74f, 0.22f, 0.42f, 0.18f, 0.62f, 0.18f, 0.0f, 0.42f),
            BiomeType.AlpineMeadow => (0.68f, 0.50f, 0.24f, 0.48f, 0.50f, 0.48f, 0.30f, 0.88f),
            BiomeType.BareRock => (0.88f, 0.10f, 0.45f, 0.10f, 0.70f, 0.06f, 0.0f, 0.22f),
            BiomeType.Farmland => (0.45f, 0.46f, 0.63f, 0.48f, 0.72f, 0.40f, 0.0f, 0.0f),
            _ => (0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f)
        };
    }

    private static FloraType GetPaintFlora(BiomeType biome)
    {
        var flora = BiomeClassifier.GetInitialFlora(biome);
        if (flora != FloraType.None)
        {
            return flora;
        }

        return biome switch
        {
            BiomeType.Farmland => FloraType.None,
            BiomeType.RockyHighlands => FloraType.Shrub,
            BiomeType.BareRock => FloraType.Moss,
            BiomeType.Beach => FloraType.Grass,
            _ => FloraType.None
        };
    }

    private static bool Equivalent(TileData a, TileData b)
    {
        return a.Biome == b.Biome &&
               a.Flora == b.Flora &&
               a.Succession == b.Succession &&
               Mathf.IsEqualApprox(a.Elevation, b.Elevation) &&
               Mathf.IsEqualApprox(a.Moisture, b.Moisture) &&
               Mathf.IsEqualApprox(a.Temperature, b.Temperature) &&
               Mathf.IsEqualApprox(a.Rainfall, b.Rainfall) &&
               Mathf.IsEqualApprox(a.Sunlight, b.Sunlight) &&
               Mathf.IsEqualApprox(a.SoilMoisture, b.SoilMoisture) &&
               Mathf.IsEqualApprox(a.Nutrients, b.Nutrients) &&
               Mathf.IsEqualApprox(a.OrganicMatter, b.OrganicMatter) &&
               Mathf.IsEqualApprox(a.FloraGrowth, b.FloraGrowth) &&
               Mathf.IsEqualApprox(a.SnowCover, b.SnowCover) &&
               a.Disturbance == b.Disturbance &&
               Mathf.IsEqualApprox(a.CropGrowth, b.CropGrowth) &&
               Mathf.IsEqualApprox(a.WeedPressure, b.WeedPressure) &&
               Mathf.IsEqualApprox(a.LastFarmCareGameHours, b.LastFarmCareGameHours);
    }
}
