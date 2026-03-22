using Godot;

namespace CWM.Scripts.World;

public readonly record struct BiomeEcologyProfile(
    float IdealTemperature,
    float TemperatureTolerance,
    float IdealSoilMoisture,
    float SoilMoistureTolerance,
    float VegetationPotential,
    SuccessionStage SuccessionCap,
    float DisturbanceRecoveryPerHour);

public static class BiomeClassifier
{
    public static BiomeType Classify(float elevation, float moisture, float temperature)
    {
        if (elevation < 0.05f)
        {
            return BiomeType.DeepWater;
        }

        if (elevation < 0.15f)
        {
            return BiomeType.ShallowWater;
        }

        if (elevation > 0.85f)
        {
            return temperature < 0.42f ? BiomeType.Snow : BiomeType.BareRock;
        }

        if (elevation > 0.65f)
        {
            if (temperature < 0.38f)
            {
                return BiomeType.Snow;
            }

            return moisture > 0.55f ? BiomeType.ConiferForest : BiomeType.RockyHighlands;
        }

        if (elevation > 0.35f)
        {
            if (temperature < 0.28f)
            {
                return moisture > 0.45f ? BiomeType.ConiferForest : BiomeType.AlpineMeadow;
            }

            if (moisture > 0.68f)
            {
                return BiomeType.Forest;
            }

            if (moisture > 0.38f)
            {
                return BiomeType.Grassland;
            }

            return temperature > 0.6f ? BiomeType.Desert : BiomeType.Beach;
        }

        if (moisture > 0.72f)
        {
            return BiomeType.Swamp;
        }

        if (moisture < 0.22f)
        {
            return BiomeType.Desert;
        }

        return elevation < 0.2f ? BiomeType.Beach : BiomeType.Grassland;
    }

    public static FloraType GetInitialFlora(BiomeType biome) => biome switch
    {
        BiomeType.Forest => FloraType.BroadleafTree,
        BiomeType.ConiferForest => FloraType.ConiferTree,
        BiomeType.Swamp => FloraType.Reed,
        BiomeType.Desert => FloraType.Cactus,
        BiomeType.Grassland => FloraType.Grass,
        BiomeType.AlpineMeadow => FloraType.AlpineBloom,
        BiomeType.Snow => FloraType.Moss,
        BiomeType.Farmland => FloraType.None,
        _ => FloraType.None
    };

    public static SuccessionStage GetDefaultSuccession(BiomeType biome) => biome switch
    {
        BiomeType.Forest or BiomeType.ConiferForest => SuccessionStage.Climax,
        BiomeType.Grassland or BiomeType.AlpineMeadow or BiomeType.Swamp => SuccessionStage.Intermediate,
        BiomeType.Desert or BiomeType.Beach => SuccessionStage.Pioneer,
        BiomeType.Farmland => SuccessionStage.Pioneer,
        _ => SuccessionStage.Bare
    };

    public static float GetBaseNutrients(BiomeType biome) => biome switch
    {
        BiomeType.Forest => 0.85f,
        BiomeType.Swamp => 0.9f,
        BiomeType.Grassland => 0.72f,
        BiomeType.ConiferForest => 0.64f,
        BiomeType.AlpineMeadow => 0.58f,
        BiomeType.Beach => 0.28f,
        BiomeType.Desert => 0.18f,
        BiomeType.Snow => 0.22f,
        BiomeType.RockyHighlands or BiomeType.BareRock => 0.12f,
        BiomeType.Farmland => 0.68f,
        _ => 0.0f
    };

    public static float GetBaseOrganicMatter(BiomeType biome) => biome switch
    {
        BiomeType.Forest => 0.82f,
        BiomeType.ConiferForest => 0.7f,
        BiomeType.Swamp => 0.9f,
        BiomeType.Grassland => 0.55f,
        BiomeType.AlpineMeadow => 0.4f,
        BiomeType.Desert => 0.08f,
        BiomeType.Beach => 0.12f,
        BiomeType.Snow => 0.14f,
        BiomeType.Farmland => 0.44f,
        _ => 0.06f
    };

