using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using AssetObject = CUE4Parse.UE4.Assets.Exports.UObject;

static string DefaultPaksPath()
{
    return OperatingSystem.IsWindows()
        ? @"C:\Program Files (x86)\Steam\steamapps\common\MECCHA CHAMELEON\Chameleon\Content\Paks"
        : "/mnt/c/Program Files (x86)/Steam/steamapps/common/MECCHA CHAMELEON/Chameleon/Content/Paks";
}

static string DefaultExePath()
{
    return OperatingSystem.IsWindows()
        ? @"C:\Program Files (x86)\Steam\steamapps\common\MECCHA CHAMELEON\Chameleon\Binaries\Win64\PenguinHotel-Win64-Shipping.exe"
        : "/mnt/c/Program Files (x86)/Steam/steamapps/common/MECCHA CHAMELEON/Chameleon/Binaries/Win64/PenguinHotel-Win64-Shipping.exe";
}

static string DefaultProfilePath()
{
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".build", "research", "profiles", "asset-probe-latest.json"));
}

static string DefaultMeshExportDir()
{
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".build", "research", "mesh_exports"));
}

static IEnumerable<EGame> CandidateVersions(string? requested)
{
    if (!string.IsNullOrWhiteSpace(requested))
    {
        if (!Enum.TryParse<EGame>(requested, true, out var parsed))
            throw new ArgumentException($"Unknown CUE4Parse game version: {requested}", nameof(requested));
        yield return parsed;
        yield break;
    }

    yield return EGame.GAME_UE5_3;
    yield return EGame.GAME_UE5_2;
    yield return EGame.GAME_UE5_1;
    yield return EGame.GAME_UE5_0;
    yield return EGame.GAME_UE4_27;
    yield return EGame.GAME_UE5_4;
    yield return EGame.GAME_UE5_5;
    yield return EGame.GAME_UE5_6;
}

static Options ParseArgs(string[] args)
{
    var options = new Options
    {
        PaksPath = DefaultPaksPath(),
        ExePath = DefaultExePath(),
        PackageLimit = 3000,
        ProfileOut = DefaultProfilePath(),
        MeshOutDir = DefaultMeshExportDir()
    };

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        string Next()
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {arg}");
            return args[++i];
        }

        switch (arg)
        {
            case "--paks":
                options.PaksPath = Next();
                break;
            case "--exe":
                options.ExePath = Next();
                break;
            case "--limit":
                options.PackageLimit = Math.Max(1, int.Parse(Next()));
                break;
            case "--usmap":
                options.UsmapPath = Next();
                break;
            case "--game":
                options.GameVersion = Next();
                break;
            case "--profile-out":
                options.ProfileOut = Next();
                break;
            case "--mesh-out-dir":
                options.MeshOutDir = Next();
                break;
            case "--export-top-skeletal":
                options.ExportTopSkeletal = Math.Max(0, int.Parse(Next()));
                break;
            default:
                if (options.PositionalPaksConsumed)
                    throw new ArgumentException($"Unknown argument: {arg}");
                options.PaksPath = arg;
                options.PositionalPaksConsumed = true;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var legacyLimit))
                {
                    options.PackageLimit = Math.Max(1, legacyLimit);
                    i++;
                }
                break;
        }
    }

    return options;
}

static int PackageInterestScore(string path)
{
    var lower = path.ToLowerInvariant();
    var score = 0;
    foreach (var (term, weight) in new (string Term, int Weight)[]
    {
        ("player", 120),
        ("character", 100),
        ("body", 90),
        ("skeletal", 80),
        ("skel", 60),
        ("mesh", 30),
        ("pawn", 60),
        ("avatar", 60),
        ("cleon", 100),
        ("paintman", 100),
        ("newpenguin", 90),
        ("penguin", 80),
        ("pengun", 80),
        ("cosmetic", 50),
        ("outfit", 50),
        ("skin", 40),
        ("clothes", 40),
        ("cloth", 35),
        ("head", 35),
        ("face", 35),
        ("torso", 35),
        ("arm", 25),
        ("hand", 25),
        ("leg", 25),
        ("foot", 25),
        ("chameleon", 20),
        ("penguin", 20)
    })
    {
        if (lower.Contains(term))
            score += weight;
    }

    foreach (var (term, weight) in new (string Term, int Weight)[]
    {
        ("camera", -100),
        ("ui", -60),
        ("icon", -60),
        ("preview", -30),
        ("logo", -30)
    })
    {
        if (lower.Contains(term))
            score += weight;
    }

    return score;
}

