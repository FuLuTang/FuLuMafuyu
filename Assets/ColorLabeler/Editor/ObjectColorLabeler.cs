using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Threading;          // ThreadPool用
using System.Text.RegularExpressions; // Regex用
using System.Text;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

// ---------- 設定データ本体(JSONシリアライズ可能) ----------
[Serializable]
public class ColorLabelInfo
{
    public string label;
    public float[] color; // RGBA
    public string iconPath = ""; // 相対パス
    public float[] iconTint = new float[] { 1, 1, 1, 1 };

    public ColorLabelInfo(string label, Color col, string icon = "")
    {
        this.label = label;
        this.color = new float[] { col.r, col.g, col.b, col.a };
        this.iconPath = icon;
        this.iconTint = new float[] { 194f / 255f, 194f / 255f, 194f / 255f, 1f };
    }
    public ColorLabelInfo() { label = ""; color = new float[] { 1, 1, 1, 1 }; iconPath = ""; }
    public Color ToColor()
    {
        if (color == null || color.Length != 4) return Color.white;
        return new Color(color[0], color[1], color[2], color[3]);
    }
    public void FromColor(Color col)
    {
        if (color == null || color.Length != 4) color = new float[4];
        color[0] = col.r; color[1] = col.g; color[2] = col.b; color[3] = col.a;
    }
    public Color IconTintColor()
    {
        if (iconTint == null || iconTint.Length != 4) return Color.white;
        return new Color(iconTint[0], iconTint[1], iconTint[2], iconTint[3]);
    }
}

[Serializable]
public class ColorLabelerSettingsData
{
    public List<ColorLabelInfo> colorPresets = new List<ColorLabelInfo>();
    public List<string> assetColorGuids = new List<string>();
    public List<int> assetColorIndices = new List<int>();
    public List<int> hierarchyInstanceIds = new List<int>();
    public List<int> hierarchyColorIndices = new List<int>();
}

public enum ColorLabelDrawMode { LeftBar, FullBar, Icon, FullBarWithIcon }

// ---------- ここが本体！全てこの static クラスに集約 ----------
[InitializeOnLoad]
public static class ObjectColorLabeler
{
    private const string SETTINGS_PATH = "Assets/ColorLabeler/colorlabeler_settings.json";

    private static ColorLabelerSettingsData _data;
    private static bool _loaded = false;
    private static bool _saving = false;

    public static ColorLabelerSettingsData Data
    {
        get
        {
            if (!_loaded) LoadSettings();
            return _data;
        }
    }

    public static List<ColorLabelInfo> colorPresets => Data.colorPresets;
    public static List<string> assetColorGuids => Data.assetColorGuids;
    public static List<int> assetColorIndices => Data.assetColorIndices;
    public static List<int> hierarchyInstanceIds => Data.hierarchyInstanceIds;
    public static List<int> hierarchyColorIndices => Data.hierarchyColorIndices;

    public static ColorLabelDrawMode drawMode
    {
        get => (ColorLabelDrawMode)EditorPrefs.GetInt("ObjectColorLabeler.DrawMode", (int)ColorLabelDrawMode.FullBarWithIcon);
        set => EditorPrefs.SetInt("ObjectColorLabeler.DrawMode", (int)value);
    }
    public static int filterActive
    {
        get => EditorPrefs.GetInt("ObjectColorLabeler.Filter", -1);
        set => EditorPrefs.SetInt("ObjectColorLabeler.Filter", value);
    }

    public static Dictionary<string, int> assetColorDict
    {
        get
        {
            var dict = new Dictionary<string, int>();
            for (int i = 0; i < assetColorGuids.Count && i < assetColorIndices.Count; i++)
                dict[assetColorGuids[i]] = assetColorIndices[i];
            return dict;
        }
    }
    public static Dictionary<int, int> hierarchyColorDict
    {
        get
        {
            var dict = new Dictionary<int, int>();
            for (int i = 0; i < hierarchyInstanceIds.Count && i < hierarchyColorIndices.Count; i++)
                dict[hierarchyInstanceIds[i]] = hierarchyColorIndices[i];
            return dict;
        }
    }

    static ObjectColorLabeler()
    {
        LoadSettings();
        CleanupInvalidIndices();
        EditorApplication.projectWindowItemOnGUI += OnProjectGUI;
        EditorApplication.projectWindowItemOnGUI += OnProjectWindowCustomRightClick;
        EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyGUI;
    }

    // ========================== データ操作 ==========================
    public static void SaveSettings()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            var dir = Path.GetDirectoryName(SETTINGS_PATH);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (colorPresets == null || colorPresets.Count == 0)
            {
                ResetPresetsToDefault();
            }

