using Godot;
using CWM.Scripts.Core;

namespace CWM.Scripts.World;

public sealed class TerrainTileCatalog
{
    public required TileSet TileSet { get; init; }

    public required Texture2D AtlasTexture { get; init; }

    public required Dictionary<BiomeType, Vector2I> TerrainAtlasCoords { get; init; }

    public required Dictionary<FloraType, Vector2I> FloraAtlasCoords { get; init; }

    public Vector2I GetTerrain(BiomeType biome) => TerrainAtlasCoords.TryGetValue(biome, out var coords)
        ? coords
        : Constants.InvalidAtlasCoords;

    public Vector2I GetFlora(FloraType flora) => FloraAtlasCoords.TryGetValue(flora, out var coords)
        ? coords
        : Constants.InvalidAtlasCoords;
}

public static class TerrainTilesetBuilder
{
    private const int AtlasColumns = 4;
    private const int AtlasRows = 6;

    public static TerrainTileCatalog Build(int seed)
    {
        var atlas = Image.CreateEmpty(
            AtlasColumns * Constants.TileSize,
            AtlasRows * Constants.TileSize,
            false,
            Image.Format.Rgba8);

        var rng = new RandomNumberGenerator
        {
            Seed = (ulong)Mathf.Abs(seed) + 11UL
        };

        var slots = new Dictionary<Vector2I, Image>
        {
            [new Vector2I(0, 0)] = CreateWaterTile(new Color("255f9a"), new Color("2c8ae1"), rng, 0.35f),
            [new Vector2I(1, 0)] = CreateWaterTile(new Color("4ca1df"), new Color("7fd4ff"), rng, 0.18f),
            [new Vector2I(2, 0)] = CreateNoiseTile(new Color("cfbc73"), new Color("e5d592"), new Color("b89954"), rng, 14),
            [new Vector2I(3, 0)] = CreateNoiseTile(new Color("6db353"), new Color("86c96a"), new Color("4f8f39"), rng, 15),
            [new Vector2I(0, 1)] = CreateNoiseTile(new Color("2d7d32"), new Color("4aa54d"), new Color("215b24"), rng, 20),
            [new Vector2I(1, 1)] = CreateNoiseTile(new Color("28563d"), new Color("3d784f"), new Color("183728"), rng, 22),
            [new Vector2I(2, 1)] = CreateNoiseTile(new Color("5b7f4b"), new Color("70925f"), new Color("39503a"), rng, 24),
            [new Vector2I(3, 1)] = TintImage(LoadPixelated("res://Assets/Wall/plastered_wall_04_diff_1k.jpg"), new Color("c9a25d")),
            [new Vector2I(0, 2)] = LoadPixelated("res://Assets/Snow/snow_02_diff_1k.jpg"),
            [new Vector2I(1, 2)] = LoadPixelated("res://Assets/Rocks/gray_rocks_diff_1k.jpg"),
            [new Vector2I(2, 2)] = CreateNoiseTile(new Color("8bbc7c"), new Color("cde5a1"), new Color("6e9d67"), rng, 18),
            [new Vector2I(3, 2)] = TintImage(LoadPixelated("res://Assets/Rocks/gray_rocks_diff_1k.jpg"), new Color("7d756f")),
            [new Vector2I(0, 3)] = CreateGrassTuftTile(),
            [new Vector2I(1, 3)] = CreateBroadleafTreeTile(),
            [new Vector2I(2, 3)] = CreateConiferTile(),
            [new Vector2I(3, 3)] = CreateReedTile(),
            [new Vector2I(0, 4)] = CreateCactusTile(),
            [new Vector2I(1, 4)] = CreateShrubTile(),
            [new Vector2I(2, 4)] = CreateFlowerTile(new Color("ffdc6b"), new Color("ce3f4c")),
            [new Vector2I(3, 4)] = CreateFlowerTile(new Color("e9f6ff"), new Color("89a4d9")),
            [new Vector2I(0, 5)] = CreateFarmlandSoilTile()
        };

        foreach (var pair in slots)
        {
            atlas.BlitRect(
                pair.Value,
                new Rect2I(Vector2I.Zero, new Vector2I(Constants.TileSize, Constants.TileSize)),
                pair.Key * Constants.TileSize);
        }

        var texture = ImageTexture.CreateFromImage(atlas);
        var tileSet = new TileSet();
        var atlasSource = new TileSetAtlasSource
        {
            Texture = texture,
            TextureRegionSize = new Vector2I(Constants.TileSize, Constants.TileSize)
        };

        foreach (var coord in slots.Keys)
        {
            atlasSource.CreateTile(coord);
        }

        tileSet.AddSource(atlasSource, Constants.TerrainSourceId);

        return new TerrainTileCatalog
        {
            TileSet = tileSet,
            AtlasTexture = texture,
            TerrainAtlasCoords = new Dictionary<BiomeType, Vector2I>
            {
                [BiomeType.DeepWater] = new Vector2I(0, 0),
                [BiomeType.ShallowWater] = new Vector2I(1, 0),
                [BiomeType.Beach] = new Vector2I(2, 0),
                [BiomeType.Grassland] = new Vector2I(3, 0),
                [BiomeType.Forest] = new Vector2I(0, 1),
                [BiomeType.ConiferForest] = new Vector2I(1, 1),
                [BiomeType.Swamp] = new Vector2I(2, 1),
                [BiomeType.Desert] = new Vector2I(3, 1),
                [BiomeType.Snow] = new Vector2I(0, 2),
                [BiomeType.RockyHighlands] = new Vector2I(1, 2),
                [BiomeType.AlpineMeadow] = new Vector2I(2, 2),
                [BiomeType.BareRock] = new Vector2I(3, 2),
                [BiomeType.Farmland] = new Vector2I(0, 5)
            },
            FloraAtlasCoords = new Dictionary<FloraType, Vector2I>
            {
                [FloraType.Grass] = new Vector2I(0, 3),
                [FloraType.BroadleafTree] = new Vector2I(1, 3),
                [FloraType.ConiferTree] = new Vector2I(2, 3),
                [FloraType.Reed] = new Vector2I(3, 3),
                [FloraType.Cactus] = new Vector2I(0, 4),
                [FloraType.Shrub] = new Vector2I(1, 4),
                [FloraType.Wildflower] = new Vector2I(2, 4),
                [FloraType.AlpineBloom] = new Vector2I(3, 4),
                [FloraType.Moss] = new Vector2I(1, 4)
            }
        };
    }

