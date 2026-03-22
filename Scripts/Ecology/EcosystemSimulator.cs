using Godot;
using CWM.Scripts.Core;
using CWM.Scripts.World;
using CWM.Scripts.Weather;
using WorldTileData = CWM.Scripts.World.TileData;

namespace CWM.Scripts.Ecology;

public partial class EcosystemSimulator : Node
{
    private WorldData? _world;
    private DayNightCycle? _dayNightCycle;
    private SeasonManager? _seasonManager;
    private WeatherSystem? _weatherSystem;
    private readonly ClimateField _climateField = new(Constants.ClimateFieldCellSize);
    private readonly RandomNumberGenerator _rng = new();
    private readonly Queue<Vector2I> _chunkQueue = new();
    private readonly List<Vector2> _focusPositions = [];
    private readonly List<Vector2I> _syncDirtyTiles = [];
    private readonly List<(Vector2I Chunk, int DistSq)> _chunkQueueSortBuffer = [];
    private readonly HashSet<Vector2I> _focusChunkDedup = [];
    private readonly Dictionary<Vector2I, float> _chunkLastUpdatedHours = [];
    private float _simulationAccumulator;
    private float _simulatedWorldHours;

    public bool Authoritative { get; set; } = true;

    public bool SuccessionAccelerationEnabled { get; private set; }

    public float SuccessionAccelerationMultiplier { get; private set; } = Constants.DefaultSuccessionAccelerationMultiplier;

    public float SimulationStepHours { get; private set; } = Constants.DefaultSuccessionStepHours;

    public float SimulatedWorldHours => _simulatedWorldHours;

    /// <summary>尚待通过 <see cref="ConsumeDirtyTilePayload"/> 发往客户端的地块数（可能含重复格）。</summary>
    public int PendingTileSyncCount => _syncDirtyTiles.Count;

    public event Action<IReadOnlyList<Vector2I>>? TilesChanged;

    public void Initialize(
        WorldData world,
        DayNightCycle dayNightCycle,
        SeasonManager seasonManager,
        WeatherSystem weatherSystem,
        int seed)
    {
        _world = world;
        _dayNightCycle = dayNightCycle;
        _seasonManager = seasonManager;
        _weatherSystem = weatherSystem;
        _rng.Seed = (ulong)(Mathf.Abs(seed) + 41041);
        _simulationAccumulator = 0.0f;
        _simulatedWorldHours = dayNightCycle.TotalGameHours;
        _chunkQueue.Clear();
        _chunkLastUpdatedHours.Clear();
        _syncDirtyTiles.Clear();
        InitializeChunkTimestamps();
        _climateField.Initialize(world);
        SetSuccessionAcceleration(false, Constants.DefaultSuccessionAccelerationMultiplier);
        SetSimulationStepHours(Constants.DefaultSuccessionStepHours);
    }

    public override void _Process(double delta)
    {
        if (!Authoritative || _world is null || _dayNightCycle is null || _seasonManager is null || _weatherSystem is null)
        {
            return;
        }

        var accelerationMultiplier = SuccessionAccelerationEnabled
            ? SuccessionAccelerationMultiplier
            : 1.0f;
        _simulationAccumulator += (float)delta * _dayNightCycle.GameHoursPerRealSecond * accelerationMultiplier;
        var stepHours = GetSimulationStepHours();
        var steps = Mathf.Min(Constants.MaxSimulationStepsPerFrame, Mathf.FloorToInt(_simulationAccumulator / stepHours));
        if (steps <= 0)
        {
            return;
        }

        _simulationAccumulator -= steps * stepHours;
        ProcessSimulationSteps(steps, stepHours);
    }

    public void SetFocusPoints(IEnumerable<Vector2> focusPositions)
    {
        _focusPositions.Clear();
        _focusPositions.AddRange(focusPositions);
    }

    public void SetSuccessionAcceleration(bool enabled, float multiplier)
    {
        SuccessionAccelerationEnabled = enabled;
        SuccessionAccelerationMultiplier = Mathf.Clamp(
            multiplier,
            Constants.MinSuccessionAccelerationMultiplier,
            Constants.MaxSuccessionAccelerationMultiplier);
    }

