namespace Verse
{
    internal static class GenFilePaths
    {
        public static string ConfigFolderPath => throw new NotSupportedException("Use the isolated test path.");
    }
}
namespace RimLLM_Framework
{
    public enum LLMError { Unknown }
    public sealed class RimLLMException(LLMError error, string message, Exception inner) : Exception(message, inner)
    {
        public LLMError Error { get; } = error;
    }
}
namespace RimLLM_Framework.Core
{
    internal static class RimLLMLog
    {
        public static void Warning(string value) => Console.WriteLine("Expected diagnostic: " + value);
        public static void Error(string value) => Console.WriteLine("Expected diagnostic: " + value);
    }
}
