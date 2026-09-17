using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Web.Script.Serialization;

namespace supp
{
    public class BotRegistryDocument
    {
        public int version { get; set; } = 1;
        public List<BotProfile> bots { get; set; } = new List<BotProfile>();
    }

    public class BotRegistryService
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly string _runtimeDir;
        private readonly string _registryPath;
        private readonly string _assetsDir;

        public BotRegistryService(string runtimeDir, string assetsDir)
        {
            _runtimeDir = runtimeDir;
            _assetsDir = assetsDir;
            _registryPath = Path.Combine(_runtimeDir, "bots_registry.json");
        }

        public ObservableCollection<BotProfile> LoadOrCreate()
        {
            Directory.CreateDirectory(_runtimeDir);
            Directory.CreateDirectory(Path.Combine(_runtimeDir, "bots"));
            if (!File.Exists(_registryPath)) CreateDefaultBot();
            try
            {
                var doc = _json.Deserialize<BotRegistryDocument>(File.ReadAllText(_registryPath));
                return new ObservableCollection<BotProfile>(doc?.bots ?? new List<BotProfile>());
            }
            catch
            {
                CreateDefaultBot();
                var doc = _json.Deserialize<BotRegistryDocument>(File.ReadAllText(_registryPath));
                return new ObservableCollection<BotProfile>(doc?.bots ?? new List<BotProfile>());
            }
        }

        public void Save(IEnumerable<BotProfile> bots)
        {
            var doc = new BotRegistryDocument { version = 1, bots = new List<BotProfile>(bots) };
            File.WriteAllText(_registryPath, _json.Serialize(doc));
        }

        public string GetBotWorkspace(BotProfile bot)
        {
            return Path.Combine(_runtimeDir, bot.Folder.Replace('/', Path.DirectorySeparatorChar));
        }

        public string CreateBotWorkspace(BotProfile bot)
        {
            var workspace = GetBotWorkspace(bot);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(workspace, "logs"));
            CopyAsset("bot.py", Path.Combine(workspace, "bot.py"), true);
            CopyAsset("faq.json", Path.Combine(workspace, "faq.json"), false);
            CopyAsset("scenario.json", Path.Combine(workspace, "scenario.json"), false);
            CopyAsset("bot_config.json", Path.Combine(workspace, "bot_config.json"), false);
            var env = Path.Combine(workspace, ".env");
            if (!File.Exists(env)) File.WriteAllLines(env, new[] { "BOT_TOKEN=", "ADMIN_ID=", "BOTSUPP_PROXY_URL=" });
            return workspace;
        }

        private void CreateDefaultBot()
        {
            var bot = new BotProfile { Id = "default_bot", Name = "Демо-бот", Folder = "bots/default_bot", Enabled = true, Autostart = false, UseProxy = true, EntryPoint = "bot.py", Template = "scenario" };
            CreateBotWorkspace(bot);
            Save(new[] { bot });
        }

        private void CopyAsset(string fileName, string dest, bool overwrite)
        {
            var candidates = new[]
            {
                Path.Combine(_assetsDir, "RuntimeAssets", fileName),
                Path.Combine(_assetsDir, fileName),
                Path.GetFullPath(Path.Combine(_assetsDir, "..", fileName)),
                Path.GetFullPath(Path.Combine(_assetsDir, "..", "..", fileName))
            };
            foreach (var src in candidates)
            {
                if (!File.Exists(src)) continue;
                if (!overwrite && File.Exists(dest)) return;
                File.Copy(src, dest, overwrite);
                return;
            }
        }
    }
}