            var ser = new DataContractJsonSerializer(typeof(ColorLabelerSettingsData));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, _data);
                // BOMなしUTF-8で書き込み
                File.WriteAllText(SETTINGS_PATH, Encoding.UTF8.GetString(ms.ToArray()), new UTF8Encoding(false));
            }
            AssetDatabase.ImportAsset(SETTINGS_PATH);
        }
        finally { _saving = false; }
    }
    public static void LoadSettings()
    {
        if (_loaded) return;
        _loaded = true;

        if (!File.Exists(SETTINGS_PATH))
        {
            // デフォルト値を生成
            _data = new ColorLabelerSettingsData();
            ResetPresetsToDefault();   // presetsなどを追加
            SaveSettings();            // ここでファイル生成

            // 直後にファイルを読んで確実に反映（新規作成直後に実体を再ロード）
            try
            {
                var ser = new DataContractJsonSerializer(typeof(ColorLabelerSettingsData));
                byte[] raw = File.ReadAllBytes(SETTINGS_PATH);
                int offset = 0;
                if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) offset = 3;
                using (var ms = new MemoryStream(raw, offset, raw.Length - offset))
                {
                    _data = (ColorLabelerSettingsData)ser.ReadObject(ms);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[ColorLabeler] 初回JSON生成直後の再読込に失敗: " + ex.Message);
            }
            return;
        }

        // 既存ファイルがある場合
        try
        {
            var ser = new DataContractJsonSerializer(typeof(ColorLabelerSettingsData));
            byte[] raw = File.ReadAllBytes(SETTINGS_PATH);
            int offset = 0;
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) offset = 3;
            using (var ms = new MemoryStream(raw, offset, raw.Length - offset))
            {
                _data = (ColorLabelerSettingsData)ser.ReadObject(ms);
            }
            if (_data == null) throw new Exception("Load failed");
        }
        catch (Exception ex)
        {
            Debug.LogError("[ColorLabeler] JSON読み込みに失敗しました。データは破棄されません: " + ex.Message);
            _data = new ColorLabelerSettingsData();
            ResetPresetsToDefault();
        }
    }

    public static void SavePresets()
    {
        SaveSettings();
        CleanupInvalidIndices();
    }

    public static void CleanupInvalidIndices()
    {
        int validPresets = colorPresets.Count;
        bool changed = false;

        // 旧Projectラベル用のみ従来通りチェック
        for (int i = assetColorIndices.Count - 1; i >= 0; i--)
            if (assetColorIndices[i] < 0 || assetColorIndices[i] >= validPresets)
            {
                assetColorGuids.RemoveAt(i);
                assetColorIndices.RemoveAt(i);
                changed = true;
            }

        // --- Hierarchyラベルは完全リセット（コンポーネント方式移行のため） ---
        if (hierarchyInstanceIds.Count > 0 || hierarchyColorIndices.Count > 0)
        {
            hierarchyInstanceIds.Clear();
            hierarchyColorIndices.Clear();
            changed = true;
        }

        if (changed)
            SaveSettings();
    }

    //初期プリセット
    public static void ResetPresetsToDefault()
    {
        colorPresets.Clear();
        colorPresets.Add(new ColorLabelInfo
        {
            label = "レッド",
            color = new float[] { 1, 0.25f, 0.22f, 1 },
            iconPath = "Assets/ColorLabeler/Icon/user-fill.png",
            iconTint = new float[] { 1, 0.2509804f, 0.21960786f, 1 }
        });
        colorPresets.Add(new ColorLabelInfo
        {
            label = "オレンジ",
            color = new float[] { 1, 0.5f, 0.18f, 1 },
            iconPath = "Assets/ColorLabeler/Icon/t-shirt-fill.png",
            iconTint = new float[] { 1, 0.5019608f, 0.180392161f, 1 }
        });
        colorPresets.Add(new ColorLabelInfo
        {
            label = "イエロー",
            color = new float[] { 1, 1, 0.4f, 1 },
            iconPath = "Assets/ColorLabeler/Icon/hair_fill.png",
            iconTint = new float[] { 1, 1, 0.400000036f, 1 }
        });
        colorPresets.Add(new ColorLabelInfo
        {
            label = "グリーン",
            color = new float[] { 0.35f, 1, 0.4f, 1 },
            iconPath = "Assets/ColorLabeler/Icon/hammer-fill.png",
            iconTint = new float[] { 0.349019617f, 1, 0.400000036f, 1 }
        });
        colorPresets.Add(new ColorLabelInfo
        {
            label = "ブルー",
            color = new float[] { 0.4f, 0.7f, 1, 1 },
            iconPath = "Assets/ColorLabeler/Icon/triangle-fill.png",
            iconTint = new float[] { 0.400000036f, 0.7019608f, 1, 1 }
        });
        colorPresets.Add(new ColorLabelInfo
        {
            label = "パープル",
            color = new float[] { 0.75f, 0.5f, 1, 1 },
            iconPath = "",
            iconTint = new float[] { 0.7607843f, 0.7607843f, 0.7607843f, 1 }
        });
        colorPresets.Add(new ColorLabelInfo
        {
            label = "ホワイト",
            color = new float[] { 1, 1, 1, 1 },
            iconPath = "",
            iconTint = new float[] { 0.7607843f, 0.7607843f, 0.7607843f, 1 }
        });
        SaveSettings();
        CleanupInvalidIndices();
        EditorApplication.RepaintProjectWindow();
        EditorApplication.RepaintHierarchyWindow();
    }


    // ========== ラベル割当/解除/編集 ==========
    public static void SetAssetColor(string guid, int colorIndex)
    {
        int idx = assetColorGuids.IndexOf(guid);
        if (colorIndex < 0)
        {
            if (idx >= 0)
            {
                assetColorGuids.RemoveAt(idx);
                assetColorIndices.RemoveAt(idx);
            }
        }
        else
        {
            if (idx >= 0)
                assetColorIndices[idx] = colorIndex;
            else
            {
                assetColorGuids.Add(guid);
                assetColorIndices.Add(colorIndex);
            }
        }
        SaveSettings();
    }
    public static void SetHierarchyColor(int instanceID, int colorIndex, bool save = true)
    {
        int idx = hierarchyInstanceIds.IndexOf(instanceID);
        if (colorIndex < 0)
        {
            if (idx >= 0)
            {
                hierarchyInstanceIds.RemoveAt(idx);
                hierarchyColorIndices.RemoveAt(idx);
            }
        }
        else
        {
            if (idx >= 0)
                hierarchyColorIndices[idx] = colorIndex;
            else
            {
                hierarchyInstanceIds.Add(instanceID);
                hierarchyColorIndices.Add(colorIndex);
            }
        }
        if (save) SaveSettings();
    }
    public static void ClearAllHierarchyLabels()
    {
        foreach (var comp in UnityEngine.Object.FindObjectsOfType<ColorLabelComponent>(true))
        {
#if UNITY_EDITOR
        UnityEditor.Undo.DestroyObjectImmediate(comp); // Undo対応
#else
            GameObject.DestroyImmediate(comp);
#endif
        }
#if UNITY_EDITOR
    UnityEditor.EditorApplication.RepaintHierarchyWindow();
#endif
    }

    public static void ClearAllColors()
    {
        assetColorGuids.Clear();
        assetColorIndices.Clear();
        SaveSettings(); // Asset側
        ClearAllHierarchyLabels(); // Component削除
    }

    public static void ClearLabelColor(int colorIndex)
    {
        // Hierarchy（シーンオブジェクトのラベル解除）
        foreach (var comp in GameObject.FindObjectsOfType<ColorLabelComponent>(true))
        {
            if (comp.labelIndex == colorIndex)
            {
                Undo.DestroyObjectImmediate(comp);
            }
        }
        EditorApplication.RepaintHierarchyWindow();

        // Projectビュー（GUIDベース管理）も解除
        // assetColorIndices と assetColorGuids を一括で
        for (int i = assetColorIndices.Count - 1; i >= 0; i--)
        {
            if (assetColorIndices[i] == colorIndex)
            {
                assetColorGuids.RemoveAt(i);
                assetColorIndices.RemoveAt(i);
            }
        }
        SaveSettings();
        EditorApplication.RepaintProjectWindow();
    }


    public static void AssignLabel(string[] guids, int labelIndex)
    {
        foreach (var guid in guids)
            SetAssetColor(guid, labelIndex);
        SaveSettings();
        EditorApplication.RepaintProjectWindow();
    }
    public static void AssignHierarchyLabel(int[] instanceIDs, int labelIndex)
    {
        foreach (var id in instanceIDs)
            SetHierarchyColor(id, labelIndex);
        SaveSettings();
        EditorApplication.RepaintHierarchyWindow();
    }
    public static void Save() => SaveSettings();

    public static void SaveDrawMode()
    {
        EditorPrefs.SetInt("ObjectColorLabeler.DrawMode", (int)drawMode);
        EditorPrefs.SetInt("ObjectColorLabeler.Filter", filterActive);
    }
    public static void DrawColorMark(Rect rect, Color color)
    {
        switch (drawMode)
        {
            case ColorLabelDrawMode.LeftBar:
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 6, rect.height), color * new Color(1, 1, 1, 0.8f));
                break;
            case ColorLabelDrawMode.FullBar:
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, rect.height), color * new Color(1, 1, 1, 0.13f));
                break;
            case ColorLabelDrawMode.Icon:
                DrawOnlyIcon(rect, color);
                break;
            case ColorLabelDrawMode.FullBarWithIcon:
                DrawFullBarWithIcon(rect, color);
                break;
        }
    }

    // 1. アイコンのみ表示（背景グレー＋tintアイコン）
    private static void DrawOnlyIcon(Rect rect, Color color)
    {
        bool isLarge = rect.height >= 40f;
        Rect iconRect;
        if (isLarge)
        {
            float baseSize = Mathf.Min(rect.width, rect.height);
            float iconSize = Mathf.Clamp(baseSize * 0.32f, 18f, 38f);
            float padX = 5f + iconSize * 0.4f; // 右端から
            float padY = 5f + iconSize * 1.1f; // 下端から
            iconRect = new Rect(
                rect.xMax - iconSize - padX,
                rect.yMax - iconSize - padY,
                iconSize, iconSize
            );
        }
        else
        {
            iconRect = new Rect(rect.x, rect.y, rect.height, rect.height);
        }
        // --- 1. グレー四角 (アイコンがある場合のみ)
        int idx = colorPresets.FindIndex(p => Approximately(p.ToColor(), color));
        bool hasIcon = (idx >= 0 && idx < colorPresets.Count && !string.IsNullOrEmpty(colorPresets[idx].iconPath));
        if (hasIcon)
        {
            Color darkGray = new Color32(56, 56, 56, 255);
            EditorGUI.DrawRect(iconRect, darkGray);
        }

        // --- 2. 全体色（その上に半透明） ---
        //Color semiTransparent = color * new Color(1f, 1f, 1f, 0.13f);
        //EditorGUI.DrawRect(rect, semiTransparent);

        // --- 3. アイコン ---
        if (hasIcon)
        {
            var preset = colorPresets[idx];
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(preset.iconPath);
            if (tex != null)
                GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit, true, 0, preset.IconTintColor(), 0, 0);
        }
    }

    private static void DrawFullBarWithIcon(Rect rect, Color color)
    {
        bool isLarge = rect.height >= 40f;
        Rect iconRect;
        if (isLarge)
        {
            float baseSize = Mathf.Min(rect.width, rect.height);
            float iconSize = Mathf.Clamp(baseSize * 0.32f, 18f, 38f);
            float padX = 5f + iconSize * 0.4f; // 右端から
            float padY = 5f + iconSize * 1.1f; // 下端から
            iconRect = new Rect(
                rect.xMax - iconSize - padX,
                rect.yMax - iconSize - padY,
                iconSize, iconSize
            );
        }
        else
        {
            iconRect = new Rect(rect.x, rect.y, rect.height, rect.height);
        }
        // --- 1. グレー四角 (アイコンがある場合のみ)
        int idx = colorPresets.FindIndex(p => Approximately(p.ToColor(), color));
        bool hasIcon = (idx >= 0 && idx < colorPresets.Count && !string.IsNullOrEmpty(colorPresets[idx].iconPath));
        if (hasIcon)
        {
            Color darkGray = new Color32(56, 56, 56, 255);
            EditorGUI.DrawRect(iconRect, darkGray);
        }

        // --- 2. 全体色（その上に半透明） ---
        Color semiTransparent = color * new Color(1f, 1f, 1f, 0.13f);
        EditorGUI.DrawRect(rect, semiTransparent);

        // --- 3. アイコン ---
        if (hasIcon)
        {
            var preset = colorPresets[idx];
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(preset.iconPath);
            if (tex != null)
                GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit, true, 0, preset.IconTintColor(), 0, 0);
        }
    }



    public static bool Approximately(Color a, Color b)
    {
        float t = 0.04f;
        return Mathf.Abs(a.r - b.r) < t && Mathf.Abs(a.g - b.g) < t && Mathf.Abs(a.b - b.b) < t && Mathf.Abs(a.a - b.a) < 0.1f;
    }


    // =================== Project/Hierarchy描画等 ===================
    static void OnProjectGUI(string guid, Rect rect)
    {
        var acd = assetColorDict;
        if (filterActive >= 0 && (!acd.TryGetValue(guid, out int idx) || idx != filterActive)) return;
        if (acd.TryGetValue(guid, out int idx2))
        {
            if (idx2 >= 0 && idx2 < colorPresets.Count)
                DrawColorMark(rect, colorPresets[idx2].ToColor());
        }
    }
    // =================== Hierarchy描画 ===================
    static void OnHierarchyGUI(int instanceID, Rect rect)
    {
        // ---- 新方式: ColorLabelComponentを参照 ----
        var go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
        int idx2 = -1;
        if (go)
        {
            var comp = go.GetComponent<ColorLabelComponent>();
            idx2 = (comp != null) ? comp.labelIndex : -1;
        }

        // フィルタ
        if (filterActive >= 0 && idx2 != filterActive)
            return;

        if (idx2 >= 0 && idx2 < colorPresets.Count)
        {
            DrawColorMark(rect, colorPresets[idx2].ToColor());
        }

        // 右クリックメニュー（ヒエラルキー上）
        Event e = Event.current;
        float left = rect.x - 10;
        float right = rect.x + 20;
        if (e != null && e.type == EventType.ContextClick &&
            e.mousePosition.x >= left && e.mousePosition.x <= right && e.mousePosition.y >= rect.y && e.mousePosition.y <= rect.yMax)
        {
            GameObject go2 = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
            if (go2 == null) return;
            if (!Selection.gameObjects.Contains(go2))
                Selection.activeObject = go2;

            Vector2 mousePos = GUIUtility.GUIToScreenPoint(e.mousePosition);

            // ここで InstanceID 配列を作る
            int[] instanceIDs = Selection.gameObjects
                .Where(g => g != null)
                .Select(g => g.GetInstanceID())
                .ToArray();

            ColorLabelPopupWindow.OpenHierarchy(
                instanceIDs,
                mousePos,
                150f,
                ColorLabelPopupWindow.GetPopupHeight()
            );
            e.Use();
        }
    }

    static void OnProjectWindowCustomRightClick(string guid, Rect rect)
    {
        Event e = Event.current;
        float left = rect.x - 10;
        float right = rect.x + 20;
        if (e != null && e.type == EventType.ContextClick &&
            e.mousePosition.x >= left && e.mousePosition.x <= right && e.mousePosition.y >= rect.y && e.mousePosition.y <= rect.yMax)
        {
            Vector2 mousePos = GUIUtility.GUIToScreenPoint(e.mousePosition);

            // 高さは常にGetPopupHeight()を使って動的に算出
            float popupHeight = ColorLabelPopupWindow.GetPopupHeight();
            ColorLabelPopupWindow.Open(Selection.assetGUIDs, mousePos, 150f, popupHeight);

            e.Use();
        }
    }


    // ----- 色分けメソッド  -----

    public static void AutoColorProjectAvatars(int colorIndex)
    {
        var topFolders = AssetDatabase.GetSubFolders("Assets")
            .Where(f => !f.StartsWith("Packages"))
            .ToArray();
        foreach (var folder in topFolders)
        {
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new string[] { folder });
            bool found = false;
            foreach (var guid in prefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var animator = prefab.GetComponent<Animator>();
                if (animator != null && animator.avatar != null)
                {
                    found = true;
                    break;
                }
            }
            if (found)
            {
                string folderGuid = AssetDatabase.AssetPathToGUID(folder);
                if (!string.IsNullOrEmpty(folderGuid))
                    SetAssetColor(folderGuid, colorIndex);
            }
        }
        Save();
        EditorApplication.RepaintProjectWindow();
    }

    public static void AutoColorHierarchyAvatars(int colorIndex)
    {
        foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
        {
            if (go.transform.parent != null) continue;
            var animator = go.GetComponent<Animator>();
            if (animator == null || animator.avatar == null) continue;
            var comp = go.GetComponent<ColorLabelComponent>();
            if (!comp)
                comp = Undo.AddComponent<ColorLabelComponent>(go);
            Undo.RecordObject(comp, "自動色分け");
            comp.labelIndex = colorIndex;
            EditorUtility.SetDirty(comp);
        }
        EditorApplication.RepaintHierarchyWindow();
    }


    public static void AutoColorProjectClothes(int colorIndex)
    {
        string[] hairKeywords = { "hair", "かみ", "ヘア", "髪" };
        var parentFolders = AssetDatabase.GetSubFolders("Assets")
            .Where(f =>
            {
                var folderName = System.IO.Path.GetFileName(f);
                foreach (var keyword in hairKeywords)
                    if (folderName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                return true;
            })
            .ToArray();

        foreach (var folder in parentFolders)
        {
            bool found = false;
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new string[] { folder });
            foreach (var guid in prefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Packages/")) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                bool isHair = false;
                string fileName = System.IO.Path.GetFileName(path);
                foreach (var keyword in hairKeywords)
                {
                    if (
                        prefab.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fileName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                    )
                    {
                        isHair = true;
                        break;
                    }
                }
                if (isHair) continue;
                if (prefab.GetComponent<Animator>() != null) continue;
                bool hasArmature = prefab.GetComponentsInChildren<Transform>(true)
                    .Any(tr => tr.name.IndexOf("armature", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hasArmature) continue;
                bool hasPhysBone = HasPhysBoneAny(prefab);
                if (!hasPhysBone) continue;

                found = true;
                Debug.Log($"[Clothes] 衣装判定ヒット: {path}");
                break;
            }

            if (found)
            {
                string folderGuid = AssetDatabase.AssetPathToGUID(folder);
                if (!string.IsNullOrEmpty(folderGuid))
                {
                    SetAssetColor(folderGuid, colorIndex);
                    Debug.Log($"[Clothes] Colored folder: {folder}");
                }
                else
                {
                    Debug.LogWarning($"[Clothes] GUID変換失敗: {folder}");
                }
            }
            else
            {
                Debug.Log($"[Clothes] 該当なし: {folder}");
            }
        }

        Save();
        EditorApplication.RepaintProjectWindow();
    }

    public static void AutoColorProjectHair(int colorIndex)
    {
        string[] keywords = { "hair", "かみ", "ヘア", "髪" };
        var parentFolders = AssetDatabase.GetSubFolders("Assets")
            .Where(f => !f.StartsWith("Packages"))
            .ToArray();

        foreach (var folder in parentFolders)
        {
            string parentName = System.IO.Path.GetFileName(folder);
            bool found = false;

            // 1. 親フォルダ名で判定
            foreach (var keyword in keywords)
            {
                if (parentName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Debug.Log($"[髪色分け] 親フォルダ名ヒット: {folder}");
                    found = true;
                    break;
                }
            }

            // 2. 配下の全Prefabで判定
            if (!found)
            {
                string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new string[] { folder });
                foreach (var guid in prefabGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.StartsWith("Packages/")) continue;
                    string fileName = System.IO.Path.GetFileName(path);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;
                    if (prefab.GetComponent<Animator>() != null) continue;
                    foreach (var keyword in keywords)
                    {
                        if (
                            prefab.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            fileName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                        )
                        {
                            Debug.Log($"[髪色分け] Prefab名/パスヒット: {path}");
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }
            }

            if (found)
            {
                string folderGuid = AssetDatabase.AssetPathToGUID(folder);
                if (!string.IsNullOrEmpty(folderGuid))
                {
                    SetAssetColor(folderGuid, colorIndex);
                    Debug.Log($"[髪色分け] 色付け: {folder}");
                }
                else
                {
                    Debug.LogWarning($"[髪色分け] GUID変換失敗: {folder}");
                }
            }
            else
            {
                Debug.Log($"[髪色分け] 該当なし: {folder}");
            }
        }

        Save();
        EditorApplication.RepaintProjectWindow();
    }


    public static void AutoColorProjectTools(int colorIndex)
    {
        var topFolders = AssetDatabase.GetSubFolders("Assets")
            .Where(f => !f.StartsWith("Packages"))
            .ToArray();
        foreach (var folder in topFolders)
        {
            string[] scriptGuids = AssetDatabase.FindAssets("t:Script", new string[] { folder });
            bool found = scriptGuids.Length > 0;
            if (found)
            {
                string folderGuid = AssetDatabase.AssetPathToGUID(folder);
                if (!string.IsNullOrEmpty(folderGuid))
                    SetAssetColor(folderGuid, colorIndex);
            }
        }
        Save();
        EditorApplication.RepaintProjectWindow();
    }

    // ツール系GameObjectにColorLabelComponentをアタッチしてindexを設定
    public static void AutoColorHierarchyTools(int colorIndex)
    {
        foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
        {
            // ルートオブジェクト（親がいないもの）限定
            if (go.transform.parent != null) continue;

            // Animatorがアバターではないもの
            var animator = go.GetComponent<Animator>();
            if (animator != null && animator.avatar != null) continue;

            // MonoBehaviourスクリプトを持つもの（ツール判定）
            var monos = go.GetComponents<MonoBehaviour>();
            if (monos == null || monos.Length == 0) continue;
            bool hasValidMono = monos.Any(mb => mb != null);
            if (!hasValidMono) continue;

            // Component方式
            var comp = go.GetComponent<ColorLabelComponent>();
            if (comp == null)
                comp = go.AddComponent<ColorLabelComponent>();
            comp.labelIndex = colorIndex;
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(comp);
#endif
        }
#if UNITY_EDITOR
    UnityEditor.EditorApplication.RepaintHierarchyWindow();
#endif
    }

    public static void AutoColorProjectAnimation(int colorIndex)
    {
        string[] excludedExts = new[] { ".prefab", ".fbx", ".cs", ".js", ".png", ".jpg", ".jpeg", ".wav", ".mp3", ".ogg", ".shader" };
        var topFolders = AssetDatabase.GetSubFolders("Assets")
            .Where(f => !f.StartsWith("Packages"))
            .ToArray();
        foreach (var folder in topFolders)
        {
            string[] fileGuids = AssetDatabase.FindAssets("", new string[] { folder });
            bool foundAnim = false;
            bool onlyAnim = true;
            foreach (var guid in fileGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Directory.Exists(path)) continue;
                string ext = System.IO.Path.GetExtension(path).ToLower();
                if (ext != ".anim")
                {
                    onlyAnim = false;
                    break;
                }
                foundAnim = true;
            }
            if (onlyAnim && foundAnim)
            {
                bool hasExcluded = false;
                foreach (var guid in fileGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (System.IO.Directory.Exists(path)) continue;
                    string ext = System.IO.Path.GetExtension(path).ToLower();
                    if (excludedExts.Contains(ext))
                    {
                        hasExcluded = true;
                        break;
                    }
                }
                if (!hasExcluded)
                {
                    string folderGuid = AssetDatabase.AssetPathToGUID(folder);
                    if (!string.IsNullOrEmpty(folderGuid))
                        SetAssetColor(folderGuid, colorIndex);
                    Debug.Log($"[Animation] Colored folder: {folder}");
                }
            }
        }
        Save();
        EditorApplication.RepaintProjectWindow();
    }

    // ---- PhysBone関係
    public static bool HasPhysBoneAny(GameObject go)
    {
        var type = FindPhysBoneType();
        if (type != null)
        {
            if (go.GetComponent(type) != null) return true;
            if (go.GetComponentsInChildren(type, true).Length > 0) return true;
        }
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            var n = c.GetType().Name;
            if (n.Contains("PhysBone")) return true;
        }
        return false;
    }
    public static Type FindPhysBoneType()
    {
        string[] candidates = {
            "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone",
            "VRCPhysBone"
        };
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in asm.GetTypes())
            {
                foreach (var name in candidates)
                {
                    if (type.FullName == name || type.Name == name)
                        return type;
                }
            }
        }
        return null;
    }
    public static void CheckAllClothesPhysBone(GameObject[] clothes)
    {
        foreach (var go in clothes)
        {
            bool hasPB = HasPhysBoneAny(go);
            Debug.Log($"[Clothes] {go.name} hasPhysBone={hasPB}");
        }
    }

    // ----- 型名指定で全検索 -----
    public static System.Type ComponentFinder(string typeName)
    {
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(typeName);
            if (t != null) return t;
        }
        return null;
    }
    // カラーラベルポップアップassets用処理
    public static void AssignLabelWithChildren(string[] guids, int labelIndex)
    {
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetDatabase.IsValidFolder(path))
            {
                // このフォルダ配下すべて
                var allGuids = GetAllGuidsRecursively(path);
                foreach (var childGuid in allGuids)
                    SetAssetColor(childGuid, labelIndex);
            }
            else
            {
                SetAssetColor(guid, labelIndex);
            }
        }
        SaveSettings();
        EditorApplication.RepaintProjectWindow();
    }

    // サブフォルダ・サブアセット含むGUID列挙
    private static List<string> GetAllGuidsRecursively(string folderPath)
    {
        var list = new List<string>();

        // まず親フォルダ自身
        string parentGuid = AssetDatabase.AssetPathToGUID(folderPath);
        if (!string.IsNullOrEmpty(parentGuid))
            list.Add(parentGuid);

        // 子ファイル・子フォルダも全て
        string[] guids = AssetDatabase.FindAssets("", new[] { folderPath });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            // フォルダ自身はすでに追加済みなのでスキップ
            if (guid == parentGuid)
                continue;

            list.Add(guid);

            if (AssetDatabase.IsValidFolder(path))
            {
                // 再帰的にサブフォルダも取得（親は再度追加しない）
                list.AddRange(GetAllGuidsRecursively(path));
            }
        }
        return list.Distinct().ToList();
    }
}

