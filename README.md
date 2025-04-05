# Unity Mod AutoPackage Utility

This is a packaging utility for class libraries targeting .NET Framework 4.8 for deployment in the Unity environment

### What does this do?
This tool ensures...
- The build output is clean (single DLL)
  
- All referenced assemblies are included as embedded resources
  - This includes implicit references that are not typically copied to the output directory
  
- Embedded assemblies are compressed
  
- MetaData for each assembly is included
  
- Manually referenced DLL's are not included `<Reference>`
  - Things like `UnityEngine.dll` will not be added as an embedded resource
    
- Project references are not included `<ProjectReference>`
    
> [!NOTE]
> Common libraries like `System.Memory` are not copied to the output directory resulting in `AssemblyLoad` errors
> 
> These libraries will be included and packaged with your project

> [!CAUTION]
> The embedded libraries are not automatically or magically loaded for you!
>
> You must implement your own `AppDomain.CurrentDomain.AssemblyResolve += handler;`
>
> For automatic dependency resolution, see [here](README.md#automatic-dependency-resolution)

This is a tool meant to run after `assets.project.json` is generated

On first build/restore, it will update the `*.csproj` file and changes *may* not take effect until the second build

> [!TIP]
> It is recommended to add the following to your `*.csproj`, be sure to include the `InitialTargets="GenerateNewTargets"`
> https://github.com/TGO-Inc/UnityModPackager/blob/fe9b4729f44907c506f0c75204d6babccf4a6d1c/example.targets#L1-L17


An example of `main.targets`:
https://github.com/TGO-Inc/UnityModPackager/blob/86d9c63810226d24030f39427d446dd9cc1f2656/example.main.targets#L1-L16

> [!IMPORTANT]
> Make sure that UnityModPackager can be accessed at `$USER_HOME$/.dotnet/tools/`
> https://github.com/TGO-Inc/UnityModPackager/blob/86d9c63810226d24030f39427d446dd9cc1f2656/UnityModPackager.csproj#L42-L44
> Here a symbolic link is created on build

Once all of the above is complete, you are ready to start modding!

If you want, you can take a peak at the [Workflow](README.md#workflow) below to see how this tool works

# Workflow

### Working in `*.csproj`
https://github.com/TGO-Inc/UnityModPackager/blob/b231e56407d47d50f3f8ddc9bbc7c17e314254cc/Program.cs#L29-L32

- **Ensure Tag**: `CopyLocalLockFileAssemblies = false`
  - Prevents references generated in `assets.project.json` [[NuGet]](https://nuget.org/) from being copied to the output directory
    https://github.com/TGO-Inc/UnityModPackager/blob/0ab66d5d05b0649d517ccd8b163110f619b7ac85/Program.cs#L49

- **Ensure Tag**: `AutoGenerateBindingRedirects = true`
  - Ensures properly linking to referenced libraries to prevent versioning errors
    https://github.com/TGO-Inc/UnityModPackager/blob/0ab66d5d05b0649d517ccd8b163110f619b7ac85/Program.cs#L50
    
- **Ensure Tag**: `GenerateBindingRedirectsOutputType = true`
  - Ensures the binding redirects are generated based on the project output type ( library )
    https://github.com/TGO-Inc/UnityModPackager/blob/0ab66d5d05b0649d517ccd8b163110f619b7ac85/Program.cs#L51
 
- **Ensure Attribute**: `Private = false`
  - Ensures that `ProjectReference` and `Reference` are not copied to the output directory
  - `PackageReference` is not required because it is handled by the tags above
    https://github.com/TGO-Inc/UnityModPackager/blob/0ab66d5d05b0649d517ccd8b163110f619b7ac85/Program.cs#L52-L54
 
- **Ensure Tag**: `<Import Project="obj/GeneratedResources.targets"/>`
  - Ensures the auto-generated resource file is imported into the project
    https://github.com/TGO-Inc/UnityModPackager/blob/0ab66d5d05b0649d517ccd8b163110f619b7ac85/Program.cs#L55

***

### Working in `assets.project.json`
https://github.com/TGO-Inc/UnityModPackager/blob/0ab66d5d05b0649d517ccd8b163110f619b7ac85/Program.cs#L58-L61

- Locate all dependencies, including implicit (default behavior of `assets.project.json`)
  https://github.com/TGO-Inc/UnityModPackager/blob/3a6a6917ff6d80a65aafd491e5955459948864e8/Program.cs#L70

> [!WARNING]
> - If our target is invalid `_._` or is under `ref`, we must look for alternatives
> https://github.com/TGO-Inc/UnityModPackager/blob/921c87e6d3f63f0f239121e3ee2b8daf949fa968/Program.cs#L71
> - Strongly prefer files found under `lib` and **DO NOT** include `.NET Framework 4.5` as it has a history of causing fatal crashes in Unity
> https://github.com/TGO-Inc/UnityModPackager/blob/921c87e6d3f63f0f239121e3ee2b8daf949fa968/Program.cs#L73-L78
> - If no candidates are found, fallback to files under `ref`
> https://github.com/TGO-Inc/UnityModPackager/blob/b231e56407d47d50f3f8ddc9bbc7c17e314254cc/Program.cs#L89-L94
> - Allow for multiple versions of the same Assembly, so that if `Assembly.Load` fails, there are fallback Assemblies to try
> https://github.com/TGO-Inc/UnityModPackager/blob/3a6a6917ff6d80a65aafd491e5955459948864e8/Program.cs#L96-L101

- Generate library metadata in place
  https://github.com/TGO-Inc/UnityModPackager/blob/b231e56407d47d50f3f8ddc9bbc7c17e314254cc/Program.cs#L118-L127

- Compress library in place
  https://github.com/TGO-Inc/UnityModPackager/blob/b231e56407d47d50f3f8ddc9bbc7c17e314254cc/Program.cs#L136-L140
  
- Generate `.targets` file
  https://github.com/TGO-Inc/UnityModPackager/blob/3a6a6917ff6d80a65aafd491e5955459948864e8/Program.cs#L156-L158
  - Ensure the LogicalName (the name of the resource in the manifest) is unique under the current Assembly
  - This allows for multiple versions of the same Assembly for fallback purposes

 - Write file to `obj/GeneratedResources.targets`

## Automatic Dependency Resolution
> [!NOTE]
> This tool only generates the workflow for automatically embedding assemblies into your project
> 
> [Repo.Shared](https://github.com/TGO-Inc/REPO.Shared) Includes the necessary components for automatically loading, decompressing, and resolving these internal assemblies
> 
> Check out [REPO.Shared.AssemblyResolver.cs](https://github.com/TGO-Inc/REPO.Shared/blob/6817cb6d2d214869e8d970d99a46c84601130347/Internal/AssemblyResolver.cs#L143-L183) to see how the library metadata is used
