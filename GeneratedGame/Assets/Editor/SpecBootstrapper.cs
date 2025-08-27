using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using TMPro;
using UnityEditor.Events;
using UnityEngine.UI;

// SpecBootstrapper builds Unity scenes from a JSON spec at editor time.
// Menu: GameGen/Build From Spec
// It loads Assets/Resources/Bootstrap/GameSpec2D.json and constructs scenes
// with background, tilemap, player, enemies, collectibles, and UI.

public static class SpecBootstrapper
{
    [Serializable]
    public class GameSpec
    {
        public GameMeta game;
        public SceneSpec[] scenes;
    }

    [Serializable]
    public class GameMeta
    {
        public string title;
    }

    [Serializable]
    public class SceneSpec
    {
        public string name;
        public string background;
        public string tilemap; // tileset filename
        public ActorSpec player;
        public ActorSpec[] enemies;
        public CollectibleSpec[] collectibles;
        public UIElementSpec[] ui;
    }

    [Serializable]
    public class ActorSpec
    {
        public string sprite;
        public string controller; // script filename
        public float[] spawn; // [x, y]
    }

    [Serializable]
    public class CollectibleSpec
    {
        public string sprite;
        public float[] spawn; // [x, y]
    }

    [Serializable]
    public class UIElementSpec
    {
        public string type; // label | button | score
        public string text;
        public string anchor; // top_left, top_right, bottom_left, bottom_right, center
        public string onClick; // optional
    }

    [MenuItem("GameGen/Build From Spec")] 
    public static void BuildFromSpec()
    {
        try
        {
            Debug.Log("[GameGen] Build started.");
            var spec = LoadSpec();
            if (spec == null)
            {
                Debug.LogError("[GameGen] Spec not found or invalid.");
                return;
            }

            // Physics defaults
            Physics2D.gravity = new Vector2(0, -9.81f);

            if (spec.scenes == null || spec.scenes.Length == 0)
            {
                Debug.LogWarning("[GameGen] No scenes defined in spec.");
                return;
            }

            string scenesDir = "Assets/Scenes";
            if (!AssetDatabase.IsValidFolder(scenesDir))
            {
                AssetDatabase.CreateFolder("Assets", "Scenes");
            }

            foreach (var sceneSpec in spec.scenes)
            {
                CreateSceneFromSpec(sceneSpec, scenesDir);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[GameGen] Build complete.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[GameGen] Exception: " + ex);
        }
    }

    private static GameSpec LoadSpec()
    {
        // Try Resources first
        var textAsset = Resources.Load<TextAsset>("Bootstrap/GameSpec2D");
        if (textAsset != null)
        {
            try
            {
                var spec = JsonUtility.FromJson<GameSpec>(textAsset.text);
                Debug.Log("[GameGen] Spec loaded from Resources: " + textAsset.name);
                return spec;
            }
            catch (Exception ex)
            {
                Debug.LogError("[GameGen] Failed to parse spec from Resources: " + ex.Message);
            }
        }

        // Fallback: direct path
        string path = "Assets/Resources/Bootstrap/GameSpec2D.json";
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var spec = JsonUtility.FromJson<GameSpec>(json);
                Debug.Log("[GameGen] Spec loaded from file: " + path);
                return spec;
            }
            catch (Exception ex)
            {
                Debug.LogError("[GameGen] Failed to parse spec from file: " + ex.Message);
            }
        }

