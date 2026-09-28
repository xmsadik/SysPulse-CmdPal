using System.Globalization;
using System.Runtime.CompilerServices;

namespace SysPulse.Tests.TestSupport;

/// <summary>
/// Forces the test process's default UI culture to en-US before any test runs (task requirement
/// H1: "Core must stay testable: existing tests assert English text").
/// </summary>
/// <remarks>
/// <see cref="SysPulse.Core.Formatting.AlertMessageFormatter"/> and
/// <see cref="SysPulse.Core.Formatting.ProcessSubtitleFormatter"/> select their resource text via
/// <see cref="CultureInfo.CurrentUICulture"/> (the generated <c>Resources.Designer.cs</c>'s
/// default lookup). Every existing test in this project asserts the English strings; without
/// pinning the default UI culture, running the suite on a non-English CI/dev machine (e.g. one
/// whose OS display language is Turkish) would flip <see cref="CultureInfo.CurrentUICulture"/> to
/// that machine's language and break those assertions for reasons unrelated to the code under
/// test. A <see cref="ModuleInitializerAttribute"/> method runs exactly once, as soon as this
/// assembly is loaded by the test host -- before test discovery or any <c>[Fact]</c> executes --
/// regardless of test framework/collection ordering, so this is not a per-test or per-collection
/// fixture that individual test classes need to opt into.
/// </remarks>
internal static class CultureFixture
{
    [ModuleInitializer]
    public static void ForceEnglishUiCulture()
    {
        CultureInfo enUs = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = enUs;
        CultureInfo.CurrentUICulture = enUs;
    }
}
