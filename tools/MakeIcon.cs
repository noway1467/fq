using System.IO;
using FeiqLight;

internal static class MakeIcon
{
    private static void Main(string[] args)
    {
        File.WriteAllBytes(args[0], Theme.AppIconBytes());
    }
}
