namespace WorkTrace.App.Shell;

/// <summary>Controlled application shutdown (tray Exit, close-to-tray disabled, ...).</summary>
public interface IAppLifetime
{
    bool IsExiting { get; }

    void RequestExit();
}