// ----------------- カラーラベルポップアップ -----------------

public class ColorLabelPopupWindow : EditorWindow
{
    private string[] guids;
    private int[] hierarchyIDs;
    private static GameObject[] hierarchyGOs; 
    private bool isHierarchy = false;
    private static ColorLabelPopupWindow lastWindow;
    private static float _popupWidth = 150f;
    private static float _popupHeight = 380f;

    // 「子も変更対象に含める」チェックボックス
    private bool includeChildren = false;

    public static float GetPopupHeight()
    {
        return 12f + 20f + ObjectColorLabeler.colorPresets.Count * 21f + 10f + 28f + 40f;
    }

    public static void Open(string[] guids, Vector2 screenPos, float width = 150f, float height = 380f)
    {
        if (lastWindow != null) lastWindow.Close();
        var win = CreateInstance<ColorLabelPopupWindow>();
        win.titleContent = new GUIContent("カラーラベル付与");
        win.guids = guids;
        win.isHierarchy = false;
        _popupWidth = width;
        _popupHeight = height;
        win.position = new Rect(screenPos.x, screenPos.y, width, height);
        win.ShowPopup();
        win.Focus();
        win.Repaint();
        lastWindow = win;
    }

    public static void OpenHierarchy(int[] ids, Vector2 screenPos, float width = 150f, float height = 380f)
    {
        if (lastWindow != null) lastWindow.Close();
        var win = CreateInstance<ColorLabelPopupWindow>();
        win.titleContent = new GUIContent("カラーラベル付与");
        win.hierarchyIDs = ids;
        win.isHierarchy = true;
        // GameObject配列へ変換
        hierarchyGOs = ids.Select(id => EditorUtility.InstanceIDToObject(id) as GameObject)
                          .Where(go => go != null).ToArray();
        _popupWidth = width;
        _popupHeight = height;
        win.position = new Rect(screenPos.x, screenPos.y, width, height);
        win.ShowPopup();
        win.Focus();
        win.Repaint();
        lastWindow = win;
    }

