using System.Text.Json;
using System.Text.Json.Serialization;

var options = Options.Parse(args);
if (string.IsNullOrWhiteSpace(options.MeshPath))
{
    Console.Error.WriteLine("usage: MecchaMeshPlanner --mesh <mesh.lod0.json> [--last-status <last_status.json>] [--camera-dir x,y,z] [--out <plan.json>]");
    return 2;
}

var mesh = JsonSerializer.Deserialize<MeshExport>(
    File.ReadAllText(options.MeshPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
if (mesh?.Lod0?.Vertices is null || mesh.Lod0.Indices is null)
    throw new InvalidOperationException($"Invalid mesh export: {options.MeshPath}");

var frontSamples = LoadFrontSamples(options.FrontSamplesPath);
var cameraDirection = options.CameraDirection
                      ?? TryReadCameraDirection(options.LastStatusPath)
                      ?? new Vec3(0.0, 0.0, -1.0);
cameraDirection = cameraDirection.Normalized();

var plan = BuildPlan(mesh, options, cameraDirection, frontSamples);

if (!string.IsNullOrWhiteSpace(options.OutPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutPath))!);
    File.WriteAllText(options.OutPath, JsonSerializer.Serialize(plan, JsonOptions()));
}

Console.WriteLine($"mesh={mesh.Export} source={mesh.SourcePath}");
Console.WriteLine($"camera_direction={cameraDirection.X:F6},{cameraDirection.Y:F6},{cameraDirection.Z:F6}");
Console.WriteLine($"triangles total={plan.TriangleStats.Total} front={plan.TriangleStats.Front} side={plan.TriangleStats.Side} back={plan.TriangleStats.Back} degenerate={plan.TriangleStats.Degenerate}");
Console.WriteLine($"uv_area front={plan.TriangleStats.FrontUvArea:F6} side={plan.TriangleStats.SideUvArea:F6} back={plan.TriangleStats.BackUvArea:F6}");
Console.WriteLine($"front_samples={frontSamples.Length} target_samples={plan.TargetSamples.Length} out={options.OutPath}");
return 0;

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

static UvPlan BuildPlan(MeshExport mesh, Options options, Vec3 cameraDirection, FrontSample[] frontSamples)
{
    var vertices = mesh.Lod0!.Vertices!;
    var indices = mesh.Lod0.Indices!;
    var stats = new TriangleStats();
    var allBounds = new UvBounds();
    var frontBounds = new UvBounds();
    var sideBounds = new UvBounds();
    var backBounds = new UvBounds();
    var plannedTriangles = new List<PlannedTriangle>();

    for (var tri = 0; tri + 2 < indices.Length; tri += 3)
    {
        var i0 = indices[tri + 0];
        var i1 = indices[tri + 1];
        var i2 = indices[tri + 2];
        if (!IsValidVertexIndex(i0, vertices.Length) ||
            !IsValidVertexIndex(i1, vertices.Length) ||
            !IsValidVertexIndex(i2, vertices.Length))
        {
            stats.Degenerate++;
            continue;
        }

        var a = vertices[i0];
        var b = vertices[i1];
        var c = vertices[i2];
        var p0 = new Vec3(a.X, a.Y, a.Z);
        var p1 = new Vec3(b.X, b.Y, b.Z);
        var p2 = new Vec3(c.X, c.Y, c.Z);
        var normal = Vec3.Cross(p1 - p0, p2 - p0).NormalizedOrZero();
        if (normal.LengthSquared < 1e-12)
        {
            stats.Degenerate++;
            continue;
        }

        var uv0 = new Uv(a.U, a.V);
        var uv1 = new Uv(b.U, b.V);
        var uv2 = new Uv(c.U, c.V);
        var uvArea = Uv.TriangleArea(uv0, uv1, uv2);
        allBounds.Add(uv0);
        allBounds.Add(uv1);
        allBounds.Add(uv2);

        var facing = Vec3.Dot(normal, cameraDirection);
        var kind = facing <= -options.FrontThreshold
            ? SurfaceKind.Front
            : facing >= options.BackThreshold
                ? SurfaceKind.Back
                : SurfaceKind.Side;
        stats.Total++;
        switch (kind)
        {
            case SurfaceKind.Front:
                stats.Front++;
                stats.FrontUvArea += uvArea;
                frontBounds.Add(uv0);
                frontBounds.Add(uv1);
                frontBounds.Add(uv2);
                break;
            case SurfaceKind.Side:
                stats.Side++;
                stats.SideUvArea += uvArea;
                sideBounds.Add(uv0);
                sideBounds.Add(uv1);
                sideBounds.Add(uv2);
                break;
            case SurfaceKind.Back:
                stats.Back++;
                stats.BackUvArea += uvArea;
                backBounds.Add(uv0);
                backBounds.Add(uv1);
                backBounds.Add(uv2);
                break;
        }

        if (kind is SurfaceKind.Side or SurfaceKind.Back || options.IncludeFrontSamples)
        {
            var texelArea = uvArea * options.TextureSize * options.TextureSize;
            var desired = Math.Max(1, (int)Math.Ceiling(texelArea / Math.Max(1.0, options.SampleStepTexels * options.SampleStepTexels)));
            plannedTriangles.Add(new PlannedTriangle(
                tri / 3,
                kind.ToString().ToLowerInvariant(),
                facing,
                uvArea,
                Math.Min(options.MaxSamplesPerTriangle, desired),
                uv0,
                uv1,
                uv2));
        }
    }

    var targetSamples = ColorizeSamples(GenerateSamples(plannedTriangles, options.MaxSamples), frontSamples);
    return new UvPlan(
        1,
        mesh.SourcePath ?? "",
        mesh.Export ?? "",
        mesh.Skeleton,
        mesh.PhysicsAsset,
        mesh.Lod0.VertexCount,
        mesh.Lod0.IndexCount,
        mesh.Bones?.Length ?? 0,
        mesh.MaterialSlots ?? [],
        cameraDirection,
        options.TextureSize,
        options.SampleStepTexels,
        options.FrontThreshold,
        options.BackThreshold,
        stats,
        allBounds.ToSnapshot(),
        frontBounds.ToSnapshot(),
        sideBounds.ToSnapshot(),
        backBounds.ToSnapshot(),
        frontSamples.Length,
        targetSamples);
}

