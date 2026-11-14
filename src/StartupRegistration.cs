namespace VgnBattery;

internal static class StartupRegistration
{
    internal static string BuildCommand(string? executable, string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("找不到程序路径");

        string command = "\"" + executable + "\"";
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            command += " \"" + Path.Combine(applicationDirectory, "VgnBattery.dll") + "\"";
        return command;
    }

    internal static bool MatchesCurrentCommand(string? registeredCommand, string currentCommand) =>
        !string.IsNullOrWhiteSpace(registeredCommand)
        && string.Equals(registeredCommand.Trim(), currentCommand, StringComparison.OrdinalIgnoreCase);

    internal static bool NeedsMigration(string? registeredCommand, string currentCommand) =>
        !string.IsNullOrWhiteSpace(registeredCommand)
        && !MatchesCurrentCommand(registeredCommand, currentCommand);
}
