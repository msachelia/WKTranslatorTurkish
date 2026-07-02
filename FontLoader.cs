using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace WKTranslator;

public static class FontLoader
{
    private static readonly Dictionary<string, TMP_FontAsset> _tmpCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Font> _legacyCache = new(StringComparer.OrdinalIgnoreCase);

    // Kept for backward compatibility with call sites that just want a single
    // "best" custom font rather than doing per-component name matching.
    public static TMP_FontAsset CustomFont;

    public static void LoadCustomFont(string folderPath)
    {
        _tmpCache.Clear();
        _legacyCache.Clear();
        CustomFont = null;

        // Asset bundles take priority over a loose .ttf/.otf if both are present,
        // since a bundle can carry multiple pre-baked fonts (and legacy Font support).
        if (TryLoadFromBundle(folderPath))
        {
            return;
        }

        LoadFromTrueTypeFile(folderPath);
    }

    private static bool TryLoadFromBundle(string folderPath)
    {
        string bundlePath = Path.Combine(folderPath, "customfonts");
        if (!File.Exists(bundlePath)) return false;

        var bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            LogManager.Error($"Failed to load font bundle at '{bundlePath}'.");
            return false;
        }

        foreach (var font in bundle.LoadAllAssets<TMP_FontAsset>())
        {
            string name = font.name.Trim();
            _tmpCache[name] = font;
            LogManager.Info($"Loaded TMP font from bundle: {name}");
        }

        foreach (var font in bundle.LoadAllAssets<Font>())
        {
            string name = font.name.Trim();
            _legacyCache[name] = font;
            LogManager.Info($"Loaded Legacy font from bundle: {name}");
        }

        bundle.Unload(false);

        if (_tmpCache.Count == 0 && _legacyCache.Count == 0)
        {
            LogManager.Warn($"Font bundle at '{bundlePath}' contained no usable TMP_FontAsset or Font assets.");
            return false;
        }

        // Pick a single "default" font for legacy call sites (FontLoader.CustomFont).
        if (!_tmpCache.TryGetValue("default", out CustomFont) && _tmpCache.Count > 0)
        {
            foreach (var font in _tmpCache.Values)
            {
                CustomFont = font;
                break;
            }
        }

        RegisterFallback(CustomFont);

        return true;
    }

    private static void LoadFromTrueTypeFile(string folderPath)
    {
        // Look for .ttf or .otf files in the translation folder
        string[] fontFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly);
        string fontPath = null;

        foreach (var file in fontFiles)
        {
            if (file.EndsWith(".ttf") || file.EndsWith(".otf"))
            {
                fontPath = file;
                break;
            }
        }

        if (string.IsNullOrEmpty(fontPath)) return;

        var unityFont = new Font(fontPath);

        // Create TMPro Asset
        CustomFont = TMP_FontAsset.CreateFontAsset(
            unityFont,
            90,
            9,
            GlyphRenderMode.SDFAA,
            1024,
            1024,
            AtlasPopulationMode.Dynamic,
            true
        );
        CustomFont.name = "WK_CustomFont";

        _tmpCache["default"] = CustomFont;

        RegisterFallback(CustomFont);

        // In theory they should work, but dunno
        LogManager.Info($"Loaded custom font: {Path.GetFileName(fontPath)}");
    }

    private static void RegisterFallback(TMP_FontAsset font)
    {
        if (font == null) return;

        var defaultFont = TMP_Settings.defaultFontAsset;
        if (defaultFont == null || defaultFont == font) return;

        defaultFont.fallbackFontAssetTable ??= [];

        if (!defaultFont.fallbackFontAssetTable.Contains(font))
        {
            defaultFont.fallbackFontAssetTable.Add(font);
        }
    }

    // Tries to match the text component's current font by name against the loaded
    // cache (bundle entries first, "default" as fallback), then falls back to
    // whatever single CustomFont was resolved (bundle "default"/first entry, or
    // the generated ttf/otf font).
    public static void TryReplace(TMP_Text text)
    {
        if (text == null) return;

        string currentFontName = text.font != null ? text.font.name.Trim() : "";

        if (_tmpCache.TryGetValue(currentFontName, out var found) ||
            _tmpCache.TryGetValue("default", out found))
        {
            if (text.font != found) text.font = found;
        }
        else if (CustomFont != null && text.font != CustomFont)
        {
            text.font = CustomFont;
        }
    }

    public static void TryReplace(Text text)
    {
        if (text == null || _legacyCache.Count == 0) return;

        string currentFontName = text.font != null ? text.font.name.Trim() : "";

        if (_legacyCache.TryGetValue(currentFontName, out var found) ||
            _legacyCache.TryGetValue("default", out found))
        {
            if (text.font != found) text.font = found;
        }
    }
}