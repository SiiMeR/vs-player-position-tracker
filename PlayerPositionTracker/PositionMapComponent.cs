using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace PlayerPositionTracker;

public class PositionMapComponent : MapComponent
{
    private readonly Vec3d _position;
    private readonly string _playerName;
    private readonly string _timestamp;
    private readonly float _yaw;
    private readonly LoadedTexture _texture;
    private readonly MeshRef _quadModel;
    private Vec2f _viewPos = new();
    private readonly Matrixf _mvMat = new();

    public PositionMapComponent(ICoreClientAPI capi, LoadedTexture texture, Vec3d position, string playerName,
        string timestamp, float yaw)
        : base(capi)
    {
        _quadModel = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        _texture = texture;
        _position = position;
        _playerName = playerName;
        _timestamp = timestamp;
        _yaw = yaw;
    }

    public override void Render(GuiElementMap map, float dt)
    {
        map.TranslateWorldPosToViewPos(_position, ref _viewPos);

        if (_viewPos.X < -10f || _viewPos.Y < -10f ||
            (double)_viewPos.X > map.Bounds.OuterWidth + 10.0 ||
            (double)_viewPos.Y > map.Bounds.OuterHeight + 10.0)
        {
            return;
        }

        float x = (float)(map.Bounds.renderX + (double)_viewPos.X);
        float y = (float)(map.Bounds.renderY + (double)_viewPos.Y);

        ICoreClientAPI api = map.Api;

        capi.Render.GlToggleBlend(true);
        IShaderProgram shader = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        shader.Uniform("rgbaIn", ColorUtil.WhiteArgbVec);
        shader.Uniform("applyColor", 0);
        shader.Uniform("extraGlow", 0);
        shader.Uniform("noTexture", 0f);
        shader.BindTexture2D("tex2d", _texture.TextureId, 0);

        _mvMat.Set(api.Render.CurrentModelviewMatrix)
            .Translate(x, y, 60f)
            .RotateZ(0f - _yaw + (float)Math.PI)
            .Scale(_texture.Width, _texture.Height, 0f)
            .Scale(0.5f, 0.5f, 0f);

        shader.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        shader.UniformMatrix("modelViewMatrix", _mvMat.Values);
        api.Render.RenderMesh(_quadModel);
    }

    public override void Dispose()
    {
        base.Dispose();
        _quadModel?.Dispose();
    }

    public override void OnMouseMove(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        Vec2f vec = new();
        mapElem.TranslateWorldPosToViewPos(_position, ref vec);
        double dx = (double)args.X - mapElem.Bounds.renderX;
        double dy = (double)args.Y - mapElem.Bounds.renderY;
        double threshold = GuiElement.scaled(5.0);

        if (Math.Abs((double)vec.X - dx) < threshold && Math.Abs((double)vec.Y - dy) < threshold)
        {
            hoverText.AppendLine(_playerName);
        }
    }
}
