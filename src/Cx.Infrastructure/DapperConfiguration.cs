using System.Runtime.CompilerServices;

namespace Cx.Infrastructure;

internal static class DapperConfiguration
{
    /// <summary>
    /// Maps snake_case columns onto PascalCase properties for every query in the platform.
    ///
    /// This runs on assembly load rather than from DI registration: the mapping is a
    /// global Dapper setting, and a repository constructed directly (a test, a migration
    /// tool, a one-off job) must materialize rows the same way the running service does.
    /// Wiring it only into AddCxInfrastructure made correctness depend on DI having run.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
}
