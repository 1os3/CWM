using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace CWM.Scripts.World;

public enum BiomeType
{
    DeepWater,
    ShallowWater,
    Beach,
    Grassland,
    Forest,
    ConiferForest,
    Swamp,
    Desert,
    Snow,
    RockyHighlands,
    AlpineMeadow,
    BareRock,
    Farmland
}

public enum SuccessionStage
{
    Bare,
    Pioneer,
    Intermediate,
    Climax
}

public enum FloraType
{
    None,
    Moss,
    Grass,
    Shrub,
    BroadleafTree,
    ConiferTree,
    Reed,
    Cactus,
    Wildflower,
    AlpineBloom
}

public enum ClimateBrushField
{
    Rainfall,
    Sunlight
}

/// <summary>耕种模式下的刷子工具（与地形预设刷子分离）。</summary>
public enum FarmBrushTool
{
    /// <summary>开垦为耕地并翻土。</summary>
    PlowFarmland,
    /// <summary>放置浅水源（灌溉渠/小水池）。</summary>
    PlaceWater,
    /// <summary>浇水提高土壤湿度。</summary>
    WaterCrops,
    /// <summary>施肥。</summary>
    Fertilize,
    /// <summary>除草降低杂草压力。</summary>
    Weed,
    /// <summary>播种。</summary>
    PlantSeed,
    /// <summary>收获成熟作物并入库。</summary>
    Harvest
}

/// <summary>当本次耕种刷子没有产生可同步的格子变化时的原因（用于 HUD 提示）。</summary>
public enum FarmBrushUserFeedback
{
    Ok,
    /// <summary>范围内没有耕地（浇水/施肥/除草/播种/收获）。</summary>
    NoFarmlandInSelection,
    /// <summary>土壤湿度已满，无法继续浇水。</summary>
    SoilMoistureAtMaximum,
    /// <summary>养分与有机质均已满（施肥无效）。</summary>
    NutrientsAndOrganicAtMaximum,
    /// <summary>杂草压力已很低。</summary>
    WeedsAlreadyMinimal,
    /// <summary>已播种，无需再播。</summary>
    CropAlreadySeeded,
    /// <summary>作物未成熟，无法收获。</summary>
    NotReadyToHarvest,
    /// <summary>地形已是目标状态（翻土/水源）。</summary>
    TerrainAlreadyMatches,
    /// <summary>无法归类（空刷子等）。</summary>
    Unknown
}

/// <summary>耕种刷子执行结果。</summary>
/// <param name="ChangedCells">需要刷新渲染/同步的格。</param>
/// <param name="HarvestCollected">收获工具累计产量。</param>
/// <param name="FeedbackWhenUnchanged">当 <see cref="ChangedCells"/> 为空时的主要原因。</param>
public readonly record struct FarmBrushApplyResult(
    IReadOnlyList<Vector2I> ChangedCells,
    float HarvestCollected,
    FarmBrushUserFeedback FeedbackWhenUnchanged);

public struct TileData
{
    public BiomeType Biome;
    public float Elevation;
    public float Moisture;
    public float Temperature;
    public float Rainfall;
    public float Sunlight;
    public float SoilMoisture;
    public float Nutrients;
    public float OrganicMatter;
    public float FloraGrowth;
    public float SnowCover;
    public SuccessionStage Succession;
    public FloraType Flora;
    public int Disturbance;

    /// <summary>作物成熟度 0~1；耕地无作物时为 0。</summary>
    public float CropGrowth;

    /// <summary>杂草竞争 0~1，过高会抑制作物并加速撂荒演替。</summary>
    public float WeedPressure;

    /// <summary>上次浇水/施肥/除草/翻土时的世界总游戏小时（<see cref="CWM.Scripts.Weather.DayNightCycle.TotalGameHours"/>）。</summary>
    public float LastFarmCareGameHours;

    [JsonIgnore]
    public bool IsWater => Biome is BiomeType.DeepWater or BiomeType.ShallowWater;
}

public sealed class WorldData
{
    private const int LegacyBinaryTileStride = 39;
    private const int ClimateBinaryTileStride = 47;
    private const int CurrentBinaryTileStride = 59;
    private readonly TileData[] _tiles;