static bool IsValidVertexIndex(int index, int count) => index >= 0 && index < count;

static TargetSample[] GenerateSamples(List<PlannedTriangle> triangles, int maxSamples)
{
    if (triangles.Count == 0 || maxSamples <= 0)
        return [];

    var desiredTotal = triangles.Sum(t => Math.Max(1, t.DesiredSamples));
    var scale = desiredTotal > maxSamples ? (double)maxSamples / desiredTotal : 1.0;
    var samples = new List<TargetSample>(Math.Min(maxSamples, desiredTotal));

    foreach (var tri in triangles.OrderBy(t => t.Kind).ThenByDescending(t => t.UvArea))
    {
        if (samples.Count >= maxSamples)
            break;

        var count = Math.Max(1, (int)Math.Round(tri.DesiredSamples * scale));
        count = Math.Min(count, maxSamples - samples.Count);
        foreach (var sample in SampleTriangle(tri, count))
        {
            samples.Add(sample);
            if (samples.Count >= maxSamples)
                break;
        }
    }

    return samples.ToArray();
}

static IEnumerable<TargetSample> SampleTriangle(PlannedTriangle tri, int count)
{
    if (count <= 1)
    {
        yield return MakeSample(tri, 1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0);
        yield break;
    }

    var emitted = 0;
    var subdiv = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(count * 2.0)));
    for (var i = 0; i < subdiv && emitted < count; i++)
    {
        for (var j = 0; j < subdiv - i && emitted < count; j++)
        {
            var a = (i + 0.5) / subdiv;
            var b = (j + 0.5) / subdiv;
            if (a + b >= 1.0)
                continue;
            var c = 1.0 - a - b;
            yield return MakeSample(tri, a, b, c);
            emitted++;
        }
    }

    while (emitted < count)
    {
        yield return MakeSample(tri, 1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0);
        emitted++;
    }
}

static TargetSample MakeSample(PlannedTriangle tri, double a, double b, double c)
{
    var u = tri.Uv0.U * a + tri.Uv1.U * b + tri.Uv2.U * c;
    var v = tri.Uv0.V * a + tri.Uv1.V * b + tri.Uv2.V * c;
    return new TargetSample
    {
        Triangle = tri.TriangleIndex,
        Kind = tri.Kind,
        U = u,
        V = v,
        FacingDot = tri.FacingDot,
        TriangleUvArea = tri.UvArea
    };
}

