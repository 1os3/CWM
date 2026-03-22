using Godot;
using CWM.Scripts.Core;
using CWM.Scripts.World;
using CWM.Scripts.Weather;

namespace CWM.Scripts.Ecology;

public sealed class ClimateField
{
    private const float TemperatureAnchorBlend = 0.65f;
    private const float MoistureAnchorBlend = 0.60f;
    private const float TemperatureRelaxationPerHour = 0.11f;
    private const float MoistureRelaxationPerHour = 0.10f;
    private const float BaseTemperatureDiffusionPerHour = 0.008f;
    private const float WindTemperatureDiffusionPerHour = 0.014f;
    private const float BaseMoistureDiffusionPerHour = 0.004f;
    private const float WindMoistureDiffusionPerHour = 0.010f;

    private readonly int _cellSize;
    private int _width;
    private int _height;
    private int _worldWidth;
    private int _worldHeight;
    private float _temperaturePhase;
    private float _moisturePhase;
    private float[] _temperature = Array.Empty<float>();
    private float[] _temperatureNext = Array.Empty<float>();
    private float[] _moisture = Array.Empty<float>();
    private float[] _moistureNext = Array.Empty<float>();
    private float[] _temperatureAnchor = Array.Empty<float>();
    private float[] _moistureAnchor = Array.Empty<float>();
    private float[] _rainfallControl = Array.Empty<float>();
    private float[] _sunlightControl = Array.Empty<float>();
    private float[] _anchorElevation = Array.Empty<float>();
    private float[] _anchorWaterFraction = Array.Empty<float>();
    private float[] _elevation = Array.Empty<float>();
    private float[] _waterFraction = Array.Empty<float>();

    public ClimateField(int cellSize)
    {
        _cellSize = Math.Max(1, cellSize);
    }

    public void Initialize(WorldData world)
    {
        _worldWidth = world.Width;
        _worldHeight = world.Height;
        _temperaturePhase = Mathf.PosMod(world.Seed * 0.17320508f, Mathf.Tau);
        _moisturePhase = Mathf.PosMod(world.Seed * 0.27182818f, Mathf.Tau);
        _width = Mathf.CeilToInt((float)world.Width / _cellSize);
        _height = Mathf.CeilToInt((float)world.Height / _cellSize);
        var count = _width * _height;
        _temperature = new float[count];
        _temperatureNext = new float[count];
        _moisture = new float[count];
        _moistureNext = new float[count];
        _temperatureAnchor = new float[count];
        _moistureAnchor = new float[count];
        _rainfallControl = new float[count];
        _sunlightControl = new float[count];
        _anchorElevation = new float[count];
        _anchorWaterFraction = new float[count];
        _elevation = new float[count];
        _waterFraction = new float[count];
        RebuildFromWorld(world);
    }

    public void RebuildFromWorld(WorldData world)
    {
        if (_temperature.Length == 0)
        {
            Initialize(world);
            return;
        }

        for (var cellY = 0; cellY < _height; cellY++)
        {
            for (var cellX = 0; cellX < _width; cellX++)
            {
                RebuildCell(world, new Vector2I(cellX, cellY), syncStateFromTiles: true, refreshAnchors: true);
            }
        }
    }

    public void RefreshForcingFromTiles(WorldData world, IEnumerable<Vector2I> changedTiles)
    {
        if (_temperature.Length == 0)
        {
            Initialize(world);
            return;
        }

        var dirtyClimateCells = new HashSet<Vector2I>();
        foreach (var tileCell in changedTiles)
        {
            dirtyClimateCells.Add(ToClimateCell(tileCell));
        }

        foreach (var climateCell in dirtyClimateCells)
        {
            RebuildCell(world, climateCell, syncStateFromTiles: false, refreshAnchors: false);
        }
    }

    public void SyncStateFromTiles(WorldData world, IEnumerable<Vector2I> changedTiles)
    {
        if (_temperature.Length == 0)
        {
            Initialize(world);
            return;
        }

        var dirtyClimateCells = new HashSet<Vector2I>();
        foreach (var tileCell in changedTiles)
        {
            dirtyClimateCells.Add(ToClimateCell(tileCell));
        }

        foreach (var climateCell in dirtyClimateCells)
        {
            RebuildCell(world, climateCell, syncStateFromTiles: true, refreshAnchors: false);
        }
    }

