using VRage.Utils;

namespace ClientPlugin.Tools;

internal static class Log
{
    public static void Error(string message) => MyLog.Default.WriteLine($"{Plugin.Name}: ERROR: {message}");
}
