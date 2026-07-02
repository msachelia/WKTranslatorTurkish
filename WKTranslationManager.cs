using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace WKTranslator;

public class WKTranslationManager : MonoBehaviour
{
    public static WKTranslationManager Instance;
    private static readonly int MainTexture = Shader.PropertyToID("_MainTex");

    private static List<string> untranslatedText = [];

    public void Awake()
    {
        if (Instance is not null && Instance != this)
        {
            LogManager.Warn("Destroying duplicate WKTranslationManager");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        LogManager.Info("WKTranslationManager Awake");
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    public void DumpAllHiddenAssets()
    {
        // This attempts to load everything in the game's Resources folders
        Object[] allObjects = Resources.LoadAll("", typeof(GameObject));

        foreach (Object obj in allObjects)
        {
            GameObject go = obj as GameObject;
            if (go == null) continue;

            // Search the prefab/object for Legacy Text
            var txts = go.GetComponentsInChildren<Text>(true);
            foreach (var t in txts) AddToDict(t.text);

            // Search for TextMeshPro
            var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var t in tmps) AddToDict(t.text);
        }
    }

    public void DumpStringsFromCode()
    {
        // Target "Assembly-CSharp", which is where the game's specific logic lives
        var asm = Assembly.Load("Assembly-CSharp");

        foreach (var type in asm.GetTypes())
        {
            // Look for static string fields (common for constants/dialogue)
            var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (field.FieldType == typeof(string))
                {
                    string val = (string)field.GetValue(null);
                    if (!string.IsNullOrEmpty(val)) AddToDict(val);
                }
            }
        }
    }

    public void DumpAllScriptableObjects()
    {
        // Find every ScriptableObject currently loaded in memory
        // This includes data files the game uses for dialogue, items, etc.
        ScriptableObject[] allData = Resources.FindObjectsOfTypeAll<ScriptableObject>();

        foreach (var data in allData)
        {
            if (data == null) continue;

            // Use reflection to find every string field inside this object
            FieldInfo[] fields = data.GetType().GetFields(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            foreach (var field in fields)
            {
                if (field.FieldType == typeof(string))
                {
                    string val = (string)field.GetValue(data);
                    if (!string.IsNullOrEmpty(val)) AddToDict(val);
                }
                // Also check for Lists or Arrays of strings
                else if (field.FieldType == typeof(string[]) || field.FieldType == typeof(List<string>))
                {
                    var list = field.GetValue(data) as IEnumerable<string>;
                    if (list != null)
                    {
                        foreach (var s in list) AddToDict(s);
                    }
                }
            }
        }
    }

    public void DumpAllPrefabs()
    {
        // This finds every GameObject Unity has in memory, including un-instantiated Prefabs
        GameObject[] allPrefabs = Resources.FindObjectsOfTypeAll<GameObject>();

        foreach (GameObject go in allPrefabs)
        {
            // Search for any component that might hold text
            // (Legacy Text, TextMeshPro, or even custom 'Name' scripts)
            Component[] components = go.GetComponentsInChildren<Component>(true);
            foreach (var comp in components)
            {
                if (comp == null) continue;

                // Use reflection to find any string property named "text" or "m_text"
                // This catches almost all UI types without needing to reference their specific DLLs
                var prop = comp.GetType().GetProperty("text") ?? comp.GetType().GetProperty("m_text");
                if (prop != null && prop.PropertyType == typeof(string))
                {
                    string val = (string)prop.GetValue(comp);
                    if (!string.IsNullOrEmpty(val)) AddToDict(val);
                }
            }
        }
    }

    public void LoadEverythingThenDump()
    {
        // Warning: This will likely freeze the game for a minute and use lots of RAM
        var bundles = AssetBundle.GetAllLoadedAssetBundles();
        foreach (var bundle in bundles)
        {
            bundle.LoadAllAssets();
        }
    }


    public void AddToDict(string txt)
    {
        if (untranslatedText.Contains(txt)) return;
        untranslatedText.Add(txt);
    }

    public void DumpTheText()
    {
        LoadEverythingThenDump();

        DumpAllHiddenAssets();
        DumpStringsFromCode();
        DumpAllScriptableObjects();
        DumpAllPrefabs();

        var pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (pluginFolder == null) return;

        var filePath = Path.Combine(pluginFolder, "untranslated.json");

        Dictionary<string, string> untranslatedDict = new();
        foreach (var text in untranslatedText)
        {
            // Simple check to avoid duplicate keys if untranslatedText has repeats
            untranslatedDict.TryAdd(text, text);
        }

        // Use JsonConvert for dictionaries + Formatting.Indented for readability
        var json = JsonConvert.SerializeObject(untranslatedDict, Formatting.Indented);

        // Fix: WriteAllText handles creating/opening/writing/closing automatically
        File.WriteAllText(filePath, json);
    }

    public async void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CommandConsole.AddCommand("dumpalltext", _ => DumpTheText(), false);
        if (!CanContinueScene(scene.name)) return;

        await PrepareAsync();

        ReplaceAllMaterial();
        ReplaceTextures();
        ReplaceAllMaterial();
        ReplaceOnSources();

        CommandConsole.AddCommand("startscanner", _ => TextScanner.RunScanner(), false);
        CommandConsole.AddCommand("endscanner", _ => TextScanner.StopScanner(), false);
        CommandConsole.AddCommand("reloadtranslation", _ => Plugin.Instance.ReloadLanguage(), false);
        LogManager.Warn("Added commands");
    }

