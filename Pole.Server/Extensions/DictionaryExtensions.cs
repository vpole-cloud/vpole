namespace Pole.Server.Extensions;

public static class DictionaryExtensions
{
    extension(Dictionary<string, string> environmentVariables)
    {
        public Dictionary<string, string> With(Dictionary<string, string> data)
        {
            foreach (var kvp in data)
            {
                environmentVariables[kvp.Key] = kvp.Value;
            }

            return environmentVariables;
        }
        
        
    }
}