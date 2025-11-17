#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

// アプリ名を MeshSyncPro に変更
public class MeshSyncPro : EditorWindow
{
    [MenuItem("Tools/MeshSyncPro")] // 菜单路径也修改
    static void Open() => GetWindow<MeshSyncPro>("MeshSyncPro"); // 窗口标题也修改

    // --- UI Elements ---
    GameObject avatar;
    SkinnedMeshRenderer bodyRenderer; // 体はSkinnedMeshRendererを想定
    Renderer clothRenderer; // 衣装はSkinnedMeshRenderer or MeshRenderer

    // --- Parameters (添付ファイル[1]の値を維持) ---
    float penetrationThreshold = 0.006f;
    float pushOutOffset = 0.02f;
    // 詳細設定へ移動するパラメータ
    int influenceRadiusSteps = 5;
    int smoothingIterations = 5;
    float smoothingFactor = 1.0f;

    // --- Automatic Iteration (添付ファイル[1]の値を維持) ---
    int autoFixTotalIterations = 5;
    bool isAutoIterating = false; // 自動反復処理中かどうかのフラグ

    // --- Protection (添付ファイル[1]の構成を維持) ---
    HumanBodyBones[] protectedBoneEnums = new HumanBodyBones[]
    {
        HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
        HumanBodyBones.LeftToes, HumanBodyBones.RightToes,
        HumanBodyBones.Head
    };
    List<Transform> protectedBoneTransforms = new List<Transform>();
    HashSet<int> protectedVertices = new HashSet<int>();

    // --- Exclusion Zones (添付ファイル[1]の構成を維持し、初期サイズ変更) ---
    [System.Serializable]
    public class ExclusionZone
    {
        public string name = "排除区域"; // 用语更改为中文
        public Vector3 center = Vector3.zero;
        public Vector3 size = Vector3.one * 0.4f; // 初始尺寸0.4m
        public bool isActive = true;
    }
    List<ExclusionZone> exclusionZones = new List<ExclusionZone>();

    // --- Internal Data (添付ファイル[1]の構成を維持) ---
    Renderer[] availableRenderers;
    string[] availableRendererNames;
    int selectedBodyRendererIndex = -1;
    int selectedClothRendererIndex = -1;

    List<int> detectedPenetrationIndices = new List<int>();
    List<Vector3> detectedWorldPositions = new List<Vector3>();
    HashSet<int> excludedIndices = new HashSet<int>(); // 修正から除外する頂点インデックス

    Vector2 scrollPosition;
    bool showProtectedBonesFold = true; // 保護ボーンは最初から表示
    bool showDetectionInScene = true;
    bool showExclusionZonesFold = true; // 修正対象外エリアは最初から表示
    bool showAdvancedSettings = false; // 詳細設定は最初は非表示
    Animator animator;

    Vector2 mainScrollPosition; // 新增主滚动条变量