    public void SetSimulationStepHours(float stepHours)
    {
        SimulationStepHours = Mathf.Clamp(
            stepHours,
            Constants.MinSuccessionStepHours,
            Constants.MaxSuccessionStepHours);
    }

    public IReadOnlyList<Vector2I> RunAccelerationBurst(int simulationHours)
    {
        if (!Authoritative || _world is null || _dayNightCycle is null || _seasonManager is null || _weatherSystem is null)
        {
            return Array.Empty<Vector2I>();
        }

        var stepHours = GetSimulationStepHours();
        var steps = Mathf.Clamp(Mathf.CeilToInt(simulationHours / stepHours), 1, Constants.MaxSimulationStepsPerFrame * 4);
        return ProcessSimulationSteps(steps, stepHours);
    }

    public IReadOnlyList<Vector2I> PaintBiome(IEnumerable<Vector2I> cells, BiomeType biome)
    {
        if (_world is null)
        {
            return Array.Empty<Vector2I>();
        }

        var changedCells = new List<Vector2I>();
        var seenCells = new HashSet<Vector2I>();

        foreach (var cell in cells)
        {
            if (!_world.Contains(cell) || !seenCells.Add(cell))
            {
                continue;
            }

            ref var tile = ref _world.GetTileRef(cell);
            var before = tile;
            _ = BiomePainter.ApplyBiome(ref tile, biome);
            ApplyFarmMetadataAfterBiomePaint(ref tile, biome);
            if (!ShouldMarkDirty(before, tile))
            {
                continue;
            }

            changedCells.Add(cell);
            _syncDirtyTiles.Add(cell);
        }

        if (changedCells.Count > 0)
        {
            _climateField.RefreshAnchorsFromTiles(_world, changedCells, syncStateFromTiles: true);
            TilesChanged?.Invoke(changedCells);
        }

        return changedCells;
    }