    public WorldData(int width, int height, int seed)
    {
        Width = width;
        Height = height;
        Seed = seed;
        _tiles = new TileData[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public int Seed { get; }

    public int TileCount => _tiles.Length;

    public bool Contains(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    public bool Contains(Vector2I cell) => Contains(cell.X, cell.Y);

    public int ToIndex(int x, int y) => y * Width + x;

    public int ToIndex(Vector2I cell) => ToIndex(cell.X, cell.Y);

    public ref TileData GetTileRef(int x, int y) => ref _tiles[ToIndex(x, y)];

    public ref TileData GetTileRef(Vector2I cell) => ref _tiles[ToIndex(cell)];

    public TileData GetTile(int x, int y) => _tiles[ToIndex(x, y)];

    public TileData GetTile(Vector2I cell) => _tiles[ToIndex(cell)];

    public void SetTile(int x, int y, TileData tile) => _tiles[ToIndex(x, y)] = tile;

    public void SetTile(Vector2I cell, TileData tile) => _tiles[ToIndex(cell)] = tile;

    public Rect2I ClampRect(Rect2I rect)
    {
        var position = new Vector2I(
            Mathf.Clamp(rect.Position.X, 0, Width),
            Mathf.Clamp(rect.Position.Y, 0, Height));
        var end = new Vector2I(
            Mathf.Clamp(rect.End.X, 0, Width),
            Mathf.Clamp(rect.End.Y, 0, Height));
        return new Rect2I(position, end - position);
    }

    public int CountNeighboringFlora(Vector2I cell)
    {
        var count = 0;
        foreach (var neighbor in GetNeighbors8(cell))
        {
            if (GetTile(neighbor).Flora != FloraType.None)
            {
                count++;
            }
        }

        return count;
    }

    public int CountWaterNeighbors(Vector2I cell)
    {
        var count = 0;
        foreach (var neighbor in GetNeighbors4(cell))
        {
            if (GetTile(neighbor).IsWater)
            {
                count++;
            }
        }

        return count;
    }

    public IEnumerable<Vector2I> GetNeighbors4(Vector2I cell)
    {
        var candidates = new[]
        {
            new Vector2I(cell.X + 1, cell.Y),
            new Vector2I(cell.X - 1, cell.Y),
            new Vector2I(cell.X, cell.Y + 1),
            new Vector2I(cell.X, cell.Y - 1)
        };

        foreach (var candidate in candidates)
        {
            if (Contains(candidate))
            {
                yield return candidate;
            }
        }
    }

    public IEnumerable<Vector2I> GetNeighbors8(Vector2I cell)
    {
        for (var y = cell.Y - 1; y <= cell.Y + 1; y++)
        {
            for (var x = cell.X - 1; x <= cell.X + 1; x++)
            {
                if (x == cell.X && y == cell.Y)
                {
                    continue;
                }

                if (Contains(x, y))
                {
                    yield return new Vector2I(x, y);
                }
            }
        }
    }

    public IEnumerable<Vector2I> EnumerateRect(Rect2I rect)
    {
        var clamped = ClampRect(rect);
        for (var y = clamped.Position.Y; y < clamped.End.Y; y++)
        {
            for (var x = clamped.Position.X; x < clamped.End.X; x++)
            {
                yield return new Vector2I(x, y);
            }
        }
    }

    public string ToJson()
    {
        var export = new WorldExportData
        {
            Seed = Seed,
            Width = Width,
            Height = Height,
            Tiles = _tiles
        };

        return JsonSerializer.Serialize(export, JsonOptions);
    }

    public byte[] ToBinary()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Seed);
        writer.Write(Width);
        writer.Write(Height);

        foreach (var tile in _tiles)
        {
            writer.Write((byte)tile.Biome);
            writer.Write(tile.Elevation);
            writer.Write(tile.Moisture);
            writer.Write(tile.Temperature);
            writer.Write(tile.Rainfall);
            writer.Write(tile.Sunlight);
            writer.Write(tile.SoilMoisture);
            writer.Write(tile.Nutrients);
            writer.Write(tile.OrganicMatter);
            writer.Write(tile.FloraGrowth);
            writer.Write(tile.SnowCover);
            writer.Write((byte)tile.Succession);
            writer.Write((byte)tile.Flora);
            writer.Write(tile.Disturbance);
            writer.Write(tile.CropGrowth);
            writer.Write(tile.WeedPressure);
            writer.Write(tile.LastFarmCareGameHours);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public Error ExportToPath(string path, out string savedPath, out string message)
    {
        savedPath = string.Empty;
        message = string.Empty;

        try
        {
            var normalizedPath = NormalizeExportPath(path);
            var directory = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(directory));
            }

            var extension = Path.GetExtension(normalizedPath).ToLowerInvariant();
            switch (extension)
            {
                case ".json":
                    File.WriteAllText(normalizedPath, ToJson());
                    break;
                case ".bin":
                    File.WriteAllBytes(normalizedPath, ToBinary());
                    break;
                default:
                    message = $"Unsupported export format: {extension}";
                    return Error.FileUnrecognized;
            }

            savedPath = normalizedPath;
            return Error.Ok;
        }
        catch (Exception exception)
        {
            GD.PushError($"Failed to export world to {path}: {exception}");
            message = exception.Message;
            return Error.CantCreate;
        }
    }

    public static bool TryImportFromPath(string path, out WorldData? world, out string message)
    {
        world = null;
        message = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                message = "File does not exist.";
                return false;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            world = extension switch
            {
                ".json" => FromJson(File.ReadAllText(path)),
                ".bin" => FromBinary(File.ReadAllBytes(path)),
                _ => null
            };

            if (world is null)
            {
                message = $"Unsupported import format: {extension}";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            GD.PushError($"Failed to import world from {path}: {exception}");
            message = exception.Message;
            world = null;
            return false;
        }
    }

    public static bool TryImportFromBytes(byte[] bytes, out WorldData? world, out string message)
    {
        world = null;
        message = string.Empty;

        try
        {
            if (bytes.Length == 0)
            {
                message = "Snapshot is empty.";
                return false;
            }

            world = FromBinary(bytes);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError($"Failed to import world from snapshot bytes: {exception}");
            message = exception.Message;
            world = null;
            return false;
        }
    }

    public string GetSuggestedFileName() => $"world_{Seed}_{Width}x{Height}.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        IncludeFields = true
    };

