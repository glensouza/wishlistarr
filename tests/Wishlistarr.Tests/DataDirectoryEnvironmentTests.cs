using Microsoft.Extensions.Configuration;
using Wishlistarr.Core;

namespace Wishlistarr.Tests;

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;

[Collection("EnvironmentVariables")]
public sealed class DataDirectoryEnvironmentTests
{
    [Fact]
    public void Data_directory_is_read_from_environment_variable()
    {
        string path = Path.Combine(Path.GetTempPath(), "wishlistarr-env-test");
        string? previous = Environment.GetEnvironmentVariable(DataDirectory.EnvironmentVariable);
        Environment.SetEnvironmentVariable(DataDirectory.EnvironmentVariable, path);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            DataDirectory directory = DataDirectory.Resolve(configuration, Path.GetTempPath());

            Assert.Equal(path, directory.Root);
            Assert.Equal(Path.Combine(path, "wishlistarr.db"), directory.DatabasePath);
            Assert.Equal(Path.Combine(path, "keys"), directory.KeysPath);
            Assert.Equal(Path.Combine(path, "backups"), directory.BackupsPath);
            Assert.Equal(Path.Combine(path, "restore-pending"), directory.RestorePendingPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataDirectory.EnvironmentVariable, previous);
        }
    }
}