    public FarmBrushApplyResult ApplyFarmBrush(IEnumerable<Vector2I> cells, FarmBrushTool tool, float gameHours)
    {
        if (_world is null)
        {
            return new FarmBrushApplyResult(Array.Empty<Vector2I>(), 0.0f, FarmBrushUserFeedback.Unknown);
        }

        var changedCells = new List<Vector2I>();
        var seenCells = new HashSet<Vector2I>();
        var harvestCollected = 0.0f;
        var totalInBrush = 0;
        var farmlandMaint = 0;
        var waterAtCap = 0;
        var fertBothAtCap = 0;
        var weedMinimal = 0;
        var seedDuplicate = 0;
        var harvestImmature = 0;
        foreach (var cell in cells)
        {
            if (!_world.Contains(cell) || !seenCells.Add(cell))
            {
                continue;
            }

            totalInBrush++;
            ref var tile = ref _world.GetTileRef(cell);
            var before = tile;

            switch (tool)
            {
                case FarmBrushTool.PlowFarmland:
                    _ = BiomePainter.ApplyBiome(ref tile, BiomeType.Farmland);
                    ApplyFarmMetadataAfterBiomePaint(ref tile, BiomeType.Farmland);
                    break;
                case FarmBrushTool.PlaceWater:
                    _ = BiomePainter.ApplyBiome(ref tile, BiomeType.ShallowWater);
                    ApplyFarmMetadataAfterBiomePaint(ref tile, BiomeType.ShallowWater);
                    break;
                case FarmBrushTool.WaterCrops:
                    if (tile.Biome != BiomeType.Farmland)
                    {
                        break;
                    }

                    farmlandMaint++;
                    if (tile.SoilMoisture >= 0.998f)
                    {
                        waterAtCap++;
                        break;
                    }

                    tile.SoilMoisture = Mathf.Clamp(tile.SoilMoisture + 0.16f, 0.0f, 1.0f);
                    tile.LastFarmCareGameHours = gameHours;
                    break;
                case FarmBrushTool.Fertilize:
                    if (tile.Biome != BiomeType.Farmland)
                    {
                        break;
                    }

                    farmlandMaint++;
                    var nutFull = tile.Nutrients >= 0.999f;
                    var orgFull = tile.OrganicMatter >= 0.999f;
                    if (nutFull && orgFull)
                    {
                        fertBothAtCap++;
                        break;
                    }

                    if (!nutFull)
                    {
                        tile.Nutrients = Mathf.Clamp(tile.Nutrients + 0.12f, 0.0f, 1.0f);
                    }

                    if (!orgFull)
                    {
                        tile.OrganicMatter = Mathf.Clamp(tile.OrganicMatter + 0.08f, 0.0f, 1.0f);
                    }

                    tile.LastFarmCareGameHours = gameHours;
                    break;
                case FarmBrushTool.Weed:
                    if (tile.Biome != BiomeType.Farmland)
                    {
                        break;
                    }

                    farmlandMaint++;
                    if (tile.WeedPressure <= 0.002f)
                    {
                        weedMinimal++;
                        break;
                    }

                    tile.WeedPressure = Mathf.Clamp(tile.WeedPressure - 0.22f, 0.0f, 1.0f);
                    tile.LastFarmCareGameHours = gameHours;
                    if (tile.WeedPressure < 0.35f)
                    {
                        tile.Flora = FloraType.Wildflower;
                    }

                    break;
                case FarmBrushTool.PlantSeed:
                    if (tile.Biome != BiomeType.Farmland)
                    {
                        break;
                    }

                    farmlandMaint++;
                    if (tile.CropGrowth >= 0.108f)
                    {
                        seedDuplicate++;
                        break;
                    }

                    tile.CropGrowth = Mathf.Max(tile.CropGrowth, 0.11f);
                    tile.LastFarmCareGameHours = gameHours;
                    break;
                case FarmBrushTool.Harvest:
                    if (tile.Biome != BiomeType.Farmland)
                    {
                        break;
                    }

                    farmlandMaint++;
                    if (tile.CropGrowth < FarmSimulation.MinHarvestCropGrowth)
                    {
                        harvestImmature++;
                        break;
                    }

                    harvestCollected += FarmSimulation.ComputeHarvestYield(tile);
                    tile.CropGrowth = 0.06f;
                    tile.WeedPressure = Mathf.Min(tile.WeedPressure, 0.38f);
                    tile.LastFarmCareGameHours = gameHours;
                    tile.Nutrients = Mathf.Clamp(tile.Nutrients - 0.06f, 0.0f, 1.0f);
                    break;
            }

            if (!ShouldMarkDirtyForFarmBrush(before, tile))
            {
                continue;
            }

            changedCells.Add(cell);
            _syncDirtyTiles.Add(cell);
        }

        if (changedCells.Count > 0)
        {
            _climateField.RefreshAnchorsFromTiles(_world, changedCells, syncStateFromTiles: true);
            TilesChanged?.Invoke(changedCells);
            return new FarmBrushApplyResult(changedCells, harvestCollected, FarmBrushUserFeedback.Ok);
        }

        var feedback = ResolveFarmBrushFeedback(
            tool,
            totalInBrush,
            farmlandMaint,
            waterAtCap,
            fertBothAtCap,
            weedMinimal,
            seedDuplicate,
            harvestImmature);
        return new FarmBrushApplyResult(changedCells, harvestCollected, feedback);
    }

