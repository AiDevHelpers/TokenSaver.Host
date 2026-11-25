using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

/// <summary>
///     Groups every test class that sets the process-wide <c>TOKENSAVER_GLOBAL_CONFIG_DIR</c> env
///     var (to redirect <c>BehaviorConfigService.GlobalConfigPath</c> away from the real
///     <c>%ProgramData%\TokenSaver\config.json</c> for isolation) into one xunit collection, so
///     xunit's default parallel-by-class execution never runs two of them at once — otherwise one
///     test's constructor/dispose could clobber the env var mid-run of another.
/// </summary>
[CollectionDefinition("GlobalConfigDirEnvVar", DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection definitions to be public.
public sealed class GlobalConfigDirEnvVarGroup;