static (int Score, string[] Reasons) ScoreMeshCandidate(
    string kind,
    string path,
    string exportName,
    IEnumerable<string> materialNames,
    IEnumerable<string> boneNames,
    int boneCount,
    int vertexCount)
{
    var score = PackageInterestScore(path);
    var reasons = new List<string>();
    var haystack = $"{path} {exportName}".ToLowerInvariant();

    if (kind == "SkeletalMesh")
    {
        score += 200;
        reasons.Add("skeletal-mesh");
    }

    foreach (var (term, weight) in new (string Term, int Weight)[]
    {
        ("player", 140),
        ("character", 120),
        ("body", 100),
        ("pawn", 80),
        ("avatar", 80),
        ("cleon", 160),
        ("paintman", 160),
        ("newpenguin", 140),
        ("penguin", 130),
        ("pengun", 130),
        ("sk_link", 80),
        ("cosmetic", 70),
        ("outfit", 70),
        ("skin", 55),
        ("head", 50),
        ("face", 50),
        ("torso", 50),
        ("arm", 35),
        ("hand", 35),
        ("leg", 35),
        ("foot", 35)
    })
    {
        if (haystack.Contains(term))
        {
            score += weight;
            reasons.Add($"path:{term}");
        }
    }

    var materialText = string.Join(' ', materialNames).ToLowerInvariant();
    foreach (var term in new[] { "skin", "body", "face", "head", "eye", "cloth", "fur", "karada", "chameleon", "cleon", "paintman", "penguin", "pengun" })
    {
        if (materialText.Contains(term))
        {
            score += 20;
            reasons.Add($"material:{term}");
        }
    }

    var anatomyBones = 0;
    var boneText = string.Join(' ', boneNames).ToLowerInvariant();
    foreach (var term in new[] { "root", "pelvis", "spine", "neck", "head", "clavicle", "upperarm", "lowerarm", "hand", "thigh", "calf", "foot", "toe", "tail" })
    {
        if (boneText.Contains(term))
            anatomyBones++;
    }

    if (anatomyBones >= 5)
    {
        score += 90;
        reasons.Add($"anatomy-bones:{anatomyBones}");
    }
    else if (anatomyBones > 0)
    {
        score += anatomyBones * 10;
        reasons.Add($"bone-hints:{anatomyBones}");
    }

    if (boneCount >= 20)
    {
        score += 40;
        reasons.Add($"bone-count:{boneCount}");
    }

    if (vertexCount >= 1000)
    {
        score += 20;
        reasons.Add($"verts:{vertexCount}");
    }

    return (score, reasons.Distinct().Take(12).ToArray());
}

static string? SafeResolvedPath(FPackageIndex? packageIndex)
{
    if (packageIndex is null || packageIndex.IsNull)
        return null;

    try
    {
        return packageIndex.ResolvedObject?.GetPathName() ?? packageIndex.Name;
    }
    catch (Exception ex)
    {
        return $"<resolve-failed:{ex.GetType().Name}:index={packageIndex.Index}>";
    }
}