static TargetSample[] ColorizeSamples(TargetSample[] targets, FrontSample[] frontSamples)
{
    if (frontSamples.Length == 0 || targets.Length == 0)
        return targets;

    var outSamples = new TargetSample[targets.Length];
    for (var i = 0; i < targets.Length; i++)
    {
        var target = targets[i];
        var bestIndex = 0;
        var bestDistance = double.PositiveInfinity;
        for (var j = 0; j < frontSamples.Length; j++)
        {
            var source = frontSamples[j];
            var du = target.U - source.U;
            var dv = target.V - source.V;
            var distance = du * du + dv * dv;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = j;
            }
        }

        var best = frontSamples[bestIndex];
        outSamples[i] = target with
        {
            R = best.R,
            G = best.G,
            B = best.B,
            Metallic = best.Metallic,
            Roughness = best.Roughness,
            SourceDistanceUv = Math.Sqrt(bestDistance),
            SourceSample = bestIndex
        };
    }

    return outSamples;
}

static FrontSample[] LoadFrontSamples(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        return [];

    var artifact = JsonSerializer.Deserialize<FrontSamplesArtifact>(
        File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    return artifact?.Samples ?? [];
}

static Vec3? TryReadCameraDirection(string? lastStatusPath)
{
    if (string.IsNullOrWhiteSpace(lastStatusPath) || !File.Exists(lastStatusPath))
        return null;

    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(lastStatusPath));
        if (TryFindNumber(doc.RootElement, "capture_direction_x", out var x) &&
            TryFindNumber(doc.RootElement, "capture_direction_y", out var y) &&
            TryFindNumber(doc.RootElement, "capture_direction_z", out var z))
            return new Vec3(x, y, z);
    }
    catch
    {
        return null;
    }

    return null;
}

static bool TryFindNumber(JsonElement element, string key, out double value)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals(key) && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out value))
                    return true;
                if (TryFindNumber(prop.Value, key, out value))
                    return true;
            }
            break;
        case JsonValueKind.Array:
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindNumber(item, key, out value))
                    return true;
            }
            break;
    }

    value = 0.0;
    return false;
}

enum SurfaceKind
{
    Front,
    Side,
    Back
}

sealed class Options
{
    public string MeshPath { get; private set; } = "";
    public string OutPath { get; private set; } = "";
    public string LastStatusPath { get; private set; } = "";
    public string FrontSamplesPath { get; private set; } = "";
    public Vec3? CameraDirection { get; private set; }
    public int TextureSize { get; private set; } = 1024;
    public double SampleStepTexels { get; private set; } = 16.0;
    public int MaxSamples { get; private set; } = 20000;
    public int MaxSamplesPerTriangle { get; private set; } = 64;
    public double FrontThreshold { get; private set; } = 0.35;
    public double BackThreshold { get; private set; } = 0.35;
    public bool IncludeFrontSamples { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mesh" when i + 1 < args.Length:
                    options.MeshPath = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    options.OutPath = args[++i];
                    break;
                case "--last-status" when i + 1 < args.Length:
                    options.LastStatusPath = args[++i];
                    break;
                case "--front-samples" when i + 1 < args.Length:
                    options.FrontSamplesPath = args[++i];
                    break;
                case "--camera-dir" when i + 1 < args.Length:
                    options.CameraDirection = Vec3.ParseCsv(args[++i]);
                    break;
                case "--texture-size" when i + 1 < args.Length && int.TryParse(args[i + 1], out var textureSize):
                    options.TextureSize = Math.Max(1, textureSize);
                    i++;
                    break;
                case "--sample-step-texels" when i + 1 < args.Length && double.TryParse(args[i + 1], out var sampleStep):
                    options.SampleStepTexels = Math.Max(1.0, sampleStep);
                    i++;
                    break;
                case "--max-samples" when i + 1 < args.Length && int.TryParse(args[i + 1], out var maxSamples):
                    options.MaxSamples = Math.Max(0, maxSamples);
                    i++;
                    break;
                case "--front-threshold" when i + 1 < args.Length && double.TryParse(args[i + 1], out var frontThreshold):
                    options.FrontThreshold = Math.Clamp(frontThreshold, 0.0, 1.0);
                    i++;
                    break;
                case "--back-threshold" when i + 1 < args.Length && double.TryParse(args[i + 1], out var backThreshold):
                    options.BackThreshold = Math.Clamp(backThreshold, 0.0, 1.0);
                    i++;
                    break;
                case "--include-front-samples":
                    options.IncludeFrontSamples = true;
                    break;
            }
        }

        return options;
    }
}

sealed class MeshExport
{
    public string? SourcePath { get; set; }
    public string? Export { get; set; }
    public string? Skeleton { get; set; }
    public string? PhysicsAsset { get; set; }
    public string[]? MaterialSlots { get; set; }
    public MeshBone[]? Bones { get; set; }
    public MeshLod? Lod0 { get; set; }
}