    public static Color GetMiniMapColor(BiomeType biome) => biome switch
    {
        BiomeType.DeepWater => new Color("21487a"),
        BiomeType.ShallowWater => new Color("3a74b0"),
        BiomeType.Beach => new Color("d0bf74"),
        BiomeType.Grassland => new Color("6db353"),
        BiomeType.Forest => new Color("2d7d32"),
        BiomeType.ConiferForest => new Color("2a5c3c"),
        BiomeType.Swamp => new Color("466d3c"),
        BiomeType.Desert => new Color("c89a49"),
        BiomeType.Snow => new Color("e8f4f8"),
        BiomeType.RockyHighlands => new Color("8a7f77"),
        BiomeType.AlpineMeadow => new Color("8bbd7d"),
        BiomeType.BareRock => new Color("706963"),
        BiomeType.Farmland => new Color("b8956a"),
        _ => Colors.Magenta
    };

    public static BiomeEcologyProfile GetEcologyProfile(BiomeType biome) => biome switch
    {
        BiomeType.DeepWater => new BiomeEcologyProfile(0.5f, 0.4f, 1.0f, 0.15f, 0.0f, SuccessionStage.Bare, 0.01f),
        BiomeType.ShallowWater => new BiomeEcologyProfile(0.55f, 0.35f, 1.0f, 0.15f, 0.0f, SuccessionStage.Bare, 0.01f),
        BiomeType.Beach => new BiomeEcologyProfile(0.72f, 0.25f, 0.22f, 0.18f, 0.18f, SuccessionStage.Pioneer, 0.03f),
        BiomeType.Grassland => new BiomeEcologyProfile(0.66f, 0.24f, 0.55f, 0.22f, 0.72f, SuccessionStage.Intermediate, 0.06f),
        BiomeType.Forest => new BiomeEcologyProfile(0.58f, 0.20f, 0.70f, 0.18f, 0.96f, SuccessionStage.Climax, 0.08f),
        BiomeType.ConiferForest => new BiomeEcologyProfile(0.30f, 0.18f, 0.60f, 0.18f, 0.88f, SuccessionStage.Climax, 0.06f),
        BiomeType.Swamp => new BiomeEcologyProfile(0.62f, 0.18f, 0.92f, 0.10f, 0.82f, SuccessionStage.Climax, 0.04f),
        BiomeType.Desert => new BiomeEcologyProfile(0.86f, 0.12f, 0.10f, 0.08f, 0.10f, SuccessionStage.Pioneer, 0.02f),
        BiomeType.Snow => new BiomeEcologyProfile(0.10f, 0.10f, 0.36f, 0.14f, 0.15f, SuccessionStage.Pioneer, 0.025f),
        BiomeType.RockyHighlands => new BiomeEcologyProfile(0.42f, 0.20f, 0.18f, 0.10f, 0.16f, SuccessionStage.Pioneer, 0.03f),
        BiomeType.AlpineMeadow => new BiomeEcologyProfile(0.24f, 0.14f, 0.48f, 0.16f, 0.52f, SuccessionStage.Intermediate, 0.05f),
        BiomeType.BareRock => new BiomeEcologyProfile(0.45f, 0.22f, 0.06f, 0.05f, 0.04f, SuccessionStage.Bare, 0.015f),
        BiomeType.Farmland => new BiomeEcologyProfile(0.64f, 0.22f, 0.48f, 0.20f, 0.22f, SuccessionStage.Pioneer, 0.05f),
        _ => new BiomeEcologyProfile(0.5f, 0.3f, 0.5f, 0.2f, 0.0f, SuccessionStage.Bare, 0.02f)
    };

    public static float GetAmbientSoilMoistureTarget(BiomeType biome, float climateMoisture)
    {
        var profile = GetEcologyProfile(biome);
        return Mathf.Clamp((climateMoisture * 0.6f) + (profile.IdealSoilMoisture * 0.4f), 0.0f, 1.0f);
    }

    public static float GetHabitatSuitability(BiomeType biome, float temperature, float soilMoisture, float ambientMoisture)
    {
        var profile = GetEcologyProfile(biome);
        if (profile.VegetationPotential <= 0.0f)
        {
            return 0.0f;
        }

        var temperatureFit = 1.0f - (Mathf.Abs(temperature - profile.IdealTemperature) / Mathf.Max(profile.TemperatureTolerance, 0.001f));
        var hydrology = Mathf.Lerp(soilMoisture, ambientMoisture, 0.35f);
        var moistureFit = 1.0f - (Mathf.Abs(hydrology - profile.IdealSoilMoisture) / Mathf.Max(profile.SoilMoistureTolerance, 0.001f));

        temperatureFit = Mathf.Clamp(temperatureFit, 0.0f, 1.0f);
        moistureFit = Mathf.Clamp(moistureFit, 0.0f, 1.0f);
        return Mathf.Sqrt(temperatureFit * moistureFit) * profile.VegetationPotential;
    }
}