    public void RefreshAnchorsFromTiles(WorldData world, IEnumerable<Vector2I> changedTiles, bool syncStateFromTiles)
    {
        if (_temperature.Length == 0)
        {
            Initialize(world);
            return;
        }

        var dirtyClimateCells = new HashSet<Vector2I>();
        foreach (var tileCell in changedTiles)
        {
            dirtyClimateCells.Add(ToClimateCell(tileCell));
        }

        foreach (var climateCell in dirtyClimateCells)
        {
            RebuildCell(world, climateCell, syncStateFromTiles, refreshAnchors: true);
        }
    }

    public void Step(DayNightCycle dayNightCycle, SeasonManager seasonManager, WeatherSystem weatherSystem, float deltaHours)
    {
        if (_temperature.Length == 0)
        {
            return;
        }

        var stepHours = Mathf.Max(deltaHours, 0.001f);
        var daylight = dayNightCycle.GetDaylightFactor();
        var seasonalBias = seasonManager.GetTemperatureOffset();
        var rainPerHour = weatherSystem.GetRainContributionPerHour();
        var evaporationModifier = weatherSystem.GetEvaporationModifier();
        var windMixing = Mathf.Clamp(weatherSystem.CurrentWind / 0.42f, 0.0f, 1.0f);
        var daylightAnomaly = (daylight - 0.5f) * 0.10f;
        var seasonalAnomaly = seasonalBias * 0.55f;
        var synopticAnomaly = weatherSystem.TemperatureModifier * 0.85f;

        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var index = ToIndex(x, y);
                var temperature = _temperature[index];
                var moisture = _moisture[index];
                var elevation = _elevation[index];
                var waterFraction = _waterFraction[index];
                var temperatureAnchor = _temperatureAnchor[index];
                var moistureAnchor = _moistureAnchor[index];
                var rainfallControl = _rainfallControl[index];
                var sunlightControl = _sunlightControl[index];
                var elevationDelta = elevation - _anchorElevation[index];
                var waterFractionDelta = waterFraction - _anchorWaterFraction[index];
                var neighborTemperature = SampleNeighborAverage(_temperature, x, y, temperature);
                var neighborMoisture = SampleNeighborAverage(_moisture, x, y, moisture);

                var heatCapacity = Mathf.Lerp(0.95f, 2.15f, waterFraction);
                var radiativeEquilibriumTemperature = Mathf.Clamp(
                    temperatureAnchor +
                    seasonalAnomaly +
                    daylightAnomaly +
                    synopticAnomaly +
                    ((sunlightControl - 0.5f) * 0.18f) +
                    (waterFractionDelta * 0.08f) -
                    (elevationDelta * 0.24f) -
                    ((moisture - moistureAnchor) * 0.03f),
                    0.0f,
                    1.0f);

                var temperatureDiffusionPerHour = (BaseTemperatureDiffusionPerHour + (windMixing * WindTemperatureDiffusionPerHour)) * (neighborTemperature - temperature);
                var radiativeRelaxationPerHour = TemperatureRelaxationPerHour * (radiativeEquilibriumTemperature - temperature);
                _temperatureNext[index] = Mathf.Clamp(
                    temperature + (((temperatureDiffusionPerHour + radiativeRelaxationPerHour) / heatCapacity) * stepHours),
                    0.0f,
                    1.0f);

                var localMoistureEquilibrium = Mathf.Clamp(
                    moistureAnchor +
                    ((rainfallControl - 0.5f) * 0.34f) +
                    (waterFractionDelta * 0.30f) -
                    (elevationDelta * 0.10f),
                    0.0f,
                    1.0f);
                var precipitationBias = Mathf.Clamp(localMoistureEquilibrium + (Mathf.Max(elevation - 0.44f, 0.0f) * 0.6f), 0.0f, 1.0f);
                var precipitationSourcePerHour =
                    rainPerHour *
                    Mathf.Lerp(0.28f, 1.0f, precipitationBias) *
                    Mathf.Lerp(0.35f, 1.25f, rainfallControl);
                var oceanVaporSourcePerHour = waterFraction * (0.006f + (windMixing * 0.014f));
                var orographicLiftPerHour = Mathf.Max(elevation - 0.44f, 0.0f) * Mathf.Lerp(0.25f, 1.0f, moisture) * (0.007f + (windMixing * 0.010f));
                var diffusionPerHour = (BaseMoistureDiffusionPerHour + (windMixing * WindMoistureDiffusionPerHour)) * (neighborMoisture - moisture);
                var relaxationPerHour = MoistureRelaxationPerHour * (localMoistureEquilibrium - moisture);
                var evaporationSinkPerHour =
                    (0.006f + (_temperatureNext[index] * 0.022f)) *
                    evaporationModifier *
                    Mathf.Lerp(0.72f, 1.38f, sunlightControl) *
                    Mathf.Lerp(1.05f, 0.72f, localMoistureEquilibrium);
                var runoffSinkPerHour = Mathf.Max(elevation - 0.62f, 0.0f) * 0.010f;

                _moistureNext[index] = Mathf.Clamp(
                    moisture + ((precipitationSourcePerHour + oceanVaporSourcePerHour + orographicLiftPerHour + diffusionPerHour + relaxationPerHour - evaporationSinkPerHour - runoffSinkPerHour) * stepHours),
                    0.0f,
                    1.0f);
            }
        }

        (_temperature, _temperatureNext) = (_temperatureNext, _temperature);
        (_moisture, _moistureNext) = (_moistureNext, _moisture);
    }

    public float SampleTemperature(Vector2 tileCoordinates) => SampleBilinear(_temperature, tileCoordinates);

    public float SampleMoisture(Vector2 tileCoordinates) => SampleBilinear(_moisture, tileCoordinates);

    private void RebuildCell(WorldData world, Vector2I climateCell, bool syncStateFromTiles, bool refreshAnchors)
    {
        if (climateCell.X < 0 || climateCell.Y < 0 || climateCell.X >= _width || climateCell.Y >= _height)
        {
            return;
        }

        var startX = climateCell.X * _cellSize;
        var startY = climateCell.Y * _cellSize;
        var endX = Math.Min(startX + _cellSize, world.Width);
        var endY = Math.Min(startY + _cellSize, world.Height);
        var elevation = 0.0f;
        var temperature = 0.0f;
        var moisture = 0.0f;
        var rainfall = 0.0f;
        var sunlight = 0.0f;
        var waterCount = 0;
        var farmCount = 0;
        var count = 0;

        for (var y = startY; y < endY; y++)
        {
            for (var x = startX; x < endX; x++)
            {
                var tile = world.GetTile(x, y);
                elevation += tile.Elevation;
                temperature += tile.Temperature;
                moisture += tile.Moisture;
                rainfall += tile.Rainfall;
                sunlight += tile.Sunlight;
                if (tile.IsWater)
                {
                    waterCount++;
                }

                if (tile.Biome == BiomeType.Farmland)
                {
                    farmCount++;
                }

                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        var index = ToIndex(climateCell.X, climateCell.Y);
        var averageElevation = elevation / count;
        var averageTemperature = temperature / count;
        var averageMoisture = moisture / count;
        var averageRainfall = rainfall / count;
        var averageSunlight = sunlight / count;
        var averageWaterFraction = waterCount / (float)count;
        var farmFraction = farmCount / (float)count;
        averageTemperature += farmFraction * 0.018f;
        averageMoisture *= Mathf.Lerp(1.0f, 0.94f, Mathf.Clamp(farmFraction * 1.35f, 0.0f, 1.0f));
        _elevation[index] = averageElevation;
        _waterFraction[index] = averageWaterFraction;
        _rainfallControl[index] = averageRainfall;
        _sunlightControl[index] = averageSunlight;
        if (refreshAnchors)
        {
            var cellCenterX = (startX + endX - 1) * 0.5f;
            var cellCenterY = (startY + endY - 1) * 0.5f;
            _anchorElevation[index] = averageElevation;
            _anchorWaterFraction[index] = averageWaterFraction;
            _temperatureAnchor[index] = Mathf.Clamp(
                Mathf.Lerp(
                    ComputeGeographicTemperatureAnchor(cellCenterX, cellCenterY, averageElevation, averageWaterFraction, averageSunlight),
                    averageTemperature,
                    TemperatureAnchorBlend),
                0.0f,
                1.0f);
            _moistureAnchor[index] = Mathf.Clamp(
                Mathf.Lerp(
                    ComputeGeographicMoistureAnchor(cellCenterX, cellCenterY, averageElevation, averageWaterFraction, averageRainfall),
                    averageMoisture,
                    MoistureAnchorBlend),
                0.0f,
                1.0f);
        }

        if (syncStateFromTiles)
        {
            _temperature[index] = averageTemperature;
            _moisture[index] = averageMoisture;
        }
    }

    private float ComputeGeographicTemperatureAnchor(float cellCenterX, float cellCenterY, float elevation, float waterFraction, float sunlightControl)
    {
        var centeredUv = GetCenteredUv(cellCenterX, cellCenterY);
        var latitudeCooling = Mathf.Abs(centeredUv.Y) * 0.28f;
        var zonalWave = Mathf.Sin(((cellCenterX / Mathf.Max(_worldWidth, 1)) * Mathf.Pi * 2.0f) + _temperaturePhase) * 0.05f;
        var meridionalWave = Mathf.Sin(((cellCenterY / Mathf.Max(_worldHeight, 1)) * Mathf.Pi * 3.0f) + (_temperaturePhase * 0.7f)) * 0.03f;

        return 0.60f -
               latitudeCooling -
               (elevation * 0.26f) +
               ((sunlightControl - 0.5f) * 0.16f) +
               (waterFraction * 0.04f) +
               zonalWave +
               meridionalWave;
    }

    private float ComputeGeographicMoistureAnchor(float cellCenterX, float cellCenterY, float elevation, float waterFraction, float rainfallControl)
    {
        var centeredUv = GetCenteredUv(cellCenterX, cellCenterY);
        var islandHumidity = (1.0f - Mathf.Clamp(centeredUv.Length(), 0.0f, 1.0f)) * 0.10f;
        var latitudeWave = Mathf.Sin(((cellCenterY / Mathf.Max(_worldHeight, 1)) * Mathf.Pi * 4.0f) + _moisturePhase) * 0.06f;
        var crossWave = Mathf.Sin(((cellCenterX / Mathf.Max(_worldWidth, 1)) * Mathf.Pi * 3.0f) + (_moisturePhase * 0.9f)) * 0.04f;

        return 0.22f +
               islandHumidity +
               ((rainfallControl - 0.5f) * 0.34f) +
               (waterFraction * 0.46f) -
               (Mathf.Max(elevation - 0.68f, 0.0f) * 0.14f) +
               latitudeWave +
               crossWave;
    }

    private Vector2 GetCenteredUv(float cellCenterX, float cellCenterY)
    {
        var halfWidth = Mathf.Max(_worldWidth * 0.5f, 0.5f);
        var halfHeight = Mathf.Max(_worldHeight * 0.5f, 0.5f);
        return new Vector2(
            (((cellCenterX + 0.5f) - halfWidth) / halfWidth),
            (((cellCenterY + 0.5f) - halfHeight) / halfHeight));
    }

    private Vector2I ToClimateCell(Vector2I tileCell)
    {
        return new Vector2I(
            Mathf.Clamp(tileCell.X / _cellSize, 0, _width - 1),
            Mathf.Clamp(tileCell.Y / _cellSize, 0, _height - 1));
    }

    private int ToIndex(int x, int y) => y * _width + x;

    private float SampleBilinear(float[] field, Vector2 tileCoordinates)
    {
        if (field.Length == 0)
        {
            return 0.0f;
        }

        var climateX = Mathf.Clamp(tileCoordinates.X / _cellSize, 0.0f, _width - 1);
        var climateY = Mathf.Clamp(tileCoordinates.Y / _cellSize, 0.0f, _height - 1);
        var x0 = Mathf.Clamp(Mathf.FloorToInt(climateX), 0, _width - 1);
        var y0 = Mathf.Clamp(Mathf.FloorToInt(climateY), 0, _height - 1);
        var x1 = Mathf.Clamp(x0 + 1, 0, _width - 1);
        var y1 = Mathf.Clamp(y0 + 1, 0, _height - 1);
        var tx = Mathf.Clamp(climateX - x0, 0.0f, 1.0f);
        var ty = Mathf.Clamp(climateY - y0, 0.0f, 1.0f);

        var v00 = field[ToIndex(x0, y0)];
        var v10 = field[ToIndex(x1, y0)];
        var v01 = field[ToIndex(x0, y1)];
        var v11 = field[ToIndex(x1, y1)];
        var top = Mathf.Lerp(v00, v10, tx);
        var bottom = Mathf.Lerp(v01, v11, tx);
        return Mathf.Lerp(top, bottom, ty);
    }

    private float SampleNeighborAverage(float[] field, int x, int y, float fallback)
    {
        var total = 0.0f;
        var count = 0;
        if (x + 1 < _width)
        {
            total += field[ToIndex(x + 1, y)];
            count++;
        }

        if (x - 1 >= 0)
        {
            total += field[ToIndex(x - 1, y)];
            count++;
        }

        if (y + 1 < _height)
        {
            total += field[ToIndex(x, y + 1)];
            count++;
        }

        if (y - 1 >= 0)
        {
            total += field[ToIndex(x, y - 1)];
            count++;
        }

        return count > 0 ? total / count : fallback;
    }
}
