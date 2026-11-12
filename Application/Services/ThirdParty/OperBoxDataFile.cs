namespace MAAUnified.Application.Services.ThirdParty;

/// <summary>
/// Locates the operator-box data file written by operator recognition and read by Core through
/// the Copilot <c>operbox_data_path</c> parameter (WPF 247db567f3 / 86e58bda95).
/// </summary>
public static class OperBoxDataFile
{
    /// <summary>
    /// Directory holding recognizer data files, relative to the runtime base directory.
    /// </summary>
    public const string DirectoryName = "data";

    /// <summary>
    /// File name used by WPF and MaaUnified for the operator-box payload.
    /// </summary>
    public const string FileName = "OperBoxData.json";

    /// <summary>
    /// Resolves the operator-box data file path. Returns an empty string when no base directory
    /// is available, so callers never guess a working directory.
    /// </summary>
    /// <param name="baseDirectory">Runtime base directory (the directory containing data/).</param>
    /// <returns>Absolute path, or an empty string.</returns>
    public static string ResolvePath(string? baseDirectory)
    {
        return string.IsNullOrWhiteSpace(baseDirectory)
            ? string.Empty
            : Path.Combine(baseDirectory, DirectoryName, FileName);
    }

    /// <summary>
    /// Resolves the operator-box data file path and verifies that the file exists.
    /// </summary>
    /// <param name="baseDirectory">Runtime base directory (the directory containing data/).</param>
    /// <param name="path">Resolved path when the file exists; otherwise an empty string.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    public static bool TryResolveExistingPath(string? baseDirectory, out string path)
    {
        path = ResolvePath(baseDirectory);
        if (path.Length == 0 || !File.Exists(path))
        {
            path = string.Empty;
            return false;
        }

        return true;
    }
}