    private async Task PrepareAsync()
    {
        LogManager.Info($"Scanning scene: {SceneManager.GetActiveScene().name} for static text...");

        // Find ALL TextMeshPro objects (including inactive ones in menus)
        // then filter to ensure they belong to the current scene.
        TMP_Text[] allTmp = Resources.FindObjectsOfTypeAll<TMP_Text>();
        foreach (var txt in allTmp)
        {
            // Safety check: ensure the object is actually part of the loaded scene 
            if (ValidForTranslation(txt.gameObject))
            {
                TryTranslate(txt);
            }
        }

        // Do the same for Legacy Text (just in case)
        Text[] allLegacy = Resources.FindObjectsOfTypeAll<Text>();
        foreach (var txt in allLegacy)
        {
            if (ValidForTranslation(txt.gameObject))
            {
                TryTranslateLegacy(txt);
            }
        }
    }

    #region Text Replacement

    private bool ValidForTranslation(GameObject go)
    {
        // We only want to translate objects that are in the scene or DontDestroyOnLoad
        // ignoring "assets" (prefabs) that haven't been instantiated yet.
        return go.scene.isLoaded || go.scene.name == "DontDestroyOnLoad";
    }

    // Helper method to apply translation
    public static void TryTranslate(TMP_Text txtComponent)
    {
        if (txtComponent == null || string.IsNullOrEmpty(txtComponent.text)) return;

        if (Plugin.TryGetTranslation(txtComponent.text, out string translated))
        {
            if (txtComponent.text == translated) return;

            txtComponent.text = translated;

            FontLoader.TryReplace(txtComponent);

            txtComponent.enableAutoSizing = true;

            txtComponent.fontSizeMax = txtComponent.fontSize;
            txtComponent.fontSizeMin = 12f;

            txtComponent.enableWordWrapping = false;

            if (txtComponent.gameObject.activeInHierarchy)
            {
                LayoutRebuilder.MarkLayoutForRebuild(txtComponent.rectTransform);
            }
        }
    }

    // Helper method to apply legacy translation
    public static void TryTranslateLegacy(Text txtComponent)
    {
        if (txtComponent == null || string.IsNullOrEmpty(txtComponent.text)) return;

        if (Plugin.TryGetTranslation(txtComponent.text, out string translated))
        {
            if (txtComponent.text == translated) return;

            txtComponent.text = translated;

            FontLoader.TryReplace(txtComponent);

            txtComponent.resizeTextForBestFit = true;
            txtComponent.resizeTextMaxSize = Mathf.FloorToInt(txtComponent.fontSize);
            txtComponent.resizeTextMinSize = 10;

            txtComponent.horizontalOverflow = HorizontalWrapMode.Overflow;

            if (txtComponent.gameObject.activeInHierarchy)
            {
                LayoutRebuilder.MarkLayoutForRebuild(txtComponent.rectTransform);
            }
        }
    }

