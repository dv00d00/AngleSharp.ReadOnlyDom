using System.Runtime.CompilerServices;

namespace AngleSharp.Readonly.Tests;

internal static class TestReporting
{
    [ModuleInitializer]
    internal static void SuppressFileReportsByDefault()
    {
        DefaultToDisabled("TUNIT_DISABLE_HTML_REPORTER");
        DefaultToDisabled("TUNIT_DISABLE_JSON_REPORT");
    }

    private static void DefaultToDisabled(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable).IsNullOrWhiteSpace())
        {
            Environment.SetEnvironmentVariable(variable, "true");
        }
    }
}
