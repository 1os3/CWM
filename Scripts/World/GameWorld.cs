using System;
using System.Collections.Generic;
using Godot;
using CWM.Scripts.Core;

namespace CWM.Scripts.World;

/// <summary>小地图叠加层实体类型（与主地图着色规则独立）。</summary>
public enum MinimapEntityKind
{
	LocalPlayer,
	RemotePlayer,
	VillageNpc
}

public readonly struct MinimapOverlayEntity
{
	public MinimapOverlayEntity(Vector2 worldPosition, MinimapEntityKind kind)
	{
		WorldPosition = worldPosition;
		Kind = kind;
	}

	public Vector2 WorldPosition { get; init; }
	public MinimapEntityKind Kind { get; init; }
}

public enum MiniMapViewMode
{
	Biome,
	Elevation,
	Moisture,
	Temperature,
	Rainfall,
	Sunlight,
	SoilMoisture,
	Nutrients,
	OrganicMatter,
	FloraGrowth,
	Succession
}

public partial class GameWorld : Node2D
{
	private enum MiniMapScalarField
	{
		Elevation,
		Moisture,
		Temperature,
		Rainfall,
		Sunlight,
		SoilMoisture,
		Nutrients,
		OrganicMatter,
		FloraGrowth
	}

	private const int MiniMapTargetSize = 256;
	private WorldData? _world;
	private TerrainTileCatalog? _catalog;
	private readonly HashSet<Vector2I> _renderedChunks = [];
	private Vector2I _focusChunk = new(int.MinValue, int.MinValue);
	private bool _brushPreviewVisible;
	private Vector2I _brushPreviewCenter = Vector2I.Zero;
	private int _brushPreviewRadius = Constants.DefaultBrushRadius;
	private Color _brushPreviewColor = new(1.0f, 0.2f, 0.2f, 0.22f);
	private MiniMapViewMode _miniMapViewMode = MiniMapViewMode.Biome;
	private Func<Vector2, float>? _temperatureFieldSampler;
	private Func<Vector2, float>? _moistureFieldSampler;
	private bool _miniMapRebuildPending;

	/// <summary>在小地图底图上绘制玩家/NPC 点位；为空则仅显示地形。</summary>
	public Func<IReadOnlyList<MinimapOverlayEntity>>? MinimapOverlayProvider { get; set; }

	private TileMapLayer _waterLayer = null!;
	private TileMapLayer _terrainLayer = null!;
	private TileMapLayer _decorationLayer = null!;
	private TileMapLayer _entityLayer = null!;

	public Node2D PlayersRoot { get; private set; } = null!;

	public Node2D FaunaRoot { get; private set; } = null!;

	public Node2D NpcRoot { get; private set; } = null!;

	public GpuParticles2D RainParticles { get; private set; } = null!;

	public GpuParticles2D SnowParticles { get; private set; } = null!;

	public Texture2D? MiniMapTexture { get; private set; }

	public MiniMapViewMode CurrentMiniMapViewMode => _miniMapViewMode;

	public override void _Ready()
	{
		_waterLayer = GetNode<TileMapLayer>("WaterLayer");
		_terrainLayer = GetNode<TileMapLayer>("TerrainLayer");
		_decorationLayer = GetNode<TileMapLayer>("DecorationLayer");
		_entityLayer = GetNode<TileMapLayer>("EntityLayer");
		PlayersRoot = GetNode<Node2D>("PlayersRoot");
		FaunaRoot = GetNode<Node2D>("FaunaRoot");
		NpcRoot = GetNode<Node2D>("NpcRoot");
		RainParticles = GetNode<GpuParticles2D>("Weather/RainParticles");
		SnowParticles = GetNode<GpuParticles2D>("Weather/SnowParticles");

		TextureFilter = TextureFilterEnum.Nearest;
		_waterLayer.TextureFilter = TextureFilterEnum.Nearest;
		_terrainLayer.TextureFilter = TextureFilterEnum.Nearest;
		_decorationLayer.TextureFilter = TextureFilterEnum.Nearest;
	}

