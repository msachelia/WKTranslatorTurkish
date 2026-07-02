using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace WKTranslator;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    public static Plugin Instance;

    private ConfigEntry<string> _langKey;
    public static readonly Dictionary<string, string> Translations = new();
    public static readonly Dictionary<string, Texture2D> TextureRegistry = new();
    public static readonly Dictionary<string, AudioClip> AudioRegistry = new();


    public static TMP_FontAsset CustomFontAsset;

    public static TextAsset CustomSubtitleAsset;

    private string PluginDir => Path.Combine(Paths.PluginPath, "WKTranslator");

    private List<TranslationFolder> _translationFolders = [];

    private List<string> _allowedImageTypes = ["png"];

    private List<string> _allowedAudioTypes = [
        "wav",
        "ogg",
        "mp3"
    ];

    private void Awake()
    {
        if (Instance == null || Instance != this)
            Instance = this;
        // Initialize logger
        LogManager.Initialize(Logger);

        // Config Entry
        _langKey = Config.Bind("General", "LanguageKey", "en",
            "Select the language corresponding to the translation JSON\ne.g.: cz");
        // Hot Reload
        _langKey.SettingChanged += (_, __) => ReloadLanguage();

        _translationFolders = TranslationScanner.Scan(Paths.PluginPath);

        // Setup
        ReloadLanguage();

        // Patch
        ApplyHarmonyPatches();

        // Plugin startup logic
        LogManager.Info($"Plugin {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} is loaded!");

        // Set Console Command
        CreateWKTranslationManagerObject();
        LogManager.Info("Added command for dumping text!");

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CreateWKTranslationManagerObject();
    }

    public void ReloadLanguage()
    {
        ClearAll();

        _translationFolders = TranslationScanner.Scan(Paths.PluginPath);

        var tf = _translationFolders.FirstOrDefault(t => t.Config.LanguageKey == _langKey.Value);
        LogManager.Info(tf);
        if (tf == null)
        {
            LogManager.Error($"Translation '{_langKey.Value}' not found or invalid.");
            return;
        }

        // Load JSON translations
        var jsonPath = Path.Combine(tf.FolderPath, tf.Config.ConfigFileName);
        FontLoader.LoadCustomFont(tf.FolderPath);
        CustomFontAsset = FontLoader.CustomFont;
        if (CustomFontAsset is null)
            LogManager.Info("No Custom Font Loaded!");
        else
            LogManager.Info("Loaded Custom font!");
        LoadTranslations(tf.FolderPath, tf.Config);
        LoadTextures(Path.Combine(tf.FolderPath, "Textures"));
        LoadAudio(Path.Combine(tf.FolderPath, "Audio"));

        LogManager.Info($"Loaded translation '{tf.Config.LanguageName}' by {string.Join(", ", tf.Config.Authors)}.");
    }

    private static void CreateWKTranslationManagerObject()
    {
        var newGameObject = new GameObject("WKTranslationManager");
        newGameObject.AddComponent<WKTranslationManager>();
        DontDestroyOnLoad(newGameObject);
    }

    private void ClearAll()
    {
        Translations.Clear();
        DynamicTranslation.Clear();
        TextureRegistry.Clear();
        AudioRegistry.Clear();
    }

    // Looks up a translation for `original`, first as an exact match, then
    // falling back to placeholder-based ({0}, {1}, ...) whole-string matching.
    public static bool TryGetTranslation(string original, out string translated)
    {
        if (Translations.TryGetValue(original, out translated)) return true;
        return DynamicTranslation.TryTranslate(original, out translated);
    }

    private void LoadTranslations(string filepath, TranslationConfig config)
    {
        LogManager.Info($"Loading translations from '{filepath}'.");

        var file = Path.Combine(filepath, $"{config.LanguageKey}.json");

        Translations.Clear();
        DynamicTranslation.Clear();

        if (!File.Exists(file))
        {
            LogManager.Error($"Translation '{file}' not found or invalid.");
            return;
        }

        try
        {
            string jsonContent = File.ReadAllText(file);
            JObject root = JObject.Parse(jsonContent);

            foreach (var property in root.Properties())
            {
                ProcessTranslationToken(property.Name, property.Value);
            }

            LogManager.Info($"Loaded {Translations.Count} exact, {DynamicTranslation.Count} template " +
                             $"translations from {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            LogManager.Error($"Failed to load translations from '{file}': {ex.Message}");
        }
    }

    // Recursively walks the translation JSON, routing each entry to the
    // exact-match dictionary or the {n}-placeholder whole-string template
    // matcher, based on whether the key contains placeholders:
    //   "Key": "Value"               -> exact match, or {n}-placeholder template if Key has placeholders
    //   "Category": { "Key": "Value", ... } -> recurse (categories can nest arbitrarily deep)
    private void ProcessTranslationToken(string keyName, JToken token)
    {
        if (token.Type == JTokenType.Object)
        {
            // Format: "CategoryName": { "Original": "Translated", ... }
            // We treat the key as the unique identifier; if duplicates exist
            // across categories, the first one loaded wins.
            foreach (var sub in ((JObject)token).Properties())
                ProcessTranslationToken(sub.Name, sub.Value);
            return;
        }

        if (token.Type != JTokenType.String) return;

        string value = token.ToString();
        if (string.IsNullOrEmpty(value.Trim())) return;

        // Format: "Original": "Translated"
        Translations.TryAdd(keyName, value);
        DynamicTranslation.TryRegister(keyName, value);
    }

    private void LoadTextures(string dir)
    {
        TextureRegistry.Clear();

        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.GetFiles(dir))
        {
            if (!_allowedImageTypes.Contains(Path.GetExtension(file)[1..]))
                continue;

            var key = Path.GetFileNameWithoutExtension(file);
            var bytes = File.ReadAllBytes(file);
            var texture = new Texture2D(2, 2);
            texture.LoadImage(bytes);
            TextureRegistry.Add(key, texture);
        }
    }

    private async void LoadAudio(string dir)
    {
        try
        {
            AudioRegistry.Clear();

            if (!Directory.Exists(dir))
                return;

            foreach (var file in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(file)[1..];

                if (!_allowedAudioTypes.Contains(ext))
                    continue;

                var key = Path.GetFileNameWithoutExtension(file);
                AudioRegistry.TryAdd(key, await LoadSound(file, ext));
            }
        }
        catch
        {
            /**/
        }
    }

    private void ApplyHarmonyPatches()
    {
        var harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}.patches");
        harmony.PatchAll();
    }

    #region Patches

    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Resources))]
    public static class ResourcesPatches
    {
        [HarmonyPatch("Load", typeof(string), typeof(Type))]
        public static bool LoadPatch(string path, Type systemTypeInstance, ref Object __result)
        {
            if (systemTypeInstance == typeof(TextAsset) && path.Contains("en") && CustomSubtitleAsset is not null)
            {
                __result = CustomSubtitleAsset;
                return false;
            }

            if (systemTypeInstance == typeof(Material))
            {
                LogManager.Warn($"this is spriteRegistry Keys: {TextureRegistry.Keys}");
                Material mat = (Material)__result;
                LogManager.Warn($"This is the material that was loaded: {mat.name}");
                if (TextureRegistry.TryGetValue(mat.mainTexture.name, out var texture))
                {
                    LogManager.Error("Trying to replace the texture with new material idk...");
                    mat.mainTexture = texture;
                    __result = mat;
                    return false;
                }
            }

            return true;
        }
    }

    #endregion

    private static async Task<AudioClip> LoadSound(string filename, string type)
    {
        AudioClip audioClip = null;
        var audioType = type.ToLower() switch
        {
            "wav" => AudioType.WAV,
            "ogg" => AudioType.OGGVORBIS,
            "mp3" => AudioType.MPEG,
            _ => AudioType.UNKNOWN
        };

        if (audioType == AudioType.UNKNOWN)
            return null;

        var uwr = new UnityWebRequest(filename, UnityWebRequest.kHttpVerbGET)
        {
            downloadHandler = new DownloadHandlerAudioClip(filename, audioType)
        };
        var dh = (DownloadHandlerAudioClip)uwr.downloadHandler;
        dh.streamAudio = false;
        dh.compressed = true;

        uwr.SendWebRequest();

        try
        {
            while (!uwr.isDone) await Task.Delay(5);

            if (uwr.result == UnityWebRequest.Result.ConnectionError || uwr.result == UnityWebRequest.Result.ProtocolError) LogManager.Error(uwr.error);
            else audioClip = dh.audioClip;
        }
        catch (Exception e)
        {
            LogManager.Error($"{e.Message}\n{e.StackTrace}");
        }

        return audioClip;
    }
}