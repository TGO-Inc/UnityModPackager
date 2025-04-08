using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

namespace UnityModPackager;

internal static class Program
{
    private static void Main(string[] args)
    {
        // Find the csproj file in the current directory
        var workingDir = Directory.GetCurrentDirectory();
        var csprojFiles = Directory.GetFiles(workingDir, "*.csproj", SearchOption.TopDirectoryOnly);
        
        var csprojFile = csprojFiles.FirstOrDefault();
        if (csprojFile is null)
        {
            Console.WriteLine("No csproj file found in the current directory: "+workingDir);
            return;
        }
        
        Console.WriteLine(csprojFile);
        var userCsProj = new XmlFile(csprojFile);
        
        if (args[0] != "--pre")
        {
            Console.WriteLine("Invalid argument. Use --pre for a prebuild run.");
            return;
        }
        
        // Make sure all the assembly references are set to CopyLocal = false
        userCsProj.AddTagToFirst("PropertyGroup", "CopyLocalLockFileAssemblies", false);
        userCsProj.AddTag("Reference", "Private", false);
        userCsProj.AddAttribute("ProjectReference", "Private", false);
        var condition = userCsProj.GetAttributeOfFirst("Target", "Condition", ("Name", "GenerateNewTargets"));
        userCsProj.AddTagToRoot("Import", ("Project", "obj/GeneratedResources.targets"), ("Condition", condition));
        userCsProj.SaveDocument();
        
        // Load all libraries in ./obj/project.assets.json
        var projectAssetsPath = Path.Combine(Path.GetDirectoryName(csprojFile) ?? "", "obj", "project.assets.json");
        var projectAssetsJson = JsonNode.Parse(File.ReadAllText(projectAssetsPath))!;
        var jsonLibraries = projectAssetsJson["targets"]!.AsObject().First().Value;
        
        // Libraries base path is UserDir/.nuget/packages/
        var librariesBasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        
        List<string> libraries = [];
        // Force include Dlls with Label="Include"
        var includeTags = userCsProj.GetAllTagsWith("Reference", ("Label", "Include"));
        var includePaths = includeTags.Select(t => t.ChildNodes.Cast<XmlLinkedNode>()
                .First(n => n.Name == "HintPath"))
            .Select(t => t.InnerText);
        libraries.AddRange(includePaths);

        var bannedLibs = includePaths.Select(Path.GetFileName).ToArray();
        
        foreach (var (key, value) in jsonLibraries.AsObject())
            if (value["compile"] is JsonObject jobj)
            {
                var libName = jobj.Select(kvp => kvp.Key).First();
                if (bannedLibs.Contains(Path.GetFileName(libName)))
                    continue;

                if (libName.EndsWith("_._") || libName.StartsWith("ref"))
                {
                    var alternatives = projectAssetsJson["libraries"]![key]!["files"]!.AsArray()
                        .Select(i => i?.AsValue().GetValue<string>())
                        .Where(f => f is not null && (
                            f.StartsWith("lib/net4") || 
                            f.StartsWith("lib/netstandard")
                            ) && f.EndsWith(".dll") && !f.StartsWith("lib/net45")).ToArray();

                    if (alternatives.Length > 0)
                    {
                        libraries.AddRange(alternatives.Select(alt =>
                            Path.Combine(librariesBasePath, key.ToLower(), alt)));
                        continue;
                    }
                    
                    alternatives = projectAssetsJson["libraries"]![key]!["files"]!.AsArray()
                        .Select(i => i?.AsValue().GetValue<string>())
                        .Where(f => f is not null && (
                            f.StartsWith("ref/net4") || 
                            f.StartsWith("ref/netstandard")
                        ) && f.EndsWith(".dll") && !f.StartsWith("ref/net45")).ToArray();
                    
                    if (alternatives.Length > 0)
                    {
                        libraries.AddRange(alternatives.Select(alt =>
                            Path.Combine(librariesBasePath, key.ToLower(), alt)));
                    }
                    
                    continue;
                }
                libraries.Add(Path.Combine(librariesBasePath, key.ToLower(), libName));
            }
        
        // Compress the dll files
        List<string> compressedLibraries = [];
        foreach (var lib in libraries.Where(File.Exists))
        {
            var dllMeta = lib + ".dllmeta";
            
            var asm = File.ReadAllBytes(lib);
            var lAsmName = GetAssemblyNameFromData(asm);
            var nameMeta = SerializeAssemblyName(lAsmName);
            File.WriteAllBytes(dllMeta, nameMeta);
            Console.WriteLine("Generated Dll MetaData " + dllMeta);
            
            var compressedLib = lib + ".gz";
            if (File.Exists(compressedLib))
            {
                compressedLibraries.Add(lib);
                continue;
            }

            using var inputStream = File.OpenRead(lib);
            using var outputStream = File.Create(compressedLib);
            using var gzipStream = new GZipStream(outputStream, CompressionMode.Compress);
            inputStream.CopyTo(gzipStream);
            compressedLibraries.Add(lib);
            Console.WriteLine("Compressed " + lib);
        }
        
        var i = 0;
        // Generate the EmbeddedResource xml tags for the libraries
        var xmlInclude = new StringBuilder();
        xmlInclude.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xmlInclude.AppendLine("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
        xmlInclude.AppendLine("<ItemGroup>");
        foreach (var lib in compressedLibraries)
        {
            Console.WriteLine("Embedding " + lib);
            Console.WriteLine("Embedding " + lib + ".dllmeta");
            
            var fName = (i++)+"."+Path.GetFileName(lib);
            xmlInclude.AppendLine($"<EmbeddedResource Include=\"{lib}.gz\" LogicalName=\"BundledAssemblies\\{fName}.gz\" Visible=\"false\"/>");
            xmlInclude.AppendLine($"<EmbeddedResource Include=\"{lib}.dllmeta\" LogicalName=\"BundledAssemblies\\{fName}.dllmeta\" Visible=\"false\"/>");
        }
        xmlInclude.AppendLine("</ItemGroup>");
        xmlInclude.Append("</Project>");
        
        // Add the xmlInclude to the csproj file xmldoc and save it
        File.WriteAllText("obj/GeneratedResources.targets", xmlInclude.ToString());
    }
    
    public static AssemblyName GetAssemblyNameFromData(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException("The file does not contain valid metadata.");
        }

        var mdReader = peReader.GetMetadataReader();
        var assemblyDefinition = mdReader.GetAssemblyDefinition();
        return assemblyDefinition.GetAssemblyName();
    }

    private static byte[] SerializeAssemblyName(AssemblyName assemblyName)
    {
        var sb = new StringBuilder();
        sb.AddWithComma(assemblyName.Name);
        sb.AddWithComma(assemblyName.Version);
        sb.AddWithComma((assemblyName.GetPublicKey() ?? []).ToHexStr());
        sb.Append((assemblyName.GetPublicKeyToken() ?? []).ToHexStr());
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

public static class StringBuilderExtensions
{
    public static StringBuilder AddWithComma(this StringBuilder sb, object? value)
        => sb.Append(value).Append(',');
}

public static class ByteExtensions
{
    public static string ToHexStr(this byte[] bytes)
        => BitConverter.ToString(bytes).Replace("-", string.Empty);
    public static byte[] FromHexToBytes(this string hex)
    {
        if (hex.Length % 2 != 0)
            throw new ArgumentException("Hex string must have an even length.");

        var bytes = new byte[hex.Length / 2];

        for (var i = 0; i < bytes.Length; i++)
        {
            var byteValue = hex.Substring(i * 2, 2);
            bytes[i] = Convert.ToByte(byteValue, 16);
        }

        return bytes;
    }
}