    void OnLostFocus() => Close();

    void OnGUI()
    {
        if (Event.current == null)
        {
            Repaint();
            return;
        }
        GUIStyle smallBold = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 11,
            alignment = TextAnchor.MiddleLeft,
            fixedHeight = 17
        };

        GUILayout.Space(2);

        Rect clearRect = EditorGUILayout.GetControlRect(false, 18, GUILayout.Width(_popupWidth - 8));
        if (GUI.Button(new Rect(clearRect.x + 2, clearRect.y, _popupWidth - 10, 16), "ラベル解除", EditorStyles.miniButton))
        {
            if (!isHierarchy)
            {
                // --- Projectビュー: 子も含めて解除 ---
                if (includeChildren)
                    ObjectColorLabeler.AssignLabelWithChildren(guids, -1);
                else
                    ObjectColorLabeler.AssignLabel(guids, -1);
                ObjectColorLabeler.Save();
            }
            else
            {
                // Hierarchy
                if (includeChildren)
                {
                    foreach (var go in hierarchyGOs)
                    {
                        foreach (var child in GetSelfAndAllChildren(go))
                        {
                            var comp = child.GetComponent<ColorLabelComponent>();
                            if (comp)
                                Undo.DestroyObjectImmediate(comp);
                        }
                    }
                }
                else
                {
                    foreach (var go in hierarchyGOs)
                    {
                        var comp = go.GetComponent<ColorLabelComponent>();
                        if (comp)
                            Undo.DestroyObjectImmediate(comp);
                    }
                }
                EditorApplication.RepaintHierarchyWindow();
            }
            Close();
            GUIUtility.ExitGUI();
        }
        GUILayout.Space(2);