	public override void _Process(double delta)
	{
		if (_miniMapRebuildPending)
		{
			_miniMapRebuildPending = false;
			RebuildMiniMap();
		}
	}

	public override void _Draw()
	{
		if (!_brushPreviewVisible || _world is null)
		{
			return;
		}

		var borderColor = _brushPreviewColor.Lightened(0.35f);
		var radiusSquared = _brushPreviewRadius * _brushPreviewRadius;

		for (var y = _brushPreviewCenter.Y - _brushPreviewRadius; y <= _brushPreviewCenter.Y + _brushPreviewRadius; y++)
		{
			for (var x = _brushPreviewCenter.X - _brushPreviewRadius; x <= _brushPreviewCenter.X + _brushPreviewRadius; x++)
			{
				var cell = new Vector2I(x, y);
				if (!_world.Contains(cell))
				{
					continue;
				}

				var offset = cell - _brushPreviewCenter;
				if (offset.LengthSquared() > radiusSquared)
				{
					continue;
				}

				var rect = new Rect2(cell * Constants.TileSize, Vector2.One * Constants.TileSize);
				DrawRect(rect, _brushPreviewColor, true);
				DrawRect(rect, borderColor, false, 1.0f);
			}
		}
	}

	public void Initialize(WorldData world, int seed)
	{
		_world = world;
		_catalog = TerrainTilesetBuilder.Build(seed);
		_focusChunk = new Vector2I(int.MinValue, int.MinValue);
		_renderedChunks.Clear();

		foreach (var layer in new[] { _waterLayer, _terrainLayer, _decorationLayer, _entityLayer })
		{
			layer.TileSet = _catalog.TileSet;
			layer.Clear();
		}

		var waterShader = GD.Load<Shader>("res://Shaders/Water.gdshader");
		var foliageShader = GD.Load<Shader>("res://Shaders/FoliageSway.gdshader");
		_waterLayer.Material = new ShaderMaterial { Shader = waterShader };
		_decorationLayer.Material = new ShaderMaterial { Shader = foliageShader };

		ConfigureWeatherParticles();
		MiniMapTexture = ImageTexture.CreateFromImage(BuildMinimapImage());
	}

	public void SetClimateFieldSamplers(Func<Vector2, float>? temperatureFieldSampler, Func<Vector2, float>? moistureFieldSampler)
	{
		_temperatureFieldSampler = temperatureFieldSampler;
		_moistureFieldSampler = moistureFieldSampler;
	}

	public bool HasWorld => _world is not null;

	public WorldData? GetWorld() => _world;

	public TileData GetTile(Vector2I cell) => _world is null || !_world.Contains(cell)
		? default
		: _world.GetTile(cell);

	public Vector2I WorldToCell(Vector2 worldPosition)
	{
		return new Vector2I(
			Mathf.FloorToInt(worldPosition.X / Constants.TileSize),
			Mathf.FloorToInt(worldPosition.Y / Constants.TileSize));
	}

	public Vector2 CellToWorld(Vector2I cell) => (cell * Constants.TileSize) + new Vector2(Constants.TileSize / 2.0f, Constants.TileSize / 2.0f);

	public void UpdateFocus(Vector2 worldPosition)
	{
		if (_world is null)
		{
			return;
		}

		var cell = WorldToCell(worldPosition);
		var chunk = new Vector2I(
			Mathf.FloorToInt((float)cell.X / Constants.ChunkSize),
			Mathf.FloorToInt((float)cell.Y / Constants.ChunkSize));

		if (chunk == _focusChunk)
		{
			return;
		}

		_focusChunk = chunk;
		RenderVisibleChunks(chunk);
	}

	public void RefreshTile(Vector2I cell)
	{
		if (_world is null)
		{
			return;
		}

		var chunk = new Vector2I(
			Mathf.FloorToInt((float)cell.X / Constants.ChunkSize),
			Mathf.FloorToInt((float)cell.Y / Constants.ChunkSize));

		if (_renderedChunks.Contains(chunk))
		{
			RenderCell(cell);
		}
	}

