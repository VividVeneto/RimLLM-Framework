using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DevTools.Testing;
using Verse;

namespace RimLLM_Framework.InGameTests;

/// <summary>
/// 語系檔是否真的被遊戲注入 keyedReplacements。
/// headless 的 CoreTests 只比對 XML 檔之間的 key 一致性；資料夾名稱對不上 LoadedLanguage、
/// 遊戲的 XML 解析器拒收、或 key 被當成佔位符，只有遊戲內看得到。
/// </summary>
[TestFixture(TestType.MainMenu)]
internal sealed class TranslationTests
{
    [Test]
    public void EveryKeyedFileIsInjectedIntoItsLanguage()
    {
        var failures = new List<string>();
        string languagesDir = Path.Combine(TestHelpers.FrameworkContent.RootDir, "Languages");

        foreach (string languageDir in Directory.GetDirectories(languagesDir))
        {
            string folder = Path.GetFileName(languageDir);
            // Core 的資料夾名稱帶母語後綴（例如 "ChineseTraditional (繁體中文)"），Mod 用的是去掉後綴的 LegacyFolderName。
            LoadedLanguage language = LanguageDatabase.AllLoadedLanguages
                .FirstOrDefault(l => l.folderName == folder || l.LegacyFolderName == folder);
            if (language == null)
            {
                failures.Add($"{folder}: no LoadedLanguage matches this folder name");
                continue;
            }

            // 非目前語言的資料是延遲載入的；刻意不呼叫 LoadData 強制載入——那會把整個語言（所有 Mod 的
            // Keyed 與 DefInjected）留在記憶體直到遊戲結束。要涵蓋其他語言，請切換遊戲語言再跑一次。
            if (language.keyedReplacements.Count == 0)
            {
                TestHelpers.LogSkipped(nameof(EveryKeyedFileIsInjectedIntoItsLanguage), $"{folder} is not loaded (switch the game language to cover it)");
                continue;
            }

            string keyedDir = Path.Combine(languageDir, "Keyed");
            if (!Directory.Exists(keyedDir)) continue;
            string ownKeyedDir = keyedDir.Replace('\\', '/');
            var ownKeys = new HashSet<string>();
            foreach (string file in Directory.GetFiles(keyedDir, "*.xml", SearchOption.AllDirectories))
            {
                foreach (XElement element in XDocument.Load(file).Root.Elements())
                {
                    string key = element.Name.LocalName;
                    ownKeys.Add(key);
                    if (!language.keyedReplacements.TryGetValue(key, out LoadedLanguage.KeyedReplacement replacement))
                    {
                        failures.Add($"{folder}/{Path.GetFileName(file)}: {key} not injected");
                    }
                    else if (replacement.isPlaceholder)
                    {
                        failures.Add($"{folder}/{Path.GetFileName(file)}: {key} injected as placeholder");
                    }
                    else if (!(replacement.fileSourceFullPath ?? "").Replace('\\', '/').StartsWith(ownKeyedDir, StringComparison.OrdinalIgnoreCase))
                    {
                        // key 存在但來源是別的 Mod：本 Mod 的檔案沒被採用，或被後載入的 Mod 覆蓋。
                        failures.Add($"{folder}/{Path.GetFileName(file)}: {key} supplied by {replacement.fileSourceFullPath}");
                    }
                }
            }

            // 載入錯誤是整個語言共用的清單（Core 與其他 Mod 的錯誤也在裡面），只挑出屬於本 Mod 的。
            string ownRoot = TestHelpers.FrameworkContent.RootDir.Replace('\\', '/');
            foreach (string error in language.loadErrors)
            {
                bool ownPath = error.Replace('\\', '/').IndexOf(ownRoot, StringComparison.OrdinalIgnoreCase) >= 0;
                bool ownDuplicate = ownKeys.Any(k => error.StartsWith($"Duplicate keyed translation key: {k} ", StringComparison.Ordinal));
                if (ownPath || ownDuplicate)
                {
                    failures.Add($"{folder}: load error: {error}");
                }
            }
        }
        TestHelpers.AssertNone(failures, "keyed translations not injected");
    }
}