static string? Sha256File(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        return null;

    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static (int propertyCount, int withSuperCount, string[] sample) DescribeMappings(DefaultFileProvider provider, string typeName)
{
    if (provider.MappingsForGame?.Types.TryGetValue(typeName, out var mappings) != true || mappings is null)
        return (0, 0, []);

    var properties = mappings.Properties;
    if (properties is null)
        return (mappings.PropertyCount, mappings.CountProperties(true), []);

    var sample = properties
        .OrderBy(kvp => kvp.Key)
        .Take(12)
        .Select(kvp => $"{kvp.Key}:{kvp.Value.MappingType.Type} {kvp.Value.Name}")
        .ToArray();
    return (mappings.PropertyCount, mappings.CountProperties(true), sample);
}

static string[] DescribeReadProperties(AssetObject export)
{
    return export.Properties
        .Take(12)
        .Select(prop => $"{prop.Name.Text}:{prop.PropertyType.Text}:{prop.ArrayIndex}")
        .ToArray();
}

static ConvertedMesh ProbeStaticMesh(DefaultFileProvider provider, string path, UStaticMesh staticMesh, EPackageFlags packageFlags, bool isCookedPackage, bool isFilterEditorOnlyPackage)
{
    var (mappingProperties, mappingPropertiesWithSuper, sampleMappingProperties) = DescribeMappings(provider, staticMesh.ExportType);
    var sampleReadProperties = DescribeReadProperties(staticMesh);
    var ok = staticMesh.TryConvert(out var converted);
    var renderLods = staticMesh.RenderData?.LODs?.Length ?? 0;
    var renderBounds = staticMesh.RenderData?.Bounds is not null;
    var nanite = staticMesh.RenderData?.NaniteResources;
    var nanitePages = nanite?.PageStreamingStates.Length ?? 0;
    var naniteTriangles = nanite?.NumInputTriangles ?? 0;
    var lods = ok && converted is not null ? converted.LODs.Count : 0;
    var verts = ok && converted is not null && lods > 0 ? converted.LODs[0].NumVerts : 0;
    var indices = ok && converted is not null && lods > 0 ? converted.LODs[0].Indices?.Value.Length ?? 0 : 0;
    var uvChannels = ok && converted is not null && lods > 0 ? converted.LODs[0].NumTexCoords : (int?) null;
    var sections = ok && converted is not null && lods > 0 ? converted.LODs[0].Sections?.Value.Length ?? 0 : (int?) null;
    var materialSlots = staticMesh.StaticMaterials?.Length ?? staticMesh.Materials?.Length ?? 0;
    var materialNames = staticMesh.StaticMaterials?
        .Select(m => m.MaterialSlotName.Text)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Take(12)
        .ToArray() ?? [];
    var (candidateScore, candidateReasons) = ScoreMeshCandidate("StaticMesh", path, staticMesh.Name, materialNames, [], 0, verts);

    return new ConvertedMesh
    {
        Kind = "StaticMesh",
        Path = path,
        Export = staticMesh.Name,
        Converted = ok,
        Lods = lods,
        VerticesLod0 = verts,
        IndicesLod0 = indices,
        Bones = null,
        UvChannelsLod0 = uvChannels,
        SectionsLod0 = sections,
        MaterialSlots = materialSlots,
        SampleMaterialSlots = materialNames,
        SampleBones = [],
        Skeleton = null,
        PhysicsAsset = null,
        MorphTargets = null,
        Sockets = staticMesh.Sockets?.Length,
        AssetCooked = staticMesh.bCooked,
        PackageFlags = packageFlags.ToString(),
        PackageCooked = isCookedPackage,
        PackageFilterEditorOnly = isFilterEditorOnlyPackage,
        HasRenderData = staticMesh.RenderData is not null,
        HasRenderBounds = renderBounds,
        RenderLods = renderLods,
        HasNanite = nanite is not null,
        NanitePages = nanitePages,
        NaniteTriangles = naniteTriangles,
        BoundsOrigin = staticMesh.RenderData?.Bounds?.Origin.ToString(),
        BoundsExtent = staticMesh.RenderData?.Bounds?.BoxExtent.ToString(),
        BoundsRadius = staticMesh.RenderData?.Bounds?.SphereRadius,
        CandidateScore = candidateScore,
        CandidateReasons = candidateReasons,
        MappingProperties = mappingProperties,
        MappingPropertiesWithSuper = mappingPropertiesWithSuper,
        SampleMappingProperties = sampleMappingProperties,
        ReadProperties = staticMesh.Properties.Count,
        SampleReadProperties = sampleReadProperties
    };
}

static ConvertedMesh ProbeSkeletalMesh(DefaultFileProvider provider, string path, USkeletalMesh skeletalMesh, EPackageFlags packageFlags, bool isCookedPackage, bool isFilterEditorOnlyPackage)
{
    var (mappingProperties, mappingPropertiesWithSuper, sampleMappingProperties) = DescribeMappings(provider, skeletalMesh.ExportType);
    var sampleReadProperties = DescribeReadProperties(skeletalMesh);
    var ok = skeletalMesh.TryConvert(out var converted);
    var lods = ok && converted is not null ? converted.LODs.Count : 0;
    var verts = ok && converted is not null && lods > 0 ? converted.LODs[0].NumVerts : 0;
    var indices = ok && converted is not null && lods > 0 ? converted.LODs[0].Indices?.Value.Length ?? 0 : 0;
    var bones = ok && converted is not null
        ? converted.RefSkeleton.Count
        : skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo.Length;
    var uvChannels = ok && converted is not null && lods > 0
        ? converted.LODs[0].NumTexCoords
        : skeletalMesh.LODModels is { Length: > 0 } ? skeletalMesh.LODModels[0].NumTexCoords : (int?) null;
    var sections = ok && converted is not null && lods > 0
        ? converted.LODs[0].Sections?.Value.Length ?? 0
        : skeletalMesh.LODModels is { Length: > 0 } ? skeletalMesh.LODModels[0].Sections.Length : (int?) null;
    var modelLods = skeletalMesh.LODModels?.Length ?? 0;
    var materialNames = skeletalMesh.SkeletalMaterials?
        .Select(m => m.MaterialSlotName.Text)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Take(16)
        .ToArray() ?? [];
    var boneNames = skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo
        .Select(bone => bone.Name.Text)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Take(32)
        .ToArray();
    var (candidateScore, candidateReasons) = ScoreMeshCandidate("SkeletalMesh", path, skeletalMesh.Name, materialNames, boneNames, bones, verts);

    return new ConvertedMesh
    {
        Kind = "SkeletalMesh",
        Path = path,
        Export = skeletalMesh.Name,
        Converted = ok,
        Lods = lods,
        VerticesLod0 = verts,
        IndicesLod0 = indices,
        Bones = bones,
        UvChannelsLod0 = uvChannels,
        SectionsLod0 = sections,
        MaterialSlots = skeletalMesh.SkeletalMaterials?.Length ?? skeletalMesh.Materials?.Length ?? 0,
        SampleMaterialSlots = materialNames,
        SampleBones = boneNames,
        Skeleton = SafeResolvedPath(skeletalMesh.Skeleton),
        PhysicsAsset = SafeResolvedPath(skeletalMesh.PhysicsAsset),
        MorphTargets = skeletalMesh.MorphTargets?.Length,
        Sockets = skeletalMesh.Sockets?.Length,
        AssetCooked = null,
        PackageFlags = packageFlags.ToString(),
        PackageCooked = isCookedPackage,
        PackageFilterEditorOnly = isFilterEditorOnlyPackage,
        HasRenderData = modelLods > 0,
        HasRenderBounds = null,
        RenderLods = modelLods,
        HasNanite = skeletalMesh.NaniteResources is not null,
        NanitePages = skeletalMesh.NaniteResources?.PageStreamingStates.Length ?? 0,
        NaniteTriangles = skeletalMesh.NaniteResources?.NumInputTriangles ?? 0,
        BoundsOrigin = skeletalMesh.ImportedBounds.Origin.ToString(),
        BoundsExtent = skeletalMesh.ImportedBounds.BoxExtent.ToString(),
        BoundsRadius = skeletalMesh.ImportedBounds.SphereRadius,
        CandidateScore = candidateScore,
        CandidateReasons = candidateReasons,
        MappingProperties = mappingProperties,
        MappingPropertiesWithSuper = mappingPropertiesWithSuper,
        SampleMappingProperties = sampleMappingProperties,
        ReadProperties = skeletalMesh.Properties.Count,
        SampleReadProperties = sampleReadProperties
    };
}

static string SafeFileStem(string value)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    var chars = value.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch).ToArray();
    var stem = new string(chars).Trim('_');
    return string.IsNullOrWhiteSpace(stem) ? "mesh" : stem;
}