	public void RefreshTiles(IEnumerable<Vector2I> cells)
	{
		var refreshedAny = false;
		foreach (var cell in cells)
		{
			RefreshTile(cell);
			refreshedAny = true;
		}

		if (refreshedAny)
		{
			_miniMapRebuildPending = true;
		}
	}

	public void RebuildMiniMap()
	{
		var image = BuildMinimapImage();
		if (MiniMapTexture is ImageTexture imageTexture)
		{
			imageTexture.Update(image);
			return;
		}

		MiniMapTexture = ImageTexture.CreateFromImage(image);
	}

	/// <summary>请求下一帧重建小地图（用于实体位置刷新等，与地形脏标记共用同一管线）。</summary>
	public void ScheduleMiniMapRebuild()
	{
		_miniMapRebuildPending = true;
	}

	public Image BuildClimateFieldImage(ClimateBrushField field, int targetSize)
	{
		var clampedTargetSize = Mathf.Max(targetSize, 16);
		return field switch
		{
			ClimateBrushField.Rainfall => BuildScalarFieldImage(
				clampedTargetSize,
				MiniMapScalarField.Rainfall,
				new Color("6d4b29"),
				new Color("5a9a57"),
				new Color("4a93d8")),
			ClimateBrushField.Sunlight => BuildScalarFieldImage(
				clampedTargetSize,
				MiniMapScalarField.Sunlight,
				new Color("3759b6"),
				new Color("e4d469"),
				new Color("f28b28")),
			_ => Image.CreateEmpty(2, 2, false, Image.Format.Rgba8)
		};
	}

	public void CycleMiniMapViewMode()
	{
		var modes = Enum.GetValues<MiniMapViewMode>();
		var currentIndex = Array.IndexOf(modes, _miniMapViewMode);
		var nextIndex = (currentIndex + 1) % modes.Length;
		_miniMapViewMode = modes[nextIndex];
		RebuildMiniMap();
	}

	public string GetMiniMapViewModeDisplayName() => GetMiniMapViewModeDisplayName(_miniMapViewMode);

	public void SetBrushPreview(Vector2I centerCell, int radius, Color color)
	{
		_brushPreviewVisible = true;
		_brushPreviewCenter = centerCell;
		_brushPreviewRadius = Mathf.Max(Constants.MinBrushRadius, radius);
		_brushPreviewColor = color;
		QueueRedraw();
	}

	public void ClearBrushPreview()
	{
		if (!_brushPreviewVisible)
		{
			return;
		}

		_brushPreviewVisible = false;
		QueueRedraw();
	}

	private void RenderVisibleChunks(Vector2I centerChunk)
	{
		if (_world is null)
		{
			return;
		}

		var target = new HashSet<Vector2I>();
		for (var y = centerChunk.Y - Constants.RenderRadiusInChunks; y <= centerChunk.Y + Constants.RenderRadiusInChunks; y++)
		{
			for (var x = centerChunk.X - Constants.RenderRadiusInChunks; x <= centerChunk.X + Constants.RenderRadiusInChunks; x++)
			{
				var chunk = new Vector2I(x, y);
				if (ChunkIntersectsWorld(chunk))
				{
					target.Add(chunk);
				}
			}
		}

		foreach (var chunk in target)
		{
			if (!_renderedChunks.Contains(chunk))
			{
				RenderChunk(chunk);
				_renderedChunks.Add(chunk);
			}
		}

		var staleChunks = _renderedChunks.Where(chunk => !target.Contains(chunk)).ToArray();
		foreach (var chunk in staleChunks)
		{
			ClearChunk(chunk);
			_renderedChunks.Remove(chunk);
		}
	}

	private bool ChunkIntersectsWorld(Vector2I chunk)
	{
		if (_world is null)
		{
			return false;
		}

		var startX = chunk.X * Constants.ChunkSize;
		var startY = chunk.Y * Constants.ChunkSize;
		return startX < _world.Width &&
			   startY < _world.Height &&
			   startX + Constants.ChunkSize >= 0 &&
			   startY + Constants.ChunkSize >= 0;
	}