        for (int i = 0; i < ObjectColorLabeler.colorPresets.Count; i++)
        {
            var preset = ObjectColorLabeler.colorPresets[i];
            Rect rect = EditorGUILayout.GetControlRect(false, 19, GUILayout.Width(_popupWidth - 8));
            float iconSize = 15f;

            EditorGUI.DrawRect(new Rect(rect.x + 4, rect.y + 2, iconSize, iconSize), preset.ToColor());
            EditorGUI.DrawRect(new Rect(rect.x + 4, rect.y + 2, iconSize, iconSize), new Color(0, 0, 0, 0.09f));
            if (GUI.Button(new Rect(rect.x + 2, rect.y + 2, _popupWidth - 10, iconSize + 2), GUIContent.none, GUIStyle.none))
            {
                if (!isHierarchy)
                {
                    // --- Projectビュー: 子も含めて付与 ---
                    if (includeChildren)
                        ObjectColorLabeler.AssignLabelWithChildren(guids, i);
                    else
                        ObjectColorLabeler.AssignLabel(guids, i);
                    ObjectColorLabeler.Save();
                }
                else
                {
                    if (includeChildren)
                    {
                        foreach (var go in hierarchyGOs)
                        {
                            foreach (var child in GetSelfAndAllChildren(go))
                            {
                                var comp = child.GetComponent<ColorLabelComponent>();
                                if (!comp)
                                {
                                    comp = Undo.AddComponent<ColorLabelComponent>(child);
                                }
                                Undo.RecordObject(comp, "カラーラベル付与");
                                comp.labelIndex = i;
                                EditorUtility.SetDirty(comp);
                            }
                        }
                    }
                    else
                    {
                        foreach (var go in hierarchyGOs)
                        {
                            var comp = go.GetComponent<ColorLabelComponent>();
                            if (!comp)
                            {
                                comp = Undo.AddComponent<ColorLabelComponent>(go);
                            }
                            Undo.RecordObject(comp, "カラーラベル付与");
                            comp.labelIndex = i;
                            EditorUtility.SetDirty(comp);
                        }
                    }
                    EditorApplication.RepaintHierarchyWindow();
                }
                Close();
                GUIUtility.ExitGUI();
            }
            GUI.Label(new Rect(rect.x + 24, rect.y + 2, _popupWidth - 28, iconSize + 2), preset.label, smallBold);
        }

        GUILayout.Space(10);
        includeChildren = GUILayout.Toggle(includeChildren, "子も変更対象に含める", GUILayout.Height(18));
        GUILayout.Space(2);

        if (GUILayout.Button("設定", EditorStyles.miniButton))
        {
            ColorLabelUtilityWindow.Open();
            Close();
            GUIUtility.ExitGUI();
        }
    }

    // ★指定したGameObjectと全ての子孫を再帰的に取得
    static IEnumerable<GameObject> GetSelfAndAllChildren(GameObject root)
    {
        yield return root;
        foreach (Transform t in root.transform)
        {
            foreach (var child in GetSelfAndAllChildren(t.gameObject))
                yield return child;
        }
    }
}


public class ColorLabelUtilityWindow : EditorWindow
{
    Vector2 scroll;
    string lastMsg = "";
    int removeLabelIndex = 0;

    private int autoAvatarColor = 0;
    private int autoClothesColor = 1;
    private int autoHairColor = 2;
    private int autoToolColor = 3;
    private int autoAnimationColor = 4;

    // バージョンチェック
    const string CURRENT_VERSION = "v1.0.2";//●●●現在のバージョン●●●
    const string VERSION_CHECK_URL = "https://booth.pm/ja/items/7092944";
    const string VERSION_REGEX = @"\[.*?v([\d\.]+)\]";
    static string latestVersion = null;
    static bool versionChecked = false;
    static bool versionIsLatest = true;
    static bool versionError = false;

    [MenuItem("Tools/Color Label Utility")]
    public static void Open()
    {
        var win = GetWindow<ColorLabelUtilityWindow>("Color Label Utility");
        win.minSize = new Vector2(410, 470);
        win.Show();
        win.Focus();
    }

    void OnEnable()
    {
        if (!versionChecked)
        {
            CheckLatestVersion();
        }
    }

