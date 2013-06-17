namespace Onyx.XPatch.Console.Domain
{
    public class Config
    {
        public Config(string code, string configPath, string testFilePath)
        {
            Code = code;
            ConfigPath = configPath;
            TestFilePath = testFilePath;
        }

        public string Code { get; private set; }
        public string ConfigPath { get; private set; }
        public string TestFilePath { get; private set; }
    }
}