	private void RenderChunk(Vector2I chunk)
	{
		if (_world is null)
		{
			return;
		}

		var startX = chunk.X * Constants.ChunkSize;
		var startY = chunk.Y * Constants.ChunkSize;
		var endX = Mathf.Min(startX + Constants.ChunkSize, _world.Width);
		var endY = Mathf.Min(startY + Constants.ChunkSize, _world.Height);

		for (var y = Mathf.Max(0, startY); y < endY; y++)
		{
			for (var x = Mathf.Max(0, startX); x < endX; x++)
			{
				RenderCell(new Vector2I(x, y));
			}
		}
	}

	private void ClearChunk(Vector2I chunk)
	{
		if (_world is null)
		{
			return;
		}

		var startX = Mathf.Max(0, chunk.X * Constants.ChunkSize);
		var startY = Mathf.Max(0, chunk.Y * Constants.ChunkSize);
		var endX = Mathf.Min(startX + Constants.ChunkSize, _world.Width);
		var endY = Mathf.Min(startY + Constants.ChunkSize, _world.Height);

		for (var y = startY; y < endY; y++)
		{
			for (var x = startX; x < endX; x++)
			{
				var cell = new Vector2I(x, y);
				_waterLayer.EraseCell(cell);
				_terrainLayer.EraseCell(cell);
				_decorationLayer.EraseCell(cell);
				_entityLayer.EraseCell(cell);
			}
		}
	}

	private void RenderCell(Vector2I cell)
	{
		if (_world is null || _catalog is null || !_world.Contains(cell))
		{
			return;
		}

		var tile = _world.GetTile(cell);
		var terrainCoords = _catalog.GetTerrain(tile.Biome);
		if (tile.IsWater)
		{
			_waterLayer.SetCell(cell, Constants.TerrainSourceId, terrainCoords);
			_terrainLayer.EraseCell(cell);
		}
		else
		{
			_terrainLayer.SetCell(cell, Constants.TerrainSourceId, terrainCoords);
			_waterLayer.EraseCell(cell);
		}

		var floraCoords = PickDecorationTile(cell, tile);
		if (floraCoords == Constants.InvalidAtlasCoords)
		{
			_decorationLayer.EraseCell(cell);
		}
		else
		{
			_decorationLayer.SetCell(cell, Constants.TerrainSourceId, floraCoords);
		}
	}

	private Vector2I PickDecorationTile(Vector2I cell, TileData tile)
	{
		if (_catalog is null || tile.Flora == FloraType.None || tile.FloraGrowth < 0.22f || tile.IsWater)
		{
			return Constants.InvalidAtlasCoords;
		}

		var densityHash = Mathf.Abs((cell.X * 73856093) ^ (cell.Y * 19349663));
		var placement = (densityHash % 100) / 100.0f;

		var threshold = tile.Succession switch
		{
			SuccessionStage.Climax => 0.18f,
			SuccessionStage.Intermediate => 0.10f,
			SuccessionStage.Pioneer => 0.05f,
			_ => 0.01f
		};

		if (placement > threshold + (tile.FloraGrowth * 0.4f))
		{
			return Constants.InvalidAtlasCoords;
		}

		return _catalog.GetFlora(tile.Flora);
	}

	private Image BuildMinimapImage()
	{
		if (_world is null)
		{
			return Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
		}

		var image = Image.CreateEmpty(MiniMapTargetSize, MiniMapTargetSize, false, Image.Format.Rgba8);
		var maxWorldX = Mathf.Max(_world.Width - 1, 0);
		var maxWorldY = Mathf.Max(_world.Height - 1, 0);

		for (var y = 0; y < MiniMapTargetSize; y++)
		{
			for (var x = 0; x < MiniMapTargetSize; x++)
			{
				var worldX = ((x + 0.5f) / MiniMapTargetSize) * maxWorldX;
				var worldY = ((y + 0.5f) / MiniMapTargetSize) * maxWorldY;
				image.SetPixel(x, y, GetMiniMapColor(worldX, worldY));
			}
		}

		DrawMinimapOverlay(image);
		return image;
	}

