using System.ComponentModel;
using Inedo.Documentation;
using Inedo.Extensibility;

namespace Inedo.Extensions.WindowsSdk.Operations.DotNet
{
    [DisplayName("dotnet publish")]
    [ScriptAlias("Publish")]
    [Description("Builds a .NET Core/Framework/Standard project using dotnet publish.")]
    [Example(@"# Publish ~\src\MyProject.csproj with Release configuration for .net core 3.1, restoring NuGet packages from the InternalNuGet source
DotNet::Publish ~\src\MyProject.csproj
(
    Configuration: Release,
    Framework: netcoreapp3.1,
    PackageSource: InternalNuGet
);")]
    [SeeAlso(typeof(DotNetBuildOperation))]
    public sealed class DotNetPublishOperation : DotNetOperation
    {
        protected override string CommandName => "publish";
    }
}
