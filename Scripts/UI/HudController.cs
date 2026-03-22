using Godot;
using CWM.Scripts.Core;
using CWM.Scripts.Ecology;
using CWM.Scripts.World;
using WorldTileData = CWM.Scripts.World.TileData;

namespace CWM.Scripts.UI;

public partial class HudController : CanvasLayer
{
    private Label _miniMapLabel = null!;
    private Label _clockLabel = null!;
    private Label _weatherLabel = null!;
    private Label _networkLabel = null!;
    private Label _tileLabel = null!;
    private Label _brushLabel = null!;
    private Label _successionLabel = null!;
    private Label _successionStepLabel = null!;
    private Label _messageLabel = null!;
    private Label _farmingLabel = null!;
    private Button _farmingModeButton = null!;
    private Label _farmToolLabel = null!;
    private OptionButton _farmToolOption = null!;
    private Label _granaryLabel = null!;
    private TextureRect _miniMapRect = null!;
    private ColorRect _miniMapMarker = null!;
    private Button _brushToggleButton = null!;
    private Button _brushMinusButton = null!;
    private Button _brushPlusButton = null!;
    private OptionButton _brushBiomeOption = null!;
    private CheckBox _successionToggle = null!;
    private SpinBox _successionMultiplierSpinBox = null!;
    private SpinBox _successionStepSpinBox = null!;
    private Button _climateEditorButton = null!;
    private Window _climateWindow = null!;
    private OptionButton _climateFieldOption = null!;
    private Label _climateValueLabel = null!;
    private HSlider _climateValueSlider = null!;
    private Label _climateStrengthLabel = null!;
    private HSlider _climateStrengthSlider = null!;
    private TextureRect _climateMapRect = null!;
    private ColorRect _climateBrushPreview = null!;
    private Label _climateStatusLabel = null!;
    private Label _snapshotSyncBanner = null!;
    private Window _npcInteractionWindow = null!;
    private Label _npcTitleLabel = null!;
    private Label _npcHintLabel = null!;
    private ImageTexture? _climateMapTexture;
    private ulong _messageHoldUntilMs;

    public event Action? ExportRequested;
    public event Action? ImportRequested;
    public event Action? BrushToggleRequested;
    public event Action<int>? BrushRadiusStepRequested;
    public event Action<BiomeType>? BrushBiomeChanged;
    public event Action<ClimateBrushField>? ClimateBrushFieldChanged;
    public event Action<float>? ClimateBrushValueChanged;
    public event Action<float>? ClimateBrushStrengthChanged;
    public event Action<bool>? ClimateEditorVisibilityChanged;
    public event Action<bool>? SuccessionAccelerationToggled;
    public event Action<float>? SuccessionAccelerationMultiplierChanged;
    public event Action<float>? SuccessionStepHoursChanged;
    public event Action? FarmingModeToggleRequested;
    public event Action<FarmBrushTool>? FarmToolChanged;
    public event Action? NpcTalkRequested;
    public event Action? NpcGiftRequested;
    public event Action? NpcHelpRequested;

    public bool IsClimateEditorOpen => _climateWindow.Visible;

    public bool IsNpcInteractionOpen => _npcInteractionWindow.Visible;