static SkeletalMeshGeometryExport BuildSkeletalMeshGeometryExport(ConvertedMesh candidate, USkeletalMesh skeletalMesh)
{
    if (!skeletalMesh.TryConvert(out var converted) || converted is null)
        throw new InvalidOperationException($"USkeletalMesh conversion failed for {candidate.Path}#{candidate.Export}");
    if (converted.LODs.Count == 0 || converted.LODs[0].Verts is null)
        throw new InvalidOperationException($"USkeletalMesh has no converted LOD0 vertices for {candidate.Path}#{candidate.Export}");

    var lod = converted.LODs[0];
    var lodVertices = lod.Verts ?? throw new InvalidOperationException($"USkeletalMesh has no converted LOD0 vertices for {candidate.Path}#{candidate.Export}");
    var indices = lod.Indices?.Value ?? [];
    var vertices = lodVertices.Select(vertex => new MeshExportVertex(
        vertex.Position.X,
        vertex.Position.Y,
        vertex.Position.Z,
        vertex.Normal.X,
        vertex.Normal.Y,
        vertex.Normal.Z,
        vertex.UV.U,
        vertex.UV.V,
        vertex.Influences
            .Select(influence => new MeshExportInfluence(influence.Bone, influence.RawWeight, influence.Weight))
            .ToArray())).ToArray();
    var extraUvChannels = lod.ExtraUV?.Value
        .Select(channel => channel.Select(uv => new MeshExportUv(uv.U, uv.V)).ToArray())
        .ToArray() ?? [];
    var bones = converted.RefSkeleton
        .Select((bone, index) => new MeshExportBone(
            index,
            bone.Name.Text,
            bone.ParentIndex,
            bone.Position.X,
            bone.Position.Y,
            bone.Position.Z,
            bone.Orientation.X,
            bone.Orientation.Y,
            bone.Orientation.Z,
            bone.Orientation.W))
        .ToArray();

    return new SkeletalMeshGeometryExport
    {
        SourcePath = candidate.Path,
        Export = candidate.Export,
        CandidateScore = candidate.CandidateScore,
        CandidateReasons = candidate.CandidateReasons,
        Skeleton = candidate.Skeleton,
        PhysicsAsset = candidate.PhysicsAsset,
        BoundsOrigin = candidate.BoundsOrigin,
        BoundsExtent = candidate.BoundsExtent,
        BoundsRadius = candidate.BoundsRadius,
        MaterialSlots = candidate.SampleMaterialSlots,
        Bones = bones,
        Lod0 = new MeshExportLod(
            lod.LODIndex,
            lod.NumTexCoords,
            vertices.Length,
            indices.Length,
            lod.Sections?.Value.Length ?? 0,
            indices,
            vertices,
            extraUvChannels)
    };
}