    void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        if (avatar != null) LoadAvatarData();
    }

    void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    void OnGUI()
    {
        mainScrollPosition = EditorGUILayout.BeginScrollView(mainScrollPosition); // 开始滚动视图

        // 标题和操作指南
        EditorGUILayout.LabelField("MeshSyncPro", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "欢迎使用 MeshSyncPro！ ✨\n" +
            "1. 将角色拖到“角色对象”栏！\n" +
            "2. 从下拉菜单选择“身体网格”和“服装网格”。\n" +
            "3. 如有需要可创建“排除区域”，设置想要露出的皮肤部位等！\n" +
            "4. 准备好后点击“穿透检测”按钮！\n" +
            "5. 找到红点后，点击“自动修复”按钮即可美化！\n\n" +
            "想要更细致调整？可以展开“高级设置”哦！😉",
            MessageType.Info);
        GUILayout.Space(10);

        // --- 角色和网格选择 ---
        EditorGUI.BeginChangeCheck();
        avatar = (GameObject)EditorGUILayout.ObjectField(new GUIContent("角色对象", "请放入需要修复的角色根对象。"), avatar, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck() || (avatar != null && availableRenderers == null))
        {
            LoadAvatarData(); // 角色变更时重新加载数据
        }

        if (avatar == null)
        {
            EditorGUILayout.HelpBox("请在上方栏设置角色（角色对象）！", MessageType.Warning);
            return;
        }
        if (availableRenderers == null || availableRenderers.Length == 0)
        {
            EditorGUILayout.HelpBox("未找到角色网格…\n请确认角色对象是否正确。", MessageType.Error);
            return;
        }

        selectedBodyRendererIndex = EditorGUILayout.Popup(new GUIContent("身体网格", "请选择角色“身体”部分的网格。"), selectedBodyRendererIndex, availableRendererNames);
        selectedClothRendererIndex = EditorGUILayout.Popup(new GUIContent("服装网格", "请选择需要修复穿透的“服装”或“饰品”网格。"), selectedClothRendererIndex, availableRendererNames);
        UpdateSelectedRenderers(); // 更新选中的渲染器

        if (bodyRenderer == null) { EditorGUILayout.HelpBox("请选择“身体网格”！", MessageType.Error); return; }
        if (clothRenderer == null) { EditorGUILayout.HelpBox("请选择“服装网格”！", MessageType.Error); return; }
        if (bodyRenderer == clothRenderer) { EditorGUILayout.HelpBox("“身体”和“服装”请选不同的网格！", MessageType.Error); return; }
        GUILayout.Space(10);

        // --- 基本设置 ---
        EditorGUILayout.LabelField("基本设置", EditorStyles.boldLabel);
        penetrationThreshold = EditorGUILayout.Slider(new GUIContent("穿透判定阈值", "身体距离服装多近时判定为“穿透”。越小越敏感。"), penetrationThreshold, 0.001f, 0.05f);
        pushOutOffset = EditorGUILayout.Slider(new GUIContent("推出距离（安全边距）", "修复穿透时，身体从服装推出的距离。适当留余量可减少再次穿透。"), pushOutOffset, 0.001f, 0.05f);
        GUILayout.Space(10);

        // --- 排除区域 ---
        showExclusionZonesFold = EditorGUILayout.Foldout(showExclusionZonesFold, new GUIContent("排除区域", "可设置“这里想露皮肤所以穿透没关系”的区域。"));
        if (showExclusionZonesFold)
        {
            EditorGUI.indentLevel++;
            for (int i = 0; i < exclusionZones.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                exclusionZones[i].name = EditorGUILayout.TextField(new GUIContent("区域名称", "请取个易懂的名字。"), exclusionZones[i].name);
                exclusionZones[i].isActive = EditorGUILayout.Toggle(new GUIContent("启用", "是否参与判定。"), exclusionZones[i].isActive, GUILayout.Width(40));
                EditorGUILayout.EndHorizontal();
                exclusionZones[i].center = EditorGUILayout.Vector3Field(new GUIContent("区域中心坐标", "区域中心位置。可在Scene视图拖动！"), exclusionZones[i].center);
                exclusionZones[i].size = EditorGUILayout.Vector3Field(new GUIContent("区域尺寸（米）", "区域宽、高、深，单位米。"), exclusionZones[i].size);
                if (GUILayout.Button("删除此区域", GUILayout.Width(120))) { exclusionZones.RemoveAt(i); break; }
                EditorGUILayout.Space();
            }
            if (GUILayout.Button("添加新区域")) exclusionZones.Add(new ExclusionZone());
            EditorGUI.indentLevel--;
        }
        GUILayout.Space(10);

        // --- 保护骨骼 ---
        showProtectedBonesFold = EditorGUILayout.Foldout(showProtectedBonesFold, new GUIContent("保护骨骼列表", "如手脚等重要部位可避免被穿透修复影响。"));
        if (showProtectedBonesFold)
        {
            if (animator != null && animator.isHuman)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("以下骨骼周边不易受穿透修复影响：");
                foreach (var boneEnum in protectedBoneEnums) EditorGUILayout.LabelField("  - " + boneEnum.ToString());
                if (GUILayout.Button("重新计算保护部位")) { CacheProtectedBoneTransforms(); CacheProtectedVertices(); }
                EditorGUILayout.HelpBox($"当前约有 {protectedVertices.Count} 个身体顶点被保护。", MessageType.None);
                EditorGUI.indentLevel--;
            }
            else
            {
                EditorGUILayout.HelpBox("未检测到人形骨骼或Animator。\n此功能仅支持Humanoid角色。", MessageType.Warning);
            }
        }
        GUILayout.Space(10);

        // --- 显示设置 ---
        showDetectionInScene = EditorGUILayout.Toggle(new GUIContent("场景显示穿透点", "在Scene视图用红点显示检测到的穿透位置。"), showDetectionInScene);
        GUILayout.Space(10);

        // --- 高级设置 (Foldout) ---
        showAdvancedSettings = EditorGUILayout.Foldout(showAdvancedSettings, "高级设置（进阶）");
        if (showAdvancedSettings)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox("这里是进阶设置！不懂可不用调整哦！😉", MessageType.None);
            influenceRadiusSteps = EditorGUILayout.IntSlider(new GUIContent("修复范围宽度", "修复穿透时，周围网格平滑融合的范围。"), influenceRadiusSteps, 0, 10);
            smoothingIterations = EditorGUILayout.IntSlider(new GUIContent("平滑次数", "修复后平滑处理的迭代次数。次数越多越平滑但计算越重。"), smoothingIterations, 0, 20);
            smoothingFactor = EditorGUILayout.Slider(new GUIContent("平滑强度", "修复后平滑处理的强度。越大越明显。"), smoothingFactor, 0.0f, 1.0f);

            if (detectedPenetrationIndices.Count > 0)
            {
                GUILayout.Space(5);
                EditorGUILayout.LabelField("检测到的穿透点单独修复开关", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("取消勾选的顶点将不参与“自动修复”。", MessageType.None);
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(Mathf.Min(120, detectedPenetrationIndices.Count * EditorGUIUtility.singleLineHeight + 5)));
                for (int i = 0; i < detectedPenetrationIndices.Count; i++)
                {
                    int vertexIndex = detectedPenetrationIndices[i];
                    bool isSelectedToFix = !excludedIndices.Contains(vertexIndex);
                    bool newIsSelectedToFix = EditorGUILayout.ToggleLeft(new GUIContent($"修复顶点 {vertexIndex}", $"是否修复身体网格顶点编号 {vertexIndex}。"), isSelectedToFix);
                    if (newIsSelectedToFix && !isSelectedToFix) excludedIndices.Remove(vertexIndex);
                    else if (!newIsSelectedToFix && isSelectedToFix) excludedIndices.Add(vertexIndex);
                }
                EditorGUILayout.EndScrollView();
                if (GUILayout.Button(new GUIContent("全部穿透点设为修复", "将列表所有点勾选为修复。"), GUILayout.Width(200))) excludedIndices.Clear();
                if (GUILayout.Button(new GUIContent("全部穿透点设为排除", "将列表所有点取消勾选。"), GUILayout.Width(220))) excludedIndices.UnionWith(detectedPenetrationIndices);
            }
            EditorGUI.indentLevel--;
        }
        GUILayout.Space(15);

        // --- 执行按钮区 ---
        EditorGUILayout.LabelField("执行命令", EditorStyles.boldLabel);
        GUI.enabled = bodyRenderer != null && clothRenderer != null && bodyRenderer != clothRenderer;

        if (GUILayout.Button(new GUIContent("步骤1：穿透检测！", "检测服装是否穿透身体。"), GUILayout.Height(35)))
        {
            DetectPenetrationsWithPhysics();
            excludedIndices.Clear();
        }

        GUI.enabled = bodyRenderer != null && clothRenderer != null && bodyRenderer != clothRenderer && detectedPenetrationIndices.Count > 0;
        if (GUILayout.Button(new GUIContent("步骤2：自动修复选中穿透！", "自动修复检测到的穿透（高级设置可选定修复对象）。"), GUILayout.Height(35)))
        {
            AutoFixPenetrations();
        }
        GUI.enabled = bodyRenderer != null && clothRenderer != null && bodyRenderer != clothRenderer;
        GUILayout.Space(5);
        autoFixTotalIterations = EditorGUILayout.IntSlider(new GUIContent("自动迭代次数", "连续执行“穿透检测”和“自动修复”的次数。"), autoFixTotalIterations, 1, 10);
        if (GUILayout.Button(new GUIContent($"连续自动修复 ({autoFixTotalIterations} 次)", $"连续执行“穿透检测”和“全部自动修复”指定次数。\n如需中止请按ESC。"), GUILayout.Height(35)))
        {
            StartAutoFixIterations();
        }
        GUI.enabled = true;

        EditorGUILayout.EndScrollView(); // 结束滚动视图
    }

    void StartAutoFixIterations()
    {
        if (bodyRenderer == null || clothRenderer == null || bodyRenderer == clothRenderer)
        {
            EditorUtility.DisplayDialog("提示", "请正确选择“身体网格”和“服装网格”后再试！", "确定");
            return;
        }
        isAutoIterating = true;
        string progressTitle = "正在连续自动修复...";
        try
        {
            for (int i = 0; i < autoFixTotalIterations; i++)
            {
                bool cancel = EditorUtility.DisplayCancelableProgressBar(
                    progressTitle,
                    $"处理中: 第 {i + 1} / {autoFixTotalIterations} 次 (步骤1：穿透检测中...)",
                    (float)(i + 0.1f) / autoFixTotalIterations);
                if (cancel) { Debug.Log("连续自动修复已取消。"); break; }

                DetectPenetrationsWithPhysics();

                cancel = EditorUtility.DisplayCancelableProgressBar(
                    progressTitle,
                    $"处理中: 第 {i + 1} / {autoFixTotalIterations} 次 (步骤2：准备自动修复... 检测到 {detectedPenetrationIndices.Count} 点)",
                    (float)(i + 0.5f) / autoFixTotalIterations);
                if (cancel) { Debug.Log("连续自动修复已取消。"); break; }

                if (detectedPenetrationIndices.Count > 0)
                {
                    excludedIndices.Clear();
                    AutoFixPenetrations();
                }
                else
                {
                    Debug.LogWarning($"第 {i + 1} 次迭代：未检测到穿透，跳过修复。");
                    if (i > 0)
                    {
                        EditorUtility.DisplayDialog("成功！", $"第 {i + 1} 次迭代后已无穿透！\n应该已经很美观啦！✨", "确定");
                        break;
                    }
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            isAutoIterating = false;
        }
        if (!EditorUtility.DisplayCancelableProgressBar(progressTitle,"",1f))
            EditorUtility.DisplayDialog("完成！", $"{autoFixTotalIterations} 次连续自动修复已完成！\n请检查效果！😊", "确定");
        Repaint();
    }

    void LoadAvatarData()
    {
        if (avatar == null) { availableRenderers = null; availableRendererNames = null; animator = null; selectedBodyRendererIndex = -1; selectedClothRendererIndex = -1; return; }
        animator = avatar.GetComponent<Animator>();

        availableRenderers = avatar.GetComponentsInChildren<Renderer>(true)
            .Where(r => (r is SkinnedMeshRenderer smr && smr.sharedMesh != null) || (r is MeshRenderer mr && mr.GetComponent<MeshFilter>()?.sharedMesh != null))
            .ToArray();
        availableRendererNames = availableRenderers.Select(r => $"{r.name} ({r.GetType().Name})").ToArray();

        selectedBodyRendererIndex = -1;
        selectedClothRendererIndex = -1;

        if (availableRenderers.Length > 0) {
            selectedBodyRendererIndex = System.Array.FindIndex(availableRenderers, r =>
                r is SkinnedMeshRenderer && (r.name.ToLower().Contains("body") || r.name.ToLower().Contains("face") || r.name.ToLower().Contains("head")));
            if (selectedBodyRendererIndex == -1)
                selectedBodyRendererIndex = System.Array.FindIndex(availableRenderers, r => r is SkinnedMeshRenderer);
            if (selectedBodyRendererIndex == -1 && availableRenderers.Length > 0)
                 selectedBodyRendererIndex = 0;

            Renderer bodyCand = (selectedBodyRendererIndex != -1) ? availableRenderers[selectedBodyRendererIndex] : null;
            selectedClothRendererIndex = System.Array.FindIndex(availableRenderers, r =>
                r != bodyCand && (r.name.ToLower().Contains("cloth") || r.name.ToLower().Contains("dress") || r.name.ToLower().Contains("shirt") || r.name.ToLower().Contains("outer")));
            if (selectedClothRendererIndex == -1)
                 selectedClothRendererIndex = System.Array.FindIndex(availableRenderers, r => r != bodyCand);

            if (selectedClothRendererIndex == -1 && availableRenderers.Length > 1 && selectedBodyRendererIndex == 0)
                selectedClothRendererIndex = 1;
            else if (selectedClothRendererIndex == -1 && availableRenderers.Length > 0 && selectedBodyRendererIndex != 0)
                 selectedClothRendererIndex = 0;
        }

        UpdateSelectedRenderers();
        detectedPenetrationIndices.Clear(); detectedWorldPositions.Clear(); excludedIndices.Clear();
        if (animator != null && animator.isHuman) { CacheProtectedBoneTransforms(); CacheProtectedVertices(); }
        else { protectedBoneTransforms.Clear(); protectedVertices.Clear(); }
        Repaint();
    }

    void UpdateSelectedRenderers()
    {
        if (availableRenderers == null) return;
        bodyRenderer = (selectedBodyRendererIndex >= 0 && selectedBodyRendererIndex < availableRenderers.Length) ? availableRenderers[selectedBodyRendererIndex] as SkinnedMeshRenderer : null;
        clothRenderer = (selectedClothRendererIndex >= 0 && selectedClothRendererIndex < availableRenderers.Length) ? availableRenderers[selectedClothRendererIndex] : null;
    }

    void CacheProtectedBoneTransforms()
    {
        protectedBoneTransforms.Clear();
        if (animator == null || !animator.isHuman) return;
        foreach (var boneEnum in protectedBoneEnums)
        {
            Transform boneTransform = animator.GetBoneTransform(boneEnum);
            if (boneTransform != null) protectedBoneTransforms.Add(boneTransform);
        }
    }

    void CacheProtectedVertices()
    {
        protectedVertices.Clear();
        if (bodyRenderer == null || bodyRenderer.sharedMesh == null || animator == null || !animator.isHuman || protectedBoneTransforms.Count == 0) return;
        Mesh mesh = bodyRenderer.sharedMesh; BoneWeight[] boneWeights = mesh.boneWeights; Transform[] bones = bodyRenderer.bones;
        if (bones == null || bones.Length == 0) { return; }
        for (int i = 0; i < mesh.vertexCount; i++)
        {
            if (IsBoneWeightProtected(boneWeights[i], bones, protectedBoneTransforms)) protectedVertices.Add(i);
        }
    }

    bool IsBoneWeightProtected(BoneWeight bw, Transform[] meshBones, List<Transform> currentProtectedBoneTransforms)
    {
        var influences = new List<(int index, float weight)>();
        if (bw.weight0 > 0) influences.Add((bw.boneIndex0, bw.weight0)); if (bw.weight1 > 0) influences.Add((bw.boneIndex1, bw.weight1));
        if (bw.weight2 > 0) influences.Add((bw.boneIndex2, bw.weight2)); if (bw.weight3 > 0) influences.Add((bw.boneIndex3, bw.weight3));
        if (influences.Count == 0) return false;
        influences = influences.OrderByDescending(item => item.weight).ToList();
        if (influences[0].index < 0 || influences[0].index >= meshBones.Length) return false;
        Transform dominantBone = meshBones[influences[0].index]; if (dominantBone == null) return false;
        foreach (Transform protectedBone in currentProtectedBoneTransforms)
        {
            if (protectedBone == null) continue;
            if (dominantBone == protectedBone || dominantBone.IsChildOf(protectedBone)) return true;
        }
        return false;
    }

    void DetectPenetrationsWithPhysics()
    {
        if (bodyRenderer == null || clothRenderer == null || bodyRenderer.sharedMesh == null) { return; }
        detectedPenetrationIndices.Clear(); detectedWorldPositions.Clear();
        if (protectedVertices.Count == 0 && animator != null && animator.isHuman) CacheProtectedVertices();

        Mesh bodyMeshBaked = new Mesh(); bodyRenderer.BakeMesh(bodyMeshBaked, true);
        Mesh clothMeshForCollider = new Mesh();
        Mesh clothMeshBakedForVertexCheck = new Mesh();

        bool clothIsSkinned = clothRenderer is SkinnedMeshRenderer;
        if (clothIsSkinned) {
            ((SkinnedMeshRenderer)clothRenderer).BakeMesh(clothMeshForCollider, true);
            ((SkinnedMeshRenderer)clothRenderer).BakeMesh(clothMeshBakedForVertexCheck, true);
        } else if (clothRenderer is MeshRenderer clothMr && clothMr.GetComponent<MeshFilter>()?.sharedMesh != null) {
            clothMeshForCollider = clothMr.GetComponent<MeshFilter>().sharedMesh;
            clothMeshBakedForVertexCheck = clothMr.GetComponent<MeshFilter>().sharedMesh;
        } else {
            Debug.LogError("[MeshSyncPro] 衣装メッシュの取得に失敗。"); DestroyImmediate(bodyMeshBaked); return;
        }

        bool physicsPenetrationDetected = false;
        MeshCollider bodyCol = null; MeshCollider clothCol = null;
        try {
            bodyCol = bodyRenderer.gameObject.GetComponent<MeshCollider>(); bool bodyColAdded = bodyCol == null;
            if (bodyColAdded) bodyCol = bodyRenderer.gameObject.AddComponent<MeshCollider>();
            bodyCol.sharedMesh = bodyMeshBaked; bodyCol.convex = true;

            clothCol = clothRenderer.gameObject.GetComponent<MeshCollider>(); bool clothColAdded = clothCol == null;
            if (clothColAdded) clothCol = clothRenderer.gameObject.AddComponent<MeshCollider>();
            clothCol.sharedMesh = clothMeshForCollider; clothCol.convex = true;

            physicsPenetrationDetected = Physics.ComputePenetration(
                bodyCol, bodyRenderer.transform.position, bodyRenderer.transform.rotation,
                clothCol, clothRenderer.transform.position, clothRenderer.transform.rotation,
                out Vector3 penetrationDirection, out float penetrationDistance);

            if (physicsPenetrationDetected) Debug.Log($"[MeshSyncPro] Physicsベース貫通検出: 方向 {penetrationDirection}, 距離 {penetrationDistance}");

            if (bodyColAdded) DestroyImmediate(bodyCol); else bodyCol.sharedMesh = null;
            if (clothColAdded) DestroyImmediate(clothCol); else clothCol.sharedMesh = null;
        } catch (System.Exception ex) {
            Debug.LogError($"[MeshSyncPro] Physics.ComputePenetration中にエラー: {ex.Message}");
            if (bodyCol != null && bodyCol.gameObject == bodyRenderer.gameObject && bodyRenderer.gameObject.GetComponent<MeshCollider>() == bodyCol) DestroyImmediate(bodyCol);
            if (clothCol != null && clothCol.gameObject == clothRenderer.gameObject && clothRenderer.gameObject.GetComponent<MeshCollider>() == clothCol) DestroyImmediate(clothCol);
        }

        Vector3[] bodyVertices_baked = bodyMeshBaked.vertices;
        Vector3[] clothVertices_baked_ws = clothMeshBakedForVertexCheck.vertices.Select(v => clothRenderer.transform.TransformPoint(v)).ToArray();
        Transform bodyTransform = bodyRenderer.transform;

        for (int i = 0; i < bodyVertices_baked.Length; i++) {
            if (protectedVertices.Contains(i)) continue;
            Vector3 bodyVertex_ws = bodyTransform.TransformPoint(bodyVertices_baked[i]);
            bool inExclusion = false; foreach(var zone in exclusionZones) { if (zone.isActive) { Bounds worldBounds = new Bounds(zone.center, zone.size); if (worldBounds.Contains(bodyVertex_ws)) { inExclusion = true; break; } } } if (inExclusion) continue;
            float minSqDistToCloth = float.MaxValue;
            foreach (Vector3 clothV_ws in clothVertices_baked_ws) minSqDistToCloth = Mathf.Min(minSqDistToCloth, (bodyVertex_ws - clothV_ws).sqrMagnitude);
            if (Mathf.Sqrt(minSqDistToCloth) < penetrationThreshold) {
                detectedPenetrationIndices.Add(i);
                detectedWorldPositions.Add(bodyVertex_ws);
            }
        }

        DestroyImmediate(bodyMeshBaked);
        if (clothIsSkinned) {
             DestroyImmediate(clothMeshForCollider);
             DestroyImmediate(clothMeshBakedForVertexCheck);
        }

        Repaint(); SceneView.RepaintAll();
        if (!isAutoIterating)
            EditorUtility.DisplayDialog("穿透检测完成！", $"检测到 {detectedPenetrationIndices.Count} 个穿透点！\n可在Scene视图查看红点。", "确定");
    }

    void AutoFixPenetrations()
    {
        if (bodyRenderer == null || bodyRenderer.sharedMesh == null || detectedPenetrationIndices.Count == 0) { return; }
        Mesh originalBodyMesh = bodyRenderer.sharedMesh;
        Mesh newBodyMesh = Instantiate(originalBodyMesh);
        Undo.RecordObject(bodyRenderer, "MeshSyncPro 自動修正");

        Vector3[] vertices_local = newBodyMesh.vertices;
        Mesh bodyMeshBaked = new Mesh(); bodyRenderer.BakeMesh(bodyMeshBaked, true);
        Mesh clothMeshBaked = new Mesh();
        bool clothIsSkinned = clothRenderer is SkinnedMeshRenderer;
        if (clothIsSkinned) ((SkinnedMeshRenderer)clothRenderer).BakeMesh(clothMeshBaked, true);
        else if (clothRenderer is MeshRenderer mr && mr.GetComponent<MeshFilter>()?.sharedMesh != null) clothMeshBaked = mr.GetComponent<MeshFilter>().sharedMesh;
        else { Debug.LogError("[MeshSyncPro] 衣装メッシュの取得に失敗。"); DestroyImmediate(bodyMeshBaked); DestroyImmediate(newBodyMesh); return; }

        Vector3[] bodyVertices_baked_local = bodyMeshBaked.vertices;
        Transform bodyTransform = bodyRenderer.transform;
        Transform clothTransform = clothRenderer.transform;
        Vector3[] clothVertices_baked_ws = clothMeshBaked.vertices.Select(v => clothTransform.TransformPoint(v)).ToArray();
        int[] clothTriangles_baked = clothMeshBaked.triangles;
        Vector3[] clothNormals_baked_local = clothMeshBaked.normals;

        foreach (int indexInOriginalMesh in detectedPenetrationIndices) {
            if (protectedVertices.Contains(indexInOriginalMesh) || excludedIndices.Contains(indexInOriginalMesh)) continue;
            Vector3 bodyVertex_ws_current_pose = bodyTransform.TransformPoint(bodyVertices_baked_local[indexInOriginalMesh]);
            Vector3 closestPointOnClothSurface_ws; float signedDistanceToClothSurface;
            bool foundClosest = FindClosestPointOnMeshSurface(
                bodyVertex_ws_current_pose, clothVertices_baked_ws, clothTriangles_baked,
                clothNormals_baked_local, clothTransform,
                out closestPointOnClothSurface_ws, out signedDistanceToClothSurface);

            if (foundClosest && signedDistanceToClothSurface < -0.0001f) {
                float penetrationDepth = -signedDistanceToClothSurface;
                Vector3 pushDirection_ws = (bodyVertex_ws_current_pose - closestPointOnClothSurface_ws).normalized;
                if (pushDirection_ws == Vector3.zero)
                     pushDirection_ws = bodyTransform.TransformDirection(bodyMeshBaked.normals[indexInOriginalMesh]).normalized;
                float pushDistance = penetrationDepth + pushOutOffset;
                Vector3 displacement_ws = pushDirection_ws * pushDistance;
                Vector3 displacement_local = bodyTransform.InverseTransformVector(displacement_ws);
                vertices_local[indexInOriginalMesh] += displacement_local;
            }
        }

        if (smoothingIterations > 0 && smoothingFactor > 0f) {
            Dictionary<int, HashSet<int>> adjacencyMap = BuildAdjacencyMap(newBodyMesh);
            HashSet<int> verticesToSmooth = GetAffectedVertices(
                detectedPenetrationIndices.Where(i => !excludedIndices.Contains(i) && !protectedVertices.Contains(i)).ToList(),
                adjacencyMap, influenceRadiusSteps);
            for (int i = 0; i < smoothingIterations; i++) ApplyLaplacianSmoothingStep(vertices_local, adjacencyMap, verticesToSmooth, smoothingFactor);
        }

        newBodyMesh.vertices = vertices_local;
        newBodyMesh.RecalculateNormals();
        newBodyMesh.RecalculateBounds();
        bodyRenderer.sharedMesh = newBodyMesh;

        detectedPenetrationIndices.Clear();
        detectedWorldPositions.Clear();
        Repaint(); SceneView.RepaintAll();

        if (!isAutoIterating)
            EditorUtility.DisplayDialog("自动修复完成！", "已尝试修复穿透！\n请检查效果！✨", "确定");

        DestroyImmediate(bodyMeshBaked);
        if (clothIsSkinned) DestroyImmediate(clothMeshBaked);
    }

    bool FindClosestPointOnMeshSurface(Vector3 point_ws, Vector3[] meshVertices_baked_ws, int[] meshTriangles, Vector3[] meshNormals_baked_local_for_sign, Transform meshTransform_for_sign, out Vector3 closestPointOnSurface_ws, out float signedDistance)
    {
        closestPointOnSurface_ws = Vector3.zero; signedDistance = float.MaxValue; bool found = false; float minSqrDistance = float.MaxValue; int bestTriIdx = -1;
        if (meshVertices_baked_ws == null || meshTriangles == null) return false;
        for (int i = 0; i < meshTriangles.Length; i += 3) {
            if (meshTriangles[i] >= meshVertices_baked_ws.Length || meshTriangles[i+1] >= meshVertices_baked_ws.Length || meshTriangles[i+2] >= meshVertices_baked_ws.Length) continue;
            Vector3 p0_ws = meshVertices_baked_ws[meshTriangles[i]]; Vector3 p1_ws = meshVertices_baked_ws[meshTriangles[i + 1]]; Vector3 p2_ws = meshVertices_baked_ws[meshTriangles[i + 2]];
            Vector3 currentClosestOnTri_ws = ClosestPointOnTriangle(point_ws, p0_ws, p1_ws, p2_ws); float sqrDist = (point_ws - currentClosestOnTri_ws).sqrMagnitude;
            if (sqrDist < minSqrDistance) { minSqrDistance = sqrDist; closestPointOnSurface_ws = currentClosestOnTri_ws; bestTriIdx = i; found = true; }
        }
        if (found) {
            Vector3 vecToPoint = point_ws - closestPointOnSurface_ws;
            if (vecToPoint.sqrMagnitude < 0.000001f) signedDistance = 0f;
            else { Vector3 triangleFaceNormal_ws = Vector3.zero;
                if (bestTriIdx != -1 && meshTriangles[bestTriIdx] < meshVertices_baked_ws.Length && meshTriangles[bestTriIdx+1] < meshVertices_baked_ws.Length && meshTriangles[bestTriIdx+2] < meshVertices_baked_ws.Length) {
                    Vector3 p0 = meshVertices_baked_ws[meshTriangles[bestTriIdx]]; Vector3 p1 = meshVertices_baked_ws[meshTriangles[bestTriIdx + 1]]; Vector3 p2 = meshVertices_baked_ws[meshTriangles[bestTriIdx + 2]];
                    triangleFaceNormal_ws = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                }
                else if (meshNormals_baked_local_for_sign != null && meshNormals_baked_local_for_sign.Length > 0 && meshTransform_for_sign != null) {
                    triangleFaceNormal_ws = meshTransform_for_sign.TransformDirection(meshNormals_baked_local_for_sign[0]).normalized;
                }
                else triangleFaceNormal_ws = (point_ws - closestPointOnSurface_ws).normalized;
                signedDistance = Vector3.Dot(vecToPoint, triangleFaceNormal_ws);
            }
        } return found;
    }
    Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c) {
        Vector3 ab = b-a; Vector3 ac = c-a; Vector3 ap = point-a; float d1=Vector3.Dot(ab,ap); float d2=Vector3.Dot(ac,ap); if(d1<=0f&&d2<=0f)return a;
        Vector3 bp = point-b; float d3=Vector3.Dot(ab,bp); float d4=Vector3.Dot(ac,bp); if(d3>=0f&&d4<=d3)return b;
        float vc=d1*d4-d3*d2; if(vc<=0f&&d1>=0f&&d3<=0f){float v=d1/(d1-d3); return a+v*ab;}
        Vector3 cp = point-c; float d5=Vector3.Dot(ab,cp); float d6=Vector3.Dot(ac,cp); if(d6>=0f&&d5<=d6)return c;
        float vb=d5*d2-d1*d6; if(vb<=0f&&d2>=0f&&d6<=0f){float w=d2/(d2-d6); return a+w*ac;}
        float va=d3*d6-d5*d4; if(va<=0f&&(d4-d3)>=0f&&(d5-d6)>=0f){float w_bc=(d4-d3)/((d4-d3)+(d5-d6)); return b+w_bc*(c-b);}
        float denom=1f/(va+vb+vc); if (Mathf.Approximately(denom, 0f)) return (a+b+c)/3f;
        float v_coord=vb*denom; float w_coord=vc*denom; return a+ab*v_coord+ac*w_coord;
    }
    Dictionary<int, HashSet<int>> BuildAdjacencyMap(Mesh mesh) {
        var map=new Dictionary<int,HashSet<int>>(); int[] triangles=mesh.triangles;
        for(int i=0;i<triangles.Length;i+=3){
            int v0=triangles[i];int v1=triangles[i+1];int v2=triangles[i+2];
            if(!map.ContainsKey(v0))map[v0]=new HashSet<int>();if(!map.ContainsKey(v1))map[v1]=new HashSet<int>();if(!map.ContainsKey(v2))map[v2]=new HashSet<int>();
            map[v0].Add(v1);map[v0].Add(v2);map[v1].Add(v0);map[v1].Add(v2);map[v2].Add(v0);map[v2].Add(v1);
        } return map;
    }
    HashSet<int> GetAffectedVertices(List<int> initialIndices, Dictionary<int, HashSet<int>> adjacencyMap, int steps) {
        HashSet<int> affected=new HashSet<int>(initialIndices);if(steps<=0)return affected;
        Queue<(int index,int dist)> queue=new Queue<(int,int)>();
        foreach(int idx in initialIndices)queue.Enqueue((idx,0));
        while(queue.Count>0){var current=queue.Dequeue();if(current.dist>=steps)continue;
            if(adjacencyMap.TryGetValue(current.index,out HashSet<int> neighbors)){
                foreach(int neighbor in neighbors)if(affected.Add(neighbor))queue.Enqueue((neighbor,current.dist+1));
            }
        } return affected;
    }
    void ApplyLaplacianSmoothingStep(Vector3[] vertices, Dictionary<int, HashSet<int>> adjacencyMap, HashSet<int> targetVertices, float factor) {
        Vector3[] smoothedDeltas=new Vector3[vertices.Length];
        foreach(int i in targetVertices){
            if(protectedVertices.Contains(i))continue;
            if(adjacencyMap.TryGetValue(i,out HashSet<int> neighbors)&&neighbors.Count>0){
                Vector3 centroid=Vector3.zero;int validNeighborCount=0;
                foreach(int neighborIdx in neighbors){centroid+=vertices[neighborIdx];validNeighborCount++;}
                if(validNeighborCount>0){centroid/=validNeighborCount;smoothedDeltas[i]=(centroid-vertices[i])*factor;}
            }
        }
        foreach(int i in targetVertices){
            if(protectedVertices.Contains(i))continue;
            vertices[i]+=smoothedDeltas[i];
        }
    }

    void OnSceneGUI(SceneView sceneView)
    {
        if (avatar == null) return;
        if (showExclusionZonesFold)
        {
            for (int i = 0; i < exclusionZones.Count; i++)
            {
                if (!exclusionZones[i].isActive) continue;
                var zone = exclusionZones[i];
                Handles.color = new Color(0.2f, 0.8f, 0.2f, 0.1f); // 半透明绿色 (填充)
                Handles.DrawSolidRectangleWithOutline( // 底面
                    new Vector3[] {
                        zone.center + new Vector3(-zone.size.x, -zone.size.y, -zone.size.z) * 0.5f,
                        zone.center + new Vector3( zone.size.x, -zone.size.y, -zone.size.z) * 0.5f,
                        zone.center + new Vector3( zone.size.x, -zone.size.y,  zone.size.z) * 0.5f,
                        zone.center + new Vector3(-zone.size.x, -zone.size.y,  zone.size.z) * 0.5f
                    }, Handles.color, Color.green * 0.8f);
                 Handles.DrawSolidRectangleWithOutline( // 顶面
                    new Vector3[] {
                        zone.center + new Vector3(-zone.size.x, zone.size.y, -zone.size.z) * 0.5f,
                        zone.center + new Vector3( zone.size.x, zone.size.y, -zone.size.z) * 0.5f,
                        zone.center + new Vector3( zone.size.x, zone.size.y,  zone.size.z) * 0.5f,
                        zone.center + new Vector3(-zone.size.x, zone.size.y,  zone.size.z) * 0.5f
                    }, Handles.color, Color.green * 0.8f);
                Handles.color = Color.green;
                Handles.DrawWireCube(zone.center, zone.size);
                EditorGUI.BeginChangeCheck();
                Vector3 newPosition = Handles.PositionHandle(zone.center, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "移动排除区域");
                    exclusionZones[i].center = newPosition;
                }
            }
        }
        if (showDetectionInScene && detectedWorldPositions.Count > 0)
        {
            Handles.color = Color.red;
            foreach (var p_ws in detectedWorldPositions)
            {
                float size = HandleUtility.GetHandleSize(p_ws) * 0.03f;
                Handles.SphereHandleCap(0, p_ws, Quaternion.identity, size, EventType.Repaint);
            }
        }
    }
}
#endif
