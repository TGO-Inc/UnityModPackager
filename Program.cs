using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;

namespace UnityModPackager;

internal static partial class Program
{
    // this is csproj xml to ignore the Build.*.csproj files
    private const string IgnoreXml = "<ItemGroup><Compile Remove=\"Build.*.csproj\"/><None Remove=\"Build.*.csproj\"/></ItemGroup>";

    private static string[] Banned =
    [
        // "System.Resources.ResourceManager", 
        // "System.Reflection.Extensions"
    ];
    
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
        userCsProj.AddTagToFirst("PropertyGroup", "AutoGenerateBindingRedirects", true);
        userCsProj.AddTagToFirst("PropertyGroup", "GenerateBindingRedirectsOutputType", true);
        userCsProj.AddTag("Reference", "Private", false);
        userCsProj.AddAttribute("ProjectReference", "Private", false);
        // userCsProj.AddAttribute("PackageReference", "ExcludeAssets", "runtime");
        userCsProj.AddTagToRoot("Import", ("Project", "obj/GeneratedResources.targets"));
        userCsProj.SaveDocument();
        
        // Load all libraries in ./obj/project.assets.json
        var projectAssetsPath = Path.Combine(Path.GetDirectoryName(csprojFile) ?? "", "obj", "project.assets.json");
        var projectAssetsJson = JsonNode.Parse(File.ReadAllText(projectAssetsPath))!;
        var jsonLibraries = projectAssetsJson["targets"]![".NETFramework,Version=v4.8"]!;
        
        // Libraries base path is UserDir/.nuget/packages/
        var librariesBasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        
        List<string> libraries = [];
        foreach (var (key, value) in jsonLibraries.AsObject())
            if (value["compile"] is JsonObject jobj)
            {
                var libName = jobj.Select(kvp => kvp.Key).First();
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
                        continue;
                    }
                }
                libraries.Add(Path.Combine(librariesBasePath, key.ToLower(), libName));
            }

        // Compress the dll files
        List<string> compressedLibraries = [];
        foreach (var lib in libraries.Where(File.Exists))
        {
            // Skip the banned libraries
            var fName = Path.GetFileNameWithoutExtension(lib);
            if (Banned.Contains(fName))
            {
                Console.WriteLine("Skipping " + lib);
                continue;
            }
            
            var dllMeta = lib + ".dllmeta";
            // if (!File.Exists(dllMeta))
            {
                var asm = File.ReadAllBytes(lib);
                var lAsmName = GetAssemblyNameFromData(asm);
                var nameMeta = SerializeAssemblyName(lAsmName);
                File.WriteAllBytes(dllMeta, nameMeta);
                
                Console.WriteLine("Generated Dll MetaData " + dllMeta);
            }
            
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
        
        // Generate the EmbeddedResource xml tags for the libraries
        var xmlInclude = new StringBuilder();
        xmlInclude.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xmlInclude.AppendLine("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
        xmlInclude.AppendLine("<ItemGroup>");
        var i = 0;
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

        // Read the assembly name, version, and culture from the metadata
        var name = mdReader.GetString(assemblyDefinition.Name);
        var version = assemblyDefinition.Version;
        var culture = mdReader.GetString(assemblyDefinition.Culture);

        var assemblyName = new AssemblyName
        {
            Name = name,
            Version = version,
            CultureName = string.IsNullOrEmpty(culture) ? null : culture
        };

        // Optionally, include public key if needed
        var publicKeyHandle = assemblyDefinition.PublicKey;
        if (publicKeyHandle.IsNil) return assemblyName;
        var publicKey = mdReader.GetBlobBytes(publicKeyHandle);
        assemblyName.SetPublicKey(publicKey);

        return assemblyName;
    }

    private static byte[] SerializeAssemblyName(AssemblyName assemblyName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(assemblyName.Name);
        sb.AppendLine(assemblyName.FullName);
        sb.AppendLine(assemblyName.Flags.ToString());
        sb.AppendLine(assemblyName.Version.ToString());
        sb.AppendLine(assemblyName.ContentType.ToString());
        sb.AppendLine(string.Join(";", assemblyName.GetPublicKey() ?? []));
        sb.AppendLine(string.Join(";", assemblyName.GetPublicKeyToken() ?? []));
        
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