sealed class MeshLod
{
    public int VertexCount { get; set; }
    public int IndexCount { get; set; }
    public int[]? Indices { get; set; }
    public MeshVertex[]? Vertices { get; set; }
}

sealed class MeshVertex
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double U { get; set; }
    public double V { get; set; }
}

sealed class MeshBone
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int ParentIndex { get; set; }
}

sealed class FrontSamplesArtifact
{
    public FrontSample[]? Samples { get; set; }
}

sealed class FrontSample
{
    public double U { get; set; }
    public double V { get; set; }
    public double R { get; set; }
    public double G { get; set; }
    public double B { get; set; }
    public double Metallic { get; set; }
    public double Roughness { get; set; }
}

sealed class UvBounds
{
    public bool HasValue { get; private set; }
    public double MinU { get; private set; } = double.PositiveInfinity;
    public double MinV { get; private set; } = double.PositiveInfinity;
    public double MaxU { get; private set; } = double.NegativeInfinity;
    public double MaxV { get; private set; } = double.NegativeInfinity;

    public void Add(Uv uv)
    {
        HasValue = true;
        MinU = Math.Min(MinU, uv.U);
        MinV = Math.Min(MinV, uv.V);
        MaxU = Math.Max(MaxU, uv.U);
        MaxV = Math.Max(MaxV, uv.V);
    }

    public UvBoundsSnapshot ToSnapshot() => HasValue
        ? new UvBoundsSnapshot(MinU, MinV, MaxU, MaxV)
        : new UvBoundsSnapshot(0.0, 0.0, 0.0, 0.0);
}

readonly record struct Vec3(double X, double Y, double Z)
{
    [JsonIgnore]
    public double LengthSquared => X * X + Y * Y + Z * Z;
    public Vec3 Normalized() => this / Math.Sqrt(Math.Max(LengthSquared, 1e-24));
    public Vec3 NormalizedOrZero() => LengthSquared <= 1e-24 ? new Vec3(0.0, 0.0, 0.0) : Normalized();
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator /(Vec3 a, double value) => new(a.X / value, a.Y / value, a.Z / value);
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static Vec3 ParseCsv(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], out var x) ||
            !double.TryParse(parts[1], out var y) ||
            !double.TryParse(parts[2], out var z))
            throw new ArgumentException($"Invalid --camera-dir value: {text}");
        return new Vec3(x, y, z);
    }
}

readonly record struct Uv(double U, double V)
{
    public static double TriangleArea(Uv a, Uv b, Uv c) =>
        Math.Abs((b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U)) * 0.5;
}

sealed record PlannedTriangle(
    int TriangleIndex,
    string Kind,
    double FacingDot,
    double UvArea,
    int DesiredSamples,
    Uv Uv0,
    Uv Uv1,
    Uv Uv2);

sealed record UvBoundsSnapshot(double MinU, double MinV, double MaxU, double MaxV);

sealed record TriangleStats
{
    public int Total { get; set; }
    public int Front { get; set; }
    public int Side { get; set; }
    public int Back { get; set; }
    public int Degenerate { get; set; }
    public double FrontUvArea { get; set; }
    public double SideUvArea { get; set; }
    public double BackUvArea { get; set; }
}

sealed record TargetSample
{
    public required int Triangle { get; init; }
    public required string Kind { get; init; }
    public required double U { get; init; }
    public required double V { get; init; }
    public required double FacingDot { get; init; }
    public required double TriangleUvArea { get; init; }
    public double? R { get; init; }
    public double? G { get; init; }
    public double? B { get; init; }
    public double? Metallic { get; init; }
    public double? Roughness { get; init; }
    public double? SourceDistanceUv { get; init; }
    public int? SourceSample { get; init; }
}

sealed record UvPlan(
    int SchemaVersion,
    string SourcePath,
    string Export,
    string? Skeleton,
    string? PhysicsAsset,
    int VertexCount,
    int IndexCount,
    int BoneCount,
    string[] MaterialSlots,
    Vec3 CameraDirection,
    int TextureSize,
    double SampleStepTexels,
    double FrontThreshold,
    double BackThreshold,
    TriangleStats TriangleStats,
    UvBoundsSnapshot AllUvBounds,
    UvBoundsSnapshot FrontUvBounds,
    UvBoundsSnapshot SideUvBounds,
    UvBoundsSnapshot BackUvBounds,
    int FrontSampleCount,
    TargetSample[] TargetSamples);
