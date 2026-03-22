using Godot;
using CWM.Scripts.Core;

namespace CWM.Scripts.World;

public partial class WorldGenerator : Node
{
    [Export]
    public int Seed { get; set; } = 1337;

    [Export]
    public int MapSize { get; set; } = Constants.DefaultMapSize;

    [Export(PropertyHint.Range, "0.001,0.02,0.001")]
    public float ElevationFrequency { get; set; } = 0.0045f;

    [Export(PropertyHint.Range, "0.001,0.02,0.001")]
    public float MoistureFrequency { get; set; } = 0.0055f;

    [Export(PropertyHint.Range, "0.001,0.02,0.001")]
    public float TemperatureFrequency { get; set; } = 0.0035f;

    public WorldData GenerateWorld() => GenerateWorld(Seed, MapSize);

    public WorldData GenerateWorld(int seed, int mapSize)
    {
        Seed = seed;
        MapSize = mapSize;

        var elevationNoise = CreateNoise(seed + 11, ElevationFrequency, FastNoiseLite.NoiseTypeEnum.SimplexSmooth);
        var detailNoise = CreateNoise(seed + 53, ElevationFrequency * 2.1f, FastNoiseLite.NoiseTypeEnum.Perlin);
        var moistureNoise = CreateNoise(seed + 101, MoistureFrequency, FastNoiseLite.NoiseTypeEnum.Cellular);
        var temperatureNoise = CreateNoise(seed + 211, TemperatureFrequency, FastNoiseLite.NoiseTypeEnum.Simplex);
        var coastNoise = CreateNoise(seed + 379, ElevationFrequency * 1.3f, FastNoiseLite.NoiseTypeEnum.Perlin);

        var world = new WorldData(mapSize, mapSize, seed);
        var half = mapSize / 2.0f;
        var maxDimension = Mathf.Max(mapSize, 1);

        for (var y = 0; y < mapSize; y++)
        {
            for (var x = 0; x < mapSize; x++)
            {
                var normalizedX = ((x + 0.5f) - half) / half;
                var normalizedY = ((y + 0.5f) - half) / half;
                var centeredUv = new Vector2(normalizedX, normalizedY);

                var baseElevation = Normalize(elevationNoise.GetNoise2D(x, y));
                var detail = Normalize(detailNoise.GetNoise2D(x, y));
                var coast = (Normalize(coastNoise.GetNoise2D(x, y)) * 2.0f) - 1.0f;
                var mask = IslandMask.Sample(centeredUv, 1.0f, 2.2f, coast, 0.14f);

                var elevation = Mathf.Clamp((baseElevation * 0.62f) + (detail * 0.18f) + (mask * 0.62f) - 0.34f, 0.0f, 1.0f);
                var moisture = Mathf.Clamp(
                    (Normalize(moistureNoise.GetNoise2D(x, y)) * 0.82f) +
                    ((1.0f - centeredUv.Length()) * 0.12f) +
                    (0.06f * Mathf.Sin(y / maxDimension * Mathf.Pi * 4.0f)),
                    0.0f,
                    1.0f);
                var latitudeCooling = Mathf.Abs(normalizedY) * 0.3f;
                var altitudeCooling = elevation * 0.38f;
                var temperature = Mathf.Clamp(
                    (Normalize(temperatureNoise.GetNoise2D(x, y)) * 0.7f) +
                    (0.25f * (1.0f - latitudeCooling)) -
                    altitudeCooling,
                    0.0f,
                    1.0f);
                var rainfall = Mathf.Clamp(
                    (moisture * 0.72f) +
                    ((1.0f - Mathf.Clamp(centeredUv.Length(), 0.0f, 1.0f)) * 0.08f) +
                    (Mathf.Max(elevation - 0.45f, 0.0f) * 0.05f) +
                    (0.05f * Normalize(moistureNoise.GetNoise2D(x + 37, y - 29))),
                    0.0f,
                    1.0f);
                var sunlight = Mathf.Clamp(
                    0.48f +
                    ((1.0f - latitudeCooling) * 0.22f) -
                    (moisture * 0.10f) -
                    (elevation * 0.08f) +
                    (0.06f * Normalize(temperatureNoise.GetNoise2D(x - 41, y + 73))),
                    0.0f,
                    1.0f);

                var biome = BiomeClassifier.Classify(elevation, moisture, temperature);
                var flora = BiomeClassifier.GetInitialFlora(biome);
                var succession = BiomeClassifier.GetDefaultSuccession(biome);
                var baseNutrients = BiomeClassifier.GetBaseNutrients(biome);
                var organic = BiomeClassifier.GetBaseOrganicMatter(biome);
                var floraGrowth = biome is BiomeType.DeepWater or BiomeType.ShallowWater
                    ? 0.0f
                    : Mathf.Clamp(organic * 0.9f + (Normalize(detailNoise.GetNoise2D(x + 91, y + 19)) * 0.15f), 0.0f, 1.0f);

                if (floraGrowth < 0.16f)
                {
                    flora = FloraType.None;
                }

                var tile = new TileData
                {
                    Biome = biome,
                    Elevation = elevation,
                    Moisture = moisture,
                    Temperature = temperature,
                    Rainfall = rainfall,
                    Sunlight = sunlight,
                    SoilMoisture = biome.IsWater() ? 1.0f : Mathf.Clamp((moisture * 0.75f) + (mask * 0.15f), 0.0f, 1.0f),
                    Nutrients = baseNutrients,
                    OrganicMatter = organic,
                    FloraGrowth = floraGrowth,
                    SnowCover = biome == BiomeType.Snow ? 0.65f : 0.0f,
                    Succession = succession,
                    Flora = flora,
                    Disturbance = 0
                };

                world.SetTile(x, y, tile);
            }
        }

        return world;
    }

    public Vector2I FindSpawnCell(WorldData world)
    {
        var center = new Vector2I(world.Width / 2, world.Height / 2);
        var bestCell = center;
        var bestScore = float.MinValue;
        var radius = Mathf.Min(world.Width, world.Height) / 3;

        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                if (!world.Contains(x, y))
                {
                    continue;
                }

                var cell = new Vector2I(x, y);
                var tile = world.GetTile(cell);
                if (tile.IsWater || tile.Biome is BiomeType.BareRock or BiomeType.RockyHighlands)
                {
                    continue;
                }

                var centerDistance = center.DistanceTo(cell);
                var score = tile.Nutrients + tile.FloraGrowth + (tile.SoilMoisture * 0.4f) - (centerDistance / radius);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCell = cell;
                }
            }
        }

        return bestCell;
    }

    private static FastNoiseLite CreateNoise(int seed, float frequency, FastNoiseLite.NoiseTypeEnum noiseType)
    {
        return new FastNoiseLite
        {
            Seed = seed,
            Frequency = frequency,
            NoiseType = noiseType,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 4,
            FractalGain = 0.55f,
            FractalLacunarity = 2.0f
        };
    }

    private static float Normalize(float noiseValue) => (noiseValue * 0.5f) + 0.5f;
}

internal static class BiomeExtensions
{
    public static bool IsWater(this BiomeType biome) => biome is BiomeType.DeepWater or BiomeType.ShallowWater;
}
