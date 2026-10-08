using System.IO;
using FeiqLight;

internal static class MakeIcon
{
    private static void Main(string[] args)
    {
        using (FileStream file = File.Create(args[0])) Theme.AppIcon.Save(file);
    }
}