	private void DrawMinimapOverlay(Image image)
	{
		if (MinimapOverlayProvider is null || _world is null)
		{
			return;
		}

		foreach (var entity in MinimapOverlayProvider())
		{
			var pixel = WorldPixelToMinimapPixel(entity.WorldPosition);
			var color = entity.Kind switch
			{
				MinimapEntityKind.LocalPlayer => new Color(0.35f, 0.82f, 1f),
				MinimapEntityKind.RemotePlayer => new Color(1f, 0.92f, 0.22f),
				MinimapEntityKind.VillageNpc => new Color(0.22f, 0.92f, 0.38f),
				_ => Colors.White
			};
			DrawMinimapDot(image, pixel, color);
		}
	}

	private Vector2I WorldPixelToMinimapPixel(Vector2 worldPixel)
	{
		var maxWx = Mathf.Max(_world!.Width - 1, 1);
		var maxWy = Mathf.Max(_world.Height - 1, 1);
		var tileX = worldPixel.X / Constants.TileSize;
		var tileY = worldPixel.Y / Constants.TileSize;
		var mx = Mathf.RoundToInt(tileX / maxWx * MiniMapTargetSize);
		var my = Mathf.RoundToInt(tileY / maxWy * MiniMapTargetSize);
		mx = Mathf.Clamp(mx, 0, MiniMapTargetSize - 1);
		my = Mathf.Clamp(my, 0, MiniMapTargetSize - 1);
		return new Vector2I(mx, my);
	}

	private static void DrawMinimapDot(Image image, Vector2I center, Color color)
	{
		for (var dy = -1; dy <= 1; dy++)
		{
			for (var dx = -1; dx <= 1; dx++)
			{
				var x = center.X + dx;
				var y = center.Y + dy;
				if (x >= 0 && x < image.GetWidth() && y >= 0 && y < image.GetHeight())
				{
					image.SetPixel(x, y, color);
				}
			}
		}
	}

	private Image BuildScalarFieldImage(int targetSize, MiniMapScalarField field, Color low, Color mid, Color high)
	{
		if (_world is null)
		{
			return Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
		}

		var image = Image.CreateEmpty(targetSize, targetSize, false, Image.Format.Rgba8);
		var maxWorldX = Mathf.Max(_world.Width - 1, 0);
		var maxWorldY = Mathf.Max(_world.Height - 1, 0);

		for (var y = 0; y < targetSize; y++)
		{
			for (var x = 0; x < targetSize; x++)
			{
				var worldX = ((x + 0.5f) / targetSize) * maxWorldX;
				var worldY = ((y + 0.5f) / targetSize) * maxWorldY;
				image.SetPixel(x, y, GetBilinearScalarColor(worldX, worldY, field, low, mid, high));
			}
		}

		return image;
	}

