using Godot;
using CWM.Scripts.Core;

namespace CWM.Scripts.Player;

public partial class PlayerController : CharacterBody2D
{
    [Export(PropertyHint.Range, "40,220,1")]
    public float MoveSpeed { get; set; } = 110.0f;

    public long PeerId { get; private set; }

    public bool IsLocalPlayer { get; private set; }

    /// <summary>联机地图快照期间冻结移动与网络上报。</summary>
    public bool MovementFrozen { get; private set; }

    public Func<Vector2, float>? MovementModifierProvider { get; set; }

    private static readonly Color[] MainMapPlayerPalette =
    [
        new("e74c3c"), new("3498db"), new("2ecc71"), new("9b59b6"),
        new("e67e22"), new("1abc9c"), new("f1c40f"), new("34495e")
    ];

    private AnimatedSprite2D _animatedSprite = null!;
    private Camera2D _camera = null!;
    private Label _playerIdLabel = null!;
    private Vector2 _facingDirection = Vector2.Down;
    private Vector2 _remoteTargetPosition;
    private Vector2 _remoteVelocity;
    private Vector2 _remoteFacingDirection = Vector2.Down;
    private float _networkSyncAccumulator;

    public event Action<long, Vector2, Vector2, Vector2>? LocalStateUpdated;

