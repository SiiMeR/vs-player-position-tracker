using System;
using System.IO;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Core;
using Cake.Frosting;
using Cake.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace CakeBuild;

public static class Program
{
    public static int Main(string[] args)
    {
        return new CakeHost()
            .UseContext<BuildContext>()
            .Run(args);
    }
}

public class BuildContext : FrostingContext
{
    public static readonly string[] Projects = { "PlayerPositionTrackerServer", "PlayerPositionTrackerClient" };
    public string BuildConfiguration { get; }
    public bool SkipJsonValidation { get; }

    public BuildContext(ICakeContext context)
        : base(context)
    {
        BuildConfiguration = context.Argument("configuration", "Release");
        SkipJsonValidation = context.Argument("skipJsonValidation", false);
    }
}

[TaskName("ValidateJson")]
public sealed class ValidateJsonTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        if (context.SkipJsonValidation)
        {
            return;
        }

        foreach (var project in BuildContext.Projects)
        {
            var jsonFiles = context.GetFiles($"../{project}/assets/**/*.json");
            foreach (var file in jsonFiles)
            {
                try
                {
                    var json = File.ReadAllText(file.FullPath);
                    JToken.Parse(json);
                }
                catch (JsonException ex)
                {
                    throw new Exception(
                        $"Validation failed for JSON file: {file.FullPath}{Environment.NewLine}{ex.Message}", ex);
                }
            }
        }
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(ValidateJsonTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        foreach (var project in BuildContext.Projects)
        {
            context.DotNetClean($"../{project}/{project}.csproj",
                new DotNetCleanSettings
                {
                    Configuration = context.BuildConfiguration
                });

            context.DotNetPublish($"../{project}/{project}.csproj",
                new DotNetPublishSettings
                {
                    Configuration = context.BuildConfiguration
                });
        }
    }
}

[TaskName("Package")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PackageTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.EnsureDirectoryExists("../Releases");
        context.CleanDirectory("../Releases");

        foreach (var project in BuildContext.Projects)
        {
            var modInfo = context.DeserializeJsonFromFile<ModInfo>($"../{project}/modinfo.json");
            var name = modInfo.ModID;
            var version = modInfo.Version;

            context.EnsureDirectoryExists($"../Releases/{name}");
            context.CopyFiles($"../bin/{context.BuildConfiguration}/Mods/{name}/publish/*",
                $"../Releases/{name}");
            if (context.DirectoryExists($"../{project}/assets"))
            {
                context.CopyDirectory($"../{project}/assets", $"../Releases/{name}/assets");
            }

            context.CopyFile($"../{project}/modinfo.json", $"../Releases/{name}/modinfo.json");
            if (context.FileExists($"../{project}/modicon.png"))
            {
                context.CopyFile($"../{project}/modicon.png", $"../Releases/{name}/modicon.png");
            }

            context.Zip($"../Releases/{name}", $"../Releases/{name}_{version}.zip");
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PackageTask))]
public class DefaultTask : FrostingTask
{
}
