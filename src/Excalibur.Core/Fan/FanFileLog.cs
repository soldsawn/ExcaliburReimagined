namespace Excalibur.Core.Fan;

/// <summary>
/// Plain daily log files, e.g. %ProgramData%\ExcaliburReimagined\logs\fan-YYYYMMDD.log.
/// Shared by the fan service ("fan") and the guardian ("guardian").
/// </summary>
public sealed class FanFileLog(string directory, string prefix)
{
    private readonly object _gate = new();

    public void Write(DateTimeOffset at, string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"{prefix}-{at:yyyyMMdd}.log"), $"{at:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            // Never let logging take the fan loop down (a full disk, a locked file or a bad ACL).
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