    private static string NormalizeExportPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Export path must not be empty.", nameof(path));
        }

        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension)
            ? $"{path}.json"
            : path;
    }

    private static WorldData FromJson(string json)
    {
        var export = JsonSerializer.Deserialize<WorldExportData>(json, JsonOptions)
                     ?? throw new InvalidDataException("World JSON is empty or invalid.");
        return FromExportData(export);
    }

    private static WorldData FromBinary(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);

        var seed = reader.ReadInt32();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var world = new WorldData(width, height, seed);
        var remainingBytes = bytes.Length - sizeof(int) * 3;
        var expectedLegacyBytes = world._tiles.Length * LegacyBinaryTileStride;
        var expectedClimateBytes = world._tiles.Length * ClimateBinaryTileStride;
        var expectedFarmBytes = world._tiles.Length * CurrentBinaryTileStride;
        var format = remainingBytes switch
        {
            var size when size == expectedFarmBytes => 2,
            var size when size == expectedClimateBytes => 1,
            var size when size == expectedLegacyBytes => 0,
            _ => throw new InvalidDataException(
                $"Unexpected binary world size. Expected {expectedLegacyBytes + 12}, {expectedClimateBytes + 12} or {expectedFarmBytes + 12} bytes, got {bytes.Length}.")
        };

        var hasClimateControls = format >= 1;

        for (var i = 0; i < world._tiles.Length; i++)
        {
            var tile = new TileData
            {
                Biome = (BiomeType)reader.ReadByte(),
                Elevation = reader.ReadSingle(),
                Moisture = reader.ReadSingle(),
                Temperature = reader.ReadSingle(),
                Rainfall = hasClimateControls ? reader.ReadSingle() : 0.0f,
                Sunlight = hasClimateControls ? reader.ReadSingle() : 0.0f,
                SoilMoisture = reader.ReadSingle(),
                Nutrients = reader.ReadSingle(),
                OrganicMatter = reader.ReadSingle(),
                FloraGrowth = reader.ReadSingle(),
                SnowCover = reader.ReadSingle(),
                Succession = (SuccessionStage)reader.ReadByte(),
                Flora = (FloraType)reader.ReadByte(),
                Disturbance = reader.ReadInt32()
            };

            if (format == 2)
            {
                tile.CropGrowth = reader.ReadSingle();
                tile.WeedPressure = reader.ReadSingle();
                tile.LastFarmCareGameHours = reader.ReadSingle();
            }

            world._tiles[i] = tile;
        }

        EnsureClimateControlsInitialized(world);
        return world;
    }

    private static WorldData FromExportData(WorldExportData export)
    {
        if (export.Width <= 0 || export.Height <= 0)
        {
            throw new InvalidDataException("World dimensions must be positive.");
        }

        var world = new WorldData(export.Width, export.Height, export.Seed);
        if (export.Tiles.Length != world._tiles.Length)
        {
            throw new InvalidDataException($"Tile data length mismatch. Expected {world._tiles.Length}, got {export.Tiles.Length}.");
        }

        Array.Copy(export.Tiles, world._tiles, export.Tiles.Length);
        EnsureClimateControlsInitialized(world);
        return world;
    }

    private static void EnsureClimateControlsInitialized(WorldData world)
    {
        if (world._tiles.Any(tile => tile.Rainfall > 0.0001f || tile.Sunlight > 0.0001f))
        {
            return;
        }

        for (var i = 0; i < world._tiles.Length; i++)
        {
            ref var tile = ref world._tiles[i];
            tile.Rainfall = DeriveLegacyRainfall(tile);
            tile.Sunlight = DeriveLegacySunlight(tile);
        }
    }

    private static float DeriveLegacyRainfall(TileData tile)
    {
        var wetBiomeBonus = tile.Biome == BiomeType.Swamp ? 0.10f : 0.0f;
        var waterBonus = tile.IsWater ? 0.18f : 0.0f;
        var upliftPenalty = Mathf.Max(tile.Elevation - 0.72f, 0.0f) * 0.12f;
        return Mathf.Clamp((tile.Moisture * 0.82f) + wetBiomeBonus + waterBonus - upliftPenalty, 0.0f, 1.0f);
    }

    private static float DeriveLegacySunlight(TileData tile)
    {
        return Mathf.Clamp(
            0.34f +
            (tile.Temperature * 0.62f) -
            (tile.Moisture * 0.08f) -
            (tile.SnowCover * 0.12f),
            0.0f,
            1.0f);
    }

    private sealed class WorldExportData
    {
        public int Seed { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public TileData[] Tiles { get; init; } = Array.Empty<TileData>();
    }
}