    private static FarmBrushUserFeedback ResolveFarmBrushFeedback(
        FarmBrushTool tool,
        int totalInBrush,
        int farmlandMaint,
        int waterAtCap,
        int fertBothAtCap,
        int weedMinimal,
        int seedDuplicate,
        int harvestImmature)
    {
        if (totalInBrush <= 0)
        {
            return FarmBrushUserFeedback.Unknown;
        }

        return tool switch
        {
            FarmBrushTool.PlowFarmland or FarmBrushTool.PlaceWater => FarmBrushUserFeedback.TerrainAlreadyMatches,
            FarmBrushTool.WaterCrops when farmlandMaint <= 0 => FarmBrushUserFeedback.NoFarmlandInSelection,
            FarmBrushTool.WaterCrops when waterAtCap >= farmlandMaint && farmlandMaint > 0 => FarmBrushUserFeedback.SoilMoistureAtMaximum,
            FarmBrushTool.Fertilize when farmlandMaint <= 0 => FarmBrushUserFeedback.NoFarmlandInSelection,
            FarmBrushTool.Fertilize when fertBothAtCap >= farmlandMaint && farmlandMaint > 0 => FarmBrushUserFeedback.NutrientsAndOrganicAtMaximum,
            FarmBrushTool.Weed when farmlandMaint <= 0 => FarmBrushUserFeedback.NoFarmlandInSelection,
            FarmBrushTool.Weed when weedMinimal >= farmlandMaint && farmlandMaint > 0 => FarmBrushUserFeedback.WeedsAlreadyMinimal,
            FarmBrushTool.PlantSeed when farmlandMaint <= 0 => FarmBrushUserFeedback.NoFarmlandInSelection,
            FarmBrushTool.PlantSeed when seedDuplicate >= farmlandMaint && farmlandMaint > 0 => FarmBrushUserFeedback.CropAlreadySeeded,
            FarmBrushTool.Harvest when farmlandMaint <= 0 => FarmBrushUserFeedback.NoFarmlandInSelection,
            FarmBrushTool.Harvest when harvestImmature >= farmlandMaint && farmlandMaint > 0 => FarmBrushUserFeedback.NotReadyToHarvest,
            _ => FarmBrushUserFeedback.Unknown
        };
    }

    /// <summary>耕地刷子使用更灵敏的阈值，避免湿度+0.01 等被 <see cref="ShouldMarkDirty"/> 吃掉。</summary>
    private static bool ShouldMarkDirtyForFarmBrush(WorldTileData before, WorldTileData after)
    {
        if (ShouldMarkDirty(before, after))
        {
            return true;
        }

        return Mathf.Abs(before.SoilMoisture - after.SoilMoisture) > 0.0008f ||
               Mathf.Abs(before.Nutrients - after.Nutrients) > 0.0008f ||
               Mathf.Abs(before.OrganicMatter - after.OrganicMatter) > 0.0008f ||
               Mathf.Abs(before.WeedPressure - after.WeedPressure) > 0.0008f ||
               Mathf.Abs(before.CropGrowth - after.CropGrowth) > 0.0008f ||
               Mathf.Abs(before.LastFarmCareGameHours - after.LastFarmCareGameHours) > 0.0001f;
    }

    private void ApplyFarmMetadataAfterBiomePaint(ref WorldTileData tile, BiomeType biome)
    {
        if (biome == BiomeType.Farmland)
        {
            tile.CropGrowth = 0.0f;
            tile.WeedPressure = 0.06f;
            tile.LastFarmCareGameHours = _simulatedWorldHours;
            return;
        }

        tile.CropGrowth = 0.0f;
        tile.WeedPressure = 0.0f;
        tile.LastFarmCareGameHours = 0.0f;
    }

    public IReadOnlyList<Vector2I> PaintClimateControl(
        IEnumerable<Vector2I> cells,
        ClimateBrushField field,
        float targetValue,
        float blendStrength)
    {
        if (_world is null)
        {
            return Array.Empty<Vector2I>();
        }

        var clampedTarget = Mathf.Clamp(targetValue, 0.0f, 1.0f);
        var clampedBlend = Mathf.Clamp(blendStrength, 0.01f, 1.0f);
        var changedCells = new List<Vector2I>();
        var seenCells = new HashSet<Vector2I>();

        foreach (var cell in cells)
        {
            if (!_world.Contains(cell) || !seenCells.Add(cell))
            {
                continue;
            }

            ref var tile = ref _world.GetTileRef(cell);
            var beforeValue = field == ClimateBrushField.Rainfall
                ? tile.Rainfall
                : tile.Sunlight;
            var afterValue = Mathf.Lerp(beforeValue, clampedTarget, clampedBlend);
            if (Mathf.Abs(afterValue - beforeValue) < 0.002f)
            {
                continue;
            }

            if (field == ClimateBrushField.Rainfall)
            {
                tile.Rainfall = afterValue;
            }
            else
            {
                tile.Sunlight = afterValue;
            }

            changedCells.Add(cell);
            _syncDirtyTiles.Add(cell);
        }

        if (changedCells.Count > 0)
        {
            _climateField.RefreshAnchorsFromTiles(_world, changedCells, syncStateFromTiles: false);
            TilesChanged?.Invoke(changedCells);
        }

        return changedCells;
    }

