using Godot;
using CWM.Scripts.Core;

namespace CWM.Scripts.UI;

public partial class MainMenuController : CanvasLayer
{
    private LineEdit _seedInput = null!;
    private OptionButton _mapSizeOption = null!;
    private LineEdit _ipInput = null!;
    private LineEdit _portInput = null!;
    private Label _statusLabel = null!;

    public event Action<int, int>? StartLocalRequested;
    public event Action<int, int, int>? HostRequested;
    public event Action<string, int>? JoinRequested;
    public event Action? ImportRequested;

    public override void _Ready()
    {
        _seedInput = GetNode<LineEdit>("Root/Panel/VBox/SeedRow/SeedInput");
        _mapSizeOption = GetNode<OptionButton>("Root/Panel/VBox/SizeRow/MapSizeOption");
        _ipInput = GetNode<LineEdit>("Root/Panel/VBox/NetworkRow/IpInput");
        _portInput = GetNode<LineEdit>("Root/Panel/VBox/NetworkRow/PortInput");
        _statusLabel = GetNode<Label>("Root/Panel/VBox/StatusLabel");

        GetNode<Button>("Root/Panel/VBox/SeedRow/RandomSeedButton").Pressed += SetRandomSeed;
        GetNode<Button>("Root/Panel/VBox/SingleButton").Pressed += () =>
        {
            var (seed, mapSize, _) = ReadSettings();
            StartLocalRequested?.Invoke(seed, mapSize);
        };
        GetNode<Button>("Root/Panel/VBox/HostButton").Pressed += () =>
        {
            var (seed, mapSize, port) = ReadSettings();
            HostRequested?.Invoke(seed, mapSize, port);
        };
        GetNode<Button>("Root/Panel/VBox/JoinButton").Pressed += () =>
        {
            var (_, _, port) = ReadSettings();
            JoinRequested?.Invoke(string.IsNullOrWhiteSpace(_ipInput.Text) ? "127.0.0.1" : _ipInput.Text, port);
        };
        GetNode<Button>("Root/Panel/VBox/ImportButton").Pressed += () => ImportRequested?.Invoke();

        _mapSizeOption.Clear();
        _mapSizeOption.AddItem("256 x 256", 256);
        _mapSizeOption.AddItem("512 x 512", 512);
        _mapSizeOption.AddItem("1024 x 1024", 1024);
        _mapSizeOption.Select(2);

        _portInput.Text = Constants.DefaultPort.ToString();
        _ipInput.Text = "127.0.0.1";
        SetRandomSeed();
        SetStatus("Ready");
    }

    public void SetStatus(string statusText)
    {
        _statusLabel.Text = statusText;
    }

    private void SetRandomSeed()
    {
        _seedInput.Text = GD.Randi().ToString();
    }

    private (int Seed, int MapSize, int Port) ReadSettings()
    {
        var seed = int.TryParse(_seedInput.Text, out var parsedSeed) ? parsedSeed : (int)GD.Randi();
        var mapSize = _mapSizeOption.GetSelectedId();
        if (mapSize <= 0)
        {
            mapSize = Constants.DefaultMapSize;
        }

        var port = int.TryParse(_portInput.Text, out var parsedPort) ? parsedPort : Constants.DefaultPort;
        return (seed, mapSize, port);
    }
}
