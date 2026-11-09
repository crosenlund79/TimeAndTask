using System.Diagnostics;
using System.Globalization;
using System.Windows;
using WorkTrace.Infrastructure.Reporting;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.App.Diagnostics;

/// <summary>
/// Developer command: <c>WorkTrace.exe --registration-report [yyyy-MM-dd] [--names]</c> writes the day's registration
/// report (<see cref="RegistrationDiagnostics"/>) to <c>{data}\Diagnostics</c>, opens it and exits. It runs beside a
/// running WorkTrace, never starts tracking and opens the database read-only.
/// </summary>
internal static class RegistrationReportCommand
{
    public const string Switch = "--registration-report";

    /// <summary>True when the command was given (and handled): the application should exit.</summary>
    public static bool TryRun(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        var day = index + 1 < args.Length && DateOnly.TryParseExact(args[index + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : DateOnly.FromDateTime(DateTime.Now);
        var names = args.Any(a => string.Equals(a, "--names", StringComparison.OrdinalIgnoreCase));
        try
        {
            var path = Task.Run(() => RegistrationDiagnostics.WriteAsync(AppPaths.Resolve(), day, names, TimeProvider.System)).GetAwaiter().GetResult();
            MessageBox.Show(
                $"Registration report for {day:yyyy-MM-dd} written to:\n{path}\n\nYour data was opened read-only and not changed. "
                + (names ? "Task and stakeholder names are included." : "Task and stakeholder names are replaced by numbers, so the file can be shared."),
                "WorkTrace diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not write the registration report: " + ex.Message, "WorkTrace diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return true;
    }
}