    public override void _Ready()
    {
        _miniMapLabel = GetNode<Label>("Root/TopRightPanel/VBox/MiniMapLabel");
        _clockLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/ClockLabel");
        _weatherLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/WeatherLabel");
        _networkLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/NetworkLabel");
        _tileLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/TileLabel");
        _brushLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/BrushLabel");
        _successionLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/SuccessionLabel");
        _successionStepLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/SuccessionStepLabel");
        _messageLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/MessageLabel");
        _farmingLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/FarmingLabel");
        _farmingModeButton = GetNode<Button>("Root/TopLeftPanel/Scroll/VBox/FarmingModeButton");
        _farmToolLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/FarmToolLabel");
        _farmToolOption = GetNode<OptionButton>("Root/TopLeftPanel/Scroll/VBox/FarmToolOption");
        _granaryLabel = GetNode<Label>("Root/TopLeftPanel/Scroll/VBox/GranaryLabel");
        _miniMapRect = GetNode<TextureRect>("Root/TopRightPanel/VBox/MiniMapRect");
        _miniMapMarker = GetNode<ColorRect>("Root/TopRightPanel/VBox/MiniMapRect/MiniMapMarker");
        _brushToggleButton = GetNode<Button>("Root/TopLeftPanel/Scroll/VBox/BrushControls/BrushToggleButton");
        _brushMinusButton = GetNode<Button>("Root/TopLeftPanel/Scroll/VBox/BrushControls/BrushMinusButton");
        _brushPlusButton = GetNode<Button>("Root/TopLeftPanel/Scroll/VBox/BrushControls/BrushPlusButton");
        _brushBiomeOption = GetNode<OptionButton>("Root/TopLeftPanel/Scroll/VBox/BrushBiomeOption");
        _successionToggle = GetNode<CheckBox>("Root/TopLeftPanel/Scroll/VBox/SuccessionControls/SuccessionToggle");
        _successionMultiplierSpinBox = GetNode<SpinBox>("Root/TopLeftPanel/Scroll/VBox/SuccessionControls/SuccessionMultiplierSpinBox");
        _successionStepSpinBox = GetNode<SpinBox>("Root/TopLeftPanel/Scroll/VBox/SuccessionStepSpinBox");
        _climateEditorButton = GetNode<Button>("Root/TopRightPanel/VBox/ClimateEditorButton");
        _climateWindow = GetNode<Window>("Root/ClimateWindow");
        _climateFieldOption = GetNode<OptionButton>("Root/ClimateWindow/Margin/VBox/FieldOption");
        _climateValueLabel = GetNode<Label>("Root/ClimateWindow/Margin/VBox/ValueLabel");
        _climateValueSlider = GetNode<HSlider>("Root/ClimateWindow/Margin/VBox/ValueSlider");
        _climateStrengthLabel = GetNode<Label>("Root/ClimateWindow/Margin/VBox/StrengthLabel");
        _climateStrengthSlider = GetNode<HSlider>("Root/ClimateWindow/Margin/VBox/StrengthSlider");
        _climateMapRect = GetNode<TextureRect>("Root/ClimateWindow/Margin/VBox/ClimateMapRect");
        _climateBrushPreview = GetNode<ColorRect>("Root/ClimateWindow/Margin/VBox/ClimateMapRect/ClimateBrushPreview");
        _climateStatusLabel = GetNode<Label>("Root/ClimateWindow/Margin/VBox/StatusLabel");
        _snapshotSyncBanner = GetNode<Label>("Root/SnapshotSyncBanner");
        _npcInteractionWindow = GetNode<Window>("Root/NpcInteractionWindow");
        _npcTitleLabel = GetNode<Label>("Root/NpcInteractionWindow/Margin/VBox/NpcTitleLabel");
        _npcHintLabel = GetNode<Label>("Root/NpcInteractionWindow/Margin/VBox/NpcHintLabel");
        GetNode<Button>("Root/NpcInteractionWindow/Margin/VBox/TalkButton").Pressed += () => NpcTalkRequested?.Invoke();
        GetNode<Button>("Root/NpcInteractionWindow/Margin/VBox/GiftButton").Pressed += () => NpcGiftRequested?.Invoke();
        GetNode<Button>("Root/NpcInteractionWindow/Margin/VBox/HelpButton").Pressed += () => NpcHelpRequested?.Invoke();
        GetNode<Button>("Root/NpcInteractionWindow/Margin/VBox/CloseNpcButton").Pressed += CloseNpcInteractionPanel;
        _npcInteractionWindow.CloseRequested += CloseNpcInteractionPanel;

        _successionMultiplierSpinBox.MinValue = Constants.MinSuccessionAccelerationMultiplier;
        _successionMultiplierSpinBox.MaxValue = Constants.MaxSuccessionAccelerationMultiplier;
        _successionMultiplierSpinBox.Step = 1.0f;
        _successionMultiplierSpinBox.SetValueNoSignal(Constants.DefaultSuccessionAccelerationMultiplier);
        _successionStepSpinBox.MinValue = Constants.MinSuccessionStepHours;
        _successionStepSpinBox.MaxValue = Constants.MaxSuccessionStepHours;
        _successionStepSpinBox.Step = Constants.SuccessionStepHoursIncrement;
        _successionStepSpinBox.SetValueNoSignal(Constants.DefaultSuccessionStepHours);

        GetNode<Button>("Root/TopRightPanel/VBox/ExportButton").Pressed += () => ExportRequested?.Invoke();
        GetNode<Button>("Root/TopRightPanel/VBox/ImportButton").Pressed += () => ImportRequested?.Invoke();
        _climateEditorButton.Pressed += ToggleClimateEditorWindow;
        _brushToggleButton.Pressed += () => BrushToggleRequested?.Invoke();
        _brushMinusButton.Pressed += () => BrushRadiusStepRequested?.Invoke(-1);
        _brushPlusButton.Pressed += () => BrushRadiusStepRequested?.Invoke(1);
        _brushBiomeOption.ItemSelected += index => BrushBiomeChanged?.Invoke((BiomeType)_brushBiomeOption.GetItemId((int)index));
        _climateFieldOption.ItemSelected += index => ClimateBrushFieldChanged?.Invoke((ClimateBrushField)_climateFieldOption.GetItemId((int)index));
        _climateValueSlider.ValueChanged += value => ClimateBrushValueChanged?.Invoke((float)value);
        _climateStrengthSlider.ValueChanged += value => ClimateBrushStrengthChanged?.Invoke((float)value);
        _successionToggle.Toggled += enabled => SuccessionAccelerationToggled?.Invoke(enabled);
        _successionMultiplierSpinBox.ValueChanged += value => SuccessionAccelerationMultiplierChanged?.Invoke((float)value);
        _successionStepSpinBox.ValueChanged += value => SuccessionStepHoursChanged?.Invoke((float)value);
        _farmingModeButton.Pressed += () => FarmingModeToggleRequested?.Invoke();
        _farmToolOption.ItemSelected += index => FarmToolChanged?.Invoke((FarmBrushTool)_farmToolOption.GetItemId((int)index));
        _climateWindow.CloseRequested += () =>
        {
            _climateWindow.Hide();
            ClearClimateMapBrushPreview();
            ClimateEditorVisibilityChanged?.Invoke(false);
        };

        _brushBiomeOption.Clear();
        foreach (BiomeType biome in Enum.GetValues(typeof(BiomeType)))
        {
            _brushBiomeOption.AddItem(GetBiomeDisplayName(biome), (int)biome);
        }

        _farmToolOption.Clear();
        foreach (FarmBrushTool tool in Enum.GetValues(typeof(FarmBrushTool)))
        {
            _farmToolOption.AddItem(GetFarmToolDisplayName(tool), (int)tool);
        }

        SelectFarmToolNoSignal(FarmBrushTool.PlowFarmland);
        SetFarmingModeUi(false, FarmBrushTool.PlowFarmland);
        UpdateGranary(0.0f);

        _climateFieldOption.Clear();
        foreach (ClimateBrushField field in Enum.GetValues(typeof(ClimateBrushField)))
        {
            _climateFieldOption.AddItem(GetClimateFieldDisplayName(field), (int)field);
        }
        _climateValueSlider.Value = 0.5f;
        _climateStrengthSlider.Value = 0.35f;
        SetClimateBrushState(ClimateBrushField.Rainfall, 0.5f, 0.35f);
        _climateBrushPreview.Visible = false;

        _miniMapMarker.Visible = false;
        Visible = false;
        SetMiniMapMode("Biome");
        ShowMessage("Brush: Q  耕种: G  村民: E  Climate 气候热力图  [Windows]");
    }

