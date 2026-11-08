using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class GameToolPipelineTests
{
    public static IEnumerable<object[]> Pipelines()
    {
        var names = new[] { ("ddraw.dll", "DirectDraw / Direct3D 1–7"), ("d3dim.dll", "DirectDraw / Direct3D 1–7"),
            ("d3d8.dll", "Direct3D 8"), ("d3d9.dll", "Direct3D 9"), ("d3d10.dll", "Direct3D 10"),
            ("d3d11.dll", "Direct3D 11"), ("d3d12.dll", "Direct3D 12"), ("opengl32.dll", "OpenGL"),
            ("vulkan-1.dll", "Vulkan"), ("glide.dll", "Glide"), ("glide2x.dll", "Glide 2"), ("glide3x.dll", "Glide 3") };
        foreach (var machine in new[] { Machine.I386, Machine.Amd64, Machine.Arm64 })
        {
            foreach (var (dll, pipeline) in names)
            {
                yield return [machine, dll, pipeline];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Pipelines))]
    public void PeImportsDeterminePipelineAndProcessArchitecture(Machine machine, string dll, string pipeline)
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, Stub(machine, [dll], []));
        Assert.Equal(PeImage.Architecture(machine), PeImage.TryArchitecture(exe));
        Assert.Equal([pipeline], GameGraphicsApi.Detect(exe));
    }

    [Theory]
    [InlineData("d3d8.dll")]
    [InlineData("D3D8.DLL")]
    [InlineData("Direct3DCreate8")]
    public void ProgramLoadingDirect3D8ByNameIsDirect3D8(string reference)
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, [.. Stub(Machine.I386, ["kernel32.dll"], []), .. Encoding.ASCII.GetBytes(reference)]);
        Assert.Equal(["Direct3D 8"], GameGraphicsApi.Detect(exe));
    }

    [Theory]
    [InlineData("D3D11CreateDevice", "Direct3D 11")]
    [InlineData("D3D12CreateDevice", "Direct3D 12")]
    [InlineData("Direct3DCreate9", "Direct3D 9")]
    public void OpenGlFallbackImportGivesWayToDirect3DLoadedByName(string entryPoint, string pipeline)
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, [.. Stub(Machine.Amd64, ["opengl32.dll"], []), .. Encoding.ASCII.GetBytes(entryPoint)]);
        Assert.Equal([pipeline], GameGraphicsApi.Detect(exe));
    }

    [Fact]
    public void EngineModuleNamingDirect3DOverridesItsOpenGlImport()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, Stub(Machine.Amd64, ["kernel32.dll"], []));
        File.WriteAllBytes(Path.Combine(directory.Root, "UnityPlayer.dll"),
            [.. Stub(Machine.Amd64, ["opengl32.dll"], []), .. Encoding.ASCII.GetBytes("D3D11CreateDevice")]);
        Assert.Equal(["Direct3D 11"], GameGraphicsApi.Detect(exe));
    }

    [Fact]
    public void EntryPointAcrossAStreamBufferBoundaryIsFound()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        var padding = new byte[(1 << 20) - 5];
        File.WriteAllBytes(exe, [.. padding, .. Encoding.ASCII.GetBytes("D3D11CreateDevice")]);
        Assert.Equal(["Direct3D 11"], GameGraphicsApi.Detect(exe));
    }

    [Fact]
    public void PlainOpenGlGameStaysOpenGl()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, Stub(Machine.Amd64, ["opengl32.dll"], []));
        Assert.Equal(["OpenGL"], GameGraphicsApi.Detect(exe));
    }

    [Fact]
    public void RendererLibraryImportingDirect3D8IsDirect3D8()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, Stub(Machine.I386, ["kernel32.dll"], []));
        File.WriteAllBytes(Path.Combine(directory.Root, "render.dll"), Stub(Machine.I386, ["d3d8.dll"], []));
        Assert.Equal(["Direct3D 8"], GameGraphicsApi.Detect(exe));
    }

    [Fact]
    public void Direct3D8ProbeInsideANewerGameIsNotDirect3D8()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, [.. Stub(Machine.I386, ["d3d9.dll"], []), .. Encoding.ASCII.GetBytes("d3d8.dll")]);
        Assert.Equal(["Direct3D 9"], GameGraphicsApi.Detect(exe));
    }

    [Fact]
    public void ProxyNamedD3D8BesideTheProgramIsNotEvidence()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, Stub(Machine.I386, ["kernel32.dll"], []));
        File.WriteAllBytes(Path.Combine(directory.Root, "d3d8.dll"), Stub(Machine.I386, ["d3d8.dll"], []));
        Assert.Empty(GameGraphicsApi.Detect(exe));
    }

    [Fact]
    public void DelayedImportsAndEngineModulesProduceOneAmbiguousSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "game.exe");
        File.WriteAllBytes(exe, Stub(Machine.Amd64, ["D3D11.DLL", "dinput8.dll"], [], delayed: true));
        File.WriteAllBytes(Path.Combine(directory.Root, "UnityPlayer.dll"), Stub(Machine.Amd64, ["d3d12.dll"], []));
        var evidence = GameGraphicsApi.Read(exe);
        Assert.Equal(["Direct3D 11", "Direct3D 12"], evidence.Pipelines);
        Assert.Contains("dinput8.dll", evidence.Imports);
        var tool = GameToolCatalog.Current.Find("special-k")!;
        Assert.Equal("", GameToolInstaller.SuggestProxy([.. tool.ProxyOptions.Select(option =>
            new GameToolProxyStatus(option.Name, option.Hint, true, null, option.Suggest))], tool.Proxy.Default, tool,
            evidence.Pipelines, "x64", evidence.Imports));
    }

    [Theory]
    [InlineData("dxvk", "Vulkan", "x64")]
    [InlineData("dgvoodoo2", "Direct3D 8", "x64")]
    public void UnsupportedPipelineOrArchitectureDoesNotSuggestAnotherRenderer(string id, string pipeline, string architecture)
    {
        var tool = GameToolCatalog.Current.Find(id)!;
        var proxies = tool.ProxyOptions.Select(option =>
            new GameToolProxyStatus(option.Name, option.Hint, true, null, option.Suggest)).ToArray();
        Assert.Equal("", GameToolInstaller.SuggestProxy(proxies, tool.Proxy.Default, tool, [pipeline], architecture, []));
    }

    [Theory]
    [InlineData("dxvk", "DXVK", "D3D11.dll", "D3D11CreateDevice", "Direct3D 11")]
    [InlineData("dgvoodoo2", "dgVoodoo", "DDraw.dll", "DirectDrawCreate", "DirectDraw / Direct3D 1–7")]
    [InlineData("dgvoodoo2", "dgVoodoo", "D3DImm.dll", "GetD3DInterface", "DirectDraw / Direct3D 1–7")]
    [InlineData("dgvoodoo2", "dgVoodoo", "D3D8.dll", "Direct3DCreate8", "Direct3D 8")]
    [InlineData("dgvoodoo2", "dgVoodoo", "D3D9.dll", "Direct3DCreate9", "Direct3D 9")]
    public void RenamedDllsUsePublisherAndExportIdentity(string id, string product, string original, string export, string pipeline)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "renamed.dll");
        File.WriteAllBytes(path, Stub(Machine.I386, [], original == "D3DImm.dll" ? [export, "GetD3DVersion"] : [export]));
        var names = PeImage.Exports(path);
        Assert.Contains(export, names);
        var info = new PeVersionInfo(original, "1", "1", "", product, "", "Publisher");
        var identity = GameToolBinaryIdentification.Classify("renamed.dll", PeImage.TryArchitecture(path)!, info, names);
        Assert.Equal(id, identity.ToolId);
        Assert.Contains(pipeline, identity.Pipelines);
        Assert.Equal("x86", identity.Architecture);
    }

    [Theory]
    [InlineData("special-k", "ddraw.dll")]
    [InlineData("reshade", "ddraw.dll")]
    public void UniqueExportsIdentifyUniversalProxiesWithoutVersionResources(string id, string loadName)
    {
        var tool = GameToolCatalog.Current.Find(id)!;
        var exports = tool.Identity.UniqueExportGroups[0].ToHashSet(StringComparer.Ordinal);
        var result = GameToolBinaryIdentification.Classify(loadName, "x64", null, exports);
        Assert.Equal(id, result.ToolId);
        Assert.Equal("", GameToolBinaryIdentification.Classify(loadName, "x64", null,
            new HashSet<string> { "DirectDrawCreate" }).ToolId);
    }

    [Fact]
    public void FileSetsAndArchitectureRulesComeFromUpstreamDescriptors()
    {
        var dxvk = GameToolCatalog.Current.Find("dxvk")!;
        Assert.Equal(["WSGM-tool.dll", "d3d9.dll"], dxvk.InstallMode("d3d8.dll").Files.Values);
        Assert.Equal(["WSGM-tool.dll", "d3d11.dll", "dxgi.dll"], dxvk.InstallMode("d3d10core.dll").Files.Values);
        Assert.Equal(["WSGM-tool.dll", "dxgi.dll"], dxvk.InstallMode("d3d11.dll").Files.Values);
        Assert.DoesNotContain("dxgi.dll", dxvk.InstallMode("d3d9.dll").Files.Values);
        var dg = GameToolCatalog.Current.Find("dgvoodoo2")!;
        Assert.Contains("D3DImm.dll", dg.InstallMode("DDraw.dll").Files.Values);
        Assert.DoesNotContain("x64", dg.InstallMode("DDraw.dll").Architectures);
        Assert.Equal("arm64x", dg.InstallMode("D3D9.dll").ArchitectureFolders["arm64"]);
        var sk = GameToolCatalog.Current.Find("special-k")!;
        Assert.Equal(["dxgi.dll", "d3d11.dll", "d3d9.dll", "d3d8.dll", "ddraw.dll", "opengl32.dll", "dinput8.dll"],
            sk.InstallModes.Select(mode => mode.Proxy));
        Assert.True(sk.InstallMode("ddraw.dll").UsesSharedPlugin);
        Assert.Equal(["dinput8.dll"], sk.InstallMode("dinput8.dll").RequiredImports);
    }

    [Theory]
    [InlineData(0x07564427u)]
    [InlineData(0x07564428u)]
    [InlineData(0x07564429u)]
    public void SteamWorkingDirectoryIsReadFromLaunchMetadata(uint magic)
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Root, "launcher"));
        var file = Path.Combine(directory.Root, "appinfo.vdf");
        File.WriteAllBytes(file, AppInfo(magic, "launcher"));
        Assert.Equal(Path.Combine(directory.Root, "launcher"), GameToolLaunchDirectory.Read(file, 218, directory.Root));
        File.WriteAllBytes(file, AppInfo(magic, "../../outside"));
        Assert.Equal("", GameToolLaunchDirectory.Read(file, 218, directory.Root));
    }

    internal static byte[] Stub(Machine machine, string[] imports, string[] exports, bool delayed = false)
    {
        var bytes = new byte[8192];
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        U16(0, 0x5a4d); U32(60, 128); U32(128, 0x4550);
        U16(132, (ushort)machine); U16(134, 1); U16(150, 0x22);
        var wide = machine != Machine.I386;
        U16(148, (ushort)(wide ? 240 : 224));
        U16(152, (ushort)(wide ? 0x20b : 0x10b));
        U32(152 + 32, 4096); U32(152 + 36, 512); U32(152 + 56, 0x4000); U32(152 + 60, 512);
        U32(152 + (wide ? 108 : 92), 16);
        var sections = 152 + (wide ? 240 : 224);
        Encoding.ASCII.GetBytes(".rdata").CopyTo(bytes, sections);
        U32(sections + 8, 7680); U32(sections + 12, 0x1000); U32(sections + 16, 7680); U32(sections + 20, 512);
        U32(sections + 36, 0x40000040);
        var directories = 152 + (wide ? 112 : 96);
        if (imports.Length > 0)
        {
            var index = delayed ? 13 : 1;
            U32(directories + index * 8, delayed ? 0x1100u : 0x1000u);
            U32(directories + index * 8 + 4, (uint)((imports.Length + 1) * (delayed ? 32 : 20)));
            var name = 0x500;
            for (var item = 0; item < imports.Length; item++)
            {
                var at = (delayed ? 0x300 : 0x200) + item * (delayed ? 32 : 20);
                if (delayed)
                {
                    U32(at, 1);
                }
                U32(at + (delayed ? 4 : 12), (uint)(name + 0xe00));
                var text = Encoding.ASCII.GetBytes(imports[item] + "\0");
                text.CopyTo(bytes, name); name += text.Length;
            }
        }
        if (exports.Length > 0)
        {
            U32(directories, 0x1400); U32(directories + 4, 0x800);
            U32(0x600 + 20, (uint)exports.Length); U32(0x600 + 24, (uint)exports.Length);
            U32(0x600 + 28, 0x1500); U32(0x600 + 32, 0x1580); U32(0x600 + 36, 0x15c0);
            var name = 0x800;
            for (var item = 0; item < exports.Length; item++)
            {
                U32(0x780 + item * 4, (uint)(name + 0xe00));
                var text = Encoding.ASCII.GetBytes(exports[item] + "\0");
                text.CopyTo(bytes, name); name += text.Length;
            }
        }
        return bytes;
    }

    private static byte[] AppInfo(uint magic, string working)
    {
        var keys = new[] { "appinfo", "config", "launch", "0", "executable", "workingdir", "oslist" };
        using var kv = new MemoryStream();
        using (var writer = new BinaryWriter(kv, Encoding.UTF8, leaveOpen: true))
        {
            void Name(string value)
            {
                if (magic == 0x07564429)
                {
                    writer.Write(Array.IndexOf(keys, value));
                }
                else
                {
                    writer.Write(Encoding.UTF8.GetBytes(value + "\0"));
                }
            }
            foreach (var key in keys.Take(4))
            {
                writer.Write((byte)0);
                Name(key);
            }
            foreach (var (key, value) in new[] { ("executable", "stub.exe"), ("workingdir", working), ("oslist", "windows") })
            { writer.Write((byte)1); Name(key); writer.Write(Encoding.UTF8.GetBytes(value + "\0")); }
            for (var index = 0; index < 4; index++)
            {
                writer.Write((byte)8);
            }
        }
        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(magic); writer.Write(1u);
            var header = magic == 0x07564427 ? 40 : 60;
            if (magic == 0x07564429)
            {
                writer.Write(16L + 8 + header + kv.Length + 4);
            }
            writer.Write(218u); writer.Write((uint)(header + kv.Length)); writer.Write(new byte[header]); writer.Write(kv.ToArray()); writer.Write(0u);
            if (magic == 0x07564429)
            {
                writer.Write(keys.Length);
                foreach (var key in keys)
                {
                    writer.Write(Encoding.UTF8.GetBytes(key + "\0"));
                }
            }
        }
        return file.ToArray();
    }
}
