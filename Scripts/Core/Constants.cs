using Godot;

namespace CWM.Scripts.Core;

public static class Constants
{
    public const int TileSize = 16;
    public const int ChunkSize = 32;
    public const int DefaultMapSize = 1024;
    public const int DefaultPort = 7777;
    public const float DefaultDayLengthSeconds = 600.0f;
    public const float DefaultYearLengthDays = 10.0f;
    public const int TerrainSourceId = 0;
    public const int RenderRadiusInChunks = 6;
    public const float LocalPlayerSyncInterval = 0.08f;
    /// <summary>主机地块同步泵 Timer 间隔（秒），与游戏 <see cref="Node._Process"/> 解耦，避免被单帧逻辑拖慢。</summary>
    public const float HostTileSyncPumpInterval = 0.05f;
    /// <summary>单条可靠 RPC 内包含的最大格数（演替会产生大量脏格；单包过大易导致 ENet 拥塞/断连，宜偏小）。</summary>
    public const int HostTileSyncBatchSize = 120;
    /// <summary>单次泵循环内最多发送几批；超出部分 <see cref="Callable.CallDeferred"/> 分帧继续。</summary>
    public const int HostTileSyncMaxBatchesPerSlice = 16;
    /// <summary>待同步格数超过此值时，在 <see cref="Node._Process"/> 中额外抢跑 drain（演替爆发等）。</summary>
    public const int HostTileSyncUrgentPendingTiles = 400;
    public const float HostFaunaSyncInterval = 1.0f;
    /// <summary>主机向客户端同步昼夜时间与天气状态的间隔（秒），由专用 Timer 驱动而非 <see cref="Node._Process"/>。</summary>
    public const float HostTimeWeatherSyncInterval = 0.25f;
    public const int MaxAnimals = 60;
    public const int DefaultBrushRadius = 2;
    public const int MinBrushRadius = 1;
    public const int MaxBrushRadius = 10;
    public const int ClimateEditorTextureSize = 384;
    public const float DefaultClimateBrushTargetValue = 0.65f;
    public const float DefaultClimateBrushStrength = 0.35f;
    public const float EcologyTargetSweepHours = 6.0f;
    public const float DefaultSuccessionAccelerationMultiplier = 24.0f;
    public const float MinSuccessionAccelerationMultiplier = 1.0f;
    public const float MaxSuccessionAccelerationMultiplier = 192.0f;
    public const float DefaultSuccessionStepHours = 0.25f;
    public const float MinSuccessionStepHours = 0.0625f;
    public const float MaxSuccessionStepHours = 2.0f;
    public const float SuccessionStepHoursIncrement = 0.0625f;
    public const int ClimateFieldCellSize = 16;
    public const int WorldSnapshotChunkSizeBytes = 48 * 1024;
    public const int WorldSnapshotChunksPerFrame = 8;
    public const int MaxSimulationStepsPerFrame = 48;

    public static readonly Vector2I InvalidAtlasCoords = new(-1, -1);
}