    public void OpenNpcInteractionPanel(string displayName, float mood, float giftCost)
    {
        _npcTitleLabel.Text = displayName;
        _npcHintLabel.Text = $"好感: {mood:0}  ·  赠送作物需粮仓 ≥ {giftCost:0.#} 单位";
        _npcInteractionWindow.Visible = true;
        _npcInteractionWindow.PopupCentered();
    }

    public void CloseNpcInteractionPanel()
    {
        _npcInteractionWindow.Hide();
    }

    public void SetMiniMap(Texture2D? texture)
    {
        _miniMapRect.Texture = texture;
        _miniMapMarker.Visible = texture is not null;
    }

    public void SetMiniMapMode(string modeName)
    {
        _miniMapLabel.Text = $"Mini Map: {modeName}  [V]";
    }

    public void UpdateClock(string text) => _clockLabel.Text = text;

    public void UpdateWeather(string text) => _weatherLabel.Text = text;

    public void UpdateNetwork(string text) => _networkLabel.Text = text;

    public void SetSnapshotSyncBanner(string text)
    {
        _snapshotSyncBanner.Text = text;
        _snapshotSyncBanner.Visible = !string.IsNullOrEmpty(text);
    }

    public void ClearSnapshotSyncBanner()
    {
        _snapshotSyncBanner.Text = string.Empty;
        _snapshotSyncBanner.Visible = false;
    }

