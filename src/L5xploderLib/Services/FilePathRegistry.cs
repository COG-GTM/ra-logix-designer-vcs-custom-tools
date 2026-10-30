namespace L5xploderLib.Services;

internal sealed class FilePathRegistry
{
    private readonly HashSet<string> _usedFilePaths = new(StringComparer.OrdinalIgnoreCase);

    public string FindUnreservedFilePath(string folderPath, string baseFileName)
    {
        string fullPath = Path.Combine(folderPath, baseFileName);

        int counter = 1;

        while (IsReserved(fullPath))
        {
            fullPath = Path.Combine(folderPath, $"{baseFileName}_{counter}");
            counter++;
        }

        Reserve(fullPath);

        return fullPath;
    }

    public void Reserve(string filePath) => _usedFilePaths.Add(FileHelpers.NormalizeRelativePath(filePath));

    public bool IsReserved(string filePath) => _usedFilePaths.Contains(FileHelpers.NormalizeRelativePath(filePath));
}