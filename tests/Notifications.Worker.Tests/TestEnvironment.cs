using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Notifications.Worker.Tests;

/// <summary>A minimal <see cref="IHostEnvironment"/>; each outbound hop's registration reads only its name.</summary>
internal sealed class TestEnvironment : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Notifications.Worker.Tests";

    public string EnvironmentName { get; set; } = Environments.Production;

    public string ContentRootPath { get; set; } = string.Empty;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
