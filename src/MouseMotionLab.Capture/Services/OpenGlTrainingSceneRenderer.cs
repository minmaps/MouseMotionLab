using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MouseMotionLab.Capture.Services;

public sealed class OpenGlTrainingSceneRenderer
{
    private const int FloatsPerVertex = 6;
    private static readonly Vector3 CameraEye = new(0f, 1.7f, 0f);
    private int _program;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _triangleVertexCount;
    private int _lineVertexCount;
    private int _viewProjectionLocation;
    private bool _initialized;

    public void Render(double width, double height, double yawDegrees, double pitchDegrees)
    {
        if (!_initialized)
        {
            Initialize();
        }

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height));
        GL.Viewport(0, 0, pixelWidth, pixelHeight);
        GL.ClearColor(0.023f, 0.043f, 0.071f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var viewProjection = CreateViewProjection(pixelWidth, pixelHeight, yawDegrees, pitchDegrees);

        GL.UseProgram(_program);
        // OpenTK stores Matrix4 in row-major order. The shader therefore uses a row vector and
        // UniformMatrix4 transposes the in-memory representation during the OpenGL upload.
        GL.UniformMatrix4(_viewProjectionLocation, true, ref viewProjection);
        GL.BindVertexArray(_vertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _triangleVertexCount);
        GL.LineWidth(1.25f);
        GL.DrawArrays(PrimitiveType.Lines, _triangleVertexCount, _lineVertexCount);
        GL.BindVertexArray(0);
    }

    public static Matrix4 CreateViewProjection(
        int pixelWidth,
        int pixelHeight,
        double yawDegrees,
        double pitchDegrees)
    {
        var direction = CreateCameraDirection(yawDegrees, pitchDegrees);
        var eye = CameraEye;
        var view = Matrix4.LookAt(eye, eye + direction, Vector3.UnitY);
        var projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(78f),
            Math.Max(1, pixelWidth) / (float)Math.Max(1, pixelHeight),
            0.03f,
            240f);
        return view * projection;
    }

    public static Vector3 CreateCameraDirection(double yawDegrees, double pitchDegrees)
    {
        var yaw = MathHelper.DegreesToRadians((float)yawDegrees);
        var pitch = MathHelper.DegreesToRadians((float)pitchDegrees);
        return new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            -MathF.Cos(pitch) * MathF.Cos(yaw));
    }

    private void Initialize()
    {
        _program = CreateProgram(VertexShaderSource, FragmentShaderSource);
        _viewProjectionLocation = GL.GetUniformLocation(_program, "uViewProjection");
        if (_viewProjectionLocation < 0)
        {
            throw new InvalidOperationException("OpenGL camera uniform was optimized away.");
        }

        var triangles = new List<float>();
        var lines = new List<float>();
        AddBox(triangles, new Vector3(0f, -0.12f, 0f), 180f, 0.2f, 180f, Color(20, 29, 41));
        for (var offset = -80; offset <= 80; offset += 5)
        {
            var color = offset % 20 == 0 ? Color(46, 106, 117) : Color(36, 66, 82);
            AddLine(lines, new Vector3(offset, 0.006f, -80f), new Vector3(offset, 0.006f, 80f), color);
            AddLine(lines, new Vector3(-80f, 0.006f, offset), new Vector3(80f, 0.006f, offset), color);
        }

        for (var index = 0; index < 24; index++)
        {
            var angle = index * MathF.PI * 2f / 24f;
            var radius = index % 2 == 0 ? 30f : 46f;
            var pillarHeight = 2.5f + index % 5 * 1.35f;
            var center = new Vector3(
                MathF.Sin(angle) * radius,
                pillarHeight / 2f,
                -MathF.Cos(angle) * radius);
            var color = index % 6 == 0 ? Color(78, 214, 178) : Color(42, 57, 76);
            AddBox(triangles, center, 1.5f, pillarHeight, 1.5f, color);
        }

        AddBox(triangles, new Vector3(0f, 4f, -62f), 10f, 8f, 1.2f, Color(78, 214, 178));
        AddBox(triangles, new Vector3(62f, 3f, 0f), 1.2f, 6f, 12f, Color(255, 165, 94));
        AddBox(triangles, new Vector3(0f, 5f, 62f), 14f, 10f, 1.2f, Color(72, 92, 142));
        AddBox(triangles, new Vector3(-62f, 2.5f, 0f), 1.2f, 5f, 8f, Color(133, 76, 130));
        AddSkyTargets(triangles);

        _triangleVertexCount = triangles.Count / FloatsPerVertex;
        _lineVertexCount = lines.Count / FloatsPerVertex;
        triangles.AddRange(lines);
        var vertices = triangles.ToArray();

        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            vertices.Length * sizeof(float),
            vertices,
            BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(
            0,
            3,
            VertexAttribPointerType.Float,
            normalized: false,
            FloatsPerVertex * sizeof(float),
            0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(
            1,
            3,
            VertexAttribPointerType.Float,
            normalized: false,
            FloatsPerVertex * sizeof(float),
            3 * sizeof(float));
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Enable(EnableCap.Multisample);
        _initialized = true;
    }

    private static void AddSkyTargets(List<float> vertices)
    {
        // Spread targets around the player at four elevations, including near
        // the top of the camera's pitch range. Their faces point at the player.
        for (var tier = 0; tier < 4; tier++)
        {
            var targetCount = 16 - tier * 4;
            var elevation = 18d + tier * 20d;
            var distance = 36f + tier * 3f;
            var radius = distance * MathF.Tan(MathHelper.DegreesToRadians(2.3f));
            var ringColor = tier % 2 == 0 ? Color(78, 214, 178) : Color(255, 206, 115);
            for (var index = 0; index < targetCount; index++)
            {
                var yaw = index * 360d / targetCount + (tier % 2 == 0 ? 0d : 180d / targetCount);
                var direction = CreateCameraDirection(yaw, elevation);
                var center = CameraEye + direction * distance;
                var normal = -direction;
                var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
                var up = Vector3.Normalize(Vector3.Cross(normal, right));

                AddTargetRing(vertices, center, right, up, radius, radius * 0.75f, ringColor);
                AddTargetRing(vertices, center, right, up, radius * 0.75f, radius * 0.42f, Color(20, 29, 41));
                AddTargetRing(vertices, center, right, up, radius * 0.42f, radius * 0.18f, ringColor);
                AddTargetRing(vertices, center, right, up, radius * 0.18f, 0f, Color(236, 244, 255));
            }
        }
    }

    private static void AddTargetRing(
        List<float> vertices,
        Vector3 center,
        Vector3 right,
        Vector3 up,
        float outerRadius,
        float innerRadius,
        Vector3 color)
    {
        const int segments = 40;
        for (var index = 0; index < segments; index++)
        {
            var angle = index * MathF.Tau / segments;
            var nextAngle = (index + 1) * MathF.Tau / segments;
            var from = right * MathF.Cos(angle) + up * MathF.Sin(angle);
            var to = right * MathF.Cos(nextAngle) + up * MathF.Sin(nextAngle);
            var outerFrom = center + from * outerRadius;
            var outerTo = center + to * outerRadius;
            var innerFrom = center + from * innerRadius;
            var innerTo = center + to * innerRadius;
            AddVertex(vertices, outerFrom, color);
            AddVertex(vertices, outerTo, color);
            AddVertex(vertices, innerFrom, color);
            if (innerRadius > 0f)
            {
                AddVertex(vertices, innerFrom, color);
                AddVertex(vertices, outerTo, color);
                AddVertex(vertices, innerTo, color);
            }
        }
    }

    private static void AddBox(
        List<float> vertices,
        Vector3 center,
        float width,
        float height,
        float depth,
        Vector3 color)
    {
        var x = width / 2f;
        var y = height / 2f;
        var z = depth / 2f;
        var corners = new[]
        {
            center + new Vector3(-x, -y, -z),
            center + new Vector3(x, -y, -z),
            center + new Vector3(x, y, -z),
            center + new Vector3(-x, y, -z),
            center + new Vector3(-x, -y, z),
            center + new Vector3(x, -y, z),
            center + new Vector3(x, y, z),
            center + new Vector3(-x, y, z)
        };
        ReadOnlySpan<int> indices =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            3, 7, 6, 3, 6, 2,
            1, 2, 6, 1, 6, 5,
            0, 4, 7, 0, 7, 3
        ];
        foreach (var index in indices)
        {
            AddVertex(vertices, corners[index], color);
        }
    }

    private static void AddLine(List<float> vertices, Vector3 from, Vector3 to, Vector3 color)
    {
        AddVertex(vertices, from, color);
        AddVertex(vertices, to, color);
    }

    private static void AddVertex(List<float> vertices, Vector3 position, Vector3 color)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);
        vertices.Add(color.X);
        vertices.Add(color.Y);
        vertices.Add(color.Z);
    }

    private static Vector3 Color(byte red, byte green, byte blue) =>
        new(red / 255f, green / 255f, blue / 255f);

    private static int CreateProgram(string vertexSource, string fragmentSource)
    {
        var vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        var log = GL.GetProgramInfoLog(program);
        GL.DetachShader(program, vertex);
        GL.DetachShader(program, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        if (linked == 0)
        {
            GL.DeleteProgram(program);
            throw new InvalidOperationException($"OpenGL program link failed: {log}");
        }

        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled != 0)
        {
            return shader;
        }

        var log = GL.GetShaderInfoLog(shader);
        GL.DeleteShader(shader);
        throw new InvalidOperationException($"OpenGL {type} compilation failed: {log}");
    }

    private const string VertexShaderSource = """
        #version 410 core
        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aColor;
        uniform mat4 uViewProjection;
        out vec3 vColor;
        out float vDepth;
        void main()
        {
            vec4 clip = vec4(aPosition, 1.0) * uViewProjection;
            gl_Position = clip;
            vColor = aColor;
            vDepth = abs(clip.w);
        }
        """;

    private const string FragmentShaderSource = """
        #version 410 core
        in vec3 vColor;
        in float vDepth;
        out vec4 outputColor;
        void main()
        {
            vec3 fogColor = vec3(0.023, 0.043, 0.071);
            float fog = smoothstep(55.0, 150.0, vDepth);
            outputColor = vec4(mix(vColor, fogColor, fog), 1.0);
        }
        """;
}