static void ExportTopSkeletalMeshes(DefaultFileProvider provider, MeshProbeSummary summary, Options options)
{
    if (options.ExportTopSkeletal <= 0)
        return;

    Directory.CreateDirectory(options.MeshOutDir);
    foreach (var candidate in summary.TopSkeletalCandidates.Take(options.ExportTopSkeletal))
    {
        try
        {
            var file = provider.Files.Values.FirstOrDefault(f => string.Equals(f.Path, candidate.Path, StringComparison.OrdinalIgnoreCase));
            if (file is null)
                throw new FileNotFoundException("candidate package is not mounted", candidate.Path);

            var package = provider.LoadPackage(file);
            var skeletalMesh = package.GetExports()
                .OfType<USkeletalMesh>()
                .FirstOrDefault(mesh => string.Equals(mesh.Name, candidate.Export, StringComparison.OrdinalIgnoreCase));
            if (skeletalMesh is null)
                throw new InvalidOperationException($"candidate export not found: {candidate.Export}");

            var export = BuildSkeletalMeshGeometryExport(candidate, skeletalMesh);
            var filename = $"{SafeFileStem(candidate.Export)}-{SafeFileStem(candidate.Path)}.lod0.json";
            var outPath = Path.Combine(options.MeshOutDir, filename);
            File.WriteAllText(outPath, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
            summary.ExportedSkeletalMeshes.Add(new MeshExportSummary(
                outPath,
                candidate.Path,
                candidate.Export,
                export.Lod0.VertexCount,
                export.Lod0.IndexCount,
                export.Lod0.NumTexCoords,
                export.Bones.Length));
            Console.WriteLine($"exported-skeletal path={outPath} source={candidate.Path} export={candidate.Export} verts={export.Lod0.VertexCount} indices={export.Lod0.IndexCount}");
        }
        catch (Exception ex)
        {
            summary.ExportFailures.Add(new ProbeFailure($"{candidate.Path}#{candidate.Export}", ex.GetType().Name, ex.Message));
            Console.WriteLine($"export-skeletal-failed path={candidate.Path} export={candidate.Export} type={ex.GetType().Name} message={ex.Message}");
        }
    }
}

static MeshProbeSummary TryFindMeshExports(DefaultFileProvider provider, int packageLimit)
{
    var summary = new MeshProbeSummary();

    var candidates = provider.Files.Values
        .Where(f => f.IsUePackage && f.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(f => PackageInterestScore(f.Path))
        .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
        .Take(packageLimit)
        .ToList();

    summary.CandidatePackages = candidates.Count;
    foreach (var candidate in candidates.Take(12))
        summary.SampleCandidates.Add(candidate.Path);

    Console.WriteLine($"candidate packages: {candidates.Count}");
    foreach (var candidate in candidates.Take(12))
        Console.WriteLine($"candidate path={candidate.Path} type={candidate.GetType().Name}");

    foreach (var file in candidates)
    {
        try
        {
            var package = provider.LoadPackage(file);
            var packageFlags = package.Summary.PackageFlags;
            var isCookedPackage = package.HasFlags(EPackageFlags.PKG_Cooked);
            var isFilterEditorOnlyPackage = package.HasFlags(EPackageFlags.PKG_FilterEditorOnly);

            summary.PackagesScanned++;
            foreach (var export in package.GetExports())
            {
                if (export is not UStaticMesh && export is not USkeletalMesh)
                    continue;

                summary.MeshExports++;
                if (export is UStaticMesh)
                    summary.StaticMeshExports++;
                if (export is USkeletalMesh)
                    summary.SkeletalMeshExports++;

                try
                {
                    if (export is UStaticMesh staticMesh)
                    {
                        var mesh = ProbeStaticMesh(provider, file.Path, staticMesh, packageFlags, isCookedPackage, isFilterEditorOnlyPackage);
                        if (mesh.Converted)
                            summary.ConvertedStaticMeshes++;

                        var recordStaticMesh = summary.RecordedStaticMeshes < 8;
                        if (recordStaticMesh)
                        {
                            summary.ConvertedMeshes.Add(mesh);
                            summary.RecordedStaticMeshes++;
                        }

                        if (recordStaticMesh)
                        {
                            Console.WriteLine($"mesh {summary.MeshExports}: StaticMesh path={file.Path} export={export.Name}");
                            Console.WriteLine(
                                $"  static convert={mesh.Converted} lods={mesh.Lods} verts0={mesh.VerticesLod0} indices0={mesh.IndicesLod0} " +
                                $"uvs0={mesh.UvChannelsLod0} sections0={mesh.SectionsLod0} materials={mesh.MaterialSlots} " +
                                $"bCooked={mesh.AssetCooked} packageFlags={mesh.PackageFlags} " +
                                $"pkgCooked={mesh.PackageCooked} pkgFilterEditorOnly={mesh.PackageFilterEditorOnly} " +
                                $"renderData={mesh.HasRenderData} renderBounds={mesh.HasRenderBounds} " +
                                $"renderLods={mesh.RenderLods} nanite={mesh.HasNanite} nanitePages={mesh.NanitePages} naniteTris={mesh.NaniteTriangles} " +
                                $"mappingProps={mesh.MappingProperties} mappingPropsWithSuper={mesh.MappingPropertiesWithSuper} readProps={mesh.ReadProperties}");
                            Console.WriteLine($"  mapping sample={string.Join(", ", mesh.SampleMappingProperties)}");
                            Console.WriteLine($"  read sample={string.Join(", ", mesh.SampleReadProperties)}");
                        }
                    }
                    else if (export is USkeletalMesh skeletalMesh)
                    {
                        var mesh = ProbeSkeletalMesh(provider, file.Path, skeletalMesh, packageFlags, isCookedPackage, isFilterEditorOnlyPackage);
                        if (mesh.Converted)
                            summary.ConvertedSkeletalMeshes++;
                        else
                            summary.SkeletalConversionFailures++;

                        summary.ConvertedMeshes.Add(mesh);
                        Console.WriteLine($"mesh {summary.MeshExports}: SkeletalMesh path={file.Path} export={export.Name}");
                        Console.WriteLine(
                            $"  skeletal convert={mesh.Converted} score={mesh.CandidateScore} reasons={string.Join("|", mesh.CandidateReasons)} " +
                            $"lods={mesh.Lods} verts0={mesh.VerticesLod0} indices0={mesh.IndicesLod0} bones={mesh.Bones} " +
                            $"uvs0={mesh.UvChannelsLod0} sections0={mesh.SectionsLod0} materials={mesh.MaterialSlots} " +
                            $"skeleton={mesh.Skeleton ?? "(none)"} physics={mesh.PhysicsAsset ?? "(none)"} morphs={mesh.MorphTargets} sockets={mesh.Sockets} " +
                            $"packageFlags={mesh.PackageFlags} pkgCooked={mesh.PackageCooked} pkgFilterEditorOnly={mesh.PackageFilterEditorOnly} " +
                            $"lodModels={mesh.RenderLods} nanite={mesh.HasNanite} " +
                            $"mappingProps={mesh.MappingProperties} mappingPropsWithSuper={mesh.MappingPropertiesWithSuper} readProps={mesh.ReadProperties}");
                        Console.WriteLine($"  material sample={string.Join(", ", mesh.SampleMaterialSlots)}");
                        Console.WriteLine($"  bone sample={string.Join(", ", mesh.SampleBones.Take(12))}");
                        Console.WriteLine($"  mapping sample={string.Join(", ", mesh.SampleMappingProperties)}");
                        Console.WriteLine($"  read sample={string.Join(", ", mesh.SampleReadProperties)}");
                    }
                }
                catch (Exception ex)
                {
                    summary.ConversionFailures.Add(new ProbeFailure($"{file.Path}#{export.Name}", ex.GetType().Name, ex.Message));
                    Console.WriteLine($"  mesh-convert-failed type={ex.GetType().Name} message={ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            if (summary.LoadFailures.Count < 20)
                summary.LoadFailures.Add(new ProbeFailure(file.Path, ex.GetType().Name, ex.Message));
            if (summary.FirstFailure is null)
            {
                summary.FirstFailure = new ProbeFailure(file.Path, ex.GetType().Name, ex.Message);
                Console.WriteLine($"first-load-failed path={file.Path} type={ex.GetType().Name} message={ex.Message}");
            }
        }
    }

    foreach (var candidate in summary.ConvertedMeshes
                 .Where(mesh => mesh.Kind == "SkeletalMesh")
                 .OrderByDescending(mesh => mesh.CandidateScore)
                 .ThenByDescending(mesh => mesh.VerticesLod0)
                 .Take(25))
    {
        summary.TopSkeletalCandidates.Add(candidate);
    }

    Console.WriteLine($"staticMeshes={summary.StaticMeshExports} skeletalMeshes={summary.SkeletalMeshExports} convertedStatic={summary.ConvertedStaticMeshes} convertedSkeletal={summary.ConvertedSkeletalMeshes}");
    foreach (var candidate in summary.TopSkeletalCandidates.Take(10))
    {
        Console.WriteLine(
            $"top-skeletal score={candidate.CandidateScore} path={candidate.Path} export={candidate.Export} " +
            $"verts0={candidate.VerticesLod0} bones={candidate.Bones} materials={candidate.MaterialSlots} reasons={string.Join("|", candidate.CandidateReasons)}");
    }

    return summary;
}

var options = ParseArgs(args);
Console.WriteLine($"paks={options.PaksPath}");
Console.WriteLine($"exe={options.ExePath}");
Console.WriteLine($"packageLimit={options.PackageLimit}");
Console.WriteLine($"usmap={options.UsmapPath ?? "(none)"}");
Console.WriteLine($"profileOut={options.ProfileOut}");
Console.WriteLine($"meshOutDir={options.MeshOutDir}");
Console.WriteLine($"exportTopSkeletal={options.ExportTopSkeletal}");

ZlibHelper.Initialize();
OodleHelper.Initialize();

var profile = new ProbeProfile
{
    TimestampUtc = DateTimeOffset.UtcNow,
    PaksPath = options.PaksPath,
    ExePath = options.ExePath,
    ExeSha256 = Sha256File(options.ExePath),
    UsmapPath = options.UsmapPath,
    UsmapSha256 = Sha256File(options.UsmapPath),
    PackageLimit = options.PackageLimit
};

var exitCode = 2;

foreach (var game in CandidateVersions(options.GameVersion))
{
    Console.WriteLine();
    Console.WriteLine($"== {game} ==");
    var result = new VersionProbeResult { Game = game.ToString() };
    profile.Results.Add(result);

    try
    {
        var version = new VersionContainer(game, ETexturePlatform.DesktopMobile);
        var provider = new DefaultFileProvider(options.PaksPath, SearchOption.TopDirectoryOnly, version, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.UsmapPath))
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(options.UsmapPath, StringComparer.OrdinalIgnoreCase);

        provider.Initialize();
        result.RegisteredArchives = provider.UnloadedVfs.Count + provider.MountedVfs.Count;
        result.FilesBeforeMount = provider.Files.Count;
        result.RequiredKeysBefore = provider.RequiredKeys.Count;
        Console.WriteLine($"registered archives={result.RegisteredArchives} files-before-mount={result.FilesBeforeMount}");
        Console.WriteLine($"required-keys-before={result.RequiredKeysBefore}");

        result.Mounted = provider.Mount();
        result.MountedTotal = provider.MountedVfs.Count;
        result.Unloaded = provider.UnloadedVfs.Count;
        result.FilesAfterMount = provider.Files.Count;
        Console.WriteLine($"mounted={result.Mounted} mounted-total={result.MountedTotal} unloaded={result.Unloaded} files-after-mount={result.FilesAfterMount}");

        try
        {
            provider.PostMount();
        }
        catch (Exception postMountEx)
        {
            result.PostMountWarning = new ProbeError(postMountEx.GetType().Name, postMountEx.Message);
            Console.WriteLine($"postmount-warning type={postMountEx.GetType().Name} message={postMountEx.Message}");
        }

        result.RequiredKeysAfter = provider.RequiredKeys.Count;
        result.Files = provider.Files.Count;
        Console.WriteLine($"required-keys-after={result.RequiredKeysAfter} files={result.Files}");

        foreach (var key in provider.RequiredKeys.Take(8))
            result.RequiredKeyGuids.Add(key.ToString());

        if (provider.Files.Count == 0 || provider.RequiredKeys.Count > 0)
            continue;

        result.MeshSummary = TryFindMeshExports(provider, options.PackageLimit);
        ExportTopSkeletalMeshes(provider, result.MeshSummary, options);
        result.Ok = result.MeshSummary.MeshExports > 0;
        Console.WriteLine($"scan packages={result.MeshSummary.PackagesScanned} meshExports={result.MeshSummary.MeshExports} ok={result.Ok}");
        if (result.Ok)
        {
            exitCode = 0;
            break;
        }
    }
    catch (Exception ex)
    {
        result.FatalError = new ProbeError(ex.GetType().Name, ex.Message);
        Console.WriteLine($"version-failed type={ex.GetType().Name} message={ex.Message}");
        Console.WriteLine(ex.ToString());
    }
}

var profileDir = Path.GetDirectoryName(Path.GetFullPath(options.ProfileOut));
if (!string.IsNullOrWhiteSpace(profileDir))
    Directory.CreateDirectory(profileDir);

File.WriteAllText(options.ProfileOut, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote-profile={options.ProfileOut}");

return exitCode;

internal sealed class Options
{
    public required string PaksPath { get; set; }
    public required string ExePath { get; set; }
    public int PackageLimit { get; set; }
    public string? UsmapPath { get; set; }
    public string? GameVersion { get; set; }
    public required string ProfileOut { get; set; }
    public required string MeshOutDir { get; set; }
    public int ExportTopSkeletal { get; set; }
    public bool PositionalPaksConsumed { get; set; }
}

internal sealed class ProbeProfile
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string? PaksPath { get; set; }
    public string? ExePath { get; set; }
    public string? ExeSha256 { get; set; }
    public string? UsmapPath { get; set; }
    public string? UsmapSha256 { get; set; }
    public int PackageLimit { get; set; }
    public List<VersionProbeResult> Results { get; } = [];
}

internal sealed class VersionProbeResult
{
    public required string Game { get; set; }
    public int RegisteredArchives { get; set; }
    public int FilesBeforeMount { get; set; }
    public int RequiredKeysBefore { get; set; }
    public int Mounted { get; set; }
    public int MountedTotal { get; set; }
    public int Unloaded { get; set; }
    public int FilesAfterMount { get; set; }
    public ProbeError? PostMountWarning { get; set; }
    public int RequiredKeysAfter { get; set; }
    public int Files { get; set; }
    public List<string> RequiredKeyGuids { get; } = [];
    public MeshProbeSummary MeshSummary { get; set; } = new();
    public ProbeError? FatalError { get; set; }
    public bool Ok { get; set; }
}

internal sealed class MeshProbeSummary
{
    public int CandidatePackages { get; set; }
    public int PackagesScanned { get; set; }
    public int MeshExports { get; set; }
    public int StaticMeshExports { get; set; }
    public int SkeletalMeshExports { get; set; }
    public int ConvertedStaticMeshes { get; set; }
    public int ConvertedSkeletalMeshes { get; set; }
    public int SkeletalConversionFailures { get; set; }
    public int RecordedStaticMeshes { get; set; }
    public List<string> SampleCandidates { get; } = [];
    public ProbeFailure? FirstFailure { get; set; }
    public List<ProbeFailure> LoadFailures { get; } = [];
    public List<ProbeFailure> ConversionFailures { get; } = [];
    public List<ProbeFailure> ExportFailures { get; } = [];
    public List<ConvertedMesh> ConvertedMeshes { get; } = [];
    public List<ConvertedMesh> TopSkeletalCandidates { get; } = [];
    public List<MeshExportSummary> ExportedSkeletalMeshes { get; } = [];
}

internal sealed record ProbeError(string Type, string Message);
internal sealed record ProbeFailure(string Path, string Type, string Message);
internal sealed record MeshExportSummary(string OutputPath, string SourcePath, string Export, int VertexCount, int IndexCount, int NumTexCoords, int Bones);
internal sealed record MeshExportUv(float U, float V);
internal sealed record MeshExportInfluence(ushort Bone, ushort RawWeight, float Weight);
internal sealed record MeshExportVertex(float X, float Y, float Z, float NormalX, float NormalY, float NormalZ, float U, float V, MeshExportInfluence[] Influences);
internal sealed record MeshExportBone(int Index, string Name, int ParentIndex, float X, float Y, float Z, float RotationX, float RotationY, float RotationZ, float RotationW);
internal sealed record MeshExportLod(int LodIndex, int NumTexCoords, int VertexCount, int IndexCount, int SectionCount, uint[] Indices, MeshExportVertex[] Vertices, MeshExportUv[][] ExtraUvChannels);
internal sealed class SkeletalMeshGeometryExport
{
    public int SchemaVersion { get; init; } = 1;
    public required string SourcePath { get; init; }
    public required string Export { get; init; }
    public int CandidateScore { get; init; }
    public string[] CandidateReasons { get; init; } = [];
    public string? Skeleton { get; init; }
    public string? PhysicsAsset { get; init; }
    public string? BoundsOrigin { get; init; }
    public string? BoundsExtent { get; init; }
    public float? BoundsRadius { get; init; }
    public string[] MaterialSlots { get; init; } = [];
    public MeshExportBone[] Bones { get; init; } = [];
    public required MeshExportLod Lod0 { get; init; }
}
internal sealed class ConvertedMesh
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public required string Export { get; init; }
    public bool Converted { get; init; }
    public int Lods { get; init; }
    public int VerticesLod0 { get; init; }
    public int IndicesLod0 { get; init; }
    public int? Bones { get; init; }
    public int? UvChannelsLod0 { get; init; }
    public int? SectionsLod0 { get; init; }
    public int? MaterialSlots { get; init; }
    public string[] SampleMaterialSlots { get; init; } = [];
    public string[] SampleBones { get; init; } = [];
    public string? Skeleton { get; init; }
    public string? PhysicsAsset { get; init; }
    public int? MorphTargets { get; init; }
    public int? Sockets { get; init; }
    public bool? AssetCooked { get; init; }
    public required string PackageFlags { get; init; }
    public bool PackageCooked { get; init; }
    public bool PackageFilterEditorOnly { get; init; }
    public bool HasRenderData { get; init; }
    public bool? HasRenderBounds { get; init; }
    public int RenderLods { get; init; }
    public bool HasNanite { get; init; }
    public int NanitePages { get; init; }
    public uint NaniteTriangles { get; init; }
    public string? BoundsOrigin { get; init; }
    public string? BoundsExtent { get; init; }
    public float? BoundsRadius { get; init; }
    public int CandidateScore { get; init; }
    public string[] CandidateReasons { get; init; } = [];
    public int MappingProperties { get; init; }
    public int MappingPropertiesWithSuper { get; init; }
    public string[] SampleMappingProperties { get; init; } = [];
    public int ReadProperties { get; init; }
    public string[] SampleReadProperties { get; init; } = [];
}