    [HarmonyPatch(typeof(TMP_Text))]
    public static class TMPTextPatches
    {
        [HarmonyPatch("text", MethodType.Setter), HarmonyPrefix]
        public static bool PrefixText(TMP_Text __instance, ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return true;

            //untranslatedText.Add(__0);

            if (Plugin.TryGetTranslation(__0, out var tr))
            {
                __0 = tr;

                FontLoader.TryReplace(__instance);
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Text))]
    public static class LegacyTextPatches
    {
        [HarmonyPatch("text", MethodType.Setter), HarmonyPrefix]
        public static bool PrefixText(Text __instance, ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return true;

            //untranslatedText.Add(__0);

            if (Plugin.TryGetTranslation(__0, out var tr))
            {
                __0 = tr;

                FontLoader.TryReplace(__instance);
            }

            return true;
        }
    }

    #endregion

    #region Material Replacement

    private void ReplaceAllMaterial()
    {

        foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (!material.HasProperty(MainTexture)) continue;
            if (material?.mainTexture is null) continue;

            LogManager.Debug($"Found {material.mainTexture.name}");

            Texture2D tex;
            if (!Plugin.TextureRegistry.ContainsKey("_ALL"))
            {
                Plugin.TextureRegistry.TryGetValue(material.mainTexture.name, out tex);
            }
            else
            {
                tex = Plugin.TextureRegistry.First(p => p.Key == "_ALL").Value;
            }

            if (tex == null) continue;

            LogManager.Debug($"Replacing {material.mainTexture.name}");
            material.mainTexture = tex;
        }

    }

    private void ReplaceTextures()
    {
        foreach (var img in Resources.FindObjectsOfTypeAll<Image>())
        {
            if (img.sprite is null) continue;

            Texture2D newTex;

            if (!Plugin.TextureRegistry.ContainsKey("_ALL"))
            {
                if (!Plugin.TextureRegistry.TryGetValue(img.sprite.name, out newTex)) continue;
            }
            else
            {
                newTex = Plugin.TextureRegistry.First(p => p.Key == "_ALL").Value;
            }



            var oldSprite = img.sprite;

            var fullRect = new Rect(0, 0, newTex.width, newTex.height);

            var newSprite = Sprite.Create(
                newTex,
                fullRect,
                new Vector2(0.5f, 0.5f),
                oldSprite.pixelsPerUnit,
                0,
                SpriteMeshType.FullRect,
                oldSprite.border
            );

            img.sprite = newSprite;
            img.overrideSprite = newSprite;

            if (img.rectTransform is not null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(img.rectTransform);

            // LogManager.Debug($"Replaced texture: {img.sprite.name}");
        }
    }

    [HarmonyPatch(typeof(Sprite))]
    private static class SpritePatches
    {
        [HarmonyPatch("texture", MethodType.Getter), HarmonyPostfix]
        private static void PostFix_texture(ref Texture2D __result)
        {
            if (__result is null) return;
            Texture2D texture;
            if (!Plugin.TextureRegistry.ContainsKey("_ALL"))
                if (!Plugin.TextureRegistry.TryGetValue(__result.name, out texture)) return;

            texture = Plugin.TextureRegistry.First(p => p.Key == "_ALL").Value;
            __result = texture;
        }
    }

    static void OverrideRendererTexture(SpriteRenderer sr, Texture2D newTex)
    {
        var mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(MainTexture, newTex);
        sr.SetPropertyBlock(mpb);
        //LogManager.Debug($"[MaterialOverride] {sr.gameObject.name}: _MainTex → {newTex.name}");
    }

    [HarmonyPatch(typeof(SpriteRenderer))]
    private static class SpriteRendererTextureOverridePatch
    {
        // Postfix on the sprite setter so every time `.sprite = ...` runs
        [HarmonyPatch("sprite", MethodType.Setter), HarmonyPostfix]
        private static void Postfix_SetSprite(SpriteRenderer __instance)
        {
            try
            {
                if (__instance is null) return;
                var spr = __instance.sprite;

                var origTexName = spr?.texture?.name;
                if (origTexName == null) return;

                Texture2D replacement;

                if (!Plugin.TextureRegistry.ContainsKey("_ALL"))
                {
                    Plugin.TextureRegistry.TryGetValue(origTexName, out replacement);
                }
                else
                {
                    replacement = Plugin.TextureRegistry.First(p => p.Key == "_ALL").Value;
                }

                if (replacement != null)
                    OverrideRendererTexture(__instance, replacement);
            }
            catch (Exception ex)
            {
                LogManager.Debug($"[MaterialOverride] failed on {__instance?.name}: {ex}");
            }
        }
    }

    #endregion

    #region AudioReplacement

    private void ReplaceOnSources()
    {
        foreach (var src in FindObjectsOfType<AudioSource>(true))
        {
            if (src.clip is null) continue;

            var clipName = src.clip.GetName();
            AudioClip newClip;

            if (!Plugin.AudioRegistry.ContainsKey("_ALL"))
            {
                if (!Plugin.AudioRegistry.TryGetValue(clipName, out newClip)) continue;
            }
            else
            {
                newClip = Plugin.AudioRegistry.First(p => p.Key == "_ALL").Value;
            }
            if (newClip is null) continue;

            var wasPlaying = src.isPlaying;
            var playingOnAwake = src.playOnAwake;
            src.clip = newClip;

            if (wasPlaying || playingOnAwake)
                src.Play();
            LogManager.Debug($"Replaced AudioSource Clip on {src.gameObject.name} ({clipName})");
        }
    }

    [HarmonyPatch]
    private static class AudioSourcePatches
    {
        // Patch parameterless Play()
        [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Play), new Type[0])]
        [HarmonyPrefix]
        private static void Play_NoArgs_Postfix(AudioSource __instance)
            => SwapClip(__instance);

        // Patch Play(double delay)
        [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Play), new[] { typeof(double) })]
        [HarmonyPrefix]
        private static void Play_DelayDouble_Postfix(AudioSource __instance)
            => SwapClip(__instance);

        // Patch Play(ulong delaySamples)
        [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Play), new[] { typeof(ulong) })]
        [HarmonyPrefix]
        private static void Play_DelayUlong_Postfix(AudioSource __instance)
            => SwapClip(__instance);

        // Patch PlayOneShot(AudioClip)
        [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip) })]
        [HarmonyPrefix]
        private static void PlayOneShot_ClipOnly_Postfix(AudioSource __instance, ref AudioClip __0)
        {
            if (Plugin.AudioRegistry.TryGetValue(__0.name, out var clip))
                __0 = clip;
        }

        // Patch PlayOneShot(AudioClip, float volumeScale)
        [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip), typeof(float) })]
        [HarmonyPrefix]
        private static void PlayOneShot_ClipAndVolume_Postfix(AudioSource __instance, ref AudioClip __0)
        {
            if (Plugin.AudioRegistry.TryGetValue(__0.name, out var clip))
                __0 = clip;
        }

        // Shared logic
        private static void SwapClip(AudioSource src)
        {
            if (src?.clip is null)
                return;

            var name = src.clip.name;
            AudioClip clip;
            if (!Plugin.AudioRegistry.ContainsKey("_ALL"))
            {
                if (!Plugin.AudioRegistry.TryGetValue(name, out clip))
                    return;
            }
            else
            {
                clip = Plugin.AudioRegistry.First(p => p.Key == "_ALL").Value;
            }
            if (clip != null)
                src.clip = clip;
            // LogManager.Debug($"[PlayPatch] Swapped '{name}' → '{clip.name}'");
        }
    }

    #endregion

    #region Helper Methods

    private bool CanContinueScene(string sceneName)
    {
        return sceneName switch
        {
            _ => true
        };
    }

    #endregion
}