    private static Image LoadPixelated(string path)
    {
        var texture = GD.Load<Texture2D>(path);
        var image = texture?.GetImage() ?? CreateNoiseTile(new Color("ff00ff"), new Color("000000"), new Color("ffffff"), new RandomNumberGenerator(), 2);
        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        image.Resize(Constants.TileSize, Constants.TileSize, Image.Interpolation.Nearest);
        return image;
    }

    private static Image TintImage(Image source, Color tint)
    {
        var copy = source.Duplicate() as Image ?? source;
        for (var y = 0; y < copy.GetHeight(); y++)
        {
            for (var x = 0; x < copy.GetWidth(); x++)
            {
                var color = copy.GetPixel(x, y);
                copy.SetPixel(x, y, new Color(
                    color.R * tint.R,
                    color.G * tint.G,
                    color.B * tint.B,
                    color.A));
            }
        }

        return copy;
    }

    private static Image CreateNoiseTile(Color baseColor, Color midColor, Color accentColor, RandomNumberGenerator rng, int speckles)
    {
        var image = Image.CreateEmpty(Constants.TileSize, Constants.TileSize, false, Image.Format.Rgba8);
        image.Fill(baseColor);

        for (var i = 0; i < speckles; i++)
        {
            var position = new Vector2I(rng.RandiRange(0, Constants.TileSize - 1), rng.RandiRange(0, Constants.TileSize - 1));
            image.SetPixel(position.X, position.Y, i % 2 == 0 ? midColor : accentColor);
        }

        for (var y = 0; y < Constants.TileSize; y++)
        {
            for (var x = 0; x < Constants.TileSize; x++)
            {
                if (((x + y) & 3) == 0)
                {
                    var pixel = image.GetPixel(x, y);
                    image.SetPixel(x, y, pixel.Lerp(midColor, 0.06f));
                }
            }
        }

        return image;
    }

    private static Image CreateFarmlandSoilTile()
    {
        var rng = new RandomNumberGenerator { Seed = 12047UL };
        return CreateNoiseTile(new Color("6b4f2a"), new Color("8b6a3f"), new Color("4a3420"), rng, 22);
    }

    private static Image CreateWaterTile(Color baseColor, Color shimmerColor, RandomNumberGenerator rng, float strength)
    {
        var image = Image.CreateEmpty(Constants.TileSize, Constants.TileSize, false, Image.Format.Rgba8);
        image.Fill(baseColor);

        for (var y = 0; y < Constants.TileSize; y++)
        {
            for (var x = 0; x < Constants.TileSize; x++)
            {
                var wave = Mathf.Sin((x * 0.6f) + (y * 0.35f)) * strength;
                var mix = Mathf.Clamp(0.25f + wave, 0.0f, 0.9f);
                var color = baseColor.Lerp(shimmerColor, mix);
                if (rng.Randf() > 0.93f)
                {
                    color = color.Lightened(0.15f);
                }

                image.SetPixel(x, y, color);
            }
        }

        return image;
    }