	private Color GetMiniMapColor(float worldX, float worldY)
	{
		if (_world is null)
		{
			return Colors.Magenta;
		}

		var nearestTile = _world.GetTile(
			Mathf.Clamp(Mathf.RoundToInt(worldX), 0, _world.Width - 1),
			Mathf.Clamp(Mathf.RoundToInt(worldY), 0, _world.Height - 1));

		return _miniMapViewMode switch
		{
			MiniMapViewMode.Biome => GetBiomeMiniMapColor(nearestTile),
			MiniMapViewMode.Elevation => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.Elevation, new Color("0e2a47"), new Color("5f9d52"), new Color("f1f4f8")),
			MiniMapViewMode.Moisture => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.Moisture, new Color("7e5830"), new Color("6cae4f"), new Color("4da8d8")),
			MiniMapViewMode.Temperature => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.Temperature, new Color("3d66d1"), new Color("e1d770"), new Color("d9643d")),
			MiniMapViewMode.Rainfall => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.Rainfall, new Color("6d4b29"), new Color("5a9a57"), new Color("4a93d8")),
			MiniMapViewMode.Sunlight => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.Sunlight, new Color("3759b6"), new Color("e4d469"), new Color("f28b28")),
			MiniMapViewMode.SoilMoisture => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.SoilMoisture, new Color("5d3c1f"), new Color("74a95a"), new Color("48b0cf")),
			MiniMapViewMode.Nutrients => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.Nutrients, new Color("4d341e"), new Color("88b653"), new Color("d8ef8c")),
			MiniMapViewMode.OrganicMatter => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.OrganicMatter, new Color("2b1d14"), new Color("6f4e37"), new Color("b5925c")),
			MiniMapViewMode.FloraGrowth => GetBilinearScalarColor(worldX, worldY, MiniMapScalarField.FloraGrowth, new Color("1b2614"), new Color("4c9543"), new Color("d3f29a")),
			MiniMapViewMode.Succession => GetSuccessionMiniMapColor(nearestTile.Succession),
			_ => Colors.Magenta
		};
	}

	private static Color GetBiomeMiniMapColor(TileData tile)
	{
		var color = BiomeClassifier.GetMiniMapColor(tile.Biome);
		if (tile.Flora != FloraType.None && !tile.IsWater)
		{
			color = color.Lerp(Colors.Black, 0.1f);
		}

		return color;
	}

	private static Color GetSuccessionMiniMapColor(SuccessionStage stage) => stage switch
	{
		SuccessionStage.Bare => new Color("4d3a26"),
		SuccessionStage.Pioneer => new Color("b59b57"),
		SuccessionStage.Intermediate => new Color("68a64f"),
		SuccessionStage.Climax => new Color("2a6f32"),
		_ => Colors.Magenta
	};

	private Color GetBilinearScalarColor(float worldX, float worldY, MiniMapScalarField field, Color low, Color mid, Color high)
	{
		return SampleScalarGradient(SampleTileScalarBilinear(worldX, worldY, field), low, mid, high);
	}

	private float SampleTileScalarBilinear(float worldX, float worldY, MiniMapScalarField field)
	{
		if (field == MiniMapScalarField.Temperature && _temperatureFieldSampler is not null)
		{
			return Mathf.Clamp(_temperatureFieldSampler(new Vector2(worldX, worldY)), 0.0f, 1.0f);
		}

		if (field == MiniMapScalarField.Moisture && _moistureFieldSampler is not null)
		{
			return Mathf.Clamp(_moistureFieldSampler(new Vector2(worldX, worldY)), 0.0f, 1.0f);
		}

		return SampleTileScalarBilinearFromTiles(worldX, worldY, field);
	}

	private float SampleTileScalarBilinearFromTiles(float worldX, float worldY, MiniMapScalarField field)
	{
		if (_world is null)
		{
			return 0.0f;
		}

		var x0 = Mathf.Clamp(Mathf.FloorToInt(worldX), 0, _world.Width - 1);
		var y0 = Mathf.Clamp(Mathf.FloorToInt(worldY), 0, _world.Height - 1);
		var x1 = Mathf.Clamp(x0 + 1, 0, _world.Width - 1);
		var y1 = Mathf.Clamp(y0 + 1, 0, _world.Height - 1);
		var tx = Mathf.Clamp(worldX - x0, 0.0f, 1.0f);
		var ty = Mathf.Clamp(worldY - y0, 0.0f, 1.0f);

		var v00 = GetScalarFieldValue(_world.GetTile(x0, y0), field);
		var v10 = GetScalarFieldValue(_world.GetTile(x1, y0), field);
		var v01 = GetScalarFieldValue(_world.GetTile(x0, y1), field);
		var v11 = GetScalarFieldValue(_world.GetTile(x1, y1), field);

		var top = Mathf.Lerp(v00, v10, tx);
		var bottom = Mathf.Lerp(v01, v11, tx);
		return Mathf.Lerp(top, bottom, ty);
	}

	private static float GetScalarFieldValue(TileData tile, MiniMapScalarField field) => field switch
	{
		MiniMapScalarField.Elevation => tile.Elevation,
		MiniMapScalarField.Moisture => tile.Moisture,
		MiniMapScalarField.Temperature => tile.Temperature,
		MiniMapScalarField.Rainfall => tile.Rainfall,
		MiniMapScalarField.Sunlight => tile.Sunlight,
		MiniMapScalarField.SoilMoisture => tile.SoilMoisture,
		MiniMapScalarField.Nutrients => tile.Nutrients,
		MiniMapScalarField.OrganicMatter => tile.OrganicMatter,
		MiniMapScalarField.FloraGrowth => tile.FloraGrowth,
		_ => 0.0f
	};

	private static Color SampleScalarGradient(float value, Color low, Color mid, Color high)
	{
		var clamped = Mathf.Clamp(value, 0.0f, 1.0f);
		return clamped < 0.5f
			? low.Lerp(mid, clamped * 2.0f)
			: mid.Lerp(high, (clamped - 0.5f) * 2.0f);
	}

	private static string GetMiniMapViewModeDisplayName(MiniMapViewMode mode) => mode switch
	{
		MiniMapViewMode.Biome => "Biome",
		MiniMapViewMode.Elevation => "Elevation",
		MiniMapViewMode.Moisture => "Moisture",
		MiniMapViewMode.Temperature => "Temperature",
		MiniMapViewMode.Rainfall => "Rainfall",
		MiniMapViewMode.Sunlight => "Sunlight",
		MiniMapViewMode.SoilMoisture => "Soil Moisture",
		MiniMapViewMode.Nutrients => "Nutrients",
		MiniMapViewMode.OrganicMatter => "Organic Matter",
		MiniMapViewMode.FloraGrowth => "Flora Growth",
		MiniMapViewMode.Succession => "Succession",
		_ => mode.ToString()
	};

	private void ConfigureWeatherParticles()
	{
		RainParticles.Amount = 240;
		RainParticles.Lifetime = 1.25f;
		RainParticles.Preprocess = 0.3f;
		RainParticles.Emitting = false;
		RainParticles.ProcessMaterial = new ParticleProcessMaterial
		{
			EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
			EmissionBoxExtents = new Vector3(1200.0f, 20.0f, 1.0f),
			Direction = new Vector3(0.08f, 1.0f, 0.0f),
			Gravity = new Vector3(0.0f, 850.0f, 0.0f),
			InitialVelocityMin = 650.0f,
			InitialVelocityMax = 850.0f,
			ScaleMin = 0.3f,
			ScaleMax = 0.45f,
			AngleMin = -6.0f,
			AngleMax = 6.0f
		};
		RainParticles.Texture = ImageTexture.CreateFromImage(CreateParticleImage(new Color("97d8ff"), 2, 6));
		RainParticles.Position = new Vector2(0.0f, -400.0f);

		SnowParticles.Amount = 180;
		SnowParticles.Lifetime = 4.2f;
		SnowParticles.Preprocess = 0.3f;
		SnowParticles.Emitting = false;
		SnowParticles.ProcessMaterial = new ParticleProcessMaterial
		{
			EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
			EmissionBoxExtents = new Vector3(1200.0f, 20.0f, 1.0f),
			Direction = new Vector3(0.1f, 1.0f, 0.0f),
			Gravity = new Vector3(0.0f, 110.0f, 0.0f),
			InitialVelocityMin = 70.0f,
			InitialVelocityMax = 120.0f,
			ScaleMin = 0.8f,
			ScaleMax = 1.3f,
			AngleMin = -18.0f,
			AngleMax = 18.0f
		};
		SnowParticles.Texture = ImageTexture.CreateFromImage(CreateParticleImage(new Color("ffffff"), 3, 3));
		SnowParticles.Position = new Vector2(0.0f, -420.0f);
	}

	private static Image CreateParticleImage(Color color, int width, int height)
	{
		var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
		image.Fill(new Color(0, 0, 0, 0));
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				image.SetPixel(x, y, color);
			}
		}

		return image;
	}
}