    static void CheckLatestVersion()
    {
        if (versionChecked) return;
        versionChecked = true;
        versionError = false;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                using (var wc = new WebClient())
                {
                    string html = wc.DownloadString(VERSION_CHECK_URL);
                    ParseVersionFromHtml(html);
                }
            }
            catch
            {
                versionError = true;
                latestVersion = null;
            }
            EditorApplication.delayCall += () =>
            {
                var win = GetWindow<ColorLabelUtilityWindow>();
                if (win) win.Repaint();
            };
        });
    }

    static void ParseVersionFromHtml(string html)
    {
        // 日付 + 半角スペース + v1.2.3 のみにマッチ
        var match = System.Text.RegularExpressions.Regex.Match(html, @"\[\d{4}/\d{2}/\d{2}\sv([\d\.]+)\]");
        if (match.Success)
        {
            latestVersion = "v" + match.Groups[1].Value;
            versionIsLatest = string.Compare(latestVersion, CURRENT_VERSION, System.StringComparison.Ordinal) <= 0;
        }
        else
        {
            latestVersion = null;
        }
    }


    static int CompareVersion(string vA, string vB)
    {
        string[] a = vA.Replace("v", "").Split('.');
        string[] b = vB.Replace("v", "").Split('.');
        for (int i = 0; i < Mathf.Max(a.Length, b.Length); i++)
        {
            int va = (i < a.Length) ? int.Parse(a[i]) : 0;
            int vb = (i < b.Length) ? int.Parse(b[i]) : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    // アイコンファイルリスト
    private static string[] IconPaths => Directory.Exists("Assets/ColorLabeler/Icon")
          ? Directory.GetFiles("Assets/ColorLabeler/Icon", "*.png").Select(p => p.Replace("\\", "/")).ToArray()
          : Array.Empty<string>();

    private static string[] IconNames => IconPaths.Select(p => Path.GetFileNameWithoutExtension(p)).Prepend("(なし)").ToArray();

    void OnGUI()
    {
        // バージョン確認
        CheckLatestVersion();

        // 上部：タイトル＋バージョン（横並び）
        EditorGUILayout.BeginHorizontal();

        // タイトル
        GUILayout.Label("色ラベルユーティリティ", EditorStyles.boldLabel);

        GUILayout.FlexibleSpace();

        // バージョン表示（どんな状態でもクリック可）
        GUIStyle verStyle = new GUIStyle(EditorStyles.label);
        verStyle.fontStyle = FontStyle.Bold;
        verStyle.fontSize = 12;

        if (!string.IsNullOrEmpty(latestVersion))
        {
            if (versionIsLatest)
            {
                // 通常バージョン：グレー（または黒）
                if (GUILayout.Button($"ColorLabeler {CURRENT_VERSION}", verStyle, GUILayout.Height(22)))
                {
                    Application.OpenURL(VERSION_CHECK_URL);
                }
            }
            else
            {
                // アップデートあり（赤・太字）
                GUIStyle updateStyle = new GUIStyle(verStyle);
                updateStyle.normal.textColor = Color.red;

                if (GUILayout.Button($"[Update] ColorLabeler {latestVersion} -> {CURRENT_VERSION}", updateStyle, GUILayout.Height(22)))
                {
                    Application.OpenURL(VERSION_CHECK_URL);
                }
            }
        }
        else
        {
            // 取得失敗時も通常ボタン
            if (GUILayout.Button($"ColorLabeler {CURRENT_VERSION}", verStyle, GUILayout.Height(22)))
            {
                Application.OpenURL(VERSION_CHECK_URL);
            }
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(2);
        GUILayout.Label("表示方法切替:");
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Toggle(ObjectColorLabeler.drawMode == ColorLabelDrawMode.LeftBar, "左端", EditorStyles.miniButtonLeft, GUILayout.Width(60)))
        {
            SetDrawMode(ColorLabelDrawMode.LeftBar);
        }
        if (GUILayout.Toggle(ObjectColorLabeler.drawMode == ColorLabelDrawMode.FullBar, "全体", EditorStyles.miniButtonMid, GUILayout.Width(60)))
        {
            SetDrawMode(ColorLabelDrawMode.FullBar);
        }
        if (GUILayout.Toggle(ObjectColorLabeler.drawMode == ColorLabelDrawMode.Icon, "アイコン", EditorStyles.miniButtonMid, GUILayout.Width(60)))
        {
            SetDrawMode(ColorLabelDrawMode.Icon);
        }
        if (GUILayout.Toggle(ObjectColorLabeler.drawMode == ColorLabelDrawMode.FullBarWithIcon, "全体+アイコン", EditorStyles.miniButtonRight, GUILayout.Width(90)))
        {
            SetDrawMode(ColorLabelDrawMode.FullBarWithIcon);
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(7);
        GUILayout.Label("フィルタ:（この色だけ表示）");
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Toggle(ObjectColorLabeler.filterActive == -1, "全て", EditorStyles.miniButtonLeft, GUILayout.Width(32)))
        {
            if (ObjectColorLabeler.filterActive != -1)
            {
                ObjectColorLabeler.filterActive = -1;
                ObjectColorLabeler.SaveDrawMode();
                EditorApplication.RepaintProjectWindow();
                EditorApplication.RepaintHierarchyWindow();
            }
        }

        float btnSize = 28;


        for (int i = 0; i < ObjectColorLabeler.colorPresets.Count; i++)
        {
            Rect r = GUILayoutUtility.GetRect(btnSize, btnSize, GUILayout.Width(btnSize), GUILayout.Height(btnSize));

            if (ObjectColorLabeler.drawMode == ColorLabelDrawMode.Icon)
            {
                // 色なしでアイコンのみ
                EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.06f));
                DrawIconInRect(r, ObjectColorLabeler.colorPresets[i].iconPath);
            }
            else
            {
                Color baseColor = ObjectColorLabeler.colorPresets[i].ToColor();
                Color showColor = (ObjectColorLabeler.filterActive == i)
                    ? Color.Lerp(baseColor, Color.white, 0.01f)
                    : Color.Lerp(baseColor, Color.black, 0.5f);

                EditorGUI.DrawRect(r, showColor);

                if (ObjectColorLabeler.drawMode == ColorLabelDrawMode.FullBarWithIcon)
                {
                    DrawIconInRect(r, ObjectColorLabeler.colorPresets[i].iconPath);
                }
            }

            UnityEditor.Handles.DrawSolidRectangleWithOutline(
                r, Color.clear,
                (ObjectColorLabeler.filterActive == i) ? Color.black : new Color(0, 0, 0, 0.15f)
            );

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
            {
                if (ObjectColorLabeler.filterActive != i)
                {
                    ObjectColorLabeler.filterActive = i;
                    ObjectColorLabeler.SaveDrawMode();
                    EditorApplication.RepaintProjectWindow();
                    EditorApplication.RepaintHierarchyWindow();
                }
            }
            GUILayout.Space(2);
        }


        EditorGUILayout.EndHorizontal();

        GUILayout.Space(10);

        // ======== 自動色分けメニュー ========
        GUILayout.Label("自動色分け（プロジェクト/ヒエラルキー）", EditorStyles.boldLabel);

        // アバター
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("アバター：", GUILayout.Width(74));
        autoAvatarColor = EditorGUILayout.Popup(autoAvatarColor, ObjectColorLabeler.colorPresets.Select(c => c.label).ToArray(), GUILayout.Width(96));
        if (GUILayout.Button("プロジェクト自動色分け", GUILayout.Width(120)))
        {
            ObjectColorLabeler.AutoColorProjectAvatars(autoAvatarColor);
            lastMsg = "アバターフォルダを自動色分けしました";
        }
        if (GUILayout.Button("ヒエラルキー自動色分け", GUILayout.Width(130)))
        {
            ObjectColorLabeler.AutoColorHierarchyAvatars(autoAvatarColor);
            lastMsg = "ヒエラルキーのアバターを自動色分けしました";
        }
        EditorGUILayout.EndHorizontal();

        // 衣装
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("衣装：", GUILayout.Width(74));
        autoClothesColor = EditorGUILayout.Popup(autoClothesColor, ObjectColorLabeler.colorPresets.Select(c => c.label).ToArray(), GUILayout.Width(96));
        if (GUILayout.Button("プロジェクト自動色分け", GUILayout.Width(120)))
        {
            ObjectColorLabeler.AutoColorProjectClothes(autoClothesColor);
            lastMsg = "衣装フォルダを自動色分けしました";
        }
        GUILayout.Label("※髪/小物も検知する場合があります", GUILayout.Width(200));
        EditorGUILayout.EndHorizontal();

        // 髪
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("髪：", GUILayout.Width(74));
        autoHairColor = EditorGUILayout.Popup(autoHairColor, ObjectColorLabeler.colorPresets.Select(c => c.label).ToArray(), GUILayout.Width(96));
        if (GUILayout.Button("プロジェクト自動色分け", GUILayout.Width(120)))
        {
            ObjectColorLabeler.AutoColorProjectHair(autoHairColor);
            lastMsg = "髪フォルダを自動色分けしました";
        }
        EditorGUILayout.EndHorizontal();

        // ツール
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("ツール：", GUILayout.Width(74));
        autoToolColor = EditorGUILayout.Popup(autoToolColor, ObjectColorLabeler.colorPresets.Select(c => c.label).ToArray(), GUILayout.Width(96));
        if (GUILayout.Button("プロジェクト自動色分け", GUILayout.Width(120)))
        {
            ObjectColorLabeler.AutoColorProjectTools(autoToolColor);
            lastMsg = "ツールフォルダを自動色分けしました";
        }
        if (GUILayout.Button("ヒエラルキー自動色分け", GUILayout.Width(130)))
        {
            ObjectColorLabeler.AutoColorHierarchyTools(autoToolColor);
            lastMsg = "ヒエラルキーのツールを自動色分けしました";
        }
        EditorGUILayout.EndHorizontal();

        // Animation
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Animation：", GUILayout.Width(74));
        autoAnimationColor = EditorGUILayout.Popup(autoAnimationColor, ObjectColorLabeler.colorPresets.Select(c => c.label).ToArray(), GUILayout.Width(96));
        if (GUILayout.Button("プロジェクト自動色分け", GUILayout.Width(120)))
        {
            ObjectColorLabeler.AutoColorProjectAnimation(autoAnimationColor);
            lastMsg = "Animationフォルダを自動色分けしました";
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(14);

        // ======= ラベル編集・削除系 =======
        GUILayout.Label("ラベル編集:", EditorStyles.boldLabel);

        if (ObjectColorLabeler.colorPresets == null || ObjectColorLabeler.colorPresets.Count == 0)
        {
            EditorGUILayout.HelpBox("ラベルがありません。＋ラベルを追加してください。", MessageType.Warning);
            if (GUILayout.Button("＋ラベルを追加", GUILayout.Height(24)))
            {
                ObjectColorLabeler.colorPresets.Add(new ColorLabelInfo("新規", Color.gray));
                ObjectColorLabeler.SavePresets();
                ObjectColorLabeler.CleanupInvalidIndices();
                EditorApplication.RepaintProjectWindow();
                EditorApplication.RepaintHierarchyWindow();
            }
            GUILayout.Space(10);
            if (GUILayout.Button("ラベルを初期設定に戻す", GUILayout.Height(24)))
            {
                if (EditorUtility.DisplayDialog("確認", "全てのラベル設定を初期化してよろしいですか？", "初期化", "キャンセル"))
                {
                    ObjectColorLabeler.ResetPresetsToDefault();
                    ObjectColorLabeler.CleanupInvalidIndices();
                    EditorApplication.RepaintProjectWindow();
                    EditorApplication.RepaintHierarchyWindow();
                    lastMsg = "初期設定に戻しました";
                }
            }
            if (!string.IsNullOrEmpty(lastMsg))
            {
                EditorGUILayout.HelpBox(lastMsg, MessageType.Info);
            }
            return;
        }

        // ======= ラベル一覧 =======
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(220));
        for (int i = 0; i < ObjectColorLabeler.colorPresets.Count;)
        {
            EditorGUILayout.BeginHorizontal();
            // ラベル名
            string newLabel = EditorGUILayout.TextField(ObjectColorLabeler.colorPresets[i].label, GUILayout.Width(74));
            if (newLabel != ObjectColorLabeler.colorPresets[i].label)
            {
                ObjectColorLabeler.colorPresets[i].label = newLabel;
                ObjectColorLabeler.SavePresets();
                ObjectColorLabeler.CleanupInvalidIndices();
                EditorApplication.RepaintProjectWindow();
                EditorApplication.RepaintHierarchyWindow();
            }

            // カラー
            Color beforeColor = ObjectColorLabeler.colorPresets[i].ToColor();

            EditorGUI.BeginChangeCheck();
            Color newColor = EditorGUILayout.ColorField(beforeColor, GUILayout.Width(80));
            bool changed = EditorGUI.EndChangeCheck();

            if (changed)
            {
                // 一時反映のみ
                ObjectColorLabeler.colorPresets[i].FromColor(newColor);

                // 保存はピッカーを閉じた時だけ
                EditorApplication.delayCall += () =>
                {
                    // カラーピッカーがまだ開いていれば保存しない
                    if (EditorWindow.focusedWindow == null || EditorWindow.focusedWindow.GetType().Name != "ColorPicker")
                    {
                        ObjectColorLabeler.SavePresets();
                        EditorApplication.RepaintProjectWindow();
                        EditorApplication.RepaintHierarchyWindow();
                    }
                };
            }

            // アイコン・ボタン用の領域
            Rect iconRect = GUILayoutUtility.GetRect(28, 28, GUILayout.Width(28), GUILayout.Height(28));

            if (ObjectColorLabeler.drawMode == ColorLabelDrawMode.Icon ||
                ObjectColorLabeler.drawMode == ColorLabelDrawMode.FullBarWithIcon)
            {
                float iconW = iconRect.width;
                float iconH = iconRect.height;
                float boxSize = 18f; 

                float boxX = iconRect.x + (iconW - boxSize) * 0.5f;
                float boxY = iconRect.y + (iconH - boxSize) * 0.5f;

                Rect boxRect = new Rect(boxX, boxY, boxSize, boxSize);

                if (string.IsNullOrEmpty(ObjectColorLabeler.colorPresets[i].iconPath))
                {
                    Color outline = new Color(0.4f, 0.4f, 0.4f, 1f);
                    Handles.DrawSolidRectangleWithOutline(iconRect, Color.clear, outline);
                }
                else
                {
                    // アイコンがあればプレビュー表示
                    DrawIconInRect(iconRect, ObjectColorLabeler.colorPresets[i].iconPath, ObjectColorLabeler.colorPresets[i].IconTintColor());
                }

                if (GUI.Button(iconRect, GUIContent.none, GUIStyle.none))
                {
                    int idx = i;
                    IconSelectPopup.Open(
                        (selectedIcon, tint) =>
                        {
                            ObjectColorLabeler.colorPresets[idx].iconPath = selectedIcon;
                            ObjectColorLabeler.colorPresets[idx].iconTint = new float[] { tint.r, tint.g, tint.b, tint.a };
                            ObjectColorLabeler.SavePresets();
                            EditorApplication.RepaintProjectWindow();
                            EditorApplication.RepaintHierarchyWindow();
                        },
                        ObjectColorLabeler.colorPresets[i].IconTintColor(),
                        idx, 
                        ObjectColorLabeler.colorPresets[i].iconPath 
                    );
                }
            }


            // ↑↓削除ボタン
            GUI.enabled = i > 0;
            if (GUILayout.Button("↑", GUILayout.Width(24)))
            {
                if (i > 0)
                {
                    var tmp = ObjectColorLabeler.colorPresets[i];
                    ObjectColorLabeler.colorPresets[i] = ObjectColorLabeler.colorPresets[i - 1];
                    ObjectColorLabeler.colorPresets[i - 1] = tmp;
                    ObjectColorLabeler.SavePresets();
                    ObjectColorLabeler.CleanupInvalidIndices();
                    EditorApplication.RepaintProjectWindow();
                    EditorApplication.RepaintHierarchyWindow();
                }
            }
            GUI.enabled = i < ObjectColorLabeler.colorPresets.Count - 1;
            if (GUILayout.Button("↓", GUILayout.Width(24)))
            {
                if (i < ObjectColorLabeler.colorPresets.Count - 1)
                {
                    var tmp = ObjectColorLabeler.colorPresets[i];
                    ObjectColorLabeler.colorPresets[i] = ObjectColorLabeler.colorPresets[i + 1];
                    ObjectColorLabeler.colorPresets[i + 1] = tmp;
                    ObjectColorLabeler.SavePresets();
                    ObjectColorLabeler.CleanupInvalidIndices();
                    EditorApplication.RepaintProjectWindow();
                    EditorApplication.RepaintHierarchyWindow();
                }
            }
            GUI.enabled = true;
            if (GUILayout.Button("削除", GUILayout.Width(36)))
            {
                if (EditorUtility.DisplayDialog("確認", $"本当に「{ObjectColorLabeler.colorPresets[i].label}」を削除しますか？", "削除", "キャンセル"))
                {
                    ObjectColorLabeler.colorPresets.RemoveAt(i);
                    ObjectColorLabeler.SavePresets();
                    ObjectColorLabeler.CleanupInvalidIndices();
                    EditorApplication.RepaintProjectWindow();
                    EditorApplication.RepaintHierarchyWindow();

                    // 削除後にremoveLabelIndexを補正
                    if (removeLabelIndex >= ObjectColorLabeler.colorPresets.Count)
                        removeLabelIndex = ObjectColorLabeler.colorPresets.Count - 1;
                    if (removeLabelIndex < 0) removeLabelIndex = 0;

                    EditorGUILayout.EndHorizontal();
                    continue;
                }
            }
            i++;
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        GUILayout.Space(4);

        if (GUILayout.Button("＋ラベルを追加", GUILayout.Height(24)))
        {
            ObjectColorLabeler.colorPresets.Add(new ColorLabelInfo("新規", Color.gray));
            ObjectColorLabeler.SavePresets();
            ObjectColorLabeler.CleanupInvalidIndices();
            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }

        GUILayout.Space(4);

        // ======= ラベル全解除 =======
        if (GUILayout.Button("ラベル全解除", GUILayout.Height(24)))
        {
            if (EditorUtility.DisplayDialog("全ラベル解除", "本当に全てのラベルを解除しますか？", "OK", "キャンセル"))
            {
                ObjectColorLabeler.ClearAllColors();
                EditorApplication.RepaintProjectWindow();
                EditorApplication.RepaintHierarchyWindow();
                lastMsg = "全解除しました";
            }
        }

        // ======= 指定色のラベル全解除 =======
        if (ObjectColorLabeler.colorPresets.Count > 0)
        {
            // 安全ガード
            if (removeLabelIndex < 0 || removeLabelIndex >= ObjectColorLabeler.colorPresets.Count)
                removeLabelIndex = 0;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("指定色のラベル全解除", GUILayout.Width(120));
            string[] popupLabels = ObjectColorLabeler.colorPresets.Select(c => c.label).ToArray();
            removeLabelIndex = EditorGUILayout.Popup(removeLabelIndex, popupLabels, GUILayout.Width(100));

            // ここも必ずガード
            if (removeLabelIndex < 0 || removeLabelIndex >= ObjectColorLabeler.colorPresets.Count)
                removeLabelIndex = 0;

            Rect chipRect2 = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));

            // --- 表示形式ごとに分岐 ---
            if (ObjectColorLabeler.drawMode == ColorLabelDrawMode.Icon)
            {
                // 色チップは描かず、アイコンだけ
                DrawIconInRect(chipRect2, ObjectColorLabeler.colorPresets[removeLabelIndex].iconPath);
            }
            else
            {
                // 色チップあり
                Color baseColor2 = ObjectColorLabeler.colorPresets[removeLabelIndex].ToColor();
                Color chipColor = Color.Lerp(baseColor2, Color.white, 0.01f);
                EditorGUI.DrawRect(chipRect2, chipColor);

                // FullBarWithIconのときは色＋アイコン
                if (ObjectColorLabeler.drawMode == ColorLabelDrawMode.FullBarWithIcon)
                {
                    DrawIconInRect(chipRect2, ObjectColorLabeler.colorPresets[removeLabelIndex].iconPath);
                }
            }
            Handles.DrawSolidRectangleWithOutline(chipRect2, Color.clear, new Color(0, 0, 0, 0.18f));


            if (GUILayout.Button("全解除", GUILayout.Height(22)))
            {
                string clrName = ObjectColorLabeler.colorPresets[removeLabelIndex].label;
                if (EditorUtility.DisplayDialog("確認", $"本当に「{clrName}」ラベルだけ全て解除しますか？", "OK", "キャンセル"))
                {
                    ObjectColorLabeler.ClearLabelColor(removeLabelIndex);
                    EditorApplication.RepaintProjectWindow();
                    EditorApplication.RepaintHierarchyWindow();
                    lastMsg = $"「{clrName}」ラベルを全解除しました";
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("ラベルを初期設定に戻す", GUILayout.Height(24)))
        {
            if (EditorUtility.DisplayDialog("確認", "全てのラベル設定を初期化してよろしいですか？", "初期化", "キャンセル"))
            {
                ObjectColorLabeler.ResetPresetsToDefault();
                EditorApplication.RepaintProjectWindow();
                EditorApplication.RepaintHierarchyWindow();
                lastMsg = "初期設定に戻しました";
            }
        }
        if (!string.IsNullOrEmpty(lastMsg))
            EditorGUILayout.HelpBox(lastMsg, MessageType.Info);
    }

    // 表示切替補助
    void SetDrawMode(ColorLabelDrawMode mode)
    {
        if (ObjectColorLabeler.drawMode != mode)
        {
            ObjectColorLabeler.drawMode = mode;
            ObjectColorLabeler.SaveDrawMode();
            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }
    }

    // アイコン画像をRectに描画
    static void DrawIconInRect(Rect rect, string iconPath, Color? tint = null)
    {
        if (string.IsNullOrEmpty(iconPath)) return;
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        if (tex != null)
            GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, true, 0, tint ?? Color.white, 0, 0);
    }

}
//ラベル編集UI：アイコンポップアップ化＋色編集UI
public class IconSelectPopup : EditorWindow
{
    public static Action<string, Color> OnIconSelected;