    public Godot.Collections.Array<Godot.Collections.Dictionary> ConsumeDirtyTilePayload(int maxTiles)
    {
        var payload = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        if (_world is null || _syncDirtyTiles.Count == 0)
        {
            return payload;
        }

        var seen = new HashSet<Vector2I>();
        var remaining = new List<Vector2I>();

        foreach (var cell in _syncDirtyTiles)
        {
            if (!seen.Add(cell))
            {
                continue;
            }

            if (payload.Count >= maxTiles)
            {
                remaining.Add(cell);
                continue;
            }

            if (!_world.Contains(cell))
            {
                continue;
            }

            var tile = _world.GetTile(cell);
            payload.Add(new Godot.Collections.Dictionary
            {
                ["x"] = cell.X,
                ["y"] = cell.Y,
                ["biome"] = (int)tile.Biome,
                ["elevation"] = tile.Elevation,
                ["ambient_moisture"] = tile.Moisture,
                ["temperature"] = tile.Temperature,
                ["rainfall"] = tile.Rainfall,
                ["sunlight"] = tile.Sunlight,
                ["flora"] = (int)tile.Flora,
                ["succession"] = (int)tile.Succession,
                ["growth"] = tile.FloraGrowth,
                ["moisture"] = tile.SoilMoisture,
                ["nutrients"] = tile.Nutrients,
                ["organic"] = tile.OrganicMatter,
                ["snow"] = tile.SnowCover,
                ["disturbance"] = tile.Disturbance,
                ["crop_growth"] = tile.CropGrowth,
                ["weed_pressure"] = tile.WeedPressure,
                ["farm_care_hours"] = tile.LastFarmCareGameHours
            });
        }

        _syncDirtyTiles.Clear();
        _syncDirtyTiles.AddRange(remaining);
        return payload;
    }