        return null;
    }

    private static void CreateSceneFromSpec(SceneSpec sceneSpec, string scenesDir)
    {
        var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        newScene.name = string.IsNullOrEmpty(sceneSpec.name) ? "GeneratedScene" : sceneSpec.name;
        Debug.Log($"[GameGen] Creating scene: {newScene.name}");

        // Camera
        var cameraGO = new GameObject("Main Camera");
        var cam = cameraGO.AddComponent<Camera>();
        cameraGO.tag = "MainCamera";
        cam.orthographic = true;
        cam.orthographicSize = 5f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.2f, 0.2f, 0.25f);

        // Background
        if (!string.IsNullOrEmpty(sceneSpec.background))
        {
            var bg = CreateSpriteObject("Background", sceneSpec.background, new Vector2(0, 0), orderInLayer: -10);
            if (bg != null)
            {
                var sr = bg.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    // Stretch to camera if possible
                    sr.drawMode = SpriteDrawMode.Sliced;
                }
                Debug.Log($"[GameGen] Background created: {sceneSpec.background}");
            }
        }

        // Tilemap
        if (!string.IsNullOrEmpty(sceneSpec.tilemap))
        {
            BuildTilemap(sceneSpec.tilemap);
        }

        // Player
        if (sceneSpec.player != null)
        {
            SpawnActor(sceneSpec.player, isPlayer: true);
        }

        // Enemies
        if (sceneSpec.enemies != null)
        {
            foreach (var e in sceneSpec.enemies)
            {
                SpawnActor(e, isPlayer: false);
            }
        }

        // Collectibles
        if (sceneSpec.collectibles != null)
        {
            foreach (var c in sceneSpec.collectibles)
            {
                SpawnCollectible(c);
            }
        }

        // UI
        if (sceneSpec.ui != null)
        {
            BuildUI(sceneSpec.ui);
        }

        // Save scene
        string safeName = MakeSafeFilename(newScene.name);
        string scenePath = $"{scenesDir}/{safeName}.unity";
        EditorSceneManager.SaveScene(newScene, scenePath);
        Debug.Log("[GameGen] Saved scene: " + scenePath);
    }

    private static void BuildTilemap(string tilesetFile)
    {
        var gridGO = new GameObject("Grid");
        gridGO.AddComponent<Grid>();

        var tilemapGO = new GameObject("Tilemap");
        tilemapGO.transform.parent = gridGO.transform;
        var tilemap = tilemapGO.AddComponent<Tilemap>();
        tilemapGO.AddComponent<TilemapRenderer>();

        // Load tileset as sprite and fill a simple ground platform
        var sprite = LoadSpriteFrom3P(tilesetFile, importAsSprite: true);
        if (sprite == null)
        {
            Debug.LogWarning("[GameGen] Tileset sprite not found: " + tilesetFile);
            return;
        }

        var tile = ScriptableObject.CreateInstance<Tile>();
        tile.sprite = sprite;

        // Simple platform from x=-10..10 at y=-2, and a staircase
        for (int x = -10; x <= 10; x++)
        {
            tilemap.SetTile(new Vector3Int(x, -2, 0), tile);
        }
        for (int i = 0; i < 5; i++)
        {
            for (int x = 0; x <= i; x++)
            {
                tilemap.SetTile(new Vector3Int(2 + x, -1 + i, 0), tile);
            }
        }

        Debug.Log("[GameGen] Tilemap built from: " + tilesetFile);
    }

    private static void SpawnActor(ActorSpec spec, bool isPlayer)
    {
        Vector2 pos = spec.spawn != null && spec.spawn.Length >= 2 ? new Vector2(spec.spawn[0], spec.spawn[1]) : Vector2.zero;
        var go = CreateSpriteObject(isPlayer ? "Player" : "Enemy", spec.sprite, pos, 0);
        if (go == null)
        {
            Debug.LogWarning("[GameGen] Failed to create actor; missing sprite: " + spec.sprite);
            return;
        }

        var rb = go.GetComponent<Rigidbody2D>();
        if (rb == null) rb = go.AddComponent<Rigidbody2D>();
        rb.gravityScale = isPlayer ? 3f : 0.5f;
        rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        var bc = go.GetComponent<BoxCollider2D>();
        if (bc == null) bc = go.AddComponent<BoxCollider2D>();
        bc.isTrigger = false;

        if (!string.IsNullOrEmpty(spec.controller))
        {
            AttachController(go, spec.controller);
        }

        Debug.Log($"[GameGen] {(isPlayer ? "Player" : "Enemy")} spawned at {pos} with {spec.controller}");
    }

    private static void SpawnCollectible(CollectibleSpec spec)
    {
        Vector2 pos = spec.spawn != null && spec.spawn.Length >= 2 ? new Vector2(spec.spawn[0], spec.spawn[1]) : Vector2.zero;
        var go = CreateSpriteObject("Collectible", spec.sprite, pos, 0);
        if (go == null)
        {
            Debug.LogWarning("[GameGen] Failed to create collectible; missing sprite: " + spec.sprite);
            return;
        }

        var col = go.GetComponent<Collider2D>();
        if (col == null) col = go.AddComponent<CircleCollider2D>();
        col.isTrigger = true;

        // Attach Collectible script if present
        AttachController(go, "Collectible.cs");
        Debug.Log($"[GameGen] Collectible spawned at {pos}");
    }

    private static GameObject CreateSpriteObject(string name, string spriteFilename, Vector2 position, int orderInLayer)
    {
        var go = new GameObject(name);
        go.transform.position = new Vector3(position.x, position.y, 0);
        var sr = go.AddComponent<SpriteRenderer>();

        var sprite = LoadSpriteFrom3P(spriteFilename, importAsSprite: true);
        if (sprite == null)
        {
            UnityEngine.Object.DestroyImmediate(go);
            return null;
        }

        sr.sprite = sprite;
        sr.sortingOrder = orderInLayer;
        return go;
    }

    private static Sprite LoadSpriteFrom3P(string filename, bool importAsSprite)
    {
        string path = Find3PAssetPath(filename);
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogWarning("[GameGen] Asset not found in Assets/3P/: " + filename);
            return null;
        }

        if (importAsSprite)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                if (importer.textureType != TextureImporterType.Sprite)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.SaveAndReimport();
                }
            }
        }

        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (texture == null)
        {
            Debug.LogWarning("[GameGen] Failed to load Texture2D at: " + path);
            return null;
        }

        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null)
        {
            // Create a sprite on the fly
            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
        }

        return sprite;
    }

    private static string Find3PAssetPath(string filename)
    {
        string baseDir = "Assets/3P";
        string candidate = Path.Combine(baseDir, filename).Replace("\\", "/");
        if (File.Exists(candidate)) return candidate;

        // Search under 3P just in case
        var guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(filename), new[] { baseDir });
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileName(path).Equals(filename, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    private static void AttachController(GameObject go, string controllerFilename)
    {
        if (string.IsNullOrEmpty(controllerFilename)) return;
        string scriptName = Path.GetFileNameWithoutExtension(controllerFilename);

        var type = FindTypeByName(scriptName);
        if (type == null)
        {
            Debug.LogWarning($"[GameGen] Script type not found: {scriptName}. Ensure Assets/Scripts/{controllerFilename} exists.");
            return;
        }
        go.AddComponent(type);
    }

    private static Type FindTypeByName(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(typeName);
            if (t != null) return t;
        }
        return null;
    }

    private static void BuildUI(UIElementSpec[] ui)
    {
        var canvasGO = new GameObject("Canvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>();

        foreach (var element in ui)
        {
            if (string.Equals(element.type, "label", StringComparison.OrdinalIgnoreCase))
            {
                CreateLabel(canvasGO.transform, element.text, element.anchor);
            }
            else if (string.Equals(element.type, "button", StringComparison.OrdinalIgnoreCase))
            {
                CreateButton(canvasGO.transform, element.text, element.anchor, element.onClick);
            }
            else if (string.Equals(element.type, "score", StringComparison.OrdinalIgnoreCase))
            {
                CreateLabel(canvasGO.transform, element.text, element.anchor);
            }
        }
    }

    private static void CreateLabel(Transform parent, string text, string anchor)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = string.IsNullOrEmpty(text) ? "Label" : text;
        tmp.fontSize = 32;
        var rt = tmp.rectTransform;
        ApplyAnchor(rt, anchor);
    }

    private static void CreateButton(Transform parent, string text, string anchor, string onClick)
    {
        var go = new GameObject("Button");
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.color = new Color(0.2f, 0.5f, 0.9f, 1f);
        var button = go.AddComponent<Button>();
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(200, 60);
        ApplyAnchor(rt, anchor);

        var label = new GameObject("Text");
        label.transform.SetParent(go.transform, false);
        var tmp = label.AddComponent<TextMeshProUGUI>();
        tmp.text = string.IsNullOrEmpty(text) ? "Button" : text;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = 28;
        var labelRT = tmp.rectTransform;
        labelRT.anchorMin = new Vector2(0, 0);
        labelRT.anchorMax = new Vector2(1, 1);
        labelRT.offsetMin = Vector2.zero;
        labelRT.offsetMax = Vector2.zero;

        if (!string.IsNullOrEmpty(onClick))
        {
            var dispatcher = go.GetComponent<UIInvokeStatic>();
            if (dispatcher == null) dispatcher = go.AddComponent<UIInvokeStatic>();
            dispatcher.methodName = onClick;
            UnityEventTools.AddPersistentListener(button.onClick, dispatcher.InvokeAction);
        }
    }

    private static void ApplyAnchor(RectTransform rt, string anchor)
    {
        Vector2 min, max, anchoredPos;
        switch ((anchor ?? "").ToLowerInvariant())
        {
            case "top_left":
                min = max = new Vector2(0, 1);
                anchoredPos = new Vector2(100, -50);
                break;
            case "top_right":
                min = max = new Vector2(1, 1);
                anchoredPos = new Vector2(-100, -50);
                break;
            case "bottom_left":
                min = max = new Vector2(0, 0);
                anchoredPos = new Vector2(100, 50);
                break;
            case "bottom_right":
                min = max = new Vector2(1, 0);
                anchoredPos = new Vector2(-100, 50);
                break;
            default:
                min = max = new Vector2(0.5f, 0.5f);
                anchoredPos = Vector2.zero;
                break;
        }
        rt.anchorMin = min; rt.anchorMax = max; rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
    }

    private static string MakeSafeFilename(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}