    public override void _Ready()
    {
        _animatedSprite = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
        _camera = GetNode<Camera2D>("Camera2D");

        _playerIdLabel = new Label();
        _playerIdLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _playerIdLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _playerIdLabel.VerticalAlignment = VerticalAlignment.Center;
        _playerIdLabel.CustomMinimumSize = new Vector2(32, 14);
        _playerIdLabel.Position = new Vector2(-16, -30);
        _playerIdLabel.AddThemeFontSizeOverride("font_size", 9);
        _playerIdLabel.AddThemeColorOverride("font_color", Colors.White);
        _playerIdLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.94f));
        _playerIdLabel.AddThemeConstantOverride("outline_size", 4);
        AddChild(_playerIdLabel);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (MovementFrozen)
        {
            Velocity = Vector2.Zero;
            return;
        }

        if (IsLocalPlayer)
        {
            UpdateLocalMovement((float)delta);
        }
        else
        {
            UpdateRemoteMovement((float)delta);
        }
    }

    public void Initialize(long peerId, bool isLocalPlayer, int displayOrdinal)
    {
        PeerId = peerId;
        IsLocalPlayer = isLocalPlayer;
        _camera.Enabled = isLocalPlayer;
        _remoteTargetPosition = GlobalPosition;
        var bodyColor = ColorForMainMap(displayOrdinal);
        _animatedSprite.SpriteFrames = BuildFrames(bodyColor);
        PlayAnimation("idle_down");
        _playerIdLabel.Text = displayOrdinal.ToString();
    }

    private static Color ColorForMainMap(int displayOrdinal)
    {
        var i = Mathf.Max(0, displayOrdinal - 1) % MainMapPlayerPalette.Length;
        return MainMapPlayerPalette[i];
    }

    public void SetMovementFrozen(bool frozen)
    {
        MovementFrozen = frozen;
        Velocity = Vector2.Zero;
        if (frozen)
        {
            PlayAnimation("idle_down");
        }
    }

    public void ApplyRemoteState(Vector2 position, Vector2 velocity, Vector2 facingDirection)
    {
        _remoteTargetPosition = position;
        _remoteVelocity = velocity;
        if (facingDirection != Vector2.Zero)
        {
            _remoteFacingDirection = facingDirection;
        }
    }

    private void UpdateLocalMovement(float delta)
    {
        var input = Input.GetVector("move_left", "move_right", "move_up", "move_down");
        if (input != Vector2.Zero)
        {
            _facingDirection = input.Normalized();
        }

        var movementModifier = MovementModifierProvider?.Invoke(GlobalPosition) ?? 1.0f;
        if (movementModifier <= 0.0f)
        {
            input = Vector2.Zero;
        }

        Velocity = input * MoveSpeed * movementModifier;
        GlobalPosition = ResolveMovement(GlobalPosition, Velocity * delta);

        UpdateAnimation(Velocity, _facingDirection);

        _networkSyncAccumulator += delta;
        if (_networkSyncAccumulator >= Constants.LocalPlayerSyncInterval)
        {
            _networkSyncAccumulator = 0.0f;
            LocalStateUpdated?.Invoke(PeerId, GlobalPosition, Velocity, _facingDirection);
        }
    }

    private void UpdateRemoteMovement(float delta)
    {
        GlobalPosition = GlobalPosition.Lerp(_remoteTargetPosition, Mathf.Clamp(delta * 12.0f, 0.0f, 1.0f));
        UpdateAnimation(_remoteVelocity, _remoteFacingDirection);
    }

    private Vector2 ResolveMovement(Vector2 currentPosition, Vector2 deltaMovement)
    {
        if (MovementModifierProvider is null)
        {
            return currentPosition + deltaMovement;
        }

        var target = currentPosition + deltaMovement;
        if (MovementModifierProvider(target) > 0.0f)
        {
            return target;
        }

        var xSlide = new Vector2(target.X, currentPosition.Y);
        if (MovementModifierProvider(xSlide) > 0.0f)
        {
            return xSlide;
        }

        var ySlide = new Vector2(currentPosition.X, target.Y);
        if (MovementModifierProvider(ySlide) > 0.0f)
        {
            return ySlide;
        }

        return currentPosition;
    }

    private void UpdateAnimation(Vector2 velocity, Vector2 facingDirection)
    {
        var moving = velocity.LengthSquared() > 1.0f;
        var directionName = GetDirectionName(facingDirection);
        PlayAnimation($"{(moving ? "walk" : "idle")}_{directionName}");
    }

    private void PlayAnimation(string animationName)
    {
        if (_animatedSprite.Animation == animationName && _animatedSprite.IsPlaying())
        {
            return;
        }

        _animatedSprite.Play(animationName);
    }

    private static string GetDirectionName(Vector2 direction)
    {
        if (Mathf.Abs(direction.X) > Mathf.Abs(direction.Y))
        {
            return direction.X > 0.0f ? "right" : "left";
        }

        return direction.Y > 0.0f ? "down" : "up";
    }

    private static SpriteFrames BuildFrames(Color bodyColor)
    {
        var frames = new SpriteFrames();
        foreach (var direction in new[] { "down", "up", "left", "right" })
        {
            var idleImage = BuildBodyFrame(bodyColor, direction, 0);
            frames.AddAnimation($"idle_{direction}");
            frames.SetAnimationSpeed($"idle_{direction}", 4.0f);
            frames.AddFrame($"idle_{direction}", ImageTexture.CreateFromImage(idleImage));

            frames.AddAnimation($"walk_{direction}");
            frames.SetAnimationSpeed($"walk_{direction}", 8.0f);
            for (var step = 0; step < 4; step++)
            {
                frames.AddFrame($"walk_{direction}", ImageTexture.CreateFromImage(BuildBodyFrame(bodyColor, direction, step)));
            }
        }

        return frames;
    }

    private static Image BuildBodyFrame(Color bodyColor, string direction, int step)
    {
        var image = Image.CreateEmpty(12, 18, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));

        var headColor = bodyColor.Lightened(0.12f);
        PaintRect(image, new Rect2I(4, 0, 4, 4), headColor);
        PaintRect(image, new Rect2I(3, 4, 6, 7), bodyColor);
        PaintRect(image, new Rect2I(2, 5, 1, 4), bodyColor.Darkened(0.15f));
        PaintRect(image, new Rect2I(9, 5, 1, 4), bodyColor.Darkened(0.15f));

        var stepOffset = step % 2 == 0 ? 0 : 1;
        PaintRect(image, new Rect2I(4, 11, 2, 5 - stepOffset), bodyColor.Darkened(0.24f));
        PaintRect(image, new Rect2I(6, 11 + stepOffset, 2, 5 - stepOffset), bodyColor.Darkened(0.24f));

        if (direction == "left" || direction == "right")
        {
            PaintRect(image, new Rect2I(direction == "left" ? 3 : 7, 6, 2, 1), Colors.Black);
        }
        else
        {
            PaintRect(image, new Rect2I(4, 6, 1, 1), Colors.Black);
            PaintRect(image, new Rect2I(7, 6, 1, 1), Colors.Black);
        }

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

}