    private void EnsureChunkQueue()
    {
        if (_world is null || _chunkQueue.Count > 0)
        {
            return;
        }

        _focusChunkDedup.Clear();
        if (_focusPositions.Count > 0)
        {
            foreach (var position in _focusPositions)
            {
                var cell = new Vector2I(
                    Mathf.Clamp(Mathf.FloorToInt(position.X / Constants.TileSize), 0, _world.Width - 1),
                    Mathf.Clamp(Mathf.FloorToInt(position.Y / Constants.TileSize), 0, _world.Height - 1));
                _focusChunkDedup.Add(new Vector2I(cell.X / Constants.ChunkSize, cell.Y / Constants.ChunkSize));
            }
        }
        else
        {
            var centerCell = new Vector2I(_world.Width / 2, _world.Height / 2);
            _focusChunkDedup.Add(new Vector2I(centerCell.X / Constants.ChunkSize, centerCell.Y / Constants.ChunkSize));
        }

        var focusChunksArray = new Vector2I[_focusChunkDedup.Count];
        var fi = 0;
        foreach (var c in _focusChunkDedup)
        {
            focusChunksArray[fi++] = c;
        }

        var chunkCountX = Mathf.CeilToInt((float)_world.Width / Constants.ChunkSize);
        var chunkCountY = Mathf.CeilToInt((float)_world.Height / Constants.ChunkSize);

        _chunkQueueSortBuffer.Clear();
        for (var y = 0; y < chunkCountY; y++)
        {
            for (var x = 0; x < chunkCountX; x++)
            {
                var chunk = new Vector2I(x, y);
                var minDistSq = int.MaxValue;
                for (var i = 0; i < focusChunksArray.Length; i++)
                {
                    var d = chunk.DistanceSquaredTo(focusChunksArray[i]);
                    if (d < minDistSq)
                    {
                        minDistSq = d;
                    }
                }

                _chunkQueueSortBuffer.Add((chunk, minDistSq));
            }
        }

        _chunkQueueSortBuffer.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));
        foreach (var item in _chunkQueueSortBuffer)
        {
            _chunkQueue.Enqueue(item.Chunk);
        }
    }

    private IReadOnlyList<Vector2I> ProcessSimulationSteps(int steps, float stepHours)
    {
        var frameDirty = new HashSet<Vector2I>();
        var chunksPerStep = GetChunksPerSimulationStep();

        for (var i = 0; i < steps; i++)
        {
            _simulatedWorldHours += stepHours;
            if (_dayNightCycle is not null && _seasonManager is not null && _weatherSystem is not null)
            {
                _climateField.Step(_dayNightCycle, _seasonManager, _weatherSystem, stepHours);
            }

            EnsureChunkQueue();
            for (var j = 0; j < chunksPerStep; j++)
            {
                if (_chunkQueue.Count == 0)
                {
                    break;
                }

                var chunk = _chunkQueue.Dequeue();
                var chunkHours = GetChunkElapsedHours(chunk, stepHours);
                ProcessChunk(chunk, frameDirty, chunkHours);
            }
        }

        CatchUpFocusedChunks(frameDirty);

        Vector2I[] distinctTiles;
        if (frameDirty.Count == 0)
        {
            distinctTiles = Array.Empty<Vector2I>();
        }
        else
        {
            distinctTiles = new Vector2I[frameDirty.Count];
            var di = 0;
            foreach (var cell in frameDirty)
            {
                distinctTiles[di++] = cell;
            }
        }

        if (distinctTiles.Length > 0)
        {
            if (_world is not null)
            {
                _climateField.RefreshForcingFromTiles(_world, distinctTiles);
            }

            TilesChanged?.Invoke(distinctTiles);
        }

        return distinctTiles;
    }

    private int GetChunksPerSimulationStep()
    {
        if (_world is null)
        {
            return 4;
        }

        var chunkCountX = Mathf.CeilToInt((float)_world.Width / Constants.ChunkSize);
        var chunkCountY = Mathf.CeilToInt((float)_world.Height / Constants.ChunkSize);
        var totalChunks = chunkCountX * chunkCountY;
        var targetSweepHours = Constants.EcologyTargetSweepHours;
        var desiredChunks = Mathf.CeilToInt(totalChunks * GetSimulationStepHours() / targetSweepHours);
        return Mathf.Clamp(desiredChunks, 4, 48);
    }

    private float GetSimulationStepHours()
    {
        return SimulationStepHours;
    }

    private float GetChunkElapsedHours(Vector2I chunk, float minimumStepHours)
    {
        var lastUpdatedHours = _chunkLastUpdatedHours.TryGetValue(chunk, out var lastHours)
            ? lastHours
            : _simulatedWorldHours;
        var elapsedHours = Mathf.Max(minimumStepHours, _simulatedWorldHours - lastUpdatedHours);
        _chunkLastUpdatedHours[chunk] = _simulatedWorldHours;
        return elapsedHours;
    }

    private void InitializeChunkTimestamps()
    {
        if (_world is null)
        {
            return;
        }

        var chunkCountX = Mathf.CeilToInt((float)_world.Width / Constants.ChunkSize);
        var chunkCountY = Mathf.CeilToInt((float)_world.Height / Constants.ChunkSize);
        for (var y = 0; y < chunkCountY; y++)
        {
            for (var x = 0; x < chunkCountX; x++)
            {
                _chunkLastUpdatedHours[new Vector2I(x, y)] = _simulatedWorldHours;
            }
        }
    }

    private void ProcessChunk(Vector2I chunk, HashSet<Vector2I> frameDirty, float stepHours)
    {
        if (_world is null || _weatherSystem is null || _dayNightCycle is null || _seasonManager is null)
        {
            return;
        }

        var context = new EcologyUpdateContext(
            _dayNightCycle.GetDaylightFactor(),
            _seasonManager.GetGrowthModifier(),
            _weatherSystem.GetRainContributionPerHour(),
            _weatherSystem.GetEvaporationModifier(),
            _weatherSystem.GetStormDamageChance());

        var startX = chunk.X * Constants.ChunkSize;
        var startY = chunk.Y * Constants.ChunkSize;
        var endX = Mathf.Min(startX + Constants.ChunkSize, _world.Width);
        var endY = Mathf.Min(startY + Constants.ChunkSize, _world.Height);

        for (var y = startY; y < endY; y++)
        {
            for (var x = startX; x < endX; x++)
            {
                var cell = new Vector2I(x, y);
                ref var tile = ref _world.GetTileRef(cell);
                var before = tile;
                var tileCoordinates = new Vector2(cell.X, cell.Y);

                var floraNeighbors = _world.CountNeighboringFlora(cell);
                var waterNeighbors = _world.CountWaterNeighbors(cell);
                tile.Temperature = _climateField.SampleTemperature(tileCoordinates);
                tile.Moisture = _climateField.SampleMoisture(tileCoordinates);
                var effectiveTemperature = _weatherSystem.GetEffectiveTemperature(tile.Temperature);
                var climateTemperature = Mathf.Lerp(tile.Temperature, effectiveTemperature, 0.35f);

                SuccessionEngine.UpdateMorphology(
                    ref tile,
                    waterNeighbors,
                    stepHours);

                FloraManager.UpdateTile(ref tile, floraNeighbors, waterNeighbors, context, effectiveTemperature, stepHours, _rng);
                FarmSimulation.SimulateFarmlandTick(
                    ref tile,
                    stepHours,
                    context,
                    effectiveTemperature,
                    _simulatedWorldHours,
                    _rng);
                SuccessionEngine.RecoverDisturbance(ref tile, climateTemperature, stepHours, _rng);
                SuccessionEngine.UpdateStage(ref tile, climateTemperature);
                SuccessionEngine.UpdateBiome(ref tile, climateTemperature, waterNeighbors, _simulatedWorldHours);

                if (_weatherSystem.CurrentState == WeatherState.Snow && tile.Biome is not BiomeType.DeepWater and not BiomeType.ShallowWater)
                {
                    var snowfallRate = 0.10f * Mathf.Lerp(0.4f, 1.0f, _weatherSystem.CurrentPrecipitationIntensity);
                    tile.SnowCover = Mathf.Clamp(tile.SnowCover + (snowfallRate * stepHours), 0.0f, 1.0f);
                }
                else
                {
                    var meltRate = Mathf.Lerp(0.01f, 0.12f, effectiveTemperature) * Mathf.Lerp(0.25f, 1.0f, _dayNightCycle.GetDaylightFactor());
                    tile.SnowCover = Mathf.Clamp(tile.SnowCover - (meltRate * stepHours), 0.0f, 1.0f);
                }

                if (ShouldMarkDirty(before, tile))
                {
                    frameDirty.Add(cell);
                    _syncDirtyTiles.Add(cell);
                }
            }
        }
    }

    private static bool ShouldMarkDirty(WorldTileData before, WorldTileData after)
    {
        return before.Biome != after.Biome ||
               Mathf.Abs(before.Elevation - after.Elevation) >= 0.002f ||
               Mathf.Abs(before.Moisture - after.Moisture) >= 0.01f ||
               Mathf.Abs(before.Temperature - after.Temperature) >= 0.01f ||
               Mathf.Abs(before.Rainfall - after.Rainfall) >= 0.01f ||
               Mathf.Abs(before.Sunlight - after.Sunlight) >= 0.01f ||
               before.Flora != after.Flora ||
               before.Succession != after.Succession ||
               Mathf.Abs(before.FloraGrowth - after.FloraGrowth) >= 0.015f ||
               Mathf.Abs(before.SoilMoisture - after.SoilMoisture) >= 0.02f ||
               Mathf.Abs(before.Nutrients - after.Nutrients) >= 0.015f ||
               Mathf.Abs(before.OrganicMatter - after.OrganicMatter) >= 0.015f ||
               Mathf.Abs(before.SnowCover - after.SnowCover) >= 0.03f ||
               Mathf.Abs(before.CropGrowth - after.CropGrowth) >= 0.008f ||
               Mathf.Abs(before.WeedPressure - after.WeedPressure) >= 0.008f ||
               Mathf.Abs(before.LastFarmCareGameHours - after.LastFarmCareGameHours) >= 0.05f;
    }

    public void RefreshClimateFieldSamples(IEnumerable<Vector2I> changedCells)
    {
        if (_world is null)
        {
            return;
        }

        _climateField.SyncStateFromTiles(_world, changedCells);
    }

    public void RefreshClimateFieldAnchors(IEnumerable<Vector2I> changedCells, bool syncStateFromTiles)
    {
        if (_world is null)
        {
            return;
        }

        _climateField.RefreshAnchorsFromTiles(_world, changedCells, syncStateFromTiles);
    }

    public float SampleTemperatureFieldAt(Vector2 tileCoordinates) => _climateField.SampleTemperature(tileCoordinates);

    public float SampleMoistureFieldAt(Vector2 tileCoordinates) => _climateField.SampleMoisture(tileCoordinates);

    public IReadOnlyList<Vector2I> SyncChunksNearFocusPoints(IEnumerable<Vector2> focusPositions)
    {
        if (!Authoritative || _world is null)
        {
            return Array.Empty<Vector2I>();
        }

        var frameDirty = new HashSet<Vector2I>();
        CatchUpChunksAroundPositions(frameDirty, focusPositions);
        Vector2I[] distinctTiles;
        if (frameDirty.Count == 0)
        {
            distinctTiles = Array.Empty<Vector2I>();
        }
        else
        {
            distinctTiles = new Vector2I[frameDirty.Count];
            var di = 0;
            foreach (var cell in frameDirty)
            {
                distinctTiles[di++] = cell;
            }
        }

        if (distinctTiles.Length > 0)
        {
            _climateField.RefreshForcingFromTiles(_world, distinctTiles);
            TilesChanged?.Invoke(distinctTiles);
        }

        return distinctTiles;
    }

    private void CatchUpChunksAroundPositions(HashSet<Vector2I> frameDirty, IEnumerable<Vector2> focusPositions)
    {
        if (_world is null)
        {
            return;
        }

        var positions = focusPositions.ToArray();
        if (positions.Length == 0)
        {
            return;
        }

        var focusChunks = new HashSet<Vector2I>();
        var maxChunkX = Mathf.CeilToInt((float)_world.Width / Constants.ChunkSize) - 1;
        var maxChunkY = Mathf.CeilToInt((float)_world.Height / Constants.ChunkSize) - 1;

        foreach (var focusPosition in positions)
        {
            var focusCell = new Vector2I(
                Mathf.Clamp(Mathf.FloorToInt(focusPosition.X / Constants.TileSize), 0, _world.Width - 1),
                Mathf.Clamp(Mathf.FloorToInt(focusPosition.Y / Constants.TileSize), 0, _world.Height - 1));
            var centerChunk = new Vector2I(focusCell.X / Constants.ChunkSize, focusCell.Y / Constants.ChunkSize);
            for (var y = centerChunk.Y - Constants.RenderRadiusInChunks; y <= centerChunk.Y + Constants.RenderRadiusInChunks; y++)
            {
                for (var x = centerChunk.X - Constants.RenderRadiusInChunks; x <= centerChunk.X + Constants.RenderRadiusInChunks; x++)
                {
                    if (x < 0 || y < 0 || x > maxChunkX || y > maxChunkY)
                    {
                        continue;
                    }

                    focusChunks.Add(new Vector2I(x, y));
                }
            }
        }

        foreach (var chunk in focusChunks)
        {
            var elapsedHours = GetChunkElapsedHours(chunk, 0.0f);
            if (elapsedHours <= 0.0001f)
            {
                continue;
            }

            ProcessChunk(chunk, frameDirty, elapsedHours);
        }
    }

    private void CatchUpFocusedChunks(HashSet<Vector2I> frameDirty)
    {
        CatchUpChunksAroundPositions(frameDirty, _focusPositions);
    }
}