    public void UpdateTileInfo(Vector2I cell, Vector2 worldPosition, WorldTileData tile)
    {
        var farmBlock = tile.Biome == BiomeType.Farmland
            ? $"\nCrop: {tile.CropGrowth:0.00}  Weeds: {tile.WeedPressure:0.00}\n" +
              $"Est. yield (if mature): {FarmSimulation.ComputeHarvestYield(tile):0.#} units"
            : string.Empty;

        _tileLabel.Text =
            $"World XY: {worldPosition.X:0.#},{worldPosition.Y:0.#}\n" +
            $"Tile: {cell.X},{cell.Y}\n" +
            $"Biome: {tile.Biome}\n" +
            $"Succession: {tile.Succession}\n" +
            $"Flora: {tile.Flora}\n" +
            $"Growth: {tile.FloraGrowth:0.00}\n" +
            $"Rainfall: {tile.Rainfall:0.00}  Sunlight: {tile.Sunlight:0.00}" +
            farmBlock;
    }

    /// <summary>角色在世界范围外时仍刷新世界坐标，避免 HUD 停留在上一格的错觉。</summary>
    public void UpdateWorldPositionOnly(Vector2 worldPosition, Vector2I rawCell)
    {
        _tileLabel.Text =
            $"World XY: {worldPosition.X:0.#},{worldPosition.Y:0.#}\n" +
            $"Tile (raw): {rawCell.X},{rawCell.Y}  (out of map)";
    }

    public void UpdateGranary(float totalUnits)
    {
        _granaryLabel.Text = $"Granary: {totalUnits:0.#} units (harvest with tool)";
    }

    public void SetFarmingModeUi(bool enabled, FarmBrushTool tool)
    {
        _farmingLabel.Text = enabled
            ? "Farming: ON [G]  Tool [R]  Hold Q to apply"
            : "Farming: Off [G]";
        _farmingModeButton.Text = enabled ? "G Farming: On" : "G Farming: Off";
        SelectFarmToolNoSignal(tool);
        _farmToolOption.Visible = enabled;
        _farmToolLabel.Visible = enabled;
    }

    public void SetFarmToolSelection(FarmBrushTool tool) => SelectFarmToolNoSignal(tool);

    private void SelectFarmToolNoSignal(FarmBrushTool tool)
    {
        for (var i = 0; i < _farmToolOption.ItemCount; i++)
        {
            if (_farmToolOption.GetItemId(i) == (int)tool)
            {
                _farmToolOption.Select(i);
                return;
            }
        }
    }

    public void SetBrushState(bool enabled, int radius, BiomeType biome)
    {
        _brushLabel.Text = $"Brush: {(enabled ? "Toggle On" : "Toggle Off")}  Radius: {radius}  Hold Q: Paint  Preset: {GetBiomeDisplayName(biome)}";
        _brushToggleButton.Text = enabled ? "B Toggle On" : "B Toggle Off";
        SelectBrushBiomeNoSignal(biome);
    }

