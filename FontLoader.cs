using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace WKTranslator;

public static class FontLoader
{
    public static TMP_FontAsset CustomFont;
    
    public static void LoadCustomFont(string folderPath)
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
        
        var defaultFont = TMP_Settings.defaultFontAsset;
        if (defaultFont != null)
        {
            if (defaultFont.fallbackFontAssetTable == null)
            {
                defaultFont.fallbackFontAssetTable = [];
            }
            
            if (!defaultFont.fallbackFontAssetTable.Contains(CustomFont))
            {
                defaultFont.fallbackFontAssetTable.Add(CustomFont);
            }
        }
        
        // In theory they should work, but dunno
        LogManager.Info($"Loaded custom font: {Path.GetFileName(fontPath)}");
    }
}