    private static Image CreateBroadleafTreeTile()
    {
        var image = BlankDecoration();
        PaintCircle(image, new Vector2I(8, 6), 4, new Color("336a2d"));
        PaintCircle(image, new Vector2I(5, 8), 3, new Color("44863c"));
        PaintCircle(image, new Vector2I(11, 8), 3, new Color("5ea64b"));
        PaintRect(image, new Rect2I(7, 10, 2, 5), new Color("6e4b24"));
        return image;
    }

    private static Image CreateConiferTile()
    {
        var image = BlankDecoration();
        PaintTriangle(image, new Vector2I(8, 2), 5, new Color("2e6a4f"));
        PaintTriangle(image, new Vector2I(8, 5), 4, new Color("3e8863"));
        PaintTriangle(image, new Vector2I(8, 8), 3, new Color("5cac7c"));
        PaintRect(image, new Rect2I(7, 11, 2, 4), new Color("6f5032"));
        return image;
    }

    private static Image CreateGrassTuftTile()
    {
        var image = BlankDecoration();
        PaintLine(image, new Vector2I(6, 14), new Vector2I(7, 8), new Color("6db353"));
        PaintLine(image, new Vector2I(8, 14), new Vector2I(8, 6), new Color("8bd26b"));
        PaintLine(image, new Vector2I(10, 14), new Vector2I(9, 7), new Color("4a8e37"));
        return image;
    }

    private static Image CreateReedTile()
    {
        var image = BlankDecoration();
        PaintLine(image, new Vector2I(5, 15), new Vector2I(5, 7), new Color("87b15c"));
        PaintLine(image, new Vector2I(8, 15), new Vector2I(8, 5), new Color("a2cb72"));
        PaintLine(image, new Vector2I(11, 15), new Vector2I(11, 6), new Color("6a8e48"));
        PaintRect(image, new Rect2I(4, 5, 2, 2), new Color("7d5d2c"));
        PaintRect(image, new Rect2I(10, 4, 2, 2), new Color("7d5d2c"));
        return image;
    }

    private static Image CreateCactusTile()
    {
        var image = BlankDecoration();
        PaintRect(image, new Rect2I(7, 4, 2, 9), new Color("5eaa4a"));
        PaintRect(image, new Rect2I(5, 6, 2, 4), new Color("5eaa4a"));
        PaintRect(image, new Rect2I(9, 7, 2, 4), new Color("6dc659"));
        return image;
    }

    private static Image CreateShrubTile()
    {
        var image = BlankDecoration();
        PaintCircle(image, new Vector2I(8, 9), 4, new Color("527b41"));
        PaintRect(image, new Rect2I(7, 11, 2, 3), new Color("5b4330"));
        return image;
    }

    private static Image CreateFlowerTile(Color stemColor, Color petalColor)
    {
        var image = BlankDecoration();
        PaintLine(image, new Vector2I(8, 14), new Vector2I(8, 8), stemColor);
        PaintCircle(image, new Vector2I(8, 7), 2, petalColor);
        image.SetPixel(8, 7, new Color("f8f4d1"));
        return image;
    }

    private static Image BlankDecoration()
    {
        var image = Image.CreateEmpty(Constants.TileSize, Constants.TileSize, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));
        return image;
    }

    private static void PaintRect(Image image, Rect2I rect, Color color)
    {
        for (var y = rect.Position.Y; y < rect.End.Y; y++)
        {
            for (var x = rect.Position.X; x < rect.End.X; x++)
            {
                if (x >= 0 && x < image.GetWidth() && y >= 0 && y < image.GetHeight())
                {
                    image.SetPixel(x, y, color);
                }
            }
        }
    }

    private static void PaintCircle(Image image, Vector2I center, int radius, Color color)
    {
        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                if (x < 0 || x >= image.GetWidth() || y < 0 || y >= image.GetHeight())
                {
                    continue;
                }

                if (center.DistanceSquaredTo(new Vector2I(x, y)) <= radius * radius)
                {
                    image.SetPixel(x, y, color);
                }
            }
        }
    }

    private static void PaintTriangle(Image image, Vector2I top, int halfWidth, Color color)
    {
        for (var y = 0; y <= halfWidth; y++)
        {
            var currentHalf = halfWidth - y;
            for (var x = top.X - currentHalf; x <= top.X + currentHalf; x++)
            {
                var py = top.Y + y * 2;
                if (x >= 0 && x < image.GetWidth() && py >= 0 && py < image.GetHeight())
                {
                    image.SetPixel(x, py, color);
                }
            }
        }
    }

    private static void PaintLine(Image image, Vector2I from, Vector2I to, Color color)
    {
        var points = Geometry2D.BresenhamLine(from, to);
        foreach (var point in points)
        {
            if (point.X >= 0 && point.X < image.GetWidth() && point.Y >= 0 && point.Y < image.GetHeight())
            {
                image.SetPixel(point.X, point.Y, color);
            }
        }
    }
}