    public void SetSuccessionAccelerationState(bool enabled, float multiplier, bool editable)
    {
        _successionLabel.Text = $"Succession Speed: {(enabled ? $"{multiplier:0.#}x" : "1x")}";
        _successionToggle.SetPressedNoSignal(enabled);
        _successionMultiplierSpinBox.SetValueNoSignal(multiplier);
        _successionToggle.Disabled = !editable;
        _successionMultiplierSpinBox.Editable = editable;
    }

    public void SetSuccessionStepState(float stepHours, bool editable)
    {
        _successionStepLabel.Text = $"Succession Step: {stepHours:0.####}h";
        _successionStepSpinBox.SetValueNoSignal(stepHours);
        _successionStepSpinBox.Editable = editable;
    }

    public void SetClimateBrushState(ClimateBrushField field, float targetValue, float strength)
    {
        SelectClimateFieldNoSignal(field);
        _climateValueSlider.SetValueNoSignal(targetValue);
        _climateStrengthSlider.SetValueNoSignal(strength);
        _climateValueLabel.Text = $"Target Value: {targetValue:0.00}";
        _climateStrengthLabel.Text = $"Brush Strength: {strength:0.00}";
        _climateStatusLabel.Text = $"Editing {GetClimateFieldDisplayName(field)}. Hold Q and move on the heatmap to paint.";
    }

    public void SetClimateMapImage(Image image)
    {
        if (_climateMapTexture is not null)
        {
            _climateMapTexture.Update(image);
        }
        else
        {
            _climateMapTexture = ImageTexture.CreateFromImage(image);
        }

        _climateMapRect.Texture = _climateMapTexture;
    }

    public bool TryGetClimateMapCell(int worldWidth, int worldHeight, out Vector2I cell)
    {
        cell = new Vector2I(int.MinValue, int.MinValue);
        if (!_climateWindow.Visible || worldWidth <= 0 || worldHeight <= 0)
        {
            return false;
        }

        var rectSize = _climateMapRect.Size;
        if (rectSize.X <= 0.0f || rectSize.Y <= 0.0f)
        {
            return false;
        }

        var localMousePosition = _climateMapRect.GetLocalMousePosition();
        if (!new Rect2(Vector2.Zero, rectSize).HasPoint(localMousePosition))
        {
            return false;
        }

        var normalizedX = Mathf.Clamp(localMousePosition.X / rectSize.X, 0.0f, 0.999999f);
        var normalizedY = Mathf.Clamp(localMousePosition.Y / rectSize.Y, 0.0f, 0.999999f);
        cell = new Vector2I(
            Mathf.Clamp(Mathf.FloorToInt(normalizedX * worldWidth), 0, worldWidth - 1),
            Mathf.Clamp(Mathf.FloorToInt(normalizedY * worldHeight), 0, worldHeight - 1));
        return true;
    }

    public void UpdateClimateMapBrushPreview(Vector2I cell, int radius, int worldWidth, int worldHeight, Color color)
    {
        if (!_climateWindow.Visible || worldWidth <= 0 || worldHeight <= 0)
        {
            ClearClimateMapBrushPreview();
            return;
        }

        var rectSize = _climateMapRect.Size;
        if (rectSize.X <= 0.0f || rectSize.Y <= 0.0f)
        {
            ClearClimateMapBrushPreview();
            return;
        }

        var normalizedX = Mathf.Clamp((cell.X + 0.5f) / worldWidth, 0.0f, 1.0f);
        var normalizedY = Mathf.Clamp((cell.Y + 0.5f) / worldHeight, 0.0f, 1.0f);
        var markerWidth = Mathf.Max(8.0f, ((radius * 2.0f) + 1.0f) / worldWidth * rectSize.X);
        var markerHeight = Mathf.Max(8.0f, ((radius * 2.0f) + 1.0f) / worldHeight * rectSize.Y);
        _climateBrushPreview.Color = color;
        _climateBrushPreview.Size = new Vector2(markerWidth, markerHeight);
        _climateBrushPreview.Position = new Vector2(
            (normalizedX * rectSize.X) - (markerWidth * 0.5f),
            (normalizedY * rectSize.Y) - (markerHeight * 0.5f));
        _climateBrushPreview.Visible = true;
    }

    public void ClearClimateMapBrushPreview()
    {
        _climateBrushPreview.Visible = false;
    }

    public void UpdateMiniMapMarker(Vector2I cell, int worldWidth, int worldHeight)
    {
        if (!_miniMapMarker.Visible || worldWidth <= 0 || worldHeight <= 0)
        {
            return;
        }

        var normalizedX = Mathf.Clamp((cell.X + 0.5f) / worldWidth, 0.0f, 1.0f);
        var normalizedY = Mathf.Clamp((cell.Y + 0.5f) / worldHeight, 0.0f, 1.0f);
        var markerSize = _miniMapMarker.Size;
        var rectSize = _miniMapRect.Size;
        _miniMapMarker.Position = new Vector2(
            normalizedX * Mathf.Max(0.0f, rectSize.X - markerSize.X),
            normalizedY * Mathf.Max(0.0f, rectSize.Y - markerSize.Y));
    }

    public void ShowMessage(string message)
    {
        var now = Time.GetTicksMsec();
        if (now < _messageHoldUntilMs)
        {
            return;
        }

        _messageHoldUntilMs = 0;
        _messageLabel.Text = message;
    }

    /// <summary>强制显示消息并在若干秒内阻止被其它 <see cref="ShowMessage"/> 覆盖（耕种反馈等）。</summary>
    public void ShowMessageHeld(string message, float holdSeconds)
    {
        _messageLabel.Text = message;
        var ms = (ulong)Mathf.Max(0.0f, holdSeconds * 1000.0f);
        _messageHoldUntilMs = Time.GetTicksMsec() + ms;
    }

    private void SelectBrushBiomeNoSignal(BiomeType biome)
    {
        for (var i = 0; i < _brushBiomeOption.ItemCount; i++)
        {
            if (_brushBiomeOption.GetItemId(i) == (int)biome)
            {
                _brushBiomeOption.Select(i);
                return;
            }
        }
    }

    private void SelectClimateFieldNoSignal(ClimateBrushField field)
    {
        for (var i = 0; i < _climateFieldOption.ItemCount; i++)
        {
            if (_climateFieldOption.GetItemId(i) == (int)field)
            {
                _climateFieldOption.Select(i);
                return;
            }
        }
    }

    private void ToggleClimateEditorWindow()
    {
        if (_climateWindow.Visible)
        {
            _climateWindow.Hide();
            ClearClimateMapBrushPreview();
            ClimateEditorVisibilityChanged?.Invoke(false);
            return;
        }

        _climateWindow.PopupCentered();
        ClimateEditorVisibilityChanged?.Invoke(true);
    }

    private static string GetBiomeDisplayName(BiomeType biome) => biome switch
    {
        BiomeType.DeepWater => "Deep Water",
        BiomeType.ShallowWater => "Shallow Water",
        BiomeType.Beach => "Beach",
        BiomeType.Grassland => "Grassland",
        BiomeType.Forest => "Forest",
        BiomeType.ConiferForest => "Conifer Forest",
        BiomeType.Swamp => "Swamp",
        BiomeType.Desert => "Desert",
        BiomeType.Snow => "Snow",
        BiomeType.RockyHighlands => "Rocky Highlands",
        BiomeType.AlpineMeadow => "Alpine Meadow",
        BiomeType.BareRock => "Bare Rock",
        BiomeType.Farmland => "耕地",
        _ => biome.ToString()
    };

    private static string GetFarmToolDisplayName(FarmBrushTool tool) => tool switch
    {
        FarmBrushTool.PlowFarmland => "翻土耕地",
        FarmBrushTool.PlaceWater => "浅水源",
        FarmBrushTool.WaterCrops => "浇水",
        FarmBrushTool.Fertilize => "施肥",
        FarmBrushTool.Weed => "除草",
        FarmBrushTool.PlantSeed => "播种",
        FarmBrushTool.Harvest => "收获",
        _ => tool.ToString()
    };

    private static string GetClimateFieldDisplayName(ClimateBrushField field) => field switch
    {
        ClimateBrushField.Rainfall => "Rainfall",
        ClimateBrushField.Sunlight => "Sunlight",
        _ => field.ToString()
    };
}