    private static int _presetIndex = -1;
    private static Color _currentColor;
    private static string _currentIcon;
    private static bool _shouldSaveOnClose = false;

    // ---- 画像キャッシュ対応 ----
    private static string[] _iconPaths;
    private static string[] _iconNames;
    private static Texture2D[] _iconTextures;
    private static bool _cacheReady = false;

    // 色選択用
    private Color _tintColor;

    // ---- キャッシュ初期化（初回だけ） ----
    private static void EnsureCache()
    {
        if (_cacheReady) return;
        string folder = "Assets/ColorLabeler/Icon";
        if (Directory.Exists(folder))
        {
            _iconPaths = Directory.GetFiles(folder, "*.png").Select(p => p.Replace("\\", "/")).ToArray();
            _iconNames = _iconPaths.Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
            _iconTextures = new Texture2D[_iconPaths.Length];
            for (int i = 0; i < _iconPaths.Length; i++)
                _iconTextures[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(_iconPaths[i]);
        }
        else
        {
            _iconPaths = Array.Empty<string>();
            _iconNames = Array.Empty<string>();
            _iconTextures = Array.Empty<Texture2D>();
        }
        _cacheReady = true;
    }
    // キャッシュ強制再構築
    private static void ClearCache()
    {
        _cacheReady = false;
        _iconPaths = null;
        _iconNames = null;
        _iconTextures = null;
    }

    public static void Open(Action<string, Color> callback, Color currentTint, int presetIndex = -1, string currentIcon = null)
    {
        EnsureCache();
        var win = CreateInstance<IconSelectPopup>();
        OnIconSelected = callback;
        _presetIndex = presetIndex;
        _currentIcon = currentIcon;
        _currentColor = currentTint;
        win._tintColor = currentTint;
        int col = 6;
        int rowCount = Mathf.CeilToInt((_iconPaths?.Length ?? 0) / (float)col);
        win.position = new Rect(Screen.width / 2, Screen.height / 2, 380, 100 + Mathf.Max(rowCount, 1) * 68);
        win.ShowUtility();
    }

    void OnGUI()
    {
        EnsureCache();

        // ラベル＋右端にリロードボタン
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("アイコン一覧（クリックで選択）", EditorStyles.boldLabel);

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("アイコンリスト再読込", GUILayout.Width(130), GUILayout.Height(20)))
        {
            ClearCache();
            EnsureCache();
            Repaint();
        }
        EditorGUILayout.EndHorizontal();

        // 色フィールド（カラーピッカー/スポイト対応）
        EditorGUI.BeginChangeCheck();
        Color newColor = EditorGUILayout.ColorField("アイコン色(Tint)", _tintColor, GUILayout.Width(320));
        bool changed = EditorGUI.EndChangeCheck();

        if (changed)
        {
            _tintColor = newColor;
            _currentColor = newColor;
            if (_presetIndex >= 0 && _presetIndex < ObjectColorLabeler.colorPresets.Count)
                ObjectColorLabeler.colorPresets[_presetIndex].iconTint = new float[] { _tintColor.r, _tintColor.g, _tintColor.b, _tintColor.a };
            _shouldSaveOnClose = true;

            // カラーピッカーのフォーカスを見て、終了時だけ保存
            EditorApplication.delayCall += () =>
            {
                var picker = EditorWindow.focusedWindow;
                if (picker == null || picker.GetType().Name != "ColorPicker")
                {
                    if (_shouldSaveOnClose && _presetIndex >= 0 && _presetIndex < ObjectColorLabeler.colorPresets.Count)
                    {
                        ObjectColorLabeler.colorPresets[_presetIndex].iconTint = new float[] { _tintColor.r, _tintColor.g, _tintColor.b, _tintColor.a };
                        ObjectColorLabeler.SavePresets();
                    }
                    _shouldSaveOnClose = false;
                }
            };
        }
        GUILayout.Space(6);

        // ---- キャッシュ済みテクスチャを使用して高速描画 ----
        int col = 6;
        int index = 0;
        int total = _iconPaths.Length;

        for (int row = 0; row < Mathf.CeilToInt(total / (float)col); row++)
        {
            EditorGUILayout.BeginHorizontal();
            for (int c = 0; c < col; c++)
            {
                if (index < total)
                {
                    var tex = _iconTextures[index];
                    GUI.enabled = tex != null;
                    if (GUILayout.Button(new GUIContent(tex, _iconNames[index]), GUILayout.Width(48), GUILayout.Height(48)))
                    {
                        _currentIcon = _iconPaths[index];
                        // ここでPresetにも反映
                        if (_presetIndex >= 0 && _presetIndex < ObjectColorLabeler.colorPresets.Count)
                        {
                            ObjectColorLabeler.colorPresets[_presetIndex].iconPath = _currentIcon;
                            ObjectColorLabeler.colorPresets[_presetIndex].iconTint = new float[] { _tintColor.r, _tintColor.g, _tintColor.b, _tintColor.a };
                            ObjectColorLabeler.SavePresets();
                            EditorApplication.RepaintProjectWindow();
                            EditorApplication.RepaintHierarchyWindow();
                        }
                        OnIconSelected?.Invoke(_currentIcon, _tintColor);
                        Close();
                        GUIUtility.ExitGUI();
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Space(52);
                }
                index++;
            }
            EditorGUILayout.EndHorizontal();
        }

        // 「アイコンなし」
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("(なし)", GUILayout.Width(60), GUILayout.Height(32)))
        {
            _currentIcon = "";
            if (_presetIndex >= 0 && _presetIndex < ObjectColorLabeler.colorPresets.Count)
            {
                ObjectColorLabeler.colorPresets[_presetIndex].iconPath = "";
                ObjectColorLabeler.colorPresets[_presetIndex].iconTint = new float[] { _tintColor.r, _tintColor.g, _tintColor.b, _tintColor.a };
                ObjectColorLabeler.SavePresets();
                EditorApplication.RepaintProjectWindow();
                EditorApplication.RepaintHierarchyWindow();
            }
            OnIconSelected?.Invoke("", _tintColor);
            Close();
            GUIUtility.ExitGUI();
        }
        EditorGUILayout.EndHorizontal();
    }

    // 閉じる時もまとめて保存
    private void OnDisable()
    {
        if (_shouldSaveOnClose && _presetIndex >= 0 && _presetIndex < ObjectColorLabeler.colorPresets.Count)
        {
            ObjectColorLabeler.colorPresets[_presetIndex].iconTint = new float[] { _tintColor.r, _tintColor.g, _tintColor.b, _tintColor.a };
            ObjectColorLabeler.colorPresets[_presetIndex].iconPath = _currentIcon;
            ObjectColorLabeler.SavePresets();
            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }
        _shouldSaveOnClose = false;
    }
